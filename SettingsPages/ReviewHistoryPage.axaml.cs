using Avalonia;
using Avalonia.Interactivity;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using IslandQuotes.Core;
using IslandQuotes.ViewModels;

namespace IslandQuotes.SettingsPages;

[HidePageTitle]
[Group("islandquotes")]
[SettingsPageInfo("islandquotes.review-history", "历史记录", "\uE990", "\uE98F")]
public partial class ReviewHistoryPage : SettingsPageBase
{
    void InitializeComponent()
    {
        RuntimeXaml.TryLoad(this);
    }

    readonly ReviewHistoryViewModel _viewModel;

    public ReviewHistoryPage()
    {
        _viewModel = new ReviewHistoryViewModel();
        DataContext = _viewModel;
        InitializeComponent();
    }

    void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _viewModel.Attach();
    }

    void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _viewModel.Detach();
    }

    void RefreshButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _viewModel.Refresh();
    }

    void ClearFiltersButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _viewModel.ClearFilters();
    }

    void PreviousPageButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _viewModel.GoToPreviousPage();
    }

    void NextPageButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _viewModel.GoToNextPage();
    }
}
