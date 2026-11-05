using System.IO;
using System.Reflection;
using System.Collections.ObjectModel;
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
    private int _pendingCount;
    private int _queuedCount;
    private int _blockedCount;
    private volatile string _lastStatusMessage = "队列尚未初始化。";
    private volatile bool _initialized;

    private static readonly JsonSerializerOptions SnapshotJsonOptions = CreateSnapshotJsonOptions();

    public ReviewQueueService(
        PluginSettingsStore settings,
        QuoteSourceService sources,
        AiReviewService ai,
        AuditLogService audit)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _ai = ai ?? throw new ArgumentNullException(nameof(ai));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _configDirectory = settings.ConfigDirectory
            ?? throw new ArgumentException("设置存储未提供配置目录。", nameof(settings));
        _snapshotPath = Path.Combine(_configDirectory, SnapshotFileName);
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
    /// 否则先批量抓取并审查，再取出第一条自动通过的内容。
    /// </summary>
    /// <remarks>
    /// 出队走快路径、不获取 <see cref="_operationGate"/>：集合变更全部经 UI 线程串行化，
    /// 且展示调用方（组件）自身保证串行，因此后台补货（抓取+审查管线）在途时出队不必等待，
    /// 播放节奏不受补货耗时影响。只有播放队列耗尽需要现场补充时才进入互斥的慢路径。
    /// </remarks>
    public async Task<ReviewQueueItem?> GetNextForDisplayAsync(
        QuoteRequestOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            await EnsureInitializedAsync(ct).ConfigureAwait(false);

            ReviewQueueItem? queued = await DequeueFirstApprovedAsync().ConfigureAwait(false);
            if (queued is not null)
            {
                return queued;
            }

            await _operationGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await EnsureInitializedLockedAsync(ct).ConfigureAwait(false);
                await FillBatchLockedAsync(options, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                {
                    await SetStatusAndNotifyAsync("操作已取消；已审查的内容已安全保留。").ConfigureAwait(false);
                    return null;
                }

                return await DequeueFirstApprovedAsync().ConfigureAwait(false);
            }
            finally
            {
                _operationGate.Release();
            }
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

    /// <summary>抓取并审查一个批次，返回本次完成审查的条目数（不会自动出队）。</summary>
    public async Task<int> RefreshBatchAsync(
        QuoteRequestOptions options,
        CancellationToken ct = default)
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
            return 0;
        }
        catch (Exception ex)
        {
            await WriteQueueErrorSafeAsync("refresh_batch", ex).ConfigureAwait(false);
            await SetStatusAndNotifyAsync($"批量刷新失败：{ex.Message}").ConfigureAwait(false);
            return 0;
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

    private async Task<int> FillBatchLockedAsync(QuoteRequestOptions options, CancellationToken ct)
    {
        int batchSize = Math.Clamp(options.BatchSize, 1, 10);
        int maxBuffer = Math.Clamp(options.MaxBufferedItems, 1, 100);
        int buffered = PendingCount + QueuedCount;
        if (buffered >= maxBuffer)
        {
            await SetStatusAndNotifyAsync($"缓冲区已满：待人工复核 {PendingCount}，播放队列 {QueuedCount}。")
                .ConfigureAwait(false);
            return 0;
        }

        int targetCount = Math.Min(batchSize, maxBuffer - buffered);
        string sessionId = !string.IsNullOrWhiteSpace(options.RefreshSessionId)
            ? options.RefreshSessionId
            : Guid.NewGuid().ToString("N");
        List<(string ItemId, QuoteContent Quote)> fetched = [];
        int fetchFailures = 0;
        string lastFetchError = string.Empty;
        bool canceled = false;

        var knownQuotes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await RunOnUiAsync(() =>
        {
            foreach (ReviewQueueItem existing in PendingManualReview.Concat(PlaybackQueue))
            {
                AddQuoteIdentity(knownQuotes, existing.Quote);
            }
        }).ConfigureAwait(false);
        int duplicateSkips = 0;

        for (int i = 0; i < targetCount; i++)
        {
            if (ct.IsCancellationRequested)
            {
                canceled = true;
                break;
            }

            string itemId = Guid.NewGuid().ToString("N");
            try
            {
                bool accepted = false;
                QuoteContent? duplicate = null;
                for (int duplicateAttempt = 0; duplicateAttempt < 3; duplicateAttempt++)
                {
                    QuoteContent quote = await _sources.FetchOneAsync(options, sessionId, itemId, ct)
                        .ConfigureAwait(false);
                    if (AddQuoteIdentity(knownQuotes, quote))
                    {
                        fetched.Add((itemId, quote));
                        accepted = true;
                        break;
                    }

                    duplicate = quote;
                    duplicateSkips++;
                }

                if (!accepted)
                {
                    lastFetchError = "上游连续返回与当前队列重复的内容，已跳过。";
                    await WriteQueueDuplicateAuditSafeAsync(sessionId, itemId, duplicate).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                canceled = true;
                break;
            }
            catch (Exception ex)
            {
                fetchFailures++;
                lastFetchError = ex.Message;
                await WriteQueueErrorSafeAsync("fetch_quote", ex, sessionId, itemId).ConfigureAwait(false);
            }
        }

        if (fetched.Count == 0)
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
            return 0;
        }

        IReadOnlyList<string> ignoreTerms = (options.IgnoreTerms ?? [])
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Select(term => term.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ReviewSettings reviewSettings = GetReviewSettings();
        ReviewFailureAction failureAction = reviewSettings.FailureAction;
        int maxConcurrent = Math.Clamp(reviewSettings.MaxConcurrentReviews, 1, 10);
        int itemsPerRequest = Math.Clamp(reviewSettings.ItemsPerReviewRequest, 1, 10);
        using SemaphoreSlim reviewLimiter = new(maxConcurrent, maxConcurrent);

        // 需要 AI 审查的候选按「单次请求审查条数」分组，同组共用一次请求和一份系统提示词；
        // 并发上限限制的是同时在途的请求数。
        List<Task<ReviewQueueItem[]>> reviewTasks = [];
        List<(string ItemId, QuoteContent Quote)> aiCandidates = [];
        foreach ((string ItemId, QuoteContent Quote) candidate in fetched)
        {
            if (IsAiReviewEnabledFor(options, candidate.Quote))
            {
                aiCandidates.Add(candidate);
                continue;
            }

            reviewTasks.Add(ApplyUnreviewedCandidateAsync(candidate.ItemId, candidate.Quote, sessionId, ignoreTerms, failureAction));
        }

        foreach ((string ItemId, QuoteContent Quote)[] group in aiCandidates.Chunk(itemsPerRequest))
        {
            reviewTasks.Add(ReviewGroupAsync(group, sessionId, ignoreTerms, failureAction, reviewLimiter, ct));
        }

        ReviewQueueItem[] reviewedItems = (await Task.WhenAll(reviewTasks).ConfigureAwait(false))
            .SelectMany(static items => items)
            .ToArray();

        int autoPassed = reviewedItems.Count(item => item.State == ReviewItemState.Approved);
        int pending = reviewedItems.Count(item => item.State == ReviewItemState.AwaitingManualReview);
        int blocked = reviewedItems.Count(item => item.State == ReviewItemState.Blocked);

        await RunOnUiAsync(() =>
        {
            foreach (ReviewQueueItem item in reviewedItems)
            {
                switch (item.State)
                {
                    case ReviewItemState.Approved:
                        if (!PlaybackQueue.Any(existing => existing.Id == item.Id))
                        {
                            PlaybackQueue.Add(item);
                        }
                        break;
                    case ReviewItemState.AwaitingManualReview:
                    case ReviewItemState.ReviewFailed:
                        if (!PendingManualReview.Any(existing => existing.Id == item.Id))
                        {
                            PendingManualReview.Add(item);
                        }
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
            string message = canceled
                ? $"批量获取已取消；已完成 {reviewedItems.Length} 条审查，其中自动通过 {autoPassed}、待人工 {pending}、拦截 {blocked}。"
                : $"本批次审查 {reviewedItems.Length} 条：自动通过 {autoPassed}、待人工 {pending}、拦截 {blocked}。";
            if (duplicateSkips > 0)
            {
                message += $" 已跳过重复候选 {duplicateSkips} 次。";
            }
            UpdateCountsLocked(message + $" 当前播放队列 {QueuedCount} 条。");
            RaiseQueueChangedLocked();
        }).ConfigureAwait(false);

        await PersistSnapshotSafeAsync().ConfigureAwait(false);
        await WriteQueueSummaryAuditSafeAsync("batch_complete").ConfigureAwait(false);
        return reviewedItems.Length;
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
        ReviewFailureAction failureAction)
    {
        ReviewQueueItem item = CreateReviewingItem(itemId, quote, refreshSessionId);
        return [await ApplyUnreviewedDecisionAsync(item, quote, ignoreTerms, failureAction).ConfigureAwait(false)];
    }

    /// <summary>用一次 AI 请求审查一组候选，再逐条套用决策规则。</summary>
    private async Task<ReviewQueueItem[]> ReviewGroupAsync(
        (string ItemId, QuoteContent Quote)[] group,
        string refreshSessionId,
        IReadOnlyList<string> ignoreTerms,
        ReviewFailureAction failureAction,
        SemaphoreSlim reviewLimiter,
        CancellationToken ct)
    {
        await reviewLimiter.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            IReadOnlyList<AiReviewResult>? results = null;
            string reviewError = string.Empty;
            try
            {
                results = await _ai.ReviewBatchAsync(group, refreshSessionId, ct).ConfigureAwait(false);
                if (results is null || results.Count != group.Length)
                {
                    results = null;
                    reviewError = "AI 审查服务未返回结果。";
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                reviewError = "AI 审查已取消。";
            }
            catch (Exception ex)
            {
                reviewError = ex.Message;
            }

            var items = new ReviewQueueItem[group.Length];
            for (int i = 0; i < group.Length; i++)
            {
                items[i] = await ReviewCandidateAsync(
                    group[i].ItemId,
                    group[i].Quote,
                    refreshSessionId,
                    ignoreTerms,
                    failureAction,
                    results?[i],
                    reviewError).ConfigureAwait(false);
            }

            return items;
        }
        finally
        {
            reviewLimiter.Release();
        }
    }

    private async Task<ReviewQueueItem> ReviewCandidateAsync(
        string itemId,
        QuoteContent quote,
        string refreshSessionId,
        IReadOnlyList<string> ignoreTerms,
        ReviewFailureAction failureAction,
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
        }            else if (!aiSucceeded)
        {
            item.Risk = RiskLevel.Unknown;
            item.State = failureAction == ReviewFailureAction.Block
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
                    item.State = ReviewItemState.Approved;
                    decision = "auto_pass";
                    break;
                case RiskLevel.Medium:
                    item.State = ReviewItemState.AwaitingManualReview;
                    decision = "manual_required";
                    break;
                case RiskLevel.High:
                    item.State = ReviewItemState.Blocked;
                    decision = "auto_block";
                    break;
                default:
                    item.Risk = RiskLevel.Unknown;
                    item.State = failureAction == ReviewFailureAction.Block
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
            failureAction,
            reviewError).ConfigureAwait(false);
        return item;
    }

    /// <summary>来源未启用 AI 审查时的决策：仅应用本地排除词，其余直接进入播放队列。</summary>
    private async Task<ReviewQueueItem> ApplyUnreviewedDecisionAsync(
        ReviewQueueItem item,
        QuoteContent quote,
        IReadOnlyList<string> ignoreTerms,
        ReviewFailureAction failureAction)
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
            failureAction,
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
    private async Task<ReviewQueueItem?> DequeueFirstApprovedAsync()
    {
        ReviewQueueItem? displayed = null;
        bool changed = false;
        await RunOnUiAsync(() =>
        {
            while (PlaybackQueue.Count > 0)
            {
                ReviewQueueItem candidate = PlaybackQueue[0];
                PlaybackQueue.RemoveAt(0);
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
                TrimHistoryLocked();
                string providerName = string.IsNullOrWhiteSpace(displayed?.Quote.ProviderName)
                    ? displayed?.Quote.ProviderId ?? "未知来源"
                    : displayed.Quote.ProviderName;
                UpdateCountsLocked(displayed is null
                    ? "播放队列中没有可安全展示的条目。"
                    : $"正在展示来自 {providerName} 的内容。");
                RaiseQueueChangedLocked();
            }
        }).ConfigureAwait(false);

        if (displayed is not null)
        {
            await WritePlaybackAuditSafeAsync(displayed).ConfigureAwait(false);
            await PersistSnapshotSafeAsync().ConfigureAwait(false);
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

            foreach (ReviewQueueItem item in changed)
            {
                WriteManualAuditSafeAsync(item, approve ? "approve" : "reject", normalizedNote)
                    .GetAwaiter()
                    .GetResult();
            }

            if (changed.Count > 0)
            {
                PersistSnapshotSafeAsync().GetAwaiter().GetResult();
                WriteQueueSummaryAuditSafeAsync("manual_decision").GetAwaiter().GetResult();
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
        await _persistGate.WaitAsync().ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            ReviewQueueSnapshot snapshot = await CreateSnapshotAsync().ConfigureAwait(false);
            string json = JsonSerializer.Serialize(snapshot, SnapshotJsonOptions);
            Directory.CreateDirectory(_configDirectory);
            temporaryPath = _snapshotPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(
                temporaryPath,
                json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                CancellationToken.None).ConfigureAwait(false);
            File.Move(temporaryPath, _snapshotPath, overwrite: true);
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
        ReviewQueueSnapshot snapshot = new()
        {
            SavedAt = DateTimeOffset.Now
        };
        await RunOnUiAsync(() =>
        {
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

    private ReviewSettings GetReviewSettings()
    {
        return _settings.Settings?.Review ?? new ReviewSettings();
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
        ReviewFailureAction failureAction,
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
                failureAction = failureAction.ToString(),
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
