// IslandQuotes - Copyright (C) 2026 Edge-HH
// 本文件改编自 ExtraIsland（https://github.com/LiPolymer/ExtraIsland），
// Copyright (C) 2024-2025 LiPolymer，以 GNU AGPLv3 授权。
// 原始文件：ExtraIsland/Components/Rhesis/RhesisConfig.cs
// 修改：2026 年由 Edge-HH 修改，详见 NOTICE.md 与 git 历史。
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Avalonia.Media;
using IslandQuotes.Core;
using IslandQuotes.Models;
using IslandQuotes.Services;

namespace IslandQuotes.Components.QuoteAi;

/// <summary>内容切换动画模式。数值会写入组件设置，已有数值不可调整。</summary>
public enum ContentAnimationMode
{
    /// <summary>不播放切换动画，内容立即替换。</summary>
    None = 0,

    /// <summary>淡出淡入（默认）。</summary>
    Fade = 1,

    /// <summary>旧内容向上离开，新内容从下方进入。</summary>
    SlideUp = 2,

    /// <summary>旧内容向下离开，新内容从上方进入。</summary>
    SlideDown = 3,

    /// <summary>旧内容向左离开，新内容从右侧进入。</summary>
    SlideLeft = 4,

    /// <summary>旧内容向右离开，新内容从左侧进入。</summary>
    SlideRight = 5,

