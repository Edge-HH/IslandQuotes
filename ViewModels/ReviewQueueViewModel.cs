using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using IslandQuotes.Core;
using IslandQuotes.Models;
using IslandQuotes.Services;

namespace IslandQuotes.ViewModels;

/// <summary>人工复核与播放队列页面的视图模型。</summary>
public sealed class ReviewQueueViewModel : ObservableObject
{
    ReviewQueueService? _queue;
    bool _subscribed;
    bool _attached;
    int _refreshScheduled;
    int _pendingCount;
    int _queuedCount;
    int _blockedCount;
    bool _isAvailable;
    bool _refreshingNow;
    string _queueStatusMessage = string.Empty;
    string _actionStatusMessage = string.Empty;

    public ReviewQueueViewModel()
    {
        _queueStatusMessage = "正在等待人工复核队列初始化。";
    }

    public ObservableCollection<ReviewQueueRowViewModel> PendingManualReview { get; } = [];

    public ObservableCollection<ReviewQueueRowViewModel> PlaybackQueue { get; } = [];

    ReviewQueueRowViewModel? _currentDisplay;

    /// <summary>主界面当前正在展示的内容（最近一次出队项）。</summary>
    public ReviewQueueRowViewModel? CurrentDisplay
    {
        get => _currentDisplay;
        private set
        {
            if (!SetProperty(ref _currentDisplay, value)) return;
            OnPropertyChanged(nameof(HasCurrentDisplay));
        }
    }

    public bool HasCurrentDisplay => CurrentDisplay is not null;

    public int PendingCount
    {
        get => _pendingCount;
        private set
        {
            if (!SetProperty(ref _pendingCount, value)) return;
            OnPropertiesChanged(nameof(HasPending), nameof(HasNoPending));
        }
    }

    public int QueuedCount
    {
        get => _queuedCount;
        private set
        {
            if (!SetProperty(ref _queuedCount, value)) return;
            OnPropertiesChanged(nameof(HasPlayback), nameof(HasNoPlayback));
        }
    }

    public int BlockedCount
    {
        get => _blockedCount;
        private set => SetProperty(ref _blockedCount, value);
    }

    public bool IsAvailable
    {
        get => _isAvailable;
        private set
        {
            if (!SetProperty(ref _isAvailable, value)) return;
            OnPropertyChanged(nameof(CanRefreshNow));
        }
    }

    public bool IsRefreshingNow
    {
        get => _refreshingNow;
        private set
        {
            if (!SetProperty(ref _refreshingNow, value)) return;
            OnPropertyChanged(nameof(CanRefreshNow));
        }
    }

    public bool CanRefreshNow => IsAvailable && !IsRefreshingNow;

    public bool HasPending => PendingCount > 0;

    public bool HasNoPending => PendingCount <= 0;

    public bool HasPlayback => QueuedCount > 0;

    public bool HasNoPlayback => QueuedCount <= 0;

    public string LastStatusMessage
    {
        get => _queueStatusMessage;
        private set => SetProperty(ref _queueStatusMessage, value);
    }

