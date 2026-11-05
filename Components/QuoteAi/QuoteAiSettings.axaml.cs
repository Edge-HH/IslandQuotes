using System.Collections.ObjectModel;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Helpers.UI;
using ClassIsland.Platforms.Abstraction;
using IslandQuotes.Core;
using IslandQuotes.Models;
using IslandQuotes.Services;
using IslandQuotes.ViewModels;

namespace IslandQuotes.Components.QuoteAi;

public partial class QuoteAiSettings : ComponentBase<QuoteAiConfig>
{
    const long MaxImportFileSizeBytes = 1024 * 1024;
    const int MaxImportEntries = 5000;
    const int MaxEntryLength = 200;

    public QuoteAiSettings()
    {
        InitializeComponent();
    }

    public ObservableCollection<QuoteProviderSettingsItem> ProviderItems { get; } = [];

    void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        RefreshProviderItems();
    }

    void RefreshProviderItems(CustomQuoteSource? expandedSource = null)
    {
        foreach (QuoteProviderSettingsItem item in ProviderItems) item.Detach();
        ProviderItems.Clear();

        List<IQuoteProvider> providers = [];
        try
        {
            providers.AddRange(PluginRuntime.QuoteSources.Providers);
            Settings.EnsureProviderSettings(providers);
        }
        catch
        {
            ShowStatus("来源服务尚未就绪。请先启用插件并在插件设置中完成配置。");
        }

        foreach (IQuoteProvider provider in providers)
        {
            if (Settings.ProviderSettings.TryGetValue(provider.Id, out var configuration))
                ProviderItems.Add(new QuoteProviderSettingsItem(provider, configuration));
        }

        IReadOnlyList<IQuoteProvider> customProviders = Settings.CreateCustomProviders();
        for (int i = 0; i < customProviders.Count; i++)
        {
            IQuoteProvider provider = customProviders[i];
            CustomQuoteSource source = Settings.CustomSources[i];
            if (!Settings.ProviderSettings.TryGetValue(provider.Id, out var configuration)) continue;
            ProviderItems.Add(new QuoteProviderSettingsItem(provider, configuration, source)
            {
                IsExpanded = ReferenceEquals(source, expandedSource)
            });
        }
    }

    void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusText.IsVisible = !string.IsNullOrWhiteSpace(message);
    }

    void AddCustomSourceButton_OnClick(object? sender, RoutedEventArgs e)
    {
        CustomQuoteSource source = Settings.AddCustomSource();
        RefreshProviderItems(source);
    }

    void RemoveCustomSourceButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not QuoteProviderSettingsItem { CustomSource: { } source }) return;
        Settings.RemoveCustomSource(source);
        RefreshProviderItems();
    }

    async void ImportCustomSourceButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not QuoteProviderSettingsItem { CustomSource: { } source }) return;

        string? path = await PickTextFileAsync("导入自定义内容");
        if (path is null) return;

        try
        {
            if (new FileInfo(path).Length > MaxImportFileSizeBytes)
            {
                ShowStatus($"导入失败：文件大小超过 {MaxImportFileSizeBytes / 1024} KB 限制");
                return;
            }

            List<string> entries = source.GetEntries().ToList();
            int before = entries.Count;
            foreach (string line in await File.ReadAllLinesAsync(path))
            {
                string entry = line.Trim();
                if (entry.Length > 0) entries.Add(entry);
            }

            if (entries.Count > MaxImportEntries)
            {
                ShowStatus($"导入失败：自定义内容条目数超过 {MaxImportEntries} 条限制");
                return;
            }

            source.Content = string.Join("\r\n", entries);
            ShowStatus($"已向「{source.Name}」导入 {entries.Count - before} 条内容。");
        }
        catch (Exception exception)
        {
            ShowStatus("导入失败：无法读取该文件。");
            Console.WriteLine(exception);
        }
    }

    async Task<string?> PickTextFileAsync(string title)
    {
        try
        {
            PopupHelper.DisableAllPopups();
            List<string> files = await PlatformServices.FilePickerService.OpenFilesPickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                FileTypeFilter = [FilePickerFileTypes.TextPlain]
            }, TopLevel.GetTopLevel(this) ?? AppBase.Current.GetRootWindow());
            return files.Count == 0 ? null : files[0];
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception);
            return null;
        }
        finally
        {
            PopupHelper.RestoreAllPopups();
        }
    }

    async void ImportIgnoreListButton_OnClick(object? sender, RoutedEventArgs e)
    {
        string? path = await PickTextFileAsync("导入排除列表");
        if (path is null) return;

        try
        {
            if (new FileInfo(path).Length > MaxImportFileSizeBytes)
            {
                ShowStatus($"导入失败：文件大小超过 {MaxImportFileSizeBytes / 1024} KB 限制");
                return;
            }

            List<string> entries = Settings.IgnoreListString
                .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            foreach (string line in await File.ReadAllLinesAsync(path))
            {
                string entry = line.Trim();
                if (entry.Length == 0) continue;
                if (entry.Length > MaxEntryLength) entry = entry[..MaxEntryLength];
                if (!entries.Contains(entry)) entries.Add(entry);
            }

            if (entries.Count > MaxImportEntries)
            {
                ShowStatus($"导入失败：排除列表条目数超过 {MaxImportEntries} 条限制");
                return;
            }

            Settings.IgnoreListString = string.Join("\r\n", entries);
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception);
        }
    }
}
