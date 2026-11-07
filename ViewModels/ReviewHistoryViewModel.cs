using System.Collections.ObjectModel;
using Avalonia.Threading;
using IslandQuotes.Core;
using IslandQuotes.Models;
using IslandQuotes.Services;

namespace IslandQuotes.ViewModels;

/// <summary>历史记录页的风险筛选项。</summary>
public enum HistoryRiskFilter
{
    All = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    NotEnabled = 4,
    Unknown = 5
}

/// <summary>历史记录页的状态筛选项。</summary>
public enum HistoryStateFilter
{
    All = 0,
    Displayed = 1,
    Blocked = 2,
    Rejected = 3
}

/// <summary>审查历史记录页的视图模型：按风险、状态与关键词筛选并分页。</summary>
public sealed class ReviewHistoryViewModel : ObservableObject
{
    /// <summary>每页显示的条数。</summary>
    public const int PageSize = 10;

    ReviewQueueService? _queue;
    bool _subscribed;
    bool _attached;
    int _refreshScheduled;
    ReviewQueueItem[] _filtered = [];
    int _pageIndex;
    int _totalCount;
    string _searchText = string.Empty;
    LabeledValue<HistoryRiskFilter> _riskFilter;
    LabeledValue<HistoryStateFilter> _stateFilter;
    bool _isAvailable;

    public ReviewHistoryViewModel()
    {
        RiskFilterOptions =
        [
            new(HistoryRiskFilter.All, "全部风险"),
            new(HistoryRiskFilter.Low, "低风险"),
            new(HistoryRiskFilter.Medium, "中风险"),
            new(HistoryRiskFilter.High, "高风险"),
            new(HistoryRiskFilter.NotEnabled, "未开启"),
            new(HistoryRiskFilter.Unknown, "风险未知")
        ];
        StateFilterOptions =
        [
            new(HistoryStateFilter.All, "全部状态"),
            new(HistoryStateFilter.Displayed, "已播放"),
            new(HistoryStateFilter.Blocked, "已拦截"),
            new(HistoryStateFilter.Rejected, "人工拒绝")
        ];
        _riskFilter = RiskFilterOptions[0];
        _stateFilter = StateFilterOptions[0];
    }

    public IReadOnlyList<LabeledValue<HistoryRiskFilter>> RiskFilterOptions { get; }

    public IReadOnlyList<LabeledValue<HistoryStateFilter>> StateFilterOptions { get; }

    public ObservableCollection<ReviewQueueRowViewModel> Items { get; } = [];

    public LabeledValue<HistoryRiskFilter> RiskFilter
    {
        get => _riskFilter;
        set
        {
            if (value is null || !SetProperty(ref _riskFilter, value)) return;
            ApplyFilter(resetPage: true);
        }
    }