    public string StatusMessage => string.IsNullOrWhiteSpace(_actionStatusMessage)
        ? (string.IsNullOrWhiteSpace(LastStatusMessage) ? "队列已就绪，尚无新事件。" : LastStatusMessage)
        : _actionStatusMessage;
    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        Refresh();
    }

    public void Detach()
    {
        _attached = false;
        if (_queue is not null && _subscribed)
        {
            _queue.QueueChanged -= OnQueueChanged;
            _subscribed = false;
        }
        _queue = null;
    }

    /// <summary>手动刷新统计和集合快照；队列事件刷新时清除一次性操作提示，让实时状态重新可见。</summary>
    public void Refresh(bool clearActionStatus = false)
    {
        try
        {
            if (clearActionStatus) _actionStatusMessage = string.Empty;
            EnsureQueueBound();
            if (_queue is null)
            {
                IsAvailable = false;
                LastStatusMessage = "人工复核队列尚未初始化。请启用插件后重试。";
                OnPropertyChanged(nameof(StatusMessage));
                return;
            }

            PendingCount = _queue.PendingCount;
            QueuedCount = _queue.QueuedCount;
            BlockedCount = _queue.BlockedCount;
            IsAvailable = true;

            ReplaceRows(PendingManualReview, _queue.PendingManualReview, playback: false);
            ReplaceRows(PlaybackQueue, _queue.PlaybackQueue, playback: true);

            CurrentDisplay = _queue.CurrentDisplayItem is { } current
                             && !string.IsNullOrWhiteSpace(current.Quote?.Content)
                ? new ReviewQueueRowViewModel(current)
                : null;

            LastStatusMessage = SanitizeStatus(_queue.LastStatusMessage);
            OnPropertyChanged(nameof(StatusMessage));
        }
        catch
        {
            IsAvailable = false;
            LastStatusMessage = "队列服务尚未就绪。请先启用插件并在 AI 审查设置中完成供应商配置。";
            OnPropertyChanged(nameof(StatusMessage));
        }
    }
    public void Approve(ReviewQueueItem? item)
    {
        if (item is null) return;
        try
        {
            EnsureQueueBound();
            _queue?.Approve(item);
            Refresh();
            SetActionStatus(item.State == ReviewItemState.Approved
                ? "已批准该内容，并加入 FIFO 播放队列。"
                : "该条目状态已变化，未执行批准。");
        }
        catch
        {
            SetActionStatus("批准操作失败。队列状态未确认，请刷新后重试。");
        }
    }

    public void Reject(ReviewQueueItem? item)
    {
        if (item is null) return;
        try
        {
            EnsureQueueBound();
            _queue?.Reject(item);
            Refresh();
            SetActionStatus(item.State == ReviewItemState.Rejected
                ? "已拒绝该内容。"
                : "该条目状态已变化，未执行拒绝。");
        }
        catch
        {
            SetActionStatus("拒绝操作失败。队列状态未确认，请刷新后重试。");
        }
    }

    public void ApproveAll()
    {
        try
        {
            EnsureQueueBound();
            int count = _queue?.ApproveAllPending("全量人工批准") ?? 0;
            Refresh();
            SetActionStatus(count > 0
                ? $"已批准全部待复核内容，共 {count} 条。"
                : "没有可批准的内容或状态已变化。");
        }
        catch
        {
            SetActionStatus("全部批准失败。请刷新队列后重试。");
        }
    }

    public void RejectAll()
    {
        try
        {
            EnsureQueueBound();
            int count = _queue?.RejectAllPending("全量人工拒绝") ?? 0;
            Refresh();
            SetActionStatus(count > 0 ? $"已拒绝全部待复核内容，共 {count} 条。" : "没有可拒绝的内容或状态已变化。");
        }
        catch
        {
            SetActionStatus("全部拒绝失败。请刷新队列后重试。");
        }
    }

    public void ApproveSelected()
    {
        try
        {
            EnsureQueueBound();
            ReviewQueueItem[] selected = PendingManualReview
                .Where(row => row.IsSelected)
                .Select(row => row.Item)
                .ToArray();
            if (selected.Length == 0)
            {
                SetActionStatus("没有选中任何待复核内容。请先勾选后再批准。");
                return;
            }

            int count = _queue?.ApproveSelected(selected, "批量人工批准") ?? 0;
            Refresh();
            SetActionStatus(count > 0 ? $"已批准选中内容，共 {count} 条。" : "没有可批准的内容或状态已变化。");
        }
        catch
        {
            SetActionStatus("批准选中内容失败。请刷新队列后重试。");
        }
    }

    public void RejectSelected()
    {
        try
        {
            EnsureQueueBound();
            ReviewQueueItem[] selected = PendingManualReview
                .Where(row => row.IsSelected)
                .Select(row => row.Item)
                .ToArray();
            if (selected.Length == 0)
            {
                SetActionStatus("没有选中任何待复核内容。请先勾选后再拒绝。");
                return;
            }

            int count = _queue?.RejectSelected(selected, "批量人工拒绝") ?? 0;
            Refresh();
            SetActionStatus(count > 0 ? $"已拒绝选中内容，共 {count} 条。" : "没有可拒绝的内容或状态已变化。");
        }
        catch
        {
            SetActionStatus("拒绝选中内容失败。请刷新队列后重试。");
        }
    }
    /// <summary>手动立即补充队列；使用最近一次组件刷新时的设置快照。</summary>
    public async Task RefreshNowAsync()
    {
        if (IsRefreshingNow) return;
        IsRefreshingNow = true;
        try
        {
            EnsureQueueBound();
            if (_queue is null)
            {
                SetActionStatus("人工复核队列尚未初始化。请启用插件后重试。");
                return;
            }

            if (_queue.IsRefilling)
            {
                SetActionStatus("正在补货，请稍候…");
                return;
            }

            SetActionStatus("正在补充队列…");
            int added = await _queue.RefreshNowAsync();
            Refresh();
            SetActionStatus(added > 0
                ? $"已补充 {added} 条内容。"
                : "本次补货没有新增内容，请查看队列状态或审计日志。");
        }
        catch
        {
            SetActionStatus("立即补货失败。请稍后重试。");
        }
        finally
        {
            IsRefreshingNow = false;
        }
    }

    void EnsureQueueBound()
    {
        _queue ??= PluginRuntime.Queue;
        if (_subscribed) return;
        _queue.QueueChanged += OnQueueChanged;
        _subscribed = true;
    }

    void OnQueueChanged(object? sender, EventArgs e)
    {
        if (!_attached) return;
        if (Interlocked.Exchange(ref _refreshScheduled, 1) == 1) return;

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _refreshScheduled, 0);
            if (_attached) Refresh(clearActionStatus: true);
        });
    }

    void ReplaceRows(
        ObservableCollection<ReviewQueueRowViewModel> target,
        IEnumerable<ReviewQueueItem> source,
        bool playback)
    {
        ReviewQueueRowViewModel.ReconcileRows(target, source.ToArray(), playback);
    }

    void SetActionStatus(string message)
    {
        _actionStatusMessage = message;
        OnPropertyChanged(nameof(StatusMessage));
    }

    /// <summary>使用系统文件管理器打开审计日志目录；通过 Win32 ShellExecuteW 打开，参数显式传递。</summary>
    public void OpenLogDirectory()
    {
        try
        {
            string directory = PluginRuntime.AuditLog.LogDirectory;
            Directory.CreateDirectory(directory);
            // 通过 ShellExecuteW 直接打开目录；参数显式传递，不经过 shell 命令行。
            ShellFolder.OpenInFileManager(directory);
            SetActionStatus($"已请求打开审计日志目录：{directory}");
        }
        catch
        {
            SetActionStatus("无法打开审计日志目录。请检查插件是否已初始化，或稍后重试。");
        }
    }

    static string SanitizeStatus(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return string.Empty;
        string text = message.Trim().Replace('\r', ' ').Replace('\n', ' ');
        return text.Length <= 240 ? text : $"{text[..240]}…";
    }
}

