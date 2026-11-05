using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using Motivation.Models;
using Motivation.ViewModels;

namespace Motivation.SettingsPages;

[HidePageTitle]
[Group("motivation")]
[SettingsPageInfo("motivation.review-queue", "人工复核与播放队列", "\uE8FD", "\uE8FD")]
public partial class ReviewQueuePage : SettingsPageBase
{
    readonly ReviewQueueViewModel _viewModel;

    public ReviewQueuePage()
    {
        _viewModel = new ReviewQueueViewModel();
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

    void ApproveButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ReviewQueueItem item) _viewModel.Approve(item);
    }

    void RejectButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ReviewQueueItem item) _viewModel.Reject(item);
    }

    void ApproveSelectedButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _viewModel.ApproveSelected();
    }

    void RejectSelectedButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _viewModel.RejectSelected();
    }

    void ApproveAllButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _viewModel.ApproveAll();
    }

    void RejectAllButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _viewModel.RejectAll();
    }

    void OpenLogDirectoryButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _viewModel.OpenLogDirectory();
    }
}