    public LabeledValue<HistoryStateFilter> StateFilter
    {
        get => _stateFilter;
        set
        {
            if (value is null || !SetProperty(ref _stateFilter, value)) return;
            ApplyFilter(resetPage: true);
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? string.Empty)) return;
            ApplyFilter(resetPage: true);
        }
    }

    public bool IsAvailable
    {
        get => _isAvailable;
        private set => SetProperty(ref _isAvailable, value);
    }

    public bool HasItems => Items.Count > 0;

    public bool IsEmpty => Items.Count == 0;

    public string EmptyMessage => _totalCount == 0 ? "暂无历史记录" : "没有符合筛选条件的记录";

    public int PageCount => Math.Max(1, (_filtered.Length + PageSize - 1) / PageSize);

    public bool HasMultiplePages => PageCount > 1;

    public bool CanGoToPreviousPage => _pageIndex > 0;

    public bool CanGoToNextPage => _pageIndex < PageCount - 1;

    public string PageLabel => $"第 {_pageIndex + 1} / {PageCount} 页";

    public string CountLabel => _filtered.Length == _totalCount
        ? $"共 {_totalCount} 条"
        : $"筛选出 {_filtered.Length} / {_totalCount} 条";

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

    public void Refresh()
    {
        try
        {
            _queue ??= PluginRuntime.Queue;
            if (!_subscribed)
            {
                _queue.QueueChanged += OnQueueChanged;
                _subscribed = true;
            }

            IsAvailable = true;
            ApplyFilter(resetPage: false);
        }
        catch
        {
            IsAvailable = false;
            _filtered = [];
            _totalCount = 0;
            ShowPage(0);
        }
    }

    public void ClearFilters()
    {
        _searchText = string.Empty;
        _riskFilter = RiskFilterOptions[0];
        _stateFilter = StateFilterOptions[0];
        OnPropertiesChanged(nameof(SearchText), nameof(RiskFilter), nameof(StateFilter));
        ApplyFilter(resetPage: true);
    }

    public void GoToPreviousPage() => ShowPage(_pageIndex - 1);

    public void GoToNextPage() => ShowPage(_pageIndex + 1);

    void ApplyFilter(bool resetPage)
    {
        // 服务端按决定顺序追加历史，倒序后即为新的在前。
        ReviewQueueItem[] all = _queue?.History.Reverse().ToArray() ?? [];
        _totalCount = all.Length;

        string keyword = SearchText.Trim();
        _filtered = all
            .Where(item => MatchesRisk(item, RiskFilter.Value))
            .Where(item => MatchesState(item, StateFilter.Value))
            .Where(item => keyword.Length == 0 || MatchesKeyword(item, keyword))
            .ToArray();

        ShowPage(resetPage ? 0 : _pageIndex);
    }

    void ShowPage(int pageIndex)
    {
        _pageIndex = Math.Clamp(pageIndex, 0, PageCount - 1);
        ReviewQueueItem[] pageItems = _filtered
            .Skip(_pageIndex * PageSize)
            .Take(PageSize)
            .ToArray();
        ReviewQueueRowViewModel.ReconcileRows(Items, pageItems, assignQueuePositions: false);

        OnPropertiesChanged(
            nameof(HasItems), nameof(IsEmpty), nameof(EmptyMessage), nameof(PageCount),
            nameof(HasMultiplePages), nameof(CanGoToPreviousPage), nameof(CanGoToNextPage),
            nameof(PageLabel), nameof(CountLabel));
    }

    static bool MatchesRisk(ReviewQueueItem item, HistoryRiskFilter filter) => filter switch
    {
        HistoryRiskFilter.All => true,
        HistoryRiskFilter.NotEnabled => item.IsAiReviewSkipped,
        HistoryRiskFilter.Low => !item.IsAiReviewSkipped && item.Risk == RiskLevel.Low,
        HistoryRiskFilter.Medium => !item.IsAiReviewSkipped && item.Risk == RiskLevel.Medium,
        HistoryRiskFilter.High => !item.IsAiReviewSkipped && item.Risk == RiskLevel.High,
        HistoryRiskFilter.Unknown => !item.IsAiReviewSkipped && item.Risk == RiskLevel.Unknown,
        _ => true
    };

    static bool MatchesState(ReviewQueueItem item, HistoryStateFilter filter) => filter switch
    {
        HistoryStateFilter.Displayed => item.State == ReviewItemState.Displayed,
        HistoryStateFilter.Blocked => item.State == ReviewItemState.Blocked,
        HistoryStateFilter.Rejected => item.State == ReviewItemState.Rejected,
        _ => true
    };

    static bool MatchesKeyword(ReviewQueueItem item, string keyword)
    {
        return Contains(item.Quote?.Content)
               || Contains(item.Quote?.Author)
               || Contains(item.Quote?.Title)
               || Contains(item.Quote?.ProviderName)
               || Contains(item.AiReason)
               || item.Categories.Any(category => Contains(category));

        bool Contains(string? value) =>
            !string.IsNullOrEmpty(value) && value.Contains(keyword, StringComparison.OrdinalIgnoreCase);
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
}