/// <summary>队列与历史行的只读展示包装。</summary>
public sealed class ReviewQueueRowViewModel : ObservableObject
{
    int _queuePosition;

    public ReviewQueueRowViewModel(ReviewQueueItem item)
    {
        Item = item;
    }

    public ReviewQueueItem Item { get; }

    public bool IsSelected
    {
        get => Item.IsSelected;
        set
        {
            if (Item.IsSelected == value) return;
            Item.IsSelected = value;
            OnPropertyChanged();
        }
    }

    public int QueuePosition
    {
        get => _queuePosition;
        set
        {
            if (!SetProperty(ref _queuePosition, value)) return;
            OnPropertyChanged(nameof(QueuePositionLabel));
        }
    }

    public string QueuePositionLabel => QueuePosition > 0 ? $"#{QueuePosition}" : string.Empty;

    /// <summary>
    /// 按 Id 增量对齐行集合，避免每次队列事件都把整页卡片清空重建，
    /// 从而保留滚动位置、选中状态和按钮焦点。
    /// </summary>
    public static void ReconcileRows(
        ObservableCollection<ReviewQueueRowViewModel> target,
        IReadOnlyList<ReviewQueueItem> snapshot,
        bool assignQueuePositions)
    {
        var desiredIds = new HashSet<string>(
            snapshot.Select(item => item.Id),
            StringComparer.OrdinalIgnoreCase);

        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!desiredIds.Contains(target[i].Item.Id))
                target.RemoveAt(i);
        }

        for (int i = 0; i < snapshot.Count; i++)
        {
            ReviewQueueItem item = snapshot[i];
            int existingIndex = -1;
            for (int j = i; j < target.Count; j++)
            {
                if (string.Equals(target[j].Item.Id, item.Id, StringComparison.OrdinalIgnoreCase))
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex < 0)
                target.Insert(i, new ReviewQueueRowViewModel(item));
            else if (existingIndex != i)
                target.Move(existingIndex, i);

            if (assignQueuePositions)
                target[i].QueuePosition = i + 1;
        }

        while (target.Count > snapshot.Count)
            target.RemoveAt(target.Count - 1);
    }

    public string DisplayContent => string.IsNullOrWhiteSpace(Item.Quote?.Content) ? "（无正文）" : Item.Quote.Content;

    public string DisplayAttribution => BuildAttribution(Item.Quote?.Author, Item.Quote?.Title);

    public bool HasAttribution => DisplayAttribution.Length > 0;

    /// <summary>来源未开启 AI 审查时显示「未开启」，否则显示 AI 风险等级。</summary>
    public string RiskLabel => Item.IsAiReviewSkipped ? "未开启" : Item.RiskLabel;

    /// <summary>风险标签颜色：低风险绿、中风险黄、高风险红，未开启与未知为灰。</summary>
    public IBrush RiskBrush => Item.IsAiReviewSkipped
        ? GrayBrush
        : Item.Risk switch
        {
            RiskLevel.Low => GreenBrush,
            RiskLevel.Medium => YellowBrush,
            RiskLevel.High => RedBrush,
            _ => GrayBrush
        };

    static readonly IBrush GreenBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43));
    static readonly IBrush YellowBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xD9, 0xA4, 0x06));
    static readonly IBrush RedBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38));
    static readonly IBrush GrayBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));

    public string ConfidenceLabel => Item.ConfidenceLabel;

    public string StateLabel => Item.StateLabel;

    public string CategoryLabel => Item.CategoryLabel;

    public string AiSummary => Item.IsAiReviewSkipped
        ? Item.AiReason ?? string.Empty
        : string.IsNullOrWhiteSpace(Item.AiReason) ? "AI 未提供理由" : Item.AiReason;

    public bool HasAiSummary => AiSummary.Length > 0;

    public string AiProviderLabel
    {
        get
        {
            string provider = Item.AiProviderName?.Trim() ?? string.Empty;
            string model = Item.AiModel?.Trim() ?? string.Empty;
            if (provider.Length == 0 && model.Length == 0) return "AI 供应商/模型：未记录";
            if (model.Length == 0) return $"AI：{provider}";
            if (provider.Length == 0) return $"AI：{model}";
            return $"AI：{provider} · {model}";
        }
    }

    public string SourceLabel => Item.SourceLabel;

    public string CreatedAtLabel => Item.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    static string BuildAttribution(string? author, string? title)
    {
        string cleanAuthor = author?.Trim() ?? string.Empty;
        string cleanTitle = title?.Trim() ?? string.Empty;
        if (cleanAuthor.Length == 0) return cleanTitle;
        if (cleanTitle.Length == 0) return cleanAuthor;
        return $"{cleanAuthor} · {cleanTitle}";
    }
}
