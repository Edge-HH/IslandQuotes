using Avalonia.Controls;
using Motivation.Models;
using Motivation.Services;

namespace Motivation.ViewModels;

/// <summary>组件设置页中一行来源配置。</summary>
public sealed class QuoteProviderSettingsItem
{
    public QuoteProviderSettingsItem(IQuoteProvider provider, QuoteProviderConfig configuration)
    {
        Id = provider.Id;
        DisplayName = provider.DisplayName;
        Description = provider.Description;
        Configuration = configuration;
        SettingsControl = (provider as IQuoteProviderSettingsFactory)?.CreateSettingsControl(configuration);
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public QuoteProviderConfig Configuration { get; }

    public Control? SettingsControl { get; }

    public bool HasSettings => SettingsControl is not null;
}
