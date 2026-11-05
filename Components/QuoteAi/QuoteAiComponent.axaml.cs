using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Attributes;
using Motivation.Core;
using Motivation.Models;

namespace Motivation.Components.QuoteAi;

[ComponentInfo(
    "4E804B23-8A13-4E8F-92A8-6C86032875CF",
    "AI 每日一言",
    "\uE3F4",
    "经过 AI 风险审查后再展示，支持批量审查与排队播放")]
public partial class QuoteAiComponent : ComponentBase<QuoteAiConfig>
{
    readonly ILessonsService _lessonsService;
    readonly ContentSwapAnimator _mainLabelAnimator;
    readonly ContentSwapAnimator _subLabelAnimator;

    CancellationTokenSource? _lifetimeCts;
    int _isUpdating;
    int _displayGeneration;
    bool _isAttached;

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

        Settings.LastUpdate = DateTime.Now;
        _ = UpdateAsync(_lifetimeCts.Token);
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
        if (!_isAttached || Settings.UpdateTimeGapSeconds <= 0) return;

        DateTime now = DateTime.Now;
        if (Settings.LastUpdate != default
            && (now - Settings.LastUpdate).TotalSeconds < Settings.UpdateTimeGapSeconds)
            return;

        // 与原组件一致：在开始工作前记录时间，避免网络请求较慢时每个计时周期都重复触发。
        Settings.LastUpdate = now;
        CancellationToken token = _lifetimeCts?.Token ?? CancellationToken.None;
        _ = UpdateAsync(token);
    }

    async Task UpdateAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _isUpdating, 1) == 1) return;

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

            string[] ignoreTerms = Settings.IgnoreListString
                .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var options = new QuoteRequestOptions
            {
                ProviderSettings = Settings.ProviderSettings,
                LengthLimitation = Math.Max(0, Settings.LengthLimitation),
                BatchSize = Math.Clamp(Settings.BatchSize, 1, 10),
                MaxBufferedItems = Math.Clamp(Settings.MaxBufferedItems, 1, 100),
                IgnoreTerms = ignoreTerms.Where(term => !string.IsNullOrWhiteSpace(term)).ToArray()
            };

            ReviewQueueItem? item = await PluginRuntime.Queue
                .GetNextForDisplayAsync(options, cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            if (!_isAttached) return;

            await Dispatcher.UIThread.InvokeAsync(() => DisplayNext(item, cancellationToken));
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
            Interlocked.Exchange(ref _isUpdating, 0);
        }
    }

    void DisplayNext(ReviewQueueItem? item, CancellationToken cancellationToken)
    {
        if (!_isAttached) return;

        // 防御性检查：组件永远不展示高风险内容，也不展示尚未完成审查或未经人工批准的中风险内容。
        bool unsafeState = item is not null
            && item.State is not (ReviewItemState.Approved or ReviewItemState.Displayed);
        bool mediumWithoutManualApproval = item is not null
            && item.Risk == RiskLevel.Medium
            && item.ManualReviewedAt is null;
        bool unknownWithoutManualApproval = item is not null
            && item.Risk == RiskLevel.Unknown
            && item.ManualReviewedAt is null;

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
        int pending = 0;
        int blocked = 0;
        string status = string.Empty;

        try
        {
            pending = PluginRuntime.Queue.PendingCount;
            blocked = PluginRuntime.Queue.BlockedCount;
            if (Settings.ShowQueueStatus)
                status = SanitizeStatus(PluginRuntime.Queue.LastStatusMessage);
        }
        catch
        {
            return "暂无通过 AI 审查的内容；AI 审查运行时尚未就绪。";
        }

        string message = $"暂无通过 AI 审查的内容（待人工复核 {pending} / 已拦截 {blocked}）";
        if (Settings.ShowQueueStatus && !string.IsNullOrWhiteSpace(status))
            message += $"\n{status}";
        else if (!Settings.ShowQueueStatus)
            message += "\nAI 审查失败时默认拦截。";
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
