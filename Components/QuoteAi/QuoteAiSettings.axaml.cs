// IslandQuotes - Copyright (C) 2026 Edge-HH
// 本文件改编自 ExtraIsland（https://github.com/LiPolymer/ExtraIsland），
// Copyright (C) 2024-2025 LiPolymer，以 GNU AGPLv3 授权。
// 原始文件：ExtraIsland/Components/Rhesis/RhesisSettings.axaml.cs
// 修改：2026 年由 Edge-HH 修改，详见 NOTICE.md 与 git 历史。
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.ObjectModel;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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
    TextBlock StatusText = null!;
    ComboBox FontFamilyComboBox = null!;

    void InitializeComponent()
    {
        if (!RuntimeXaml.TryLoad(this))
        {
            StatusText = new TextBlock();
            FontFamilyComboBox = new ComboBox();
            return;
        }

        StatusText = this.FindControl<TextBlock>("StatusText")
            ?? throw new InvalidOperationException("XAML 中缺少控件 StatusText");
        FontFamilyComboBox = this.FindControl<ComboBox>("FontFamilyComboBox")
            ?? new ComboBox();

        // 原先用 {Binding RelativeSource={RelativeSource FindAncestor, AncestorType=local:QuoteAiSettings}} 绑定，
        // 但宿主把插件放在独立的 AssemblyLoadContext 中，运行时 XAML 编译器在绑定里解析不到插件自身的类型。
        // 直接在代码里把界面的 DataContext 设为本控件，效果相同。
        Control root = this.FindControl<ScrollViewer>("RootScrollViewer")
            ?? throw new InvalidOperationException("XAML 中缺少控件 RootScrollViewer");
        root.DataContext = this;
    }

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
        try
        {
            RefreshProviderItems();
        }
        catch (Exception exception)
        {
            RuntimeXaml.ReportError(this, "加载来源列表", exception);
        }

        // 枚举系统字体可能较慢，放到页面加载完成后再做，避免打开设置页卡顿。
        Dispatcher.UIThread.Post(PopulateFontFamilies);
    }

    /// <summary>系统字体列表只枚举一次；失败时保留可手动输入的下拉框。</summary>
    static readonly Lazy<IReadOnlyList<string>> SystemFontNames = new(LoadSystemFontNames);

    /// <summary>把系统字体填进可编辑下拉框；枚举失败时仍允许手动输入字体名。</summary>
    void PopulateFontFamilies()
    {
        FontFamilyComboBox.ItemsSource = SystemFontNames.Value;
    }

    static IReadOnlyList<string> LoadSystemFontNames()
    {
        try
        {
            return FontManager.Current.SystemFonts
                .Select(font => font.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception exception)
        {
            // 字体枚举依赖平台实现；失败不影响其余设置项。
            Console.WriteLine(exception);
            return [];
        }
    }

    void MainColorPresetButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string value)
            Settings.MainTextColor = value;
    }

    void ResetFontFamilyButton_OnClick(object? sender, RoutedEventArgs e)
    {
        // 只改设置源；ComboBox.Text 由 TwoWay 绑定同步，直接赋值会覆盖绑定。
        Settings.MainFontFamilyName = string.Empty;
    }

    /// <summary>可编辑 ComboBox 在不同 Avalonia 版本下对 Text 的提交时机不一致，失焦时显式提交一次。</summary>
    void FontFamilyComboBox_OnLostFocus(object? sender, RoutedEventArgs e)
    {
        Settings.MainFontFamilyName = FontFamilyComboBox.Text ?? string.Empty;
    }

    void FontFamilyComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (FontFamilyComboBox.SelectedItem is string name)
            Settings.MainFontFamilyName = name;
    }

    void AttributionColorPresetButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string value)
            Settings.AttributionTextColor = value;
    }

    void ShimmerColorPresetButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string value)
            Settings.ShimmerColor = value;
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
            // 不用 AppBase.GetRootWindow()：其返回类型在 2.1.1.1 由 Window 改为 TopLevel，跨版本会 MissingMethodException。
            }, TopLevel.GetTopLevel(this) ?? AppBase.Current.MainWindow!);
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
