using System.IO;
using System.Reflection;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IslandQuotes.Models;

namespace IslandQuotes.Services;

/// <summary>
/// 管理 AI 审查、待人工复核、FIFO 播放队列与历史记录。
/// 所有集合变更都会尽量调度到 Avalonia UI 线程；没有 UI 线程时可直接用于无头测试。
/// </summary>
public sealed class ReviewQueueService
{
    private const string SnapshotFileName = "review-queue.json";
    private readonly PluginSettingsStore _settings;
    private readonly QuoteSourceService _sources;
    private readonly AiReviewService _ai;
    private readonly AuditLogService _audit;
    private readonly string _configDirectory;
    private readonly string _snapshotPath;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private readonly CancellationToken _lifetimeToken;
    private readonly object _refillSync = new();
    private Task? _refillTask;
    private long _refillCooldownUntil;
    private int _refillFailureCount;
    private long _snapshotSequence;
    private long _lastPersistedSnapshotSequence;
    private int _pendingCount;
    private int _queuedCount;
    private int _blockedCount;
    private volatile string _lastStatusMessage = "队列尚未初始化。";
    private volatile bool _initialized;
    private volatile ReviewQueueItem? _currentDisplay;
    private volatile QuoteRequestOptions? _lastRequestOptions;

    private static readonly JsonSerializerOptions SnapshotJsonOptions = CreateSnapshotJsonOptions();

    public ReviewQueueService(
        PluginSettingsStore settings,
        QuoteSourceService sources,
        AiReviewService ai,
        AuditLogService audit,
        CancellationToken lifetimeToken = default)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _ai = ai ?? throw new ArgumentNullException(nameof(ai));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _configDirectory = settings.ConfigDirectory
            ?? throw new ArgumentException("设置存储未提供配置目录。", nameof(settings));
        _snapshotPath = Path.Combine(_configDirectory, SnapshotFileName);
        _lifetimeToken = lifetimeToken;

