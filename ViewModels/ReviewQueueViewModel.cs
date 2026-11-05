using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
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
    string _queueStatusMessage = string.Empty;
    string _actionStatusMessage = string.Empty;
    ReviewQueueItem[] _historySnapshot = [];
    int _historyPageIndex;

    /// <summary>审查历史每页显示的条数。</summary>
    public const int HistoryPageSize = 10;

    public ReviewQueueViewModel()
    {
        _queueStatusMessage = "正在等待人工复核队列初始化。";
    }

    public ObservableCollection<ReviewQueueRowViewModel> PendingManualReview { get; } = [];

    public ObservableCollection<ReviewQueueRowViewModel> PlaybackQueue { get; } = [];

    public ObservableCollection<ReviewQueueRowViewModel> History { get; } = [];

    public int PendingCount
    {
        get => _pendingCount;
        private set
        {
            if (!SetProperty(ref _pendingCount, value)) return;
            OnPropertyChanged(nameof(HasPending));
        }
    }

    public int QueuedCount
    {
        get => _queuedCount;
        private set
        {
            if (!SetProperty(ref _queuedCount, value)) return;
            OnPropertyChanged(nameof(HasPlayback));
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
        private set => SetProperty(ref _isAvailable, value);
    }

    public bool HasPending => PendingCount > 0;

    public bool HasPlayback => QueuedCount > 0;

    public bool HasHistory => History.Count > 0;

    public int HistoryTotalCount => _historySnapshot.Length;

    public int HistoryPageCount => Math.Max(1, (_historySnapshot.Length + HistoryPageSize - 1) / HistoryPageSize);

    public bool HasMultipleHistoryPages => HistoryPageCount > 1;

    public bool CanGoToPreviousHistoryPage => _historyPageIndex > 0;

    public bool CanGoToNextHistoryPage => _historyPageIndex < HistoryPageCount - 1;

    public string HistoryPageLabel => $"第 {_historyPageIndex + 1} / {HistoryPageCount} 页，共 {HistoryTotalCount} 条";

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

    /// <summary>手动刷新统计和三个 ObservableCollection 的界面快照。</summary>
    public void Refresh()
    {
        try
        {
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
            // 服务端按决定顺序追加历史，倒序后即为新的在前。
            _historySnapshot = _queue.History.Reverse().ToArray();
            ShowHistoryPage(_historyPageIndex);

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
            SetActionStatus("已批准选中的待复核内容，并加入 FIFO 播放队列。");
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
            SetActionStatus(string.Empty);
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
            SetActionStatus($"已批准全部待复核内容，共 {count} 条。");
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
            _queue?.RejectAllPending("全量人工拒绝");
            Refresh();
            SetActionStatus(string.Empty);
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
            SetActionStatus($"已批准选中内容，共 {count} 条。");
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

            _queue?.RejectSelected(selected, "批量人工拒绝");
            Refresh();
            SetActionStatus(string.Empty);
        }
        catch
        {
            SetActionStatus("拒绝选中内容失败。请刷新队列后重试。");
        }
    }
    public void GoToPreviousHistoryPage() => ShowHistoryPage(_historyPageIndex - 1);

    public void GoToNextHistoryPage() => ShowHistoryPage(_historyPageIndex + 1);

    void ShowHistoryPage(int pageIndex)
    {
        _historyPageIndex = Math.Clamp(pageIndex, 0, HistoryPageCount - 1);
        ReplaceRows(
            History,
            _historySnapshot.Skip(_historyPageIndex * HistoryPageSize).Take(HistoryPageSize),
            playback: false);

        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HistoryTotalCount));
        OnPropertyChanged(nameof(HistoryPageCount));
        OnPropertyChanged(nameof(HasMultipleHistoryPages));
        OnPropertyChanged(nameof(CanGoToPreviousHistoryPage));
        OnPropertyChanged(nameof(CanGoToNextHistoryPage));
        OnPropertyChanged(nameof(HistoryPageLabel));
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
            if (_attached) Refresh();
        });
    }

    void ReplaceRows(
        ObservableCollection<ReviewQueueRowViewModel> target,
        IEnumerable<ReviewQueueItem> source,
        bool playback)
    {
        ReviewQueueItem[] snapshot = source.ToArray();
        target.Clear();
        for (int index = 0; index < snapshot.Length; index++)
        {
            var row = new ReviewQueueRowViewModel(snapshot[index]);
            if (playback) row.QueuePosition = index + 1;
            target.Add(row);
        }
    }

    void SetActionStatus(string message)
    {
        _actionStatusMessage = message;
        OnPropertyChanged(nameof(StatusMessage));
    }

    /// <summary>使用系统文件管理器打开审计日志目录；路径通过 explorer.exe 的参数列表传递。</summary>
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

/// <summary>
/// 队列行的只读展示包装。所有高风险或已拦截项目都会在这里统一隐藏正文与署名，
/// 防止组件和历史页面意外渲染第三方高风险引用。
/// </summary>
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

    public string DisplayContent => MustMask
        ? "高风险内容已隐藏"
        : string.IsNullOrWhiteSpace(Item.Quote?.Content) ? "（无正文）" : Item.Quote.Content;

    public string DisplayAttribution => MustMask
        ? "引用正文与署名已隐藏"
        : BuildAttribution(Item.Quote?.Author, Item.Quote?.Title);

    public string RiskLabel => Item.RiskLabel;

    public string ConfidenceLabel => Item.ConfidenceLabel;

    public string StateLabel => Item.StateLabel;

    public string CategoryLabel => Item.CategoryLabel;

    public string AiSummary => string.IsNullOrWhiteSpace(Item.AiReason)
        ? $"{Item.RiskLabel} · AI 未提供理由"
        : $"{Item.RiskLabel} · {Item.AiReason}";

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

    public bool MustMask => Item.Risk == RiskLevel.High || Item.State == ReviewItemState.Blocked;

    static string BuildAttribution(string? author, string? title)
    {
        string cleanAuthor = author?.Trim() ?? string.Empty;
        string cleanTitle = title?.Trim() ?? string.Empty;
        if (cleanAuthor.Length == 0) return cleanTitle;
        if (cleanTitle.Length == 0) return cleanAuthor;
        return $"{cleanAuthor} · {cleanTitle}";
    }
}
