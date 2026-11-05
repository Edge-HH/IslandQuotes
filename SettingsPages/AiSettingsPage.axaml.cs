using Avalonia;
using Avalonia.Interactivity;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using IslandQuotes.ViewModels;

namespace IslandQuotes.SettingsPages;

[HidePageTitle]
[Group("islandquotes")]
[SettingsPageInfo("islandquotes.ai-review", "AI审查设置", "\uE21D", "\uE21D")]
public partial class AiSettingsPage : SettingsPageBase
{
    readonly AiSettingsViewModel _viewModel;

    public AiSettingsPage()
    {
        _viewModel = new AiSettingsViewModel();
        DataContext = _viewModel;
        InitializeComponent();
    }

    void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_viewModel.Profiles.Count == 0) _viewModel.Reload();
    }

    async void SaveButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await _viewModel.SaveAsync();
    }

    async void TestButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await _viewModel.TestAsync();
    }

    async void FetchModelsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await _viewModel.FetchModelsAsync();
    }

    void OpenLogDirectoryButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _viewModel.OpenLogDirectory();
    }
}
