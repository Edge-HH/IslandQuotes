using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassIsland.Core.Abstractions.Controls;
using Motivation.Core;
using Motivation.ViewModels;

namespace Motivation.Components.QuoteAi;

public partial class QuoteAiSettings : ComponentBase<QuoteAiConfig>
{
    public QuoteAiSettings()
    {
        InitializeComponent();
    }

    public ObservableCollection<QuoteProviderSettingsItem> ProviderItems { get; } = [];

    void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        RefreshProviderItems();
    }

    void RefreshProviderItems()
    {
        ProviderItems.Clear();
        try
        {
            var providers = PluginRuntime.QuoteSources.Providers;
            Settings.EnsureProviderSettings(providers);
            foreach (var provider in providers)
            {
                if (Settings.ProviderSettings.TryGetValue(provider.Id, out var configuration))
                    ProviderItems.Add(new QuoteProviderSettingsItem(provider, configuration));
            }
            RuntimeStatusText.Text = ProviderItems.Count == 0
                ? "没有可用的第三方来源。请检查插件是否已正确初始化。"
                : "审计日志记录审查、拦截与播放事件。AI 失败时默认保持隐藏。";
        }
        catch
        {
            RuntimeStatusText.Text = "来源服务尚未就绪。请先启用插件并在插件设置中完成配置。";
        }
    }

    void OpenLogDirectoryButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            string directory = PluginRuntime.AuditLog.LogDirectory;
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            RuntimeStatusText.Text = $"已请求打开日志目录：{directory}";
        }
        catch
        {
            RuntimeStatusText.Text = "无法打开日志目录。请检查插件是否已初始化，或稍后重试。";
        }
    }
}
