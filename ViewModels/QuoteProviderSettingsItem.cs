using System.ComponentModel;
using Avalonia.Controls;
using IslandQuotes.Core;
using IslandQuotes.Models;
using IslandQuotes.Services;

namespace IslandQuotes.ViewModels;

/// <summary>组件设置页中一行来源配置。</summary>
public sealed class QuoteProviderSettingsItem : ObservableObject
{
    readonly IQuoteProvider _provider;

    public QuoteProviderSettingsItem(
        IQuoteProvider provider,
        QuoteProviderConfig configuration,
        CustomQuoteSource? customSource = null)
    {
        _provider = provider;
        Id = provider.Id;
        Configuration = configuration;
        CustomSource = customSource;
        SettingsControl = (provider as IQuoteProviderSettingsFactory)?.CreateSettingsControl(configuration);
        if (customSource is not null) customSource.PropertyChanged += OnCustomSourcePropertyChanged;
    }

    public string Id { get; }

    public string DisplayName => _provider.DisplayName;

    public string Description => CustomSource is null
        ? _provider.Description
        : $"{_provider.Description}，当前 {CustomSource.EntryCount} 条";

    public QuoteProviderConfig Configuration { get; }

    public Control? SettingsControl { get; }

    public bool HasSettings => SettingsControl is not null;

    public CustomQuoteSource? CustomSource { get; }

    public bool IsCustom => CustomSource is not null;

    bool _isExpanded;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public void Detach()
    {
        if (CustomSource is not null) CustomSource.PropertyChanged -= OnCustomSourcePropertyChanged;
    }

    void OnCustomSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CustomQuoteSource.Name)) OnPropertyChanged(nameof(DisplayName));
        else if (e.PropertyName == nameof(CustomQuoteSource.EntryCount)) OnPropertyChanged(nameof(Description));
    }
}
