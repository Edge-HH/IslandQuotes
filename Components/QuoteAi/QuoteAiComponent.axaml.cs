// IslandQuotes - Copyright (C) 2026 Edge-HH
// 本文件改编自 ExtraIsland（https://github.com/LiPolymer/ExtraIsland），
// Copyright (C) 2024-2025 LiPolymer，以 GNU AGPLv3 授权。
// 原始文件：ExtraIsland/Components/Rhesis/Rhesis.axaml.cs
// 修改：2026 年由 Edge-HH 修改，详见 NOTICE.md 与 git 历史。
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Attributes;
using IslandQuotes.Core;
using IslandQuotes.Models;
using IslandQuotes.Services;

namespace IslandQuotes.Components.QuoteAi;

[ComponentInfo(
    "4E804B23-8A13-4E8F-92A8-6C86032875CF",
    "GoodQuotes",
    "\uE3F4",
    "可自定义的，可选带审查的每日一言")]
public partial class QuoteAiComponent : ComponentBase<QuoteAiConfig>
{
    Label MainLabel = null!;
    Label SubLabel = null!;
    Grid MainViewport = null!;
    Grid SubViewport = null!;

    void InitializeComponent()
    {
        if (!RuntimeXaml.TryLoad(this))
        {
            MainLabel = new Label();
            SubLabel = new Label();
            MainViewport = new Grid { ClipToBounds = true };
            SubViewport = new Grid { ClipToBounds = true, IsVisible = false };
            MainViewport.Children.Add(MainLabel);
            SubViewport.Children.Add(SubLabel);
            return;
        }

        MainLabel = this.FindControl<Label>("MainLabel")
            ?? throw new InvalidOperationException("XAML 中缺少控件 MainLabel");
        SubLabel = this.FindControl<Label>("SubLabel")
            ?? throw new InvalidOperationException("XAML 中缺少控件 SubLabel");
        MainViewport = this.FindControl<Grid>("MainViewport")
            ?? throw new InvalidOperationException("XAML 中缺少控件 MainViewport");
        SubViewport = this.FindControl<Grid>("SubViewport")
            ?? throw new InvalidOperationException("XAML 中缺少控件 SubViewport");
    }

    readonly ILessonsService _lessonsService;
    readonly ContentSwapAnimator _mainLabelAnimator;
    readonly ContentSwapAnimator _subLabelAnimator;
    readonly TranslateTransform _mainViewportTransform = new();
    readonly TranslateTransform _subViewportTransform = new();

    /// <summary>单次刷新允许占用的最长时间；超时后释放节流锁，避免某次等待异常卡死整个刷新。</summary>
    static readonly TimeSpan UpdateTimeout = TimeSpan.FromSeconds(90);

    /// <summary>等待内容时提前轮询的最小间隔；真实内容到达由 QueueChanged 立即唤醒。</summary>
    const double WaitingPollIntervalSeconds = 5;

    CancellationTokenSource? _lifetimeCts;
    DispatcherTimer? _refreshTimer;
    int _updatePending;
    int _updateGeneration;
    int _displayGeneration;
    int _refreshRequested;
    bool _isAttached;

    /// <summary>当前正显示「等待内容」提示；队列一旦有内容就立即刷新，不必等满刷新间隔。</summary>
    volatile bool _waitingForContent;

    /// <summary>当前标签上是否已经显示过一条真实内容；用于避免空队列时把句子闪成占位提示。</summary>
    bool _hasDisplayedContent;

    /// <summary>开始等待新内容的时间；等待过久时改为显示状态提示，避免界面看起来卡死。</summary>
    long _waitingSinceTimestamp;

    /// <summary>主标签当前显示的是不是「分开展示」的额外信息；决定外观刷新使用哪个颜色。</summary>
    bool _mainShowsAttribution;

    bool _queueSubscribed;

    /// <summary>上一次展示后播放队列的条数；自动刷新关闭时用于识别「新增了已批准内容」。</summary>
    int _lastQueuedCount;

    long _lastUpdateTimestamp;
    string? _sourceFingerprint;
    Task _sourceSyncTask = Task.CompletedTask;
    string _appliedAppearanceKey = string.Empty;
    bool _defaultsCaptured;
    double _defaultMainFontSize;
    double _defaultSubFontSize;

    const string MainFontSizeResourceKey = "MainWindowLargeFontSize";
    const string SubFontSizeResourceKey = "MainWindowEmphasizedFontSize";