    /// <summary>新内容淡入后，一道高光从左向右扫过文字。</summary>
    Shimmer = 6
}

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

    public int LengthLimitation { get; set; }

    TimeSpan _updateTimeGap = TimeSpan.FromSeconds(30);

    /// <summary>自动刷新间隔；持久化字段。读取旧配置时也会经过规范化，防止异常值让刷新长期停摆。</summary>
    public TimeSpan UpdateTimeGap
    {
        get => _updateTimeGap;
        set
        {
            TimeSpan next = NormalizeUpdateGap(value);
            if (_updateTimeGap == next) return;
            _updateTimeGap = next;
            OnPropertiesChanged(nameof(UpdateTimeGap), nameof(UpdateTimeGapSeconds));
        }
    }

    [JsonIgnore]
    public double UpdateTimeGapSeconds
    {
        get => _updateTimeGap.TotalSeconds;
        set
        {
            double seconds = double.IsFinite(value) ? value : 30;
            if (seconds < 0) seconds = 30;
            UpdateTimeGap = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 86400));
        }
    }

    /// <summary>负值/非有限值回退为默认 30 秒；上限收紧到一天，避免误输入导致「十天后才刷新」。</summary>
    static TimeSpan NormalizeUpdateGap(TimeSpan value)
    {
        double seconds = value.TotalSeconds;
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 30;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 86400));
    }

    /// <summary>旧版「启用动画」开关；仅为读取旧设置保留，新设置以 <see cref="AnimationMode"/> 为准。</summary>
    public bool IsAnimationEnabled { get; set; } = true;

    /// <summary>旧版「启用翻页动画」开关；仅为读取旧设置保留，对应向下翻页。</summary>
    public bool IsSwapAnimationEnabled { get; set; }

    ContentAnimationMode? _animationMode;

    /// <summary>
    /// 内容切换动画。为 null 表示设置来自旧版本，此时按 <see cref="IsAnimationEnabled"/> /
    /// <see cref="IsSwapAnimationEnabled"/> 推导，保证升级后行为不变。
    /// </summary>
    public ContentAnimationMode? AnimationMode
    {
        get => _animationMode;
        set
        {
            if (_animationMode == value) return;
            _animationMode = value;
            OnPropertiesChanged(
                nameof(AnimationMode),
                nameof(EffectiveAnimationMode),
                nameof(ContentAnimationModeIndex));
        }
    }

    /// <summary>实际生效的动画模式；无效数值回退为淡入淡出。</summary>
    [JsonIgnore]
    public ContentAnimationMode EffectiveAnimationMode
    {
        get
        {
            ContentAnimationMode mode = AnimationMode ?? (!IsAnimationEnabled
                ? ContentAnimationMode.None
                : IsSwapAnimationEnabled
                    ? ContentAnimationMode.SlideDown
                    : ContentAnimationMode.Fade);
            return Enum.IsDefined(mode) ? mode : ContentAnimationMode.Fade;
        }
    }

    /// <summary>供 ComboBox 的 SelectedIndex 绑定。</summary>
    [JsonIgnore]
    public int ContentAnimationModeIndex
    {
        get => (int)EffectiveAnimationMode;
        set
        {
            // ComboBox 初始化/清空时可能回写 -1；不能把它当成「无动画」写进设置。
            if (value < (int)ContentAnimationMode.None || value > (int)ContentAnimationMode.Shimmer)
                return;

            ContentAnimationMode mode = (ContentAnimationMode)value;
            AnimationMode = mode;

            // 同步旧字段，使旧版本插件读取新设置时仍能得到接近的效果。
            // 旧版「翻页」是旧内容下滑、新内容从上方进入，等价于向下翻页。
            IsAnimationEnabled = mode != ContentAnimationMode.None;
            IsSwapAnimationEnabled = mode == ContentAnimationMode.SlideDown;
            OnPropertiesChanged(nameof(EffectiveAnimationMode), nameof(ContentAnimationModeIndex));
        }
    }

    string _mainFontFamilyName = string.Empty;

    /// <summary>主文字字体；留空继承主界面字体。</summary>
    public string MainFontFamilyName
    {
        get => _mainFontFamilyName;
        set => SetProperty(ref _mainFontFamilyName, (value ?? string.Empty).Trim());
    }

    double _mainFontSize;

    /// <summary>主文字字号；0 表示继承主界面字号。</summary>
    public double MainFontSize
    {
        get => _mainFontSize;
        set => SetProperty(ref _mainFontSize, double.IsFinite(value) ? Math.Clamp(value, 0, 200) : 0);
    }

    int _mainFontWeightIndex;

    /// <summary>主文字字重：0 继承、1 常规、2 中等、3 半粗、4 粗体、5 特粗、6 极粗。</summary>
    public int MainFontWeightIndex
    {
        get => _mainFontWeightIndex;
        set
        {
            // ComboBox 清空时可能回写 -1；保持原值而不是退回「继承」。
            if (value is < 0 or > 6) return;
            SetProperty(ref _mainFontWeightIndex, value);
        }
    }

    string _mainTextColor = string.Empty;

    /// <summary>主文字颜色；留空继承主界面文字颜色。支持 #RRGGBB、#AARRGGBB 或颜色名。</summary>
    public string MainTextColor
    {
        get => _mainTextColor;
        set
        {
            if (!SetProperty(ref _mainTextColor, (value ?? string.Empty).Trim())) return;
            OnPropertyChanged(nameof(MainTextBrush));
        }
    }

    /// <summary>主文字颜色的预览画刷；颜色无效时为 null。</summary>
    [JsonIgnore]
    public IBrush? MainTextBrush => TryParseBrush(MainTextColor);

    string _shimmerColor = string.Empty;

    /// <summary>扫光高光颜色；留空为自动（文字本色半透明扫过）。</summary>
    public string ShimmerColor
    {
        get => _shimmerColor;
        set
        {
            if (!SetProperty(ref _shimmerColor, (value ?? string.Empty).Trim())) return;
            OnPropertyChanged(nameof(ShimmerBrush));
        }
    }

    /// <summary>扫光颜色的预览画刷；颜色无效时为 null（自动半透明）。</summary>
    [JsonIgnore]
    public IBrush? ShimmerBrush => TryParseBrush(ShimmerColor);

    int _scrollSpeedPixelsPerSecond = 42;

    /// <summary>长内容滚动的速度，单位像素/秒。</summary>
    public int ScrollSpeedPixelsPerSecond
    {
        get => _scrollSpeedPixelsPerSecond;
        set => SetProperty(ref _scrollSpeedPixelsPerSecond, Math.Clamp(value, 5, 500));
    }

    double _textVerticalOffset;

    /// <summary>文字整体的垂直微调，单位像素；负数上移，正数下移。</summary>
    public double TextVerticalOffset
    {
        get => _textVerticalOffset;
        set => SetProperty(
            ref _textVerticalOffset,
            double.IsFinite(value) ? Math.Clamp(value, -200, 200) : 0);
    }

    double _attributionFontSize;

    /// <summary>额外信息字号；0 表示继承主界面额外信息字号。</summary>
    public double AttributionFontSize
    {
        get => _attributionFontSize;
        set => SetProperty(ref _attributionFontSize, double.IsFinite(value) ? Math.Clamp(value, 0, 200) : 0);
    }

    string _attributionTextColor = string.Empty;

    /// <summary>额外信息颜色；留空继承主界面文字颜色。</summary>
    public string AttributionTextColor
    {
        get => _attributionTextColor;
        set
        {
            if (!SetProperty(ref _attributionTextColor, (value ?? string.Empty).Trim())) return;
            OnPropertyChanged(nameof(AttributionTextBrush));
        }
    }

    /// <summary>额外信息颜色的预览画刷；颜色无效时为 null。</summary>
    [JsonIgnore]
    public IBrush? AttributionTextBrush => TryParseBrush(AttributionTextColor);

    int _scrollMaxLength;

    /// <summary>
    /// 内容超过该字数时启用左右滚动显示；0 表示不启用滚动。
    /// 只影响显示，不影响 <see cref="LengthLimitation"/> 的抓取过滤。
    /// </summary>
    public int ScrollMaxLength
    {
        get => _scrollMaxLength;
        set => SetProperty(ref _scrollMaxLength, Math.Clamp(value, 0, 500));
    }

    static IBrush? TryParseBrush(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return Color.TryParse(text.Trim(), out Color color) ? new SolidColorBrush(color) : null;
    }

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
        set
        {
            // ComboBox 清空时可能回写 -1；不要把它当成「同时展示」改掉用户设置。
            if (value is < 0 or > 1) return;
            AttributesRule = value == 1
                ? AttributesDisplayRule.Separate
                : AttributesDisplayRule.Sametime;
        }
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
