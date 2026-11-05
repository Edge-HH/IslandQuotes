using Avalonia;
using Avalonia.Interactivity;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using Motivation.ViewModels;

namespace Motivation.SettingsPages;

[HidePageTitle]
[Group("motivation")]
[SettingsPageInfo("motivation.ai-review", "AI 审查与供应商", "\uE9D9", "\uE9D9")]
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

    void OpenLogDirectoryButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _viewModel.OpenLogDirectory();
    }
}
