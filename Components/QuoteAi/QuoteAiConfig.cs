using System.Text.Json.Serialization;
using Motivation.Core;
using Motivation.Models;
using Motivation.Services;

namespace Motivation.Components.QuoteAi;

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

    public string IgnoreListString { get; set; } = string.Empty;

    public DateTime LastUpdate { get; set; } = DateTime.Today;

    public int LengthLimitation { get; set; }

    public TimeSpan UpdateTimeGap { get; set; } = TimeSpan.FromSeconds(30);

    [JsonIgnore]
    public double UpdateTimeGapSeconds
    {
        get => UpdateTimeGap.TotalSeconds;
        set
        {
            double seconds = double.IsFinite(value) ? Math.Max(0, value) : 30;
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

    int _batchSize = 3;

    /// <summary>缺少可播放内容时一次拉取并审查的候选数量。</summary>
    public int BatchSize
    {
        get => _batchSize;
        set
        {
            int next = Math.Clamp(value, 1, 10);
            if (_batchSize == next) return;
            _batchSize = next;
            OnPropertyChanged();
        }
    }

    int _maxBufferedItems = 12;

    /// <summary>待复核和已排队内容的总缓冲上限。</summary>
    public int MaxBufferedItems
    {
        get => _maxBufferedItems;
        set
        {
            int next = Math.Clamp(value, 1, 100);
            if (_maxBufferedItems == next) return;
            _maxBufferedItems = next;
            OnPropertyChanged();
        }
    }

    public bool ShowQueueStatus { get; set; } = true;

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
                Weight = provider.DefaultWeight
            };
            changed = true;
        }

        if (changed) OnPropertyChanged(nameof(ProviderSettings));
    }
}
