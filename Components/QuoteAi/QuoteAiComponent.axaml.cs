using Avalonia;
using Avalonia.Controls;
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
    readonly ILessonsService _lessonsService;
    readonly ContentSwapAnimator _mainLabelAnimator;
    readonly ContentSwapAnimator _subLabelAnimator;

    CancellationTokenSource? _lifetimeCts;
    int _updatePending;
    int _isRefilling;
    int _displayGeneration;
    bool _isAttached;

    /// <summary>上一次刷新（无论成败）完成的时间；展示间隔从完成时刻起算，保证节奏稳定。</summary>
    DateTime _lastUpdate;

    public QuoteAiComponent(ILessonsService lessonsService)
    {
        _lessonsService = lessonsService;
        InitializeComponent();
        _mainLabelAnimator = new ContentSwapAnimator(MainLabel);
        _subLabelAnimator = new ContentSwapAnimator(SubLabel);
    }

    void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _isAttached = true;
        _lifetimeCts?.Cancel();
        _lifetimeCts?.Dispose();
        _lifetimeCts = new CancellationTokenSource();

        _lessonsService.PostMainTimerTicked += OnPostMainTimerTicked;

        try
        {
            Settings.EnsureProviderSettings(PluginRuntime.QuoteSources.Providers);
        }
        catch
        {
            // 运行时尚未初始化时，下面的 UpdateAsync 会显示配置提示；这里静默等待。
        }

        // 宿主在主界面隐藏（IsVisible=false 会脱离视觉树）后再显示时会重新 Attach；
        // 这里必须走与计时器相同的间隔检查，否则每次显示面板都会立即出队一条内容。
        TryTriggerRefresh();
    }

    void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        _lessonsService.PostMainTimerTicked -= OnPostMainTimerTicked;

        CancellationTokenSource? cts = Interlocked.Exchange(ref _lifetimeCts, null);
        if (cts is null) return;
        cts.Cancel();
        cts.Dispose();
    }

    void OnPostMainTimerTicked(object? sender, EventArgs e)
    {
        TryTriggerRefresh();
    }

    /// <summary>
    /// 统一的刷新入口：距上次刷新完成不足间隔时不触发，已有刷新在途时不再叠加。
    /// 宿主 PostMainTimerTicked 约 50ms 触发一次，节流完全依赖本方法。
    /// </summary>
    void TryTriggerRefresh()
    {
        if (!_isAttached || Settings.UpdateTimeGapSeconds <= 0) return;
        if (Interlocked.Exchange(ref _updatePending, 1) != 0) return;

        DateTime now = DateTime.Now;
        if (_lastUpdate != default
            && (now - _lastUpdate).TotalSeconds < Settings.UpdateTimeGapSeconds)
        {
            Volatile.Write(ref _updatePending, 0);
            return;
        }

        CancellationToken token = _lifetimeCts?.Token ?? CancellationToken.None;
        _ = UpdateAsync(token);
    }

    async Task UpdateAsync(CancellationToken cancellationToken)
    {
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

            IReadOnlyList<IQuoteProvider> customProviders = Settings.CreateCustomProviders();

            string[] ignoreTerms = Settings.IgnoreListString
                .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var options = new QuoteRequestOptions
            {
                ProviderSettings = Settings.ProviderSettings,
                LengthLimitation = Math.Max(0, Settings.LengthLimitation),
                IgnoreTerms = ignoreTerms.Where(term => !string.IsNullOrWhiteSpace(term)).ToArray(),
                ExtraProviders = customProviders
            };

            ReviewQueueItem? item = await PluginRuntime.Queue
                .GetNextForDisplayAsync(options, cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            if (!_isAttached) return;

            await Dispatcher.UIThread.InvokeAsync(() => DisplayNext(item, cancellationToken));

            // 补货使用插件级生命周期令牌：主界面隐藏导致组件脱离视觉树时，不应中断在途的抓取与审查。
            if (PluginRuntime.Queue.QueuedCount <= PluginRuntime.SettingsStore.Settings.Review.RefillThreshold)
                _ = RefillInBackgroundAsync(options, PluginRuntime.LifetimeToken);
        }
        catch (OperationCanceledException)
        {
            // 组件已脱离视觉树或宿主正在退出。
        }
        catch
        {
            if (!_isAttached) return;
            await Dispatcher.UIThread.InvokeAsync(
                () => DisplayUnavailable("审查运行时尚未就绪，请先在插件设置中配置 AI 供应商；当前保持失败即关闭。"));
        }
        finally
        {
            // 无论成败都在完成时推进节流时间戳：刷新间隔稳定为 max(设置间隔, 上次刷新耗时)，
            // 不会因网络或审查耗时抖动而忽快忽慢。
            _lastUpdate = DateTime.Now;
            Volatile.Write(ref _updatePending, 0);
        }
    }

    /// <summary>队列降到阈值时在后台抓取并审查下一批，使下次刷新可直接从队列出队而无需等待审查。</summary>
    async Task RefillInBackgroundAsync(QuoteRequestOptions options, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _isRefilling, 1) == 1) return;

        try
        {
            await PluginRuntime.Queue.RefreshBatchAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 失败已由队列服务写入审计日志；下次刷新时会再次尝试补充。
        }
        finally
        {
            Interlocked.Exchange(ref _isRefilling, 0);
        }
    }

    void DisplayNext(ReviewQueueItem? item, CancellationToken cancellationToken)
    {
        if (!_isAttached) return;

        // 防御性检查：组件永远不展示高风险内容，也不展示尚未完成审查或未经人工批准的中风险内容；
        // 仅来源明确关闭 AI 审查的内容可在未知风险下播放。
        bool unsafeState = item is not null
            && item.State is not (ReviewItemState.Approved or ReviewItemState.Displayed);
        bool mediumWithoutManualApproval = item is not null
            && item.Risk == RiskLevel.Medium
            && item.ManualReviewedAt is null;
        bool unknownWithoutManualApproval = item is not null
            && item.Risk == RiskLevel.Unknown
            && item.ManualReviewedAt is null
            && !item.IsAiReviewSkipped;

        if (item is null
            || item.Risk == RiskLevel.High
            || string.IsNullOrWhiteSpace(item.Quote?.Content)
            || unsafeState
            || mediumWithoutManualApproval
            || unknownWithoutManualApproval)
        {
            string message = BuildEmptyMessage();
            Interlocked.Increment(ref _displayGeneration);
            _mainLabelAnimator.Update(message, false, false);
            SubLabel.IsVisible = false;
            _subLabelAnimator.Update(string.Empty, false, false);
            return;
        }

        string content = item.Quote.Content.Trim();
        string title = item.Quote.Title?.Trim() ?? string.Empty;
        string author = item.Quote.Author?.Trim() ?? string.Empty;
        string attribution = BuildAttribution(
            Settings.IsAuthorShowEnabled ? author : string.Empty,
            Settings.IsTitleShowEnabled ? title : string.Empty);
        bool hasAttribution = attribution.Length > 0;

        int generation = Interlocked.Increment(ref _displayGeneration);
        _mainLabelAnimator.Update(content, Settings.IsAnimationEnabled, Settings.IsSwapAnimationEnabled);

        bool showExtra = hasAttribution
            && Settings.AttributesRule == QuoteAiConfig.AttributesDisplayRule.Sametime;

        SubLabel.IsVisible = showExtra;
        if (showExtra)
            _subLabelAnimator.Update(attribution, Settings.IsAnimationEnabled, false);
        else
            _subLabelAnimator.Update(string.Empty, false, false);

        if (hasAttribution
            && Settings.AttributesRule == QuoteAiConfig.AttributesDisplayRule.Separate
            && Settings.AttributesShowingInterval > 0)
        {
            _ = ShowAttributionSeparatelyAsync(
                attribution,
                generation,
                TimeSpan.FromSeconds(Settings.AttributesShowingInterval),
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
                SubLabel.IsVisible = false;
                _mainLabelAnimator.Update(attribution, Settings.IsAnimationEnabled, Settings.IsSwapAnimationEnabled);
            });
        }
        catch (OperationCanceledException)
        {
            // 生命周期或内容已经切换。
        }
    }

    void DisplayUnavailable(string message)
    {
        if (!_isAttached) return;
        Interlocked.Increment(ref _displayGeneration);
        _mainLabelAnimator.Update(message, false, false);
        SubLabel.IsVisible = false;
        _subLabelAnimator.Update(string.Empty, false, false);
    }

    string BuildEmptyMessage()
    {
        int pending;
        int blocked;
        string status;

        try
        {
            pending = PluginRuntime.Queue.PendingCount;
            blocked = PluginRuntime.Queue.BlockedCount;
            status = SanitizeStatus(PluginRuntime.Queue.LastStatusMessage);
        }
        catch
        {
            return "暂无可播放的内容；审查运行时尚未就绪。";
        }

        string message = $"暂无可播放的内容（待人工复核 {pending} / 已拦截 {blocked}）";
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
}