        // 修改供应商/队列设置后立即解除补货退避，不必等冷却到期。
        _settings.SettingsChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        Interlocked.Exchange(ref _refillFailureCount, 0);
        Interlocked.Exchange(ref _refillCooldownUntil, 0);
    }

    /// <summary>AI 无法自动放行、等待人工复核的条目。</summary>
    public ObservableCollection<ReviewQueueItem> PendingManualReview { get; } = [];

    /// <summary>已通过审查、按入队顺序等待展示的条目。</summary>
    public ObservableCollection<ReviewQueueItem> PlaybackQueue { get; } = [];

    /// <summary>已播放、已拦截（含人工拒绝）的近期条目。</summary>
    public ObservableCollection<ReviewQueueItem> History { get; } = [];

    public event EventHandler? QueueChanged;

    public int PendingCount => Volatile.Read(ref _pendingCount);
    public int QueuedCount => Volatile.Read(ref _queuedCount);
    public int BlockedCount => Volatile.Read(ref _blockedCount);
    public string LastStatusMessage => _lastStatusMessage;

    /// <summary>恢复上次队列快照。未完成审查和待审查条目会安全转入人工复核。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        bool entered = false;
        try
        {
            await _operationGate.WaitAsync(ct).ConfigureAwait(false);
            entered = true;
            if (_initialized)
            {
                return;
            }

            await RestoreSnapshotLockedAsync(ct).ConfigureAwait(false);
            _initialized = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _lastStatusMessage = "队列初始化已取消。";
        }
        catch (Exception ex)
        {
            await WriteQueueErrorSafeAsync("initialize", ex).ConfigureAwait(false);
            _lastStatusMessage = "队列初始化失败，将在下次刷新时重试。";
        }
        finally
        {
            if (entered)
            {
                _operationGate.Release();
            }
        }
    }

    /// <summary>
    /// 获取下一条可展示内容。播放队列非空时严格按 FIFO 出队；
    /// 队列为空时确保后台补货在运行，最多等待 <paramref name="waitForRefill"/>，
    /// 期间没有内容就立即返回 null，让组件保持自己的刷新节奏。
    /// </summary>
    /// <remarks>
    /// 出队走快路径、不获取 <see cref="_operationGate"/>：集合变更全部经 UI 线程串行化，
    /// 且展示调用方（组件）自身保证串行，因此后台补货（抓取+审查管线）在途时出队不必等待，
    /// 播放节奏不受补货耗时影响。抓取与 AI 审查可能持续数分钟，绝不能阻塞展示调用方。
    /// </remarks>
    public Task<ReviewQueueItem?> GetNextForDisplayAsync(
        QuoteRequestOptions options,
        CancellationToken ct = default)
        => GetNextForDisplayAsync(options, TimeSpan.Zero, allowBackgroundRefill: true, ct);

    /// <summary>
    /// 获取下一条可展示内容；队列为空时最多等待指定时长让后台补货产出内容。
    /// <paramref name="allowBackgroundRefill"/> 为 false 时只从现有队列出队，不启动新的抓取/审查
    /// （用于关闭自动刷新后的人工新增内容展示）。
    /// </summary>
    public async Task<ReviewQueueItem?> GetNextForDisplayAsync(
        QuoteRequestOptions options,
        TimeSpan waitForRefill,
        bool allowBackgroundRefill = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            await EnsureInitializedAsync(ct).ConfigureAwait(false);

            // 只出队当前组件仍启用的来源：来源被停用后，队列里已有的旧条目不应继续播放；
            // 多个组件共用队列时，也不会显示别的组件才启用的来源。
            HashSet<string> eligibleProviders = _sources.GetEligibleProviderIds(options);
            ReviewQueueItem? queued = await DequeueFirstApprovedAsync(eligibleProviders).ConfigureAwait(false);
            if (queued is not null)
            {
                return queued;
            }

            Task refill = allowBackgroundRefill
                ? EnsureBackgroundRefill(options)
                : Task.CompletedTask;

            if (waitForRefill > TimeSpan.Zero)
            {
                if (!refill.IsCompleted)
                    await Task.WhenAny(refill, Task.Delay(waitForRefill, ct)).ConfigureAwait(false);

                ct.ThrowIfCancellationRequested();

                queued = await DequeueFirstApprovedAsync(eligibleProviders).ConfigureAwait(false);
                if (queued is not null)
                {
                    return queued;
                }
            }

            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await SetStatusAndNotifyAsync("操作已取消；队列状态未被破坏。").ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            await WriteQueueErrorSafeAsync("get_next", ex).ConfigureAwait(false);
            await SetStatusAndNotifyAsync($"获取内容失败：{ex.Message}").ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>最近一次出队并交给组件的内容，用于设置页展示「正在显示」。</summary>
    public ReviewQueueItem? CurrentDisplayItem => _currentDisplay;

    /// <summary>组件当前不再显示任何内容时清除「正在显示」。</summary>
    public void ClearCurrentDisplay()
    {
        if (_currentDisplay is null) return;
        _currentDisplay = null;

        try
        {
            RunOnUiForSync(RaiseQueueChangedLocked);
        }
        catch
        {
            // 关闭阶段队列不可用时忽略。
        }
    }

    /// <summary>是否有后台抓取/审查批次正在运行。</summary>
    public bool IsRefilling
    {
        get
        {
            lock (_refillSync)
            {
                return _refillTask is { IsCompleted: false };
            }
        }
    }

    /// <summary>
    /// 手动立即补充队列，使用最近一次组件请求的设置快照；返回本批实际加入的条数。
    /// 会先清除补货退避，便于人工在供应商恢复后立刻重试。
    /// </summary>
    public async Task<int> RefreshNowAsync()
    {
        QuoteRequestOptions? options = _lastRequestOptions;
        if (options is null) return 0;

        Interlocked.Exchange(ref _refillFailureCount, 0);
        Interlocked.Exchange(ref _refillCooldownUntil, 0);
        return await RefreshBatchAsync(options).ConfigureAwait(false);
    }

    /// <summary>
    /// 确保有一个后台补货任务在运行（同一时间只跑一个）。
    /// 连续多个批次都没有产出可播放内容时会逐渐冷却，避免供应商故障时无限重试。
    /// </summary>
    public Task EnsureBackgroundRefill(QuoteRequestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _lastRequestOptions = options;

        lock (_refillSync)
        {
            if (_refillTask is { IsCompleted: false })
            {
                return _refillTask;
            }

            if (QueuedCount + AiInFlightCount >= GetEffectiveQueueMax(options, BuildReviewPolicy()))
            {
                return Task.CompletedTask;
            }

            if (Stopwatch.GetTimestamp() < Interlocked.Read(ref _refillCooldownUntil))
            {
                return Task.CompletedTask;
            }

            CancellationTokenSource cts = _lifetimeToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken)
                : new CancellationTokenSource();
            _refillTask = RunBackgroundRefillAsync(options, cts);
            return _refillTask;
        }
    }

    private async Task RunBackgroundRefillAsync(
        QuoteRequestOptions options,
        CancellationTokenSource cts)
    {
        int added = 0;
        bool fetchFailed = false;
        bool canceled = false;
        try
        {
            (added, fetchFailed) = await RefreshBatchWithOutcomeAsync(options, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }
        catch
        {
            canceled = true;
        }
        finally
        {
            // RefreshBatchAsync 自己吞掉取消并返回 0，不能只看返回值判断是否被取消。
            bool wasCanceled = canceled || cts.IsCancellationRequested;
            cts.Dispose();

            if (!wasCanceled)
            {
                if (added > 0)
                {
                    // 本批确实新增了播放/待复核条目，说明管线正常；
                    // 不能用「播放队列是否增长」判断，那会被并发出队和正常的中风险结果误导。
                    Interlocked.Exchange(ref _refillFailureCount, 0);
                    Interlocked.Exchange(ref _refillCooldownUntil, 0);
                }
                else
                {
                    // 队列里还有内容、或者直通来源仍在等 AI 审查同时推进时：
                    // 短暂退避即可，不能按故障升级到 60 秒。
                    int delaySeconds;
                    bool hasDirectSources = !AllEnabledSourcesNeedAiReview(options);
                    if (QueuedCount > 0 || (hasDirectSources && IsAiReviewing) || !fetchFailed)
                    {
                        // 没抓到新内容但没有抓取错误（例如内容重复/队列刚被填满）不是故障。
                        delaySeconds = 3;
                    }
                    else
                    {
                        // 10s、20s、40s、60s：真正没有可播放内容时才升级退避。
                        int failures = Interlocked.Increment(ref _refillFailureCount);
                        delaySeconds = Math.Min(60, 10 << Math.Min(failures - 1, 3));
                    }

                    long until = Stopwatch.GetTimestamp()
                                 + (long)(delaySeconds * (double)Stopwatch.Frequency);
                    Interlocked.Exchange(ref _refillCooldownUntil, until);
                }
            }
        }
    }

    /// <summary>确保队列已从快照恢复；仅在尚未初始化时获取操作闸门。</summary>
    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
        {
            return;
        }

        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureInitializedLockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>是否有后台 AI 审查在运行或排队。</summary>
    public bool IsAiReviewing
    {
        get
        {
            lock (_aiReviewSync)
            {
                return _aiReviewLoopRunning || _pendingAiReviews.Count > 0;
            }
        }
    }

    /// <summary>
    /// 把 AI 审查放入独立的后台队列：不占用补货闸门，直通内容可以继续补货和播放。
    /// 返回实际受理的候选数（为 0 表示积压已达上限而未受理）。
    /// </summary>
    private int QueueAiReview(
        (string ItemId, QuoteContent Quote)[] candidates,
        string sessionId,
        IReadOnlyList<string> ignoreTerms,
        ReviewPolicy policy)
    {
        if (candidates.Length == 0) return 0;

        lock (_aiReviewSync)
        {
            // 不在这里按积压上限丢弃：补货目标已扣除审查中的条目，积压不会超过播放缓冲；
            // 抓到后再丢弃会让送审来源的实际份额被 AI 吞吐量卡住，而直通来源占满其余份额。
            _pendingAiReviews.Add(new AiReviewWork(candidates, sessionId, ignoreTerms, policy));
            Interlocked.Add(ref _aiInFlightCount, candidates.Length);
            if (_aiReviewLoopRunning) return candidates.Length;
            _aiReviewLoopRunning = true;
        }

        _ = RunAiReviewLoopAsync();
        return candidates.Length;
    }

    private async Task RunAiReviewLoopAsync()
    {
        while (!_lifetimeToken.IsCancellationRequested)
        {
            AiReviewWork work;
            lock (_aiReviewSync)
            {
                if (_pendingAiReviews.Count == 0)
                {
                    _aiReviewLoopRunning = false;
                    return;
                }

                work = _pendingAiReviews[0];
                _pendingAiReviews.RemoveAt(0);
                _currentAiReview = work;
            }

            try
            {
                ReviewQueueItem[] reviewed = await ReviewGroupAsync(
                        work.Candidates,
                        work.SessionId,
                        work.IgnoreTerms,
                        work.Policy,
                        _lifetimeToken)
                    .ConfigureAwait(false);

                (int added, int dropped) = await AddReviewedItemsAsync(reviewed).ConfigureAwait(false);
                await RunOnUiAsync(() =>
                {
                    int autoPassed = reviewed.Count(item => item.State == ReviewItemState.Approved);
                    int pending = reviewed.Count(item => item.State == ReviewItemState.AwaitingManualReview);
                    int blocked = reviewed.Count(item => item.State == ReviewItemState.Blocked);
                    string message = $"AI 审查 {reviewed.Length} 条：自动通过 {autoPassed}、待人工 {pending}、拦截 {blocked}";
                    if (added > 0)
                        message += $"，已加入 {added} 条";
                    message += "。";
                    if (dropped > 0)
                        message += $" 待人工复核已达上限，{dropped} 条未加入列表。";
                    UpdateCountsLocked(message + $" 当前播放队列 {QueuedCount} 条。");
                    RaiseQueueChangedLocked();
                }).ConfigureAwait(false);

                await PersistSnapshotSafeAsync().ConfigureAwait(false);
                await WriteQueueSummaryAuditSafeAsync("ai_batch_complete").ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                await WriteQueueErrorSafeAsync("ai_review_loop", ex).ConfigureAwait(false);
            }
            finally
            {
                lock (_aiReviewSync)
                {
                    if (ReferenceEquals(_currentAiReview, work)) _currentAiReview = null;
                }

                Interlocked.Add(ref _aiInFlightCount, -work.Candidates.Length);
            }
        }

        lock (_aiReviewSync)
        {
            _aiReviewLoopRunning = false;
            // 生命周期结束时未处理的积压不再计入在途数量。
            Interlocked.Add(ref _aiInFlightCount, -_pendingAiReviews.Sum(static work => work.Candidates.Length));
            _pendingAiReviews.Clear();
        }
    }

    /// <summary>
    /// 抓取并审查一个批次，返回本批新增到播放队列或待复核列表的条目数（不会自动出队）。
    /// 全部被拦截或因待复核饱和而被丢弃时返回 0，便于后台补货判断退避。
    /// </summary>
    public async Task<int> RefreshBatchAsync(
        QuoteRequestOptions options,
        CancellationToken ct = default)
        => (await RefreshBatchWithOutcomeAsync(options, ct).ConfigureAwait(false)).Added;

    private async Task<(int Added, bool FetchFailed)> RefreshBatchWithOutcomeAsync(
        QuoteRequestOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        bool entered = false;
        try
        {
            await _operationGate.WaitAsync(ct).ConfigureAwait(false);
            entered = true;
            await EnsureInitializedLockedAsync(ct).ConfigureAwait(false);
            return await FillBatchLockedAsync(options, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await SetStatusAndNotifyAsync("批量刷新已取消。").ConfigureAwait(false);
            return (0, false);
        }
        catch (Exception ex)
        {
            await WriteQueueErrorSafeAsync("refresh_batch", ex).ConfigureAwait(false);
            await SetStatusAndNotifyAsync($"批量刷新失败：{ex.Message}").ConfigureAwait(false);
            return (0, true);
        }
        finally
        {
            if (entered)
            {
                _operationGate.Release();
            }
        }
    }

    /// <summary>人工批准一条中风险或 AI 审查失败的条目，并追加到播放队列末尾。</summary>
    public void Approve(ReviewQueueItem item, string note = "") => ProcessManualDecision([item], true, note);

    /// <summary>人工拒绝一条待复核条目；被拒绝的内容不会进入播放队列。</summary>
    public void Reject(ReviewQueueItem item, string note = "") => ProcessManualDecision([item], false, note);

    /// <summary>批准当前快照中的全部可批准条目。</summary>
    public int ApproveAllPending(string note = "全量人工批准")
    {
        List<ReviewQueueItem> snapshot = SnapshotPending();
        return ProcessManualDecision(snapshot, true, note);
    }

    /// <summary>拒绝当前快照中的全部待复核条目。</summary>
    public int RejectAllPending(string note = "全量人工拒绝")
    {
        List<ReviewQueueItem> snapshot = SnapshotPending();
        return ProcessManualDecision(snapshot, false, note);
    }

    /// <summary>批准所选条目。调用开始时即固定列表快照。</summary>
    public int ApproveSelected(IEnumerable<ReviewQueueItem> items, string note = "批量人工批准")
        => ProcessManualDecision(SnapshotItems(items), true, note);

    /// <summary>拒绝所选条目。调用开始时即固定列表快照。</summary>
    public int RejectSelected(IEnumerable<ReviewQueueItem> items, string note = "批量人工拒绝")
        => ProcessManualDecision(SnapshotItems(items), false, note);

    /// <summary>使用当前激活的 AI 供应商审查一段测试文本；未提供时使用内置的良性文本。</summary>
    public async Task<AiReviewResult> TestActiveAiAsync(string? content = null, CancellationToken ct = default)
    {
        AiProviderProfile? profile;
        try
        {
            profile = _settings.GetActiveProfile();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("无法读取当前激活的 AI 供应商配置。", ex);
        }

        if (profile is null || string.IsNullOrWhiteSpace(profile.Id) || !profile.IsEnabled)
        {
            throw new InvalidOperationException("当前没有可用的 AI 供应商，请先在插件设置中选择并启用供应商。");
        }

        if (string.IsNullOrWhiteSpace(profile.BaseUrl) || string.IsNullOrWhiteSpace(profile.Model))
        {
            throw new InvalidOperationException("当前激活的 AI 供应商缺少接口地址或模型名称。");
        }

        try
        {
            return await _ai.TestAsync(profile, content, ct).ConfigureAwait(false)
                ?? new AiReviewResult
                {
                    IsSuccess = false,
                    Risk = RiskLevel.Unknown,
                    Error = "AI 测试未返回结果。"
                };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await WriteQueueErrorSafeAsync("test_active_ai", ex).ConfigureAwait(false);
            return new AiReviewResult
            {
                IsSuccess = false,
                Risk = RiskLevel.Unknown,
                Error = ex.Message
            };
        }
    }

    /// <summary>将当前队列快照立即落盘，供宿主关闭时调用。</summary>
    public async Task FlushAsync()
    {
        if (!_initialized)
        {
            return;
        }

        await PersistSnapshotSafeAsync().ConfigureAwait(false);

    }
    private async Task EnsureInitializedLockedAsync(CancellationToken ct)
    {
        if (_initialized)
        {
            return;
        }

        await RestoreSnapshotLockedAsync(ct).ConfigureAwait(false);
        _initialized = true;
    }

    private async Task<(int Added, bool FetchFailed)> FillBatchLockedAsync(QuoteRequestOptions options, CancellationToken ct)
    {
        ReviewPolicy policy = BuildReviewPolicy();

        // 补到有效队列长度：需要审查时为「最大队列长度 - 当前队列长度」；
        // 完全不审查的直通内容使用更大的缓冲，短刷新间隔下也不会因排队等抓取而卡住。
        // 正在 AI 审查的候选审完后会占用队列位置，必须一并扣除：
        // 否则每批都会继续抓送审候选，直通内容先入队占满缓冲，送审来源的份额被压低。
        int effectiveMax = GetEffectiveQueueMax(options, policy);
        int inFlight = AiInFlightCount;
        int targetCount = effectiveMax - QueuedCount - inFlight;
        if (targetCount <= 0)
        {
            await SetStatusAndNotifyAsync(inFlight > 0
                    ? $"播放队列已满（{QueuedCount}/{effectiveMax}，另有 {inFlight} 条正在 AI 审查）。"
                    : $"播放队列已满（{QueuedCount}/{effectiveMax}）。")
                .ConfigureAwait(false);
            return (0, false);
        }

        bool pendingSaturated = PendingCount >= MaxPendingManualReview;
        if (pendingSaturated)
        {
            await SetStatusAndNotifyAsync(
                    $"待人工复核已达 {PendingCount} 条上限；仍会审查新内容，需人工复核的结果暂不加入列表。")
                .ConfigureAwait(false);
        }

        // 没有可用的 AI 供应商时先暂停抓取：否则每条都会审查失败并堆积到待复核，
        // 几十条之后整条流水线就永久停摆，而主界面只显示「暂无可播放的内容」。
        if (AllEnabledSourcesNeedAiReview(options))
        {
            string? profileError;
            try
            {
                profileError = _settings.ValidateActiveProfile();
            }
            catch (Exception ex)
            {
                profileError = ex.Message;
            }

            if (!string.IsNullOrWhiteSpace(profileError))
            {
                await SetStatusAndNotifyAsync(
                        $"AI 供应商不可用，暂停获取内容：{TruncateForStatus(profileError)}")
                    .ConfigureAwait(false);
                return (0, true);
            }
        }

        string sessionId = !string.IsNullOrWhiteSpace(options.RefreshSessionId)
            ? options.RefreshSessionId
            : Guid.NewGuid().ToString("N");
        IReadOnlyList<string> ignoreTerms = (options.IgnoreTerms ?? [])
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Select(term => term.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        List<(string ItemId, QuoteContent Quote)> fetched = [];
        int fetchFailures = 0;
        string lastFetchError = string.Empty;
        bool canceled = false;
        int directAddedCount = 0;
        int directProcessedCount = 0;
        int directDroppedManual = 0;

        var knownQuotes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await RunOnUiAsync(() =>
        {
            foreach (ReviewQueueItem existing in PendingManualReview.Concat(PlaybackQueue))
            {
                AddQuoteIdentity(knownQuotes, existing.Quote);
            }
        }).ConfigureAwait(false);

        // 正在 AI 审查的候选也参与去重：否则同一句会被重复抓取、各审一次，最后在队列里出现两次。
        lock (_aiReviewSync)
        {
            IEnumerable<AiReviewWork> inFlightWork = _currentAiReview is null
                ? _pendingAiReviews
                : _pendingAiReviews.Prepend(_currentAiReview);
            foreach (AiReviewWork work in inFlightWork)
            {
                foreach ((string _, QuoteContent quote) in work.Candidates)
                    AddQuoteIdentity(knownQuotes, quote);
            }
        }

        int duplicateSkips = 0;
        var fetchSync = new object();

        // 存在要求顺序抓取的来源（自定义顺序播放）且没有网络来源时串行抓取；
        // 混合配置仍使用 4 路并发，避免一个自定义来源拖慢整批网络请求。
        bool hasNetworkSource = _sources.Providers.Any(provider =>
            options.ProviderSettings is not null
            && options.ProviderSettings.TryGetValue(provider.Id, out QuoteProviderConfig? providerConfig)
            && providerConfig is { IsEnabled: true }
            && providerConfig.Weight > 0);
        bool sequentialFetch = !hasNetworkSource
            && (options.ExtraProviders ?? []).Any(provider => provider.RequiresSequentialFetch);
        using var fetchLimiter = new SemaphoreSlim(sequentialFetch ? 1 : 4);

        // 并行抓取候选：串行请求多个来源会让短刷新间隔下的补货追不上播放。
        async Task<(int Index, (string ItemId, QuoteContent Quote)? Item)> FetchSlotAsync(int index)
        {
            bool entered = false;
            try
            {
                await fetchLimiter.WaitAsync(ct).ConfigureAwait(false);
                entered = true;

                if (ct.IsCancellationRequested)
                {
                    lock (fetchSync) canceled = true;
                    return (index, null);
                }

                string itemId = Guid.NewGuid().ToString("N");
                QuoteContent? duplicate = null;
                bool fetchFailed = false;
                for (int duplicateAttempt = 0; duplicateAttempt < 3; duplicateAttempt++)
                {
                    try
                    {
                        // 单个槽位最多等 8 秒：某个来源挂死时不能拖住整批已抓到的内容。
                        using var slotCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        slotCts.CancelAfter(SlotFetchTimeout);
                        QuoteContent quote = await _sources.FetchOneAsync(
                                options, sessionId, itemId, slotCts.Token, duplicate?.ProviderId)
                            .ConfigureAwait(false);

                        bool accepted;
                        lock (fetchSync)
                        {
                            accepted = AddQuoteIdentity(knownQuotes, quote);
                            if (!accepted) duplicateSkips++;
                        }

                        if (accepted)
                        {
                            if (IsAiReviewEnabledFor(options, quote))
                                return (index, (itemId, quote));

                            // 直通内容在本槽位抓取完成后立刻入队，不等待其它槽位或 AI 批次。
                            ReviewQueueItem[] directItems = await ApplyUnreviewedCandidateAsync(
                                    itemId, quote, sessionId, ignoreTerms, policy)
                                .ConfigureAwait(false);
                            (int added, int dropped) = await AddReviewedItemsAsync(directItems)
                                .ConfigureAwait(false);
                            lock (fetchSync)
                            {
                                directAddedCount += added;
                                directProcessedCount += directItems.Length;
                                directDroppedManual += dropped;
                            }

                            return (index, null);
                        }

                        duplicate = quote;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        lock (fetchSync) canceled = true;
                        return (index, null);
                    }
                    catch (Exception ex)
                    {
                        string error = ex is OperationCanceledException
                            ? "抓取超时（8 秒）。"
                            : ex.Message;
                        lock (fetchSync)
                        {
                            fetchFailures++;
                            lastFetchError = error;
                        }

                        await WriteQueueErrorSafeAsync("fetch_quote", ex, sessionId, itemId)
                            .ConfigureAwait(false);

                        // FetchOneAsync 自己已经重试过多个来源；这里不再为“抓取异常”重跑整轮。
                        fetchFailed = true;
                        break;
                    }
                }

                if (!fetchFailed && duplicate is not null)
                {
                    lock (fetchSync)
                    {
                        lastFetchError = "上游连续返回与当前队列重复的内容，已跳过。";
                    }

                    await WriteQueueDuplicateAuditSafeAsync(sessionId, itemId, duplicate)
                        .ConfigureAwait(false);
                }

                return (index, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                lock (fetchSync) canceled = true;
                return (index, null);
            }
            finally
            {
                if (entered) fetchLimiter.Release();
            }
        }

        Task<(int Index, (string ItemId, QuoteContent Quote)? Item)>[] fetchTasks =
            Enumerable.Range(0, targetCount).Select(FetchSlotAsync).ToArray();
        (int Index, (string ItemId, QuoteContent Quote)? Item)[] fetchResults =
            await Task.WhenAll(fetchTasks).ConfigureAwait(false);

        foreach ((int Index, (string ItemId, QuoteContent Quote)? Item) result in
                 fetchResults.OrderBy(static result => result.Index))
        {
            if (result.Item is { } fetchedItem)
                fetched.Add(fetchedItem);
        }

        if (fetched.Count == 0 && directProcessedCount == 0)
        {
            string message = canceled
                ? "批量获取已取消。"
                : fetchFailures > 0
                    ? $"本批次未能获取内容：{TruncateForStatus(lastFetchError)}"
                    : duplicateSkips > 0
                        ? "本批次获取到的内容与当前队列重复，已全部跳过。"
                        : "没有抓取到任何内容。";
            await SetStatusAndNotifyAsync($"{message} 待人工复核 {PendingCount}，已拦截 {BlockedCount}。")
                .ConfigureAwait(false);
            return (0, fetchFailures > 0);
        }

        // 剩下的候选都需要 AI 审查；审查放入独立后台队列，不阻塞直通补货。
        int aiQueued = fetched.Count > 0
            ? QueueAiReview(fetched.ToArray(), sessionId, ignoreTerms, policy)
            : 0;

        await RunOnUiAsync(() =>
        {
            string message = canceled ? "批量获取已取消；" : string.Empty;
            message += $"本批次处理 {directProcessedCount} 条直通内容（加入播放队列 {directAddedCount} 条）";
            if (aiQueued > 0)
            {
                message += $"；{aiQueued} 条正在后台 AI 审查";
            }
            message += "。";
            if (duplicateSkips > 0)
            {
                message += $" 已跳过重复候选 {duplicateSkips} 次。";
            }
            if (directDroppedManual > 0)
            {
                message += $" 待人工复核已达 {MaxPendingManualReview} 条上限，{directDroppedManual} 条未加入列表。";
            }
            UpdateCountsLocked(message + $" 当前播放队列 {QueuedCount} 条。");
            RaiseQueueChangedLocked();
        }).ConfigureAwait(false);

        await PersistSnapshotSafeAsync().ConfigureAwait(false);
        await WriteQueueSummaryAuditSafeAsync("batch_complete").ConfigureAwait(false);
        return (directAddedCount, false);
    }

    /// <summary>把审查完成的条目加入队列；已加入条目会立即通知组件出队显示。</summary>
    private async Task<(int Added, int DroppedManual)> AddReviewedItemsAsync(
        IReadOnlyList<ReviewQueueItem> items)
    {
        if (items.Count == 0) return (0, 0);

        int added = 0;
        int droppedManual = 0;
        await RunOnUiAsync(() =>
        {
            foreach (ReviewQueueItem item in items)
            {
                switch (item.State)
                {
                    case ReviewItemState.Approved:
                        // 来源在抓取或审查途中被停用：按旧配置产出的条目不再加入播放队列。
                        if (_activeProviders is { } active
                            && !active.Contains(item.Quote?.ProviderId ?? string.Empty))
                        {
                            break;
                        }

                        if (!PlaybackQueue.Any(existing => existing.Id == item.Id))
                        {
                            PlaybackQueue.Add(item);
                            RestoreSourceOrderLocked(item);
                            added++;
                        }
                        break;
                    case ReviewItemState.AwaitingManualReview:
                    case ReviewItemState.ReviewFailed:
                        if (PendingManualReview.Any(existing => existing.Id == item.Id))
                        {
                            break;
                        }

                        // 待复核达到上限时不再堆积，但抓取与 AI 审查继续进行，
                        // 供应商恢复后低风险内容可以立刻恢复播放，无需人工先清空 50 条。
                        if (PendingManualReview.Count >= MaxPendingManualReview)
                        {
                            droppedManual++;
                            break;
                        }

                        PendingManualReview.Add(item);
                        added++;
                        break;
                    case ReviewItemState.Blocked:
                    case ReviewItemState.Rejected:
                    case ReviewItemState.Displayed:
                        if (!History.Any(existing => existing.Id == item.Id))
                        {
                            History.Add(item);
                        }
                        break;
                }
            }

            TrimHistoryLocked();
            UpdateCountsLocked(null);
            RaiseQueueChangedLocked();
        }).ConfigureAwait(false);

        return (added, droppedManual);
    }

    /// <summary>
    /// 顺序播放来源的条目刚追加到队尾时，若队列里已有同一来源、序号更大的条目
    /// （并发槽位或 AI 审查先完成），就把它移到这些条目之前，保持来源内的播放顺序。
    /// </summary>
    private void RestoreSourceOrderLocked(ReviewQueueItem item)
    {
        long sequence = item.Quote?.SourceSequence ?? 0;
        if (sequence <= 0) return;

        string providerId = item.Quote!.ProviderId ?? string.Empty;
        int last = PlaybackQueue.Count - 1;
        for (int i = 0; i < last; i++)
        {
            QuoteContent? other = PlaybackQueue[i].Quote;
            if (other is { SourceSequence: > 0 }
                && other.SourceSequence > sequence
                && string.Equals(other.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
            {
                PlaybackQueue.Move(last, i);
                return;
            }
        }
    }

    /// <summary>按内容所属来源的配置决定是否送 AI 审查；找不到配置时保守地启用审查。</summary>
    private static bool IsAiReviewEnabledFor(QuoteRequestOptions options, QuoteContent quote)
    {
        return options.ProviderSettings is null
            || string.IsNullOrWhiteSpace(quote.ProviderId)
            || !options.ProviderSettings.TryGetValue(quote.ProviderId, out QuoteProviderConfig? config)
            || config is null
            || config.IsAiReviewEnabled;
    }

    /// <summary>所有启用且权重大于 0 的来源是否都需要 AI 审查；混合配置时仍继续抓取。</summary>
    private static bool AllEnabledSourcesNeedAiReview(QuoteRequestOptions options)
    {
        if (options.ProviderSettings is null) return false;

        bool any = false;
        foreach (QuoteProviderConfig config in options.ProviderSettings.Values)
        {
            if (config is not { IsEnabled: true } || config.Weight <= 0) continue;
            any = true;
            if (!config.IsAiReviewEnabled) return false;
        }

        return any;
    }

    /// <summary>单个候选抓取槽位的最长等待时间；避免某个来源挂死拖住整批。</summary>
    private static readonly TimeSpan SlotFetchTimeout = TimeSpan.FromSeconds(8);

    /// <summary>已抓取、尚在后台 AI 审查（排队或进行中）的候选数；计入补货目标，避免超额抓取。</summary>
    private int _aiInFlightCount;

    private int AiInFlightCount => Math.Max(0, Volatile.Read(ref _aiInFlightCount));

    private readonly object _aiReviewSync = new();
    private readonly List<AiReviewWork> _pendingAiReviews = [];
    private AiReviewWork? _currentAiReview;

    /// <summary>最近一次整理队列时启用的来源；为 null 表示尚未整理，不做过滤。</summary>
    private volatile HashSet<string>? _activeProviders;
    private bool _aiReviewLoopRunning;

    private sealed record AiReviewWork(
        (string ItemId, QuoteContent Quote)[] Candidates,
        string SessionId,
        IReadOnlyList<string> IgnoreTerms,
        ReviewPolicy Policy);

    /// <summary>直通播放（存在不审查来源）时的独立缓冲区大小；不复用审查队列的最大长度。</summary>
    private const int DirectPlayBuffer = 20;

    /// <summary>
    /// 有效播放队列上限：所有启用来源都需要 AI 审查时使用用户设置的最大队列长度；
    /// 只要存在直通来源，就使用独立的 20 条缓冲，不会被最大队列长度卡住。
    /// </summary>
    private static int GetEffectiveQueueMax(QuoteRequestOptions options, ReviewPolicy policy)
    {
        if (AllEnabledSourcesNeedAiReview(options)) return policy.MaxQueueLength;
        return DirectPlayBuffer;
    }

    /// <summary>有效补货阈值；直通来源存在时提前在缓冲区半空时补货。</summary>
    public int GetEffectiveRefillThreshold(QuoteRequestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        ReviewPolicy policy = BuildReviewPolicy();
        int configured = GetConfiguredRefillThreshold();
        if (AllEnabledSourcesNeedAiReview(options)) return configured;
        return Math.Max(configured, GetEffectiveQueueMax(options, policy) / 2);
    }

    private int GetConfiguredRefillThreshold()
        => Math.Clamp(_settings.Settings?.Review?.RefillThreshold ?? 1, 0, 100);

    private static ReviewQueueItem CreateReviewingItem(string itemId, QuoteContent quote, string refreshSessionId) => new()
    {
        Id = itemId,
        RefreshSessionId = refreshSessionId,
        Quote = quote,
        CreatedAt = DateTimeOffset.Now,
        State = ReviewItemState.Reviewing
    };

    private async Task<ReviewQueueItem[]> ApplyUnreviewedCandidateAsync(
        string itemId,
        QuoteContent quote,
        string refreshSessionId,
        IReadOnlyList<string> ignoreTerms,
        ReviewPolicy policy)
    {
        ReviewQueueItem item = CreateReviewingItem(itemId, quote, refreshSessionId);
        return [await ApplyUnreviewedDecisionAsync(item, quote, ignoreTerms, policy).ConfigureAwait(false)];
    }

    /// <summary>
    /// 用一次 AI 请求审查一组候选，再逐条套用决策规则。出错的条目（请求失败或结果无效）
    /// 会合并后再次请求，直到成功或达到最大尝试次数。
    /// </summary>
    private async Task<ReviewQueueItem[]> ReviewGroupAsync(
        (string ItemId, QuoteContent Quote)[] group,
        string refreshSessionId,
        IReadOnlyList<string> ignoreTerms,
        ReviewPolicy policy,
        CancellationToken ct)
    {
        var results = new AiReviewResult?[group.Length];
        var errors = new string[group.Length];
        int maxAttempts = Math.Max(1, policy.MaxReviewAttempts);

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            int[] pending = Enumerable.Range(0, group.Length)
                .Where(index => !IsUsableResult(results[index]))
                .ToArray();
            if (pending.Length == 0 || ct.IsCancellationRequested) break;

            if (attempt > 1)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt - 1)), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
            }

            (string ItemId, QuoteContent Quote)[] batch = pending.Select(index => group[index]).ToArray();
            try
            {
                IReadOnlyList<AiReviewResult>? batchResults =
                    await _ai.ReviewBatchAsync(batch, refreshSessionId, ct).ConfigureAwait(false);
                if (batchResults is null || batchResults.Count != batch.Length)
                {
                    foreach (int index in pending) errors[index] = "AI 审查服务未返回结果。";
                    continue;
                }

                for (int i = 0; i < pending.Length; i++)
                {
                    results[pending[i]] = batchResults[i];
                    errors[pending[i]] = string.Empty;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                foreach (int index in pending) errors[index] = "AI 审查已取消。";
                break;
            }
            catch (Exception ex)
            {
                foreach (int index in pending) errors[index] = ex.Message;
            }
        }

        var items = new ReviewQueueItem[group.Length];
        for (int i = 0; i < group.Length; i++)
        {
            items[i] = await ReviewCandidateAsync(
                group[i].ItemId,
                group[i].Quote,
                refreshSessionId,
                ignoreTerms,
                policy,
                results[i],
                errors[i] ?? string.Empty).ConfigureAwait(false);
        }

        return items;
    }

    private static bool IsUsableResult(AiReviewResult? result)
        => result is { IsSuccess: true, Risk: RiskLevel.Low or RiskLevel.Medium or RiskLevel.High };

    private async Task<ReviewQueueItem> ReviewCandidateAsync(
        string itemId,
        QuoteContent quote,
        string refreshSessionId,
        IReadOnlyList<string> ignoreTerms,
        ReviewPolicy policy,
        AiReviewResult? result,
        string reviewError)
    {
        ReviewQueueItem item = CreateReviewingItem(itemId, quote, refreshSessionId);

        if (result is null)
        {
            result = new AiReviewResult
            {
                IsSuccess = false,
                Risk = RiskLevel.Unknown,
                Confidence = 0,
                Reason = "AI 审查未完成。",
                Error = reviewError,
                Categories = []
            };
        }

        result.Categories ??= [];
        try
        {
            item.ApplyResult(result);
        }
        catch (Exception ex)
        {
            reviewError = $"AI 返回结果无法应用：{ex.Message}";
            result = new AiReviewResult
            {
                IsSuccess = false,
                Risk = RiskLevel.Unknown,
                Confidence = 0,
                Reason = "AI 审查结果格式无效。",
                Error = reviewError,
                Categories = []
            };
            item.Risk = RiskLevel.Unknown;
            item.Confidence = 0;
            item.AiReason = result.Reason;
            item.Categories = [];
            item.ReviewedAt = DateTimeOffset.Now;
        }

        RiskLevel aiRisk = item.Risk;
        bool aiSucceeded = result.IsSuccess
            && result.Risk is RiskLevel.Low or RiskLevel.Medium or RiskLevel.High;
        string? ignoredTerm = FindIgnoredTerm(quote.Content, ignoreTerms);
        string? blockedTag = aiSucceeded
            ? QuoteTagCatalog.FindBlockedTag(result.Categories, policy.BlockedTags)
            : null;
        string decision;
        if (ignoredTerm is not null)
        {
            string existingReason = string.IsNullOrWhiteSpace(item.AiReason)
                ? string.Empty
                : " AI reason: " + item.AiReason;
            item.Risk = RiskLevel.High;
            item.AiReason = "命中本地忽略词: " + ignoredTerm + "." + existingReason;
            item.State = ReviewItemState.Blocked;
            decision = "local_ignore";
        }
        else if (blockedTag is not null)
        {
            // 禁止主题在本地强制执行：即使 AI 判为低风险也拦截，与提示词中的规则互为兜底。
            string existingReason = string.IsNullOrWhiteSpace(item.AiReason)
                ? string.Empty
                : " AI reason: " + item.AiReason;
            item.Risk = RiskLevel.High;
            item.AiReason = "命中禁止主题: " + blockedTag + "." + existingReason;
            item.State = ReviewItemState.Blocked;
            decision = "blocked_tag";
        }
        else if (!aiSucceeded)
        {
            item.Risk = RiskLevel.Unknown;
            item.State = policy.FailureAction == ReviewFailureAction.Block
                ? ReviewItemState.Blocked
                : ReviewItemState.AwaitingManualReview;
            item.AiReason = string.IsNullOrWhiteSpace(item.AiReason)
                ? "AI 审查失败，已按失败关闭策略处理。"
                : item.AiReason;
            decision = "ai_failure";
        }
        else
        {
            switch (result.Risk)
            {
                case RiskLevel.Low:
                    if (policy.Strictness == ReviewStrictness.Strict && result.Confidence < StrictLowConfidenceFloor)
                    {
                        // 严格档：低风险但把握不足时仍转人工，宁可多复核也不冒险放行。
                        // 风险提升到 medium，保证条目能被人工批准而不是卡死在复核列表。
                        item.Risk = RiskLevel.Medium;
                        item.State = ReviewItemState.AwaitingManualReview;
                        decision = "manual_required_strict";
                    }
                    else
                    {
                        item.State = ReviewItemState.Approved;
                        decision = "auto_pass";
                    }
                    break;
                case RiskLevel.Medium:
                    if (policy.Strictness == ReviewStrictness.Relaxed)
                    {
                        // 宽松档：中风险自动放行，风险与理由保留在历史/队列中可查。
                        item.State = ReviewItemState.Approved;
                        decision = "auto_pass_relaxed";
                    }
                    else
                    {
                        item.State = ReviewItemState.AwaitingManualReview;
                        decision = "manual_required";
                    }
                    break;
                case RiskLevel.High:
                    item.State = ReviewItemState.Blocked;
                    decision = "auto_block";
                    break;
                default:
                    item.Risk = RiskLevel.Unknown;
                    item.State = policy.FailureAction == ReviewFailureAction.Block
                        ? ReviewItemState.Blocked
                        : ReviewItemState.AwaitingManualReview;
                    decision = "ai_failure";
                    break;
            }
        }
        await WriteDecisionAuditSafeAsync(
            item,
            decision,
            result,
            ignoredTerm,
            aiRisk,
            policy,
            reviewError).ConfigureAwait(false);
        return item;
    }

    /// <summary>来源未启用 AI 审查时的决策：仅应用本地排除词，其余直接进入播放队列。</summary>
    private async Task<ReviewQueueItem> ApplyUnreviewedDecisionAsync(
        ReviewQueueItem item,
        QuoteContent quote,
        IReadOnlyList<string> ignoreTerms,
        ReviewPolicy policy)
    {
        string? ignoredTerm = FindIgnoredTerm(quote.Content, ignoreTerms);
        string decision;
        item.ReviewedAt = DateTimeOffset.Now;
        item.Categories = [];
        if (ignoredTerm is not null)
        {
            item.Risk = RiskLevel.High;
            item.AiReason = "命中本地忽略词: " + ignoredTerm + ".";
            item.State = ReviewItemState.Blocked;
            decision = "local_ignore";
        }
        else
        {
            item.Risk = RiskLevel.Unknown;
            item.AiReason = "该来源未启用 AI 审查，未经审查直接播放。";
            item.IsAiReviewSkipped = true;
            item.State = ReviewItemState.Approved;
            decision = "review_disabled";
        }

        AiReviewResult result = new()
        {
            IsSuccess = false,
            Risk = RiskLevel.Unknown,
            Reason = item.AiReason,
            Categories = []
        };
        await WriteDecisionAuditSafeAsync(
            item,
            decision,
            result,
            ignoredTerm,
            RiskLevel.Unknown,
            policy,
            string.Empty).ConfigureAwait(false);
        return item;
    }

    private static bool AddQuoteIdentity(HashSet<string> known, QuoteContent? quote)
    {
        if (quote is null) return false;

        string content = (quote.Content ?? string.Empty).Trim();
        string provider = (quote.ProviderId ?? string.Empty).Trim();
        string upstreamId = (quote.UpstreamId ?? string.Empty).Trim();
        string? contentKey = content.Length == 0 ? null : "content:" + content;
        string? upstreamKey = upstreamId.Length == 0 ? null : "upstream:" + provider + ":" + upstreamId;
        if (contentKey is null && upstreamKey is null) return false;

        bool duplicate = (contentKey is not null && known.Contains(contentKey))
                         || (upstreamKey is not null && known.Contains(upstreamKey));
        if (duplicate) return false;

        if (contentKey is not null) known.Add(contentKey);
        if (upstreamKey is not null) known.Add(upstreamKey);
        return true;
    }

    private static string? FindIgnoredTerm(string content, IReadOnlyList<string> terms)
    {
        foreach (string term in terms)
        {
            if (content.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return term;
            }
        }

        return null;
    }
    /// <summary>
    /// 弹出播放队列头部第一条可安全展示的内容并标记为已展示。
    /// 调用方不要求持有 <see cref="_operationGate"/>：集合变更经 UI 线程串行化，
    /// 与补货、人工操作及快照恢复天然互斥。
    /// </summary>
    private async Task<ReviewQueueItem?> DequeueFirstApprovedAsync(IReadOnlySet<string> eligibleProviders)
    {
        ReviewQueueItem? displayed = null;
        bool changed = false;
        await RunOnUiAsync(() =>
        {
            int index = 0;
            while (index < PlaybackQueue.Count)
            {
                ReviewQueueItem candidate = PlaybackQueue[index];
                if (candidate.State == ReviewItemState.Approved
                    && !eligibleProviders.Contains(candidate.Quote?.ProviderId ?? string.Empty))
                {
                    // 不属于当前组件已启用来源的条目留在原位，交给其它组件或配置变更时清理。
                    index++;
                    continue;
                }

                PlaybackQueue.RemoveAt(index);
                changed = true;
                if (candidate.State != ReviewItemState.Approved)
                {
                    candidate.State = ReviewItemState.AwaitingManualReview;
                    candidate.AiReason = "播放队列中的条目状态异常，已转人工复核。";
                    if (!PendingManualReview.Any(existing => existing.Id == candidate.Id))
                    {
                        PendingManualReview.Add(candidate);
                    }
                    continue;
                }

                candidate.State = ReviewItemState.Displayed;
                candidate.DisplayedAt = DateTimeOffset.Now;
                if (!History.Any(existing => existing.Id == candidate.Id))
                {
                    History.Add(candidate);
                }
                displayed = candidate;
                break;
            }

            if (changed)
            {
                if (displayed is not null) _currentDisplay = displayed;
                TrimHistoryLocked();
                string providerName = string.IsNullOrWhiteSpace(displayed?.Quote.ProviderName)
                    ? displayed?.Quote.ProviderId ?? "未知来源"
                    : displayed.Quote.ProviderName;
                UpdateCountsLocked(displayed is null
                    ? "播放队列中没有可安全展示的条目。"
                    : $"正在展示来自 {providerName} 的内容。");

                // 队列播空时清除退避：下一次刷新应立即允许补充，而不是继续等冷却。
                if (QueuedCount == 0)
                    Interlocked.Exchange(ref _refillCooldownUntil, 0);

                RaiseQueueChangedLocked();
            }
        }).ConfigureAwait(false);

        if (displayed is not null)
        {
            // 播放审计与快照不应阻塞展示节奏：后台写入，关闭时 FlushAsync 会再落盘最新状态。
            _ = WritePlaybackAuditSafeAsync(displayed);
            _ = PersistSnapshotSafeAsync();
        }

        return displayed;
    }
    private int ProcessManualDecision(IEnumerable<ReviewQueueItem> items, bool approve, string note)
    {
        try
        {
            List<ReviewQueueItem> snapshot = SnapshotItems(items);
            if (snapshot.Count == 0)
            {
                return 0;
            }

            List<ReviewQueueItem> changed = [];
            string normalizedNote = note ?? string.Empty;
            RunOnUiForSync(() =>
            {
                foreach (ReviewQueueItem item in snapshot)
                {
                    int index = PendingManualReview.IndexOf(item);
                    if (index < 0)
                    {
                        continue;
                    }

                    bool allowed = approve ? CanApprove(item) : CanReject(item);
                    if (!allowed)
                    {
                        continue;
                    }

                    PendingManualReview.RemoveAt(index);
                    item.ManualReviewedAt = DateTimeOffset.Now;
                    item.ManualNote = normalizedNote;
                    if (approve)
                    {
                        item.State = ReviewItemState.Approved;
                        if (!PlaybackQueue.Any(existing => existing.Id == item.Id))
                        {
                            PlaybackQueue.Add(item);
                        }
                    }
                    else
                    {
                        item.State = ReviewItemState.Rejected;
                        if (!History.Any(existing => existing.Id == item.Id))
                        {
                            History.Add(item);
                        }
                    }

                    changed.Add(item);
                }

                if (changed.Count > 0)
                {
                    TrimHistoryLocked();
                    UpdateCountsLocked(null);
                    string counts = $"待人工复核 {PendingCount}，播放队列 {QueuedCount}，已拦截 {BlockedCount}。";
                    _lastStatusMessage = approve ? $"人工批准 {changed.Count} 条；{counts}" : counts;
                }
                else
                {
                    _lastStatusMessage = "所选条目的状态已变化，未执行任何人工操作。";
                }

                RaiseQueueChangedLocked();
            });

            if (changed.Count > 0)
            {
                // 批量批准/拒绝可能一次几十条；审计写入放到后台，避免逐条阻塞 UI 线程。
                _ = WriteManualAuditsAsync(changed, approve ? "approve" : "reject", normalizedNote);
            }

            if (changed.Count > 0)
            {
                // 人工处理说明队列状态已有人工介入；立即解除补货冷却。
                Interlocked.Exchange(ref _refillFailureCount, 0);
                Interlocked.Exchange(ref _refillCooldownUntil, 0);

                // 人工操作的持久化与审计不阻塞 UI 线程；队列状态已先更新，关闭时 FlushAsync 兜底。
                _ = PersistSnapshotSafeAsync();
                _ = WriteQueueSummaryAuditSafeAsync("manual_decision");
            }

            return changed.Count;
        }
        catch (Exception ex)
        {
            WriteQueueErrorSafeAsync("manual_decision", ex).GetAwaiter().GetResult();
            _lastStatusMessage = "人工操作失败，详情已写入审计日志。";
            return 0;
        }
    }

    private static bool CanApprove(ReviewQueueItem item)
    {
        return item.State == ReviewItemState.ReviewFailed
            || (item.State == ReviewItemState.AwaitingManualReview
                && item.Risk is RiskLevel.Medium or RiskLevel.Unknown);
    }

    private static bool CanReject(ReviewQueueItem item)
    {
        return item.State is ReviewItemState.Pending
            or ReviewItemState.AwaitingManualReview
            or ReviewItemState.ReviewFailed;
    }
    private static List<ReviewQueueItem> SnapshotItems(IEnumerable<ReviewQueueItem>? items)
    {
        if (items is null)
        {
            return [];
        }

        try
        {
            return items
                .Where(item => item is not null)
                .DistinctBy(item => string.IsNullOrWhiteSpace(item.Id) ? $"#{item.GetHashCode()}" : item.Id)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private List<ReviewQueueItem> SnapshotPending()
    {
        List<ReviewQueueItem> snapshot = [];
        try
        {
            RunOnUiForSync(() => snapshot = PendingManualReview.ToList());
        }
        catch (Exception ex)
        {
            WriteQueueErrorSafeAsync("snapshot_pending", ex).GetAwaiter().GetResult();
        }

        return snapshot;
    }
    private async Task RestoreSnapshotLockedAsync(CancellationToken ct)
    {
        bool fileExisted = File.Exists(_snapshotPath);
        ReviewQueueSnapshot? snapshot = null;
        if (fileExisted)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                // 同步读取可避免 PluginRuntime.Initialize 在 UI 线程上等待时，
                // 文件 I/O 的续体又投递回无法泵送的 UI 线程。
                string json = File.ReadAllText(_snapshotPath, Encoding.UTF8);
                snapshot = JsonSerializer.Deserialize<ReviewQueueSnapshot>(json, SnapshotJsonOptions);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await WriteQueueErrorSafeAsync("load_snapshot", ex).ConfigureAwait(false);
                TryPreserveCorruptSnapshot();
            }
        }

        List<ReviewQueueItem> pending = [];
        List<ReviewQueueItem> playback = [];
        List<ReviewQueueItem> history = [];
        HashSet<string> seenIds = new(StringComparer.OrdinalIgnoreCase);
        int resetToManual = 0;
        List<ReviewQueueItem> loadedItems = snapshot?.Items ?? [];

        foreach (ReviewQueueItem? item in loadedItems)
        {
            if (item is null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.Id))
            {
                item.Id = Guid.NewGuid().ToString("N");
            }

            if (!seenIds.Add(item.Id))
            {
                continue;
            }

            item.Quote ??= new QuoteContent();
            switch (item.State)
            {
                case ReviewItemState.Pending:
                case ReviewItemState.Reviewing:
                    item.State = ReviewItemState.AwaitingManualReview;
                    resetToManual++;
                    pending.Add(item);
                    break;
                case ReviewItemState.AwaitingManualReview:
                case ReviewItemState.ReviewFailed:
                    pending.Add(item);
                    break;
                case ReviewItemState.Approved:
                    playback.Add(item);
                    break;
                case ReviewItemState.Displayed:
                case ReviewItemState.Blocked:
                case ReviewItemState.Rejected:
                    history.Add(item);
                    break;
                default:
                    item.State = ReviewItemState.AwaitingManualReview;
                    resetToManual++;
                    pending.Add(item);
                    break;
            }
        }
        int maxHistory = GetMaxHistoryItems();
        if (history.Count > maxHistory)
        {
            history.RemoveRange(0, history.Count - maxHistory);
        }

        await RunOnUiAsync(() =>
        {
            PendingManualReview.Clear();
            PlaybackQueue.Clear();
            History.Clear();
            foreach (ReviewQueueItem item in pending)
            {
                PendingManualReview.Add(item);
            }
            foreach (ReviewQueueItem item in playback)
            {
                PlaybackQueue.Add(item);
            }
            foreach (ReviewQueueItem item in history)
            {
                History.Add(item);
            }

            UpdateCountsLocked(
                $"队列已恢复：待人工复核 {PendingCount}，播放队列 {QueuedCount}，历史 {History.Count}。");
            RaiseQueueChangedLocked();
        }).ConfigureAwait(false);

        await PersistSnapshotSafeAsync().ConfigureAwait(false);
        await WriteQueueRestoredAuditSafeAsync(
            fileExisted,
            loadedItems.Count,
            pending.Count,
            playback.Count,
            history.Count,
            resetToManual).ConfigureAwait(false);
    }

    private void TryPreserveCorruptSnapshot()
    {
        try
        {
            if (!File.Exists(_snapshotPath))
            {
                return;
            }

            string backupPath = Path.Combine(
                _configDirectory,
                $"review-queue.corrupt-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
            File.Move(_snapshotPath, backupPath, overwrite: true);
        }
        catch
        {
            // 保留损坏文件是尽力而为；不能让它阻断插件启动。
        }
    }
    private async Task PersistSnapshotSafeAsync()
    {
        ReviewQueueSnapshot snapshot;
        try
        {
            // 快照必须在获取写入闸门之前采集：采集需要回到 UI 线程，
            // 而 UI 线程上的调用方可能正在同步等待该闸门，先占闸门会造成死锁。
            snapshot = await CreateSnapshotAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteQueueErrorSafeAsync("persist_snapshot", ex).ConfigureAwait(false);
            return;
        }

        await _persistGate.WaitAsync().ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            // 序号在采集快照时分配，顺序与状态快照严格一致；并发采集时只允许较新的快照落盘。
            long sequence = snapshot.Sequence;
            if (sequence <= Interlocked.Read(ref _lastPersistedSnapshotSequence))
            {
                return;
            }

            string json = JsonSerializer.Serialize(snapshot, SnapshotJsonOptions);
            Directory.CreateDirectory(_configDirectory);
            temporaryPath = _snapshotPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(
                temporaryPath,
                json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                CancellationToken.None).ConfigureAwait(false);
            File.Move(temporaryPath, _snapshotPath, overwrite: true);
            Interlocked.Exchange(ref _lastPersistedSnapshotSequence, sequence);
            temporaryPath = null;
        }
        catch (Exception ex)
        {
            await WriteQueueErrorSafeAsync("persist_snapshot", ex).ConfigureAwait(false);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // 清理临时文件失败不影响主流程。
                }
            }

            _persistGate.Release();
        }
    }

    private async Task<ReviewQueueSnapshot> CreateSnapshotAsync()
    {
        ReviewQueueSnapshot snapshot = new();
        await RunOnUiAsync(() =>
        {
            snapshot.SavedAt = DateTimeOffset.Now;
            snapshot.Sequence = Interlocked.Increment(ref _snapshotSequence);

            HashSet<string> seenIds = new(StringComparer.OrdinalIgnoreCase);
            AppendUniqueItems(snapshot.Items, PendingManualReview, seenIds);
            AppendUniqueItems(snapshot.Items, PlaybackQueue, seenIds);
            AppendUniqueItems(snapshot.Items, History, seenIds);
        }).ConfigureAwait(false);
        return snapshot;
    }

    private static void AppendUniqueItems(
        List<ReviewQueueItem> output,
        IEnumerable<ReviewQueueItem> source,
        HashSet<string> seenIds)
    {
        foreach (ReviewQueueItem item in source)
        {
            if (seenIds.Add(item.Id))
            {
                output.Add(item);
            }
        }
    }

    private static JsonSerializerOptions CreateSnapshotJsonOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
    private void TrimHistoryLocked()
    {
        int maxHistory = GetMaxHistoryItems();
        while (History.Count > maxHistory)
        {
            History.RemoveAt(0);
        }
    }

    private int GetMaxHistoryItems()
    {
        ReviewSettings review = _settings.Settings?.Review ?? new ReviewSettings();
        return Math.Max(0, review.MaxHistoryItems);
    }

    /// <summary>严格档下低风险结论需要的最低置信度；低于该值仍转人工复核。</summary>
    private const double StrictLowConfidenceFloor = 0.7;

    /// <summary>待人工复核积压达到该数量时暂停获取新内容，避免无人处理时无限堆积。</summary>
    private const int MaxPendingManualReview = 50;

    /// <summary>
    /// 一次刷新批次内所有决策共用的策略快照：失败策略、严格程度、禁止主题、队列长度与重试次数，
    /// 避免同一批条目因设置中途变更而使用不同标准。
    /// </summary>
    private sealed record ReviewPolicy(
        ReviewFailureAction FailureAction,
        ReviewStrictness Strictness,
        IReadOnlyList<string> BlockedTags,
        int MaxQueueLength,
        int MaxReviewAttempts);

    private ReviewPolicy BuildReviewPolicy()
    {
        ReviewSettings review = _settings.Settings?.Review ?? new ReviewSettings();
        AiSettings? ai = _settings.Settings?.Ai;
        ReviewStrictness strictness = ai is not null && Enum.IsDefined(ai.Strictness)
            ? ai.Strictness
            : ReviewStrictness.Balanced;

        return new ReviewPolicy(
            review.FailureAction,
            strictness,
            QuoteTagCatalog.Normalize(ai?.BlockedTags),
            Math.Clamp(review.MaxQueueLength, PluginSettingsStore.MinQueueLength, PluginSettingsStore.MaxQueueLengthLimit),
            Math.Clamp(review.MaxReviewAttempts, 1, PluginSettingsStore.MaxReviewAttemptsLimit));
    }

    private void UpdateCountsLocked(string? statusMessage)
    {
        Volatile.Write(ref _pendingCount, PendingManualReview.Count);
        Volatile.Write(ref _queuedCount, PlaybackQueue.Count);
        Volatile.Write(ref _blockedCount, History.Count(item =>
            item.State is ReviewItemState.Blocked or ReviewItemState.Rejected));
        if (statusMessage is not null)
        {
            _lastStatusMessage = statusMessage;
        }
    }

    private async Task SetStatusAndNotifyAsync(string message)
    {
        await RunOnUiAsync(() =>
        {
            _lastStatusMessage = message;
            RaiseQueueChangedLocked();
        }).ConfigureAwait(false);
    }

    private void RaiseQueueChangedLocked()
    {
        try
        {
            EventHandler? handlers = QueueChanged;
            if (handlers is null)
            {
                return;
            }

            foreach (Delegate callback in handlers.GetInvocationList())
            {
                try
                {
                    ((EventHandler)callback)(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    _ = WriteQueueErrorSafeAsync("queue_changed_handler", ex);
                }
            }
        }
        catch (Exception ex)
        {
            _ = WriteQueueErrorSafeAsync("queue_changed", ex);
        }
    }

    private static string TruncateForStatus(string? value)
    {
        return RhesisHttp.Truncate(value, 160);
    }

    private static bool ShouldDispatchToUi()
    {
        try
        {
            Type? applicationType = Type.GetType("Avalonia.Application, Avalonia.Controls", throwOnError: false);
            object? application = applicationType?
                .GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?
                .GetValue(null);
            if (application is null)
            {
                return false;
            }

            Type? dispatcherType = Type.GetType("Avalonia.Threading.Dispatcher, Avalonia.Base", throwOnError: false);
            object? dispatcher = dispatcherType?
                .GetProperty("UIThread", BindingFlags.Public | BindingFlags.Static)?
                .GetValue(null);
            if (dispatcher is null)
            {
                return false;
            }

            MethodInfo? checkAccess = dispatcherType!.GetMethod(
                "CheckAccess",
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            return checkAccess?.Invoke(dispatcher, null) is bool hasAccess && !hasAccess;
        }
        catch
        {
            // 无 Avalonia 宿主的单元测试/无头进程直接在当前线程执行。
            return false;
        }
    }

    private static async Task RunOnUiAsync(Action action)
    {
        if (!ShouldDispatchToUi())
        {
            action();
            return;
        }

        TaskCompletionSource<object?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Type dispatcherType = Type.GetType("Avalonia.Threading.Dispatcher, Avalonia.Base", throwOnError: true)!;
            object dispatcher = dispatcherType
                .GetProperty("UIThread", BindingFlags.Public | BindingFlags.Static)!
                .GetValue(null)!;
            Action postedAction = () =>
            {
                try
                {
                    action();
                    completion.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            };

            MethodInfo? post = dispatcherType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(method =>
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    return method.Name == "Post"
                        && parameters.Length == 2
                        && parameters[0].ParameterType == typeof(Action);
                });
            if (post is not null)
            {
                Type priorityType = post.GetParameters()[1].ParameterType;
                object priority = Enum.GetNames(priorityType).Contains("Normal")
                    ? Enum.Parse(priorityType, "Normal")
                    : Activator.CreateInstance(priorityType)!;
                post.Invoke(dispatcher, [postedAction, priority]);
            }
            else
            {
                MethodInfo? invokeAsync = dispatcherType.GetMethod(
                    "InvokeAsync",
                    BindingFlags.Public | BindingFlags.Instance,
                    binder: null,
                    types: [typeof(Action)],
                    modifiers: null);
                if (invokeAsync is null)
                {
                    action();
                    return;
                }

                invokeAsync.Invoke(dispatcher, [postedAction]);
            }
        }
        catch
        {
            action();
            return;
        }

        await completion.Task.ConfigureAwait(false);
    }
    private static void RunOnUiForSync(Action action)
    {
        RunOnUiAsync(action).ConfigureAwait(false).GetAwaiter().GetResult();
    }
    private async Task WriteDecisionAuditSafeAsync(
        ReviewQueueItem item,
        string decision,
        AiReviewResult result,
        string? ignoredTerm,
        RiskLevel aiRisk,
        ReviewPolicy policy,
        string reviewError)
    {
        QuoteContent quote = item.Quote ?? new QuoteContent();
        await WriteAuditSafeAsync(new AuditLogEvent
        {
            Stage = "decision",
            RefreshSessionId = item.RefreshSessionId,
            ItemId = item.Id,
            ProviderId = quote.ProviderId,
            UpstreamId = quote.UpstreamId,
            CorrelationId = item.Id,
            Data = new
            {
                decision,
                state = item.State.ToString(),
                risk = item.Risk.ToString(),
                aiRisk = aiRisk.ToString(),
                confidence = item.Confidence,
                reason = item.AiReason,
                categories = item.Categories,
                aiSuccess = result.IsSuccess,
                aiError = string.IsNullOrWhiteSpace(result.Error) ? reviewError : result.Error,
                aiProvider = item.AiProviderName,
                aiModel = item.AiModel,
                aiRequestId = item.AiRequestId,
                ignoredTerm = ignoredTerm ?? string.Empty,
                failureAction = policy.FailureAction.ToString(),
                upstreamId = quote.UpstreamId
            }
        }).ConfigureAwait(false);
    }

    private async Task WritePlaybackAuditSafeAsync(ReviewQueueItem item)
    {
        QuoteContent quote = item.Quote ?? new QuoteContent();
        await WriteAuditSafeAsync(new AuditLogEvent
        {
            Stage = "playback",
            RefreshSessionId = item.RefreshSessionId,
            ItemId = item.Id,
            ProviderId = quote.ProviderId,
            UpstreamId = quote.UpstreamId,
            CorrelationId = item.Id,
            Data = new
            {
                displayedAt = item.DisplayedAt,
                risk = item.Risk.ToString(),
                upstreamId = quote.UpstreamId,
                upstreamIdKind = quote.UpstreamIdKind,
                providerId = quote.ProviderId,
                providerName = quote.ProviderName
            }
        }).ConfigureAwait(false);
    }
    private async Task WriteManualAuditSafeAsync(ReviewQueueItem item, string action, string note)
    {
        QuoteContent quote = item.Quote ?? new QuoteContent();
        await WriteAuditSafeAsync(new AuditLogEvent
        {
            Stage = "manual_review",
            RefreshSessionId = item.RefreshSessionId,
            ItemId = item.Id,
            ProviderId = quote.ProviderId,
            UpstreamId = quote.UpstreamId,
            CorrelationId = item.Id,
            Data = new
            {
                action,
                note,
                itemId = item.Id,
                state = item.State.ToString(),
                risk = item.Risk.ToString(),
                confidence = item.Confidence,
                reason = item.AiReason,
                reviewedAt = item.ManualReviewedAt,
                providerId = quote.ProviderId,
                upstreamId = quote.UpstreamId,
                upstreamIdKind = quote.UpstreamIdKind
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// 把已经出队、但因刷新过期或展示前校验未通过而没能展示的条目退回：
    /// 需要人工复核的退回待复核列表，其余退回播放队列队尾，避免静默丢失并保持历史准确。
    /// </summary>
    public async Task ReturnUndeployedAsync(
        ReviewQueueItem? item,
        bool toManualReview,
        string note)
    {
        if (item is null) return;

        try
        {
            await RunOnUiAsync(() =>
            {
                if (ReferenceEquals(_currentDisplay, item)) _currentDisplay = null;

                ReviewQueueItem? historyItem = History.FirstOrDefault(existing => existing.Id == item.Id);
                if (historyItem is not null) History.Remove(historyItem);
                PlaybackQueue.Remove(item);
                PendingManualReview.Remove(item);

                item.DisplayedAt = null;
                if (toManualReview)
                {
                    item.State = ReviewItemState.AwaitingManualReview;
                    if (!string.IsNullOrWhiteSpace(note))
                    {
                        item.AiReason = string.IsNullOrWhiteSpace(item.AiReason)
                            ? note
                            : $"{note} {item.AiReason}";
                    }

                    if (!PendingManualReview.Any(existing => existing.Id == item.Id))
                        PendingManualReview.Add(item);
                }
                else
                {
                    item.State = ReviewItemState.Approved;
                    if (!PlaybackQueue.Any(existing => existing.Id == item.Id))
                    {
                        PlaybackQueue.Add(item);
                        // 它本来是队首取出的；放回队首以保持 FIFO，而不是排到队尾。
                        int lastIndex = PlaybackQueue.Count - 1;
                        if (lastIndex > 0) PlaybackQueue.Move(lastIndex, 0);
                    }
                }

                UpdateCountsLocked(toManualReview
                    ? "一条内容未通过展示前校验，已退回人工复核。"
                    : "一条内容未展示，已退回播放队列。");
                RaiseQueueChangedLocked();
            }).ConfigureAwait(false);

            await PersistSnapshotSafeAsync().ConfigureAwait(false);
            await WriteQueueSummaryAuditSafeAsync("undeployed_item").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteQueueErrorSafeAsync("return_undeployed", ex).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 组件的来源配置（启用、权重、AI 审查开关、长度限制）变化后整理播放队列，返回移除的条数：
    /// 移除已停用来源和超出新长度限制的条目；<paramref name="dropUnreviewed"/> 为 true 时
    /// 还会移除未经审查的直通条目，让后续内容按新权重重新抓取。已通过 AI 审查的条目保留，避免重复审查。
    /// 被移除的条目从未展示，不写入历史。
    /// </summary>
    public async Task<int> ApplySourceConfigurationAsync(QuoteRequestOptions options, bool dropUnreviewed)
    {
        ArgumentNullException.ThrowIfNull(options);

        int removed = 0;
        try
        {
            await EnsureInitializedAsync(CancellationToken.None).ConfigureAwait(false);

            HashSet<string> eligible = _sources.GetEligibleProviderIds(options);
            _activeProviders = eligible;
            HashSet<string> customIds = (options.ExtraProviders ?? [])
                .OfType<CustomQuoteProvider>()
                .Select(provider => provider.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            int lengthLimit = options.LengthLimitation;

            await RunOnUiAsync(() =>
            {
                for (int i = PlaybackQueue.Count - 1; i >= 0; i--)
                {
                    ReviewQueueItem item = PlaybackQueue[i];
                    string providerId = item.Quote?.ProviderId ?? string.Empty;
                    bool tooLong = lengthLimit > 0
                                   && (item.Quote?.Content?.Length ?? 0) > lengthLimit
                                   && !customIds.Contains(providerId);
                    if (!eligible.Contains(providerId) || tooLong || (dropUnreviewed && item.IsAiReviewSkipped))
                    {
                        PlaybackQueue.RemoveAt(i);
                        removed++;
                    }
                }

                if (removed > 0)
                {
                    UpdateCountsLocked($"来源配置已变更，已从播放队列移除 {removed} 条旧内容。");
                    RaiseQueueChangedLocked();
                }
            }).ConfigureAwait(false);

            Interlocked.Exchange(ref _refillFailureCount, 0);
            Interlocked.Exchange(ref _refillCooldownUntil, 0);

            if (removed > 0)
            {
                await PersistSnapshotSafeAsync().ConfigureAwait(false);
                await WriteQueueSummaryAuditSafeAsync("source_config_changed").ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            await WriteQueueErrorSafeAsync("source_config_changed", ex).ConfigureAwait(false);
        }

        return removed;
    }

    private async Task WriteManualAuditsAsync(
        IReadOnlyList<ReviewQueueItem> items,
        string action,
        string note)
    {
        try
        {
            foreach (ReviewQueueItem item in items)
                await WriteManualAuditSafeAsync(item, action, note).ConfigureAwait(false);
        }
        catch
        {
            // 审计失败不应影响已经完成的人工操作。
        }
    }

    private async Task WriteQueueRestoredAuditSafeAsync(
        bool fileExisted,
        int loadedCount,
        int pendingCount,
        int queuedCount,
        int historyCount,
        int resetToManualCount)
    {
        await WriteAuditSafeAsync(new AuditLogEvent
        {
            Stage = "queue_restored",
            Data = new
            {
                fileExisted,
                snapshotPath = _snapshotPath,
                loadedCount,
                pendingCount,
                queuedCount,
                historyCount,
                resetToManualCount
            }
        }).ConfigureAwait(false);
    }

    private async Task WriteQueueSummaryAuditSafeAsync(string reason)
    {
        await WriteAuditSafeAsync(new AuditLogEvent
        {
            Stage = "queue_summary",
            Data = new
            {
                reason,
                pendingCount = PendingCount,
                queuedCount = QueuedCount,
                blockedCount = BlockedCount,
                status = LastStatusMessage
            }
        }).ConfigureAwait(false);
    }
    private async Task WriteQueueDuplicateAuditSafeAsync(
        string refreshSessionId,
        string itemId,
        QuoteContent? duplicate)
    {
        await WriteAuditSafeAsync(new AuditLogEvent
        {
            Stage = "queue_duplicate",
            RefreshSessionId = refreshSessionId,
            ItemId = itemId,
            ProviderId = duplicate?.ProviderId ?? string.Empty,
            UpstreamId = duplicate?.UpstreamId ?? string.Empty,
            CorrelationId = itemId,
            Data = new
            {
                success = false,
                providerId = duplicate?.ProviderId ?? string.Empty,
                upstreamId = duplicate?.UpstreamId ?? string.Empty,
                upstreamIdKind = duplicate?.UpstreamIdKind ?? string.Empty,
                contentLength = duplicate?.Content?.Length ?? 0,
                reason = "duplicate_in_pending_or_playback_queue"
            }
        }).ConfigureAwait(false);
    }

    private async Task WriteQueueErrorSafeAsync(
        string operation,
        Exception exception,
        string refreshSessionId = "",
        string itemId = "")
    {
        try
        {
            await _audit.WriteAsync(new AuditLogEvent
            {
                Stage = "queue_error",
                RefreshSessionId = refreshSessionId,
                ItemId = itemId,
                CorrelationId = itemId,
                Data = new
                {
                    operation,
                    error = exception.Message,
                    exceptionType = exception.GetType().FullName
                }
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // 审计服务本身失败时仍必须让队列保持可用。
        }
    }

    private async Task WriteAuditSafeAsync(AuditLogEvent auditEvent)
    {
        try
        {
            await _audit.WriteAsync(auditEvent, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // 审计失败不应导致展示或人工操作中断。
        }
    }
}
