using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using IslandQuotes.Core;
using IslandQuotes.Models;
using IslandQuotes.Services;

namespace IslandQuotes.Components.QuoteAi;

/// <summary>
/// 每日一言组件的本地设置。AI 凭据与全局审查策略保存在插件设置中，不随组件设置持久化。
/// </summary>
public sealed class QuoteAiConfig : ObservableObject
{
    Dictionary<string, QuoteProviderConfig> _providerSettings =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, QuoteProviderConfig> ProviderSettings
    {
        get => _providerSettings;
        set => SetProperty(
            ref _providerSettings,
            value is null
                ? new Dictionary<string, QuoteProviderConfig>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, QuoteProviderConfig>(value, StringComparer.OrdinalIgnoreCase));
    }

    ObservableCollection<CustomQuoteSource> _customSources = [];

    /// <summary>用户添加的自定义来源；启用状态、权重与 AI 审查开关保存在 <see cref="ProviderSettings"/> 中。</summary>
    public ObservableCollection<CustomQuoteSource> CustomSources
    {
        get => _customSources;
        set => SetProperty(ref _customSources, value ?? []);
    }

    string _ignoreListString = string.Empty;

    public string IgnoreListString
    {
        get => _ignoreListString;
        set => SetProperty(ref _ignoreListString, value ?? string.Empty);
    }

    public DateTime LastUpdate { get; set; } = DateTime.Today;

    public int LengthLimitation { get; set; }

    public TimeSpan UpdateTimeGap { get; set; } = TimeSpan.FromSeconds(30);

    [JsonIgnore]
    public double UpdateTimeGapSeconds
    {
        get => UpdateTimeGap.TotalSeconds;
        set
        {
            double seconds = double.IsFinite(value) ? Math.Clamp(value, 0, 864000) : 30;
            TimeSpan next = TimeSpan.FromSeconds(seconds);
            if (UpdateTimeGap == next) return;
            UpdateTimeGap = next;
            OnPropertyChanged();
        }
    }

    public bool IsAnimationEnabled { get; set; } = true;

    public bool IsSwapAnimationEnabled { get; set; }

    public bool IsAuthorShowEnabled { get; set; }

    public bool IsTitleShowEnabled { get; set; }

    int _attributesShowingInterval = 3;

    public int AttributesShowingInterval
    {
        get => _attributesShowingInterval;
        set
        {
            int next = Math.Max(0, value);
            if (_attributesShowingInterval == next) return;
            _attributesShowingInterval = next;
            OnPropertyChanged();
        }
    }

    AttributesDisplayRule _attributesRule = AttributesDisplayRule.Sametime;

    public AttributesDisplayRule AttributesRule
    {
        get => _attributesRule;
        set
        {
            if (_attributesRule == value) return;
            _attributesRule = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AttributesRuleIndex));
            OnPropertyChanged(nameof(IsAttributesSeparate));
        }
    }

    /// <summary>供 ComboBox 的 SelectedIndex 绑定，0 为同时展示，1 为分开展示。</summary>
    [JsonIgnore]
    public int AttributesRuleIndex
    {
        get => AttributesRule == AttributesDisplayRule.Separate ? 1 : 0;
        set => AttributesRule = value == 1
            ? AttributesDisplayRule.Separate
            : AttributesDisplayRule.Sametime;
    }

    [JsonIgnore]
    public bool IsAttributesSeparate => AttributesRule == AttributesDisplayRule.Separate;

    public enum AttributesDisplayRule
    {
        Sametime = 0,
        Separate = 1
    }

    /// <summary>
    /// 为每个可用来源补齐默认配置，同时保留用户已有的启用状态、权重和来源选项。
    /// </summary>
    public void EnsureProviderSettings(IEnumerable<IQuoteProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        bool changed = false;
        foreach (IQuoteProvider provider in providers)
        {
            if (string.IsNullOrWhiteSpace(provider.Id) || ProviderSettings.ContainsKey(provider.Id))
                continue;

            ProviderSettings[provider.Id] = new QuoteProviderConfig
            {
                IsEnabled = provider.IsEnabledByDefault,
                Weight = provider.DefaultWeight,
                IsAiReviewEnabled = provider.IsAiReviewEnabledByDefault
            };
            changed = true;
        }

        if (changed) OnPropertyChanged(nameof(ProviderSettings));
    }

    /// <summary>为当前的自定义来源创建来源实例，并补齐它们的默认配置。</summary>
    public IReadOnlyList<IQuoteProvider> CreateCustomProviders()
    {
        IQuoteProvider[] providers = CustomSources
            .Where(source => source is not null)
            .Select(source => (IQuoteProvider)new CustomQuoteProvider(source))
            .ToArray();
        EnsureProviderSettings(providers);
        return providers;
    }

    public CustomQuoteSource AddCustomSource()
    {
        var source = new CustomQuoteSource { Name = $"自定义来源 {CustomSources.Count + 1}" };
        EnsureProviderSettings([new CustomQuoteProvider(source)]);
        CustomSources.Add(source);
        OnPropertyChanged(nameof(CustomSources));
        return source;
    }

    public void RemoveCustomSource(CustomQuoteSource source)
    {
        if (!CustomSources.Remove(source)) return;
        ProviderSettings.Remove(source.ProviderId);
        OnPropertiesChanged(nameof(ProviderSettings), nameof(CustomSources));
    }
}