    public QuoteAiComponent(ILessonsService lessonsService)
    {
        _lessonsService = lessonsService;
        InitializeComponent();
        MainViewport.RenderTransform = _mainViewportTransform;
        SubViewport.RenderTransform = _subViewportTransform;
        _mainLabelAnimator = new ContentSwapAnimator(MainLabel, MainViewport, this);
        _subLabelAnimator = new ContentSwapAnimator(SubLabel, SubViewport, this);
        SizeChanged += OnComponentSizeChanged;
    }

    void OnComponentSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!_isAttached) return;
        _mainLabelAnimator.RefreshMetrics(Settings.ScrollMaxLength);
        _subLabelAnimator.RefreshMetrics(Settings.ScrollMaxLength);
    }

    void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _isAttached = true;
        _lifetimeCts?.Cancel();
        _lifetimeCts = new CancellationTokenSource();

        _lessonsService.PostMainTimerTicked -= OnPostMainTimerTicked;
        _lessonsService.PostMainTimerTicked += OnPostMainTimerTicked;

        try
        {
            Settings.EnsureProviderSettings(PluginRuntime.QuoteSources.Providers);
        }
        catch
        {
            // 运行时尚未初始化时，下面的 UpdateAsync 会显示配置提示；这里静默等待。
        }

        EnsureQueueSubscription();

        CaptureDefaultsIfNeeded();
        ApplyAppearanceIfChanged();

        // 不依赖宿主的事件节奏：即使 PostMainTimerTicked 因宿主状态暂停，也能按秒检查刷新。
        StartRefreshTimer();

        _mainLabelAnimator.Resume();
        _subLabelAnimator.Resume();

        // 宿主重新显示主界面时若重新附着组件，这里必须走与计时器相同的间隔检查，
        // 否则每次重新附着都可能立即出队一条内容。
        TryTriggerRefresh();
    }

    void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        _lessonsService.PostMainTimerTicked -= OnPostMainTimerTicked;
        StopRefreshTimer();

        if (_queueSubscribed)
        {
            try
            {
                PluginRuntime.Queue.QueueChanged -= OnQueueChanged;
            }
            catch
            {
                // 关闭阶段运行时可能已不可用。
            }

            _queueSubscribed = false;
        }

        CancellationTokenSource? cts = Interlocked.Exchange(ref _lifetimeCts, null);
        if (cts is null) return;
        cts.Cancel();

        ClearQueueCurrentDisplay();

        // 不在此时 Dispose：在途的 Task.Delay / 队列等待仍持有该令牌，
        // 提前 Dispose 会让它们抛 ObjectDisposedException；取消已足够，取消源交给 GC。
    }

    static void ClearQueueCurrentDisplay()
    {
        try
        {
            PluginRuntime.Queue.ClearCurrentDisplay();
        }
        catch
        {
            // 关闭阶段运行时可能已不可用。
        }
    }

    /// <summary>运行时可能在组件首次附着后才可用，因此每次 tick 都尝试补上队列事件订阅。</summary>
    void EnsureQueueSubscription()
    {
        if (_queueSubscribed) return;

        try
        {
            PluginRuntime.Queue.QueueChanged -= OnQueueChanged;
            PluginRuntime.Queue.QueueChanged += OnQueueChanged;
            _queueSubscribed = true;
        }
        catch
        {
            // 运行时尚未初始化；下一次 tick 再试。
        }
    }

    void StartRefreshTimer()
    {
        _refreshTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick -= OnRefreshTimerTick;
        _refreshTimer.Tick += OnRefreshTimerTick;
        _refreshTimer.Start();
    }

    void StopRefreshTimer()
    {
        if (_refreshTimer is null) return;
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTimerTick;
    }

    void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        if (!_isAttached) return;

        try
        {
            EnsureQueueSubscription();
            ApplyAppearanceIfChanged();
            _mainLabelAnimator.Tick();
            _subLabelAnimator.Tick();
            if (_sourceFingerprint is not null)
                _ = SyncSourceConfiguration(BuildRequestOptions());
            TryTriggerRefresh();
        }
        catch (Exception exception)
        {
            // 定时器里的异常不能冒泡到宿主 UI 线程。
            Console.WriteLine(exception);
        }
    }

    void OnPostMainTimerTicked(object? sender, EventArgs e)
    {
        TryTriggerRefresh();
    }

    /// <summary>队列新增了可播放内容时提前结束「等待内容」状态；平时不改变刷新节奏。</summary>
    void OnQueueChanged(object? sender, EventArgs e)
    {
        if (!_isAttached) return;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnQueueChanged(sender, e));
            return;
        }

        int queued = GetQueuedCountSafe();
        if (queued <= 0) return;

        if (Settings.UpdateTimeGapSeconds > 0)
        {
            if (!_waitingForContent) return;
            TryTriggerRefresh(force: true);
            return;
        }

        // 自动刷新关闭：只有人工批准等操作让队列真正变长时才展示下一条。
        if (queued <= _lastQueuedCount) return;
        TryTriggerRefresh(force: true);
    }

    static int GetQueuedCountSafe()
    {
        try
        {
            return PluginRuntime.Queue.QueuedCount;
        }
        catch
        {
            return 0;
        }
    }

    void CaptureDefaultsIfNeeded()
    {
        if (_defaultsCaptured) return;
        _defaultsCaptured = true;

        _defaultMainFontSize = MainLabel.FontSize;
        _defaultSubFontSize = SubLabel.FontSize;
    }

    /// <summary>
    /// 统一的刷新入口：距上次刷新完成不足间隔时不触发，已有刷新在途时不再叠加。
    /// 宿主 PostMainTimerTicked 约 50ms 触发一次，另有 1 秒定时器兜底，节流完全依赖本方法。
    /// </summary>
    void TryTriggerRefresh(bool force = false)
    {
        if (!_isAttached) return;
        if (Interlocked.Exchange(ref _updatePending, 1) != 0)
        {
            // 刷新在途时被丢弃的强制请求（例如 interval=0 下连续人工批准）必须补一次，
            // 否则第二条内容会一直留在队列里。
            if (force) Volatile.Write(ref _refreshRequested, 1);
            return;
        }

        try
        {
            bool autoRefreshDisabled = Settings.UpdateTimeGapSeconds <= 0;
            long lastUpdate = Interlocked.Read(ref _lastUpdateTimestamp);

            // 间隔设为 0 表示关闭「周期性」刷新，但首次展示和人工新增内容仍然继续。
            if (autoRefreshDisabled)
            {
                if (lastUpdate != 0 && !_waitingForContent && !force && _hasDisplayedContent)
                {
                    Volatile.Write(ref _updatePending, 0);
                    return;
                }
            }
            else
            {
                long now = Stopwatch.GetTimestamp();
                double elapsed = lastUpdate == 0
                    ? double.PositiveInfinity
                    : (now - lastUpdate) / (double)Stopwatch.Frequency;

                if (elapsed < Settings.UpdateTimeGapSeconds)
                {
                    // 等待内容时允许提前刷新；短刷新间隔下按间隔重试，长间隔时最多每 5 秒重试一次。
                    double waitingPollInterval = Math.Min(
                        WaitingPollIntervalSeconds,
                        Settings.UpdateTimeGapSeconds);
                    bool allowedWhileWaiting = _waitingForContent
                        && (force || elapsed >= waitingPollInterval);
                    if (!allowedWhileWaiting)
                    {
                        Volatile.Write(ref _updatePending, 0);
                        return;
                    }
                }
            }

            CancellationToken token = _lifetimeCts?.Token ?? CancellationToken.None;
            _ = RunUpdateAsync(token);
        }
        catch (Exception exception)
        {
            // 任何意外都不能把刷新锁永久留在占用状态。
            Volatile.Write(ref _updatePending, 0);
            Console.WriteLine(exception);
        }
    }

    async Task RunUpdateAsync(CancellationToken lifetimeToken)
    {
        CancellationTokenSource? timeoutCts = null;
        Task? update = null;
        int updateGeneration = Interlocked.Increment(ref _updateGeneration);

        try
        {
            // 取消源创建也可能抛异常（例如生命周期令牌已释放）；必须放在 try 内，
            // 否则 _updatePending 会永久保持为 1，组件再也不刷新。
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            timeoutCts.CancelAfter(UpdateTimeout);
            update = UpdateAsync(timeoutCts.Token, updateGeneration);

            await update.WaitAsync(UpdateTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 队列/持久化某次等待异常地卡住：释放节流锁，下一个 tick 重新开始，
            // 后台补货不受影响，内容准备好后会再次显示。
        }
        catch (OperationCanceledException)
        {
            // 组件已脱离视觉树或宿主正在退出。
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception);
        }
        finally
        {
            if (timeoutCts is not null)
            {
                // 超时后内层任务可能仍在收尾，等它结束再释放关联的取消源，
                // 否则在途的令牌注册会抛 ObjectDisposedException。
                if (update is null || update.IsCompleted)
                {
                    timeoutCts.Dispose();
                }
                else
                {
                    _ = update.ContinueWith(_ => timeoutCts.Dispose(), TaskScheduler.Default);
                }
            }

            // 无论成败都在完成时推进节流时间戳：刷新间隔稳定为 max(设置间隔, 上次刷新耗时)，
            // 不会因网络或审查耗时抖动而忽快忽慢。
            Interlocked.Exchange(ref _lastUpdateTimestamp, Stopwatch.GetTimestamp());
            Volatile.Write(ref _updatePending, 0);

            if (Interlocked.Exchange(ref _refreshRequested, 0) != 0)
            {
                Dispatcher.UIThread.Post(() => TryTriggerRefresh(force: true));
            }
        }
    }

    /// <summary>参与抽签的来源及其权重、AI 审查开关与长度限制的指纹；任一变化都需要整理队列。</summary>
    static string BuildSourceFingerprint(QuoteRequestOptions options)
    {
        IEnumerable<string> parts = PluginRuntime.QuoteSources.GetEligibleProviderIds(options)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(id =>
            {
                options.ProviderSettings.TryGetValue(id, out QuoteProviderConfig? config);
                return $"{id}:{config?.Weight ?? 0}:{(config?.IsAiReviewEnabled ?? true ? 1 : 0)}";
            });
        return $"{options.LengthLimitation}|{string.Join(",", parts)}";
    }

    QuoteRequestOptions BuildRequestOptions()
    {
        IReadOnlyList<IQuoteProvider> customProviders = Settings.CreateCustomProviders();

        string[] ignoreTerms = Settings.IgnoreListString
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new QuoteRequestOptions
        {
            // 抓取/审查在后台线程使用设置快照，避免设置页同时修改来源配置时出现枚举异常。
            ProviderSettings = new Dictionary<string, QuoteProviderConfig>(
                Settings.ProviderSettings,
                StringComparer.OrdinalIgnoreCase),
            LengthLimitation = Math.Max(0, Settings.LengthLimitation),
            IgnoreTerms = ignoreTerms.Where(term => !string.IsNullOrWhiteSpace(term)).ToArray(),
            ExtraProviders = customProviders
        };
    }

    /// <summary>
    /// 来源配置变化后整理队列：停用来源和超长的旧条目立即移出播放队列，直通条目按新权重重新抓取。
    /// 首次调用只清理不再启用或超长的条目（例如快照里的旧内容），不丢弃直通条目。
    /// 设置页直接改写来源配置对象、没有变更通知，因此由每秒定时器与每次刷新调用本方法检测。
    /// 必须在 UI 线程调用；返回进行中的整理任务。
    /// </summary>
    Task SyncSourceConfiguration(QuoteRequestOptions options)
    {
        string fingerprint = BuildSourceFingerprint(options);
        if (string.Equals(fingerprint, _sourceFingerprint, StringComparison.Ordinal))
            return _sourceSyncTask;

        bool configChanged = _sourceFingerprint is not null;
        _sourceFingerprint = fingerprint;
        Task previous = _sourceSyncTask;
        _sourceSyncTask = RunAsync();
        return _sourceSyncTask;

        async Task RunAsync()
        {
            await previous.ConfigureAwait(false);
            await PluginRuntime.Queue
                .ApplySourceConfigurationAsync(options, dropUnreviewed: configChanged)
                .ConfigureAwait(false);
        }
    }

    async Task UpdateAsync(CancellationToken cancellationToken, int updateGeneration)
    {
        ReviewQueueItem? item = null;
        bool displayed = false;

        try
        {
            if (!_isAttached) return;

            try
            {
                Settings.EnsureProviderSettings(PluginRuntime.QuoteSources.Providers);
            }
            catch
            {
                // 由下方统一的运行时不可用提示处理。
            }

            QuoteRequestOptions options = BuildRequestOptions();

            // 出队前确保队列已按当前来源配置整理（定时器可能已经在整理，等它完成）。
            await SyncSourceConfiguration(options).ConfigureAwait(false);

            // 抓取和 AI 审查可能持续几分钟；先确保后台补货在跑，显示只等一小段时间，
            // 避免「队列空了 → 同步审查 → 几十分钟不刷新」的卡死观感。
            // 自动刷新关闭时只在首次启动补货，之后不主动抓取新内容（人工批准的内容仍会展示）。
            bool firstUpdate = Interlocked.Read(ref _lastUpdateTimestamp) == 0;
            bool autoRefreshEnabled = Settings.UpdateTimeGapSeconds > 0;
            bool allowRefill = autoRefreshEnabled || !_hasDisplayedContent;
            if (allowRefill)
            {
                int refillThreshold = PluginRuntime.Queue.GetEffectiveRefillThreshold(options);
                if (firstUpdate || PluginRuntime.Queue.QueuedCount <= refillThreshold)
                    _ = PluginRuntime.Queue.EnsureBackgroundRefill(options);
            }

            item = await PluginRuntime.Queue
                .GetNextForDisplayAsync(options, TimeSpan.FromMilliseconds(600), allowRefill, cancellationToken)
                .ConfigureAwait(false);

            // 条目一旦出队就已被标记为已显示；取消、脱离或已过期时必须退回队列，
            // 否则历史会记成已播放而用户从未看到。
            bool stale = cancellationToken.IsCancellationRequested
                         || !_isAttached
                         || updateGeneration != Volatile.Read(ref _updateGeneration);
            if (stale)
            {
                if (item is not null)
                {
                    _ = PluginRuntime.Queue.ReturnUndeployedAsync(
                        item,
                        toManualReview: false,
                        "刷新已取消或过期，内容未展示。");
                }

                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            await Dispatcher.UIThread.InvokeAsync(() => DisplayNext(item, updateGeneration, cancellationToken));
            displayed = true;

            try
            {
                if (autoRefreshEnabled)
                {
                    int refillThreshold = PluginRuntime.Queue.GetEffectiveRefillThreshold(options);
                    if (PluginRuntime.Queue.QueuedCount <= refillThreshold)
                        _ = PluginRuntime.Queue.EnsureBackgroundRefill(options);
                }
            }
            catch
            {
                // 补货失败已由队列服务记录；不能覆盖已经显示成功的内容。
            }
        }
        catch (OperationCanceledException)
        {
            // 组件已脱离视觉树或宿主正在退出；已出队但未展示的条目退回队列。
            if (!displayed && item is not null)
            {
                _ = PluginRuntime.Queue.ReturnUndeployedAsync(
                    item, toManualReview: false, "刷新已取消，内容未展示。");
            }
        }
        catch
        {
            if (!displayed && item is not null)
            {
                _ = PluginRuntime.Queue.ReturnUndeployedAsync(
                    item, toManualReview: false, "刷新失败，内容未展示。");
            }

            if (!_isAttached) return;
            if (updateGeneration != Volatile.Read(ref _updateGeneration)) return;
            await Dispatcher.UIThread.InvokeAsync(
                () => DisplayUnavailable("审查运行时尚未就绪，请先在插件设置中配置 AI 供应商；当前保持失败即关闭。"));
        }
    }

    void DisplayNext(ReviewQueueItem? item, int updateGeneration, CancellationToken cancellationToken)
    {
        // 条目已出队；这里被拒时必须退回队列，不能让它同时从队列消失又出现在历史里。
        if (!_isAttached)
        {
            if (item is not null)
            {
                _ = PluginRuntime.Queue.ReturnUndeployedAsync(
                    item, toManualReview: false, "组件已脱离视觉树，内容未展示。");
            }

            return;
        }

        if (updateGeneration != Volatile.Read(ref _updateGeneration))
        {
            if (item is not null)
            {
                _ = PluginRuntime.Queue.ReturnUndeployedAsync(
                    item, toManualReview: false, "刷新已过期，内容未展示。");
            }

            return;
        }

        // 队列中的这一条已经在本方法被调用前出队，记录剩余条数供「自动刷新关闭」时判断新增内容。
        _lastQueuedCount = GetQueuedCountSafe();

        // 防御性检查：组件永远不展示高风险内容，也不展示尚未完成审查或未经人工批准的中风险内容；
        // 仅来源明确关闭 AI 审查的内容可在未知风险下播放。
        bool unsafeState = item is not null
            && item.State is not (ReviewItemState.Approved or ReviewItemState.Displayed);
        bool mediumWithoutManualApproval = item is not null
            && item.Risk == RiskLevel.Medium
            && item.ManualReviewedAt is null
            && !IsRelaxedReviewMode();
        bool unknownWithoutManualApproval = item is not null
            && item.Risk == RiskLevel.Unknown
            && item.ManualReviewedAt is null
            && !item.IsAiReviewSkipped;

        if (item is null)
        {
            _waitingForContent = true;

            // 已有句子且后台还在抓取/审查时保持当前内容，等队列一有结果再替换，
            // 避免每个刷新周期都把句子闪成「正在获取…」。等待过久则显示状态，防止看起来卡死。
            if (_hasDisplayedContent && IsRefillingSafe())
            {
                if (_waitingSinceTimestamp == 0)
                {
                    _waitingSinceTimestamp = Stopwatch.GetTimestamp();
                }
                else
                {
                    double waited = (Stopwatch.GetTimestamp() - _waitingSinceTimestamp)
                                    / (double)Stopwatch.Frequency;
                    double noticeAfter = Math.Clamp(Settings.UpdateTimeGapSeconds * 2, 60, 600);
                    if (waited >= noticeAfter)
                        ShowEmptyMessage();
                }

                return;
            }

            ShowEmptyMessage();
            return;
        }

        if (item.Risk == RiskLevel.High
            || string.IsNullOrWhiteSpace(item.Quote?.Content)
            || unsafeState)
        {
            // 高风险、空正文或状态异常的内容不重新入播放队列，但也不该在历史里留下“已播放”假记录。
            _waitingForContent = true;
            _ = PluginRuntime.Queue.ReturnUndeployedAsync(
                item, toManualReview: true, "内容未通过展示前校验，已退回人工复核。");
            ShowEmptyMessage();
            return;
        }

        if (mediumWithoutManualApproval || unknownWithoutManualApproval)
        {
            // 例如宽松档放行中风险后用户又把严格度改回均衡/严格：退回复核，而不是静默丢弃。
            _waitingForContent = true;
            _ = PluginRuntime.Queue.ReturnUndeployedAsync(
                item, toManualReview: true, "展示前校验未通过，已退回人工复核。");
            ShowEmptyMessage();
            return;
        }

        _waitingForContent = false;
        _hasDisplayedContent = true;
        _waitingSinceTimestamp = 0;
        ContentAnimationMode mode = Settings.EffectiveAnimationMode;
        ContentAnimationMode secondaryMode = ResolveSecondaryAnimationMode(mode);

        string content = item.Quote.Content.Trim();
        string title = item.Quote.Title?.Trim() ?? string.Empty;
        string author = item.Quote.Author?.Trim() ?? string.Empty;
        string attribution = BuildAttribution(
            Settings.IsAuthorShowEnabled ? author : string.Empty,
            Settings.IsTitleShowEnabled ? title : string.Empty);
        bool hasAttribution = attribution.Length > 0;

        int generation = Interlocked.Increment(ref _displayGeneration);
        _mainShowsAttribution = false;
        _mainLabelAnimator.SetNormalForeground(ParseBrush(Settings.MainTextColor));
        _mainLabelAnimator.Update(content, mode, Settings.ScrollMaxLength);

        bool showExtra = hasAttribution
            && Settings.AttributesRule == QuoteAiConfig.AttributesDisplayRule.Sametime;

        // 先写好内容再显示，避免切换成「展示额外信息」时先闪一帧上一条归属信息。
        if (showExtra)
        {
            _subLabelAnimator.Update(attribution, secondaryMode, Settings.ScrollMaxLength);
            SubViewport.IsVisible = true;
        }
        else
        {
            _subLabelAnimator.Update(string.Empty, ContentAnimationMode.None, 0);
            SubViewport.IsVisible = false;
        }

        if (hasAttribution
            && Settings.AttributesRule == QuoteAiConfig.AttributesDisplayRule.Separate
            && Settings.AttributesShowingInterval > 0)
        {
            // 延迟长于刷新间隔时，额外信息会在显示前就被下一条内容取消；收敛到间隔内。
            double delaySeconds = Settings.AttributesShowingInterval;
            double refreshGap = Settings.UpdateTimeGapSeconds;
            if (refreshGap > 0)
                delaySeconds = Math.Min(delaySeconds, Math.Max(1, refreshGap - 1));

            _ = ShowAttributionSeparatelyAsync(
                attribution,
                generation,
                TimeSpan.FromSeconds(delaySeconds),
                cancellationToken);
        }
    }

    async Task ShowAttributionSeparatelyAsync(
        string attribution,
        int generation,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            if (!_isAttached || generation != Volatile.Read(ref _displayGeneration)) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_isAttached || generation != Volatile.Read(ref _displayGeneration)) return;
                SubViewport.IsVisible = false;
                // 分开展示时主标签显示的是额外信息，颜色也必须换成额外信息颜色。
                _mainShowsAttribution = true;
                _mainLabelAnimator.SetNormalForeground(ParseBrush(Settings.AttributionTextColor));
                _mainLabelAnimator.Update(
                    attribution,
                    ResolveSecondaryAnimationMode(Settings.EffectiveAnimationMode),
                    Settings.ScrollMaxLength);
            });
        }
        catch (OperationCanceledException)
        {
            // 生命周期或内容已经切换。
        }
        catch (ObjectDisposedException)
        {
            // 取消源已随组件生命周期释放。
        }
    }

    void DisplayUnavailable(string message)
    {
        if (!_isAttached) return;
        _waitingForContent = false;
        _hasDisplayedContent = false;
        _mainShowsAttribution = false;
        Interlocked.Increment(ref _displayGeneration);
        ClearQueueCurrentDisplay();
        _mainLabelAnimator.SetNormalForeground(ParseBrush(Settings.MainTextColor));
        _mainLabelAnimator.Update(message, ContentAnimationMode.None, 0);
        SubViewport.IsVisible = false;
        _subLabelAnimator.Update(string.Empty, ContentAnimationMode.None, 0);
    }

    void ShowEmptyMessage()
    {
        string message = BuildEmptyMessage();
        Interlocked.Increment(ref _displayGeneration);
        _mainShowsAttribution = false;
        ClearQueueCurrentDisplay();
        _mainLabelAnimator.SetNormalForeground(ParseBrush(Settings.MainTextColor));
        _mainLabelAnimator.Update(message, ContentAnimationMode.None, 0);
        SubViewport.IsVisible = false;
        _subLabelAnimator.Update(string.Empty, ContentAnimationMode.None, 0);
    }

    /// <summary>额外信息不重复播放扫光：主句扫一次即可，避免同时出现两条光带。</summary>
    static ContentAnimationMode ResolveSecondaryAnimationMode(ContentAnimationMode mode)
        => mode == ContentAnimationMode.Shimmer ? ContentAnimationMode.Fade : mode;

    /// <summary>宽松严格度下中风险会被策略自动放行，展示层不能再用「中风险未人工批准」拦截。</summary>
    static bool IsRelaxedReviewMode()
    {
        try
        {
            return PluginRuntime.SettingsStore.Settings.Ai.Strictness == ReviewStrictness.Relaxed;
        }
        catch
        {
            return false;
        }
    }

    static bool IsRefillingSafe()
    {
        try
        {
            return PluginRuntime.Queue.IsRefilling;
        }
        catch
        {
            return false;
        }
    }

    string BuildEmptyMessage()
    {
        int pending;
        int blocked;
        bool refilling;
        string status;

        try
        {
            pending = PluginRuntime.Queue.PendingCount;
            blocked = PluginRuntime.Queue.BlockedCount;
            refilling = PluginRuntime.Queue.IsRefilling;
            status = SanitizeStatus(PluginRuntime.Queue.LastStatusMessage);
        }
        catch
        {
            return "暂无可播放的内容；审查运行时尚未就绪。";
        }

        string headline = refilling ? "正在获取并审查内容…" : "暂无可播放的内容";
        string message = $"{headline}（待人工复核 {pending} / 已拦截 {blocked}）";
        if (!string.IsNullOrWhiteSpace(status))
            message += $"\n{status}";
        return message;
    }

    static string BuildAttribution(string author, string title)
    {
        if (author.Length == 0) return title;
        if (title.Length == 0) return author;
        return $"{author} · {title}";
    }

    static string SanitizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return string.Empty;
        string value = status.Trim().Replace('\r', ' ').Replace('\n', ' ');
        if (value.Contains("http://", StringComparison.OrdinalIgnoreCase)
            || value.Contains("https://", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Exception", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Socket", StringComparison.OrdinalIgnoreCase))
            return "队列状态已更新；详细信息请查看审计日志。";
        return value.Length <= 160 ? value : $"{value[..160]}…";
    }

    /// <summary>字体、字号或颜色设置变化后立即套用到标签；每秒检查一次，开销可忽略。</summary>
    void ApplyAppearanceIfChanged()
    {
        CaptureDefaultsIfNeeded();

        // 把「继承时实际解析到的主题字号」也放进 key：主题字号变化时同样能重新套用。
        double mainSize = ResolveEffectiveFontSize(
            Settings.MainFontSize, MainFontSizeResourceKey, _defaultMainFontSize);
        double subSize = ResolveEffectiveFontSize(
            Settings.AttributionFontSize, SubFontSizeResourceKey, _defaultSubFontSize);

        string key = string.Join(
            '\u001f',
            Settings.MainFontFamilyName,
            mainSize.ToString(CultureInfo.InvariantCulture),
            Settings.MainFontWeightIndex.ToString(CultureInfo.InvariantCulture),
            Settings.MainTextColor,
            subSize.ToString(CultureInfo.InvariantCulture),
            Settings.AttributionTextColor,
            Settings.ShimmerColor,
            Settings.ScrollMaxLength.ToString(CultureInfo.InvariantCulture),
            Settings.ScrollSpeedPixelsPerSecond.ToString(CultureInfo.InvariantCulture),
            Settings.TextVerticalOffset.ToString(CultureInfo.InvariantCulture));

        if (string.Equals(key, _appliedAppearanceKey, StringComparison.Ordinal)) return;
        _appliedAppearanceKey = key;

        double verticalOffset = Settings.TextVerticalOffset;
        _mainViewportTransform.Y = verticalOffset;
        _subViewportTransform.Y = verticalOffset;

        IBrush? shimmerBrush = ParseBrush(Settings.ShimmerColor);
        _mainLabelAnimator.SetShimmerColor(shimmerBrush);
        _subLabelAnimator.SetShimmerColor(shimmerBrush);
        _mainLabelAnimator.SetScrollSpeed(Settings.ScrollSpeedPixelsPerSecond);
        _subLabelAnimator.SetScrollSpeed(Settings.ScrollSpeedPixelsPerSecond);

        ApplyLabelStyle(
            MainLabel,
            mainSize,
            Settings.MainFontFamilyName,
            Settings.MainFontWeightIndex,
            _mainShowsAttribution ? Settings.AttributionTextColor : Settings.MainTextColor,
            _mainLabelAnimator);

        ApplyLabelStyle(
            SubLabel,
            subSize,
            Settings.MainFontFamilyName,
            Settings.MainFontWeightIndex,
            Settings.AttributionTextColor,
            _subLabelAnimator);

        _mainLabelAnimator.RefreshMetrics(Settings.ScrollMaxLength);
        _subLabelAnimator.RefreshMetrics(Settings.ScrollMaxLength);
    }

    void ApplyLabelStyle(
        Label label,
        double resolvedSize,
        string fontFamilyName,
        int fontWeightIndex,
        string colorText,
        ContentSwapAnimator animator)
    {
        FontFamily? family = TryCreateFontFamily(fontFamilyName);
        if (family is null)
        {
            // 让标签继续继承主界面的主题字体，而不是把当前值固化成局部值。
            label.ClearValue(TemplatedControl.FontFamilyProperty);
        }
        else
        {
            label.FontFamily = family;
        }

        label.FontSize = resolvedSize;

        if (fontWeightIndex is >= 1 and <= 6)
        {
            label.FontWeight = fontWeightIndex switch
            {
                1 => FontWeight.Normal,
                2 => FontWeight.Medium,
                3 => FontWeight.SemiBold,
                4 => FontWeight.Bold,
                5 => FontWeight.ExtraBold,
                _ => FontWeight.Black
            };
        }
        else
        {
            label.ClearValue(TemplatedControl.FontWeightProperty);
        }

        animator.SetNormalForeground(ParseBrush(colorText));
    }

    /// <summary>解析生效字号：用户指定了就用指定值，否则跟随主界面主题资源。</summary>
    double ResolveEffectiveFontSize(double configuredSize, string resourceKey, double fallback)
    {
        if (configuredSize > 0) return Math.Clamp(configuredSize, 6, 200);
        return ResolveInheritedFontSize(resourceKey, fallback);
    }

    /// <summary>重新读取主界面的动态字号资源，主题字号改变时标签也能跟随。</summary>
    double ResolveInheritedFontSize(string resourceKey, double fallback)
    {
        try
        {
            if (this.TryFindResource(resourceKey, out object? value) && value is not null)
            {
                double resolved = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsFinite(resolved) && resolved > 0) return resolved;
            }
        }
        catch
        {
            // 资源不可用时退回附着时捕获的值。
        }

        return fallback;
    }

    static FontFamily? TryCreateFontFamily(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            return new FontFamily(name.Trim());
        }
        catch
        {
            return null;
        }
    }

    static IBrush? ParseBrush(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return Color.TryParse(text.Trim(), out Color color) ? new SolidColorBrush(color) : null;
    }
}
