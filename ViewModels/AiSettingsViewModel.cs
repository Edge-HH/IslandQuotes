using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using IslandQuotes.Core;
using IslandQuotes.Models;
using IslandQuotes.Services;

namespace IslandQuotes.ViewModels;

/// <summary>AI 审查设置页的视图模型。</summary>
public sealed class AiSettingsViewModel : ObservableObject
{
    PluginSettings? _settings;
    AiProviderProfile? _selectedProfile;
    AiProviderProfile? _selectedThirdPartyProfile;
    AiProviderProfile? _customProfile;
    bool _isCustomMode;
    ReviewFailureAction _failureAction = ReviewFailureAction.ManualReview;
    ReviewStrictness _strictness = ReviewStrictness.Balanced;
    string _systemPrompt = string.Empty;
    int _maxConcurrentReviews = 3;
    int _itemsPerReviewRequest = 5;
    int _refillThreshold = 1;
    int _maxHistoryItems = 500;
    int _requestTimeoutSeconds = 45;
    bool _logFullPrompts = true;
    bool _logFullResponses = true;
    bool _logEnabled = true;
    int _retentionDays = 30;
    string _testText = "当有一天你不再纠结于答案 当我们又重逢于天涯或沧海";
    bool _isBusy;
    string _statusMessage = "尚未加载设置。";

    public AiSettingsViewModel()
    {
        Protocols =
        [
            new LabeledValue<AiProtocol>(AiProtocol.OpenAiCompatible, "OpenAI 兼容"),
            new LabeledValue<AiProtocol>(AiProtocol.AnthropicMessages, "Anthropic Messages"),
            new LabeledValue<AiProtocol>(AiProtocol.GeminiGenerateContent, "Gemini GenerateContent")
        ];
        FailureActionOptions =
        [
            new LabeledValue<ReviewFailureAction>(ReviewFailureAction.ManualReview, "转人工复核（推荐）"),
            new LabeledValue<ReviewFailureAction>(ReviewFailureAction.Block, "直接拦截")
        ];
        FailureActions = Enum.GetValues<ReviewFailureAction>();
        Reload();
    }

    public ObservableCollection<AiProviderProfile> Profiles { get; } = [];

    /// <summary>「第三方模型提供商」页签中可选的内置供应商（不含通用 OpenAI 兼容配置）。</summary>
    public ObservableCollection<AiProviderProfile> ThirdPartyProfiles { get; } = [];

    /// <summary>当前是否处于「自定义模型 API」页签；切换页签时同步切换正在编辑的预设。</summary>
    public bool IsCustomMode
    {
        get => _isCustomMode;
        set
        {
            if (!SetProperty(ref _isCustomMode, value)) return;
            OnPropertyChanged(nameof(IsThirdPartyMode));
            OnPropertyChanged(nameof(ModeDescription));
            SelectedProfile = value ? _customProfile : SelectedThirdPartyProfile;
        }
    }

    public bool IsThirdPartyMode
    {
        get => !IsCustomMode;
        set => IsCustomMode = !value;
    }

    public string ModeDescription => IsCustomMode
        ? "连接任意 OpenAI 兼容、Anthropic Messages 或 Gemini 协议的服务，或自建网关。"
        : "从内置目录中选择 DeepSeek、通义千问、Gemini 等提供商，填入其 API 密钥即可使用。";

    public AiProviderProfile? SelectedThirdPartyProfile
    {
        get => _selectedThirdPartyProfile;
        set
        {
            if (!SetProperty(ref _selectedThirdPartyProfile, value) || IsCustomMode) return;
            SelectedProfile = value;
        }
    }

    /// <summary>当前预设的 API 密钥；经由视图模型转发以便实时刷新未填写提示。</summary>
    public string SelectedApiKey
    {
        get => SelectedProfile?.ApiKey ?? string.Empty;
        set
        {
            if (SelectedProfile is null || SelectedProfile.ApiKey == value) return;
            SelectedProfile.ApiKey = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowApiKeyWarning));
        }
    }

    public bool ShowApiKeyWarning => SelectedProfile is { RequiresApiKey: true } profile
                                     && string.IsNullOrWhiteSpace(profile.ApiKey);

    /// <summary>API 地址输入框的占位文本：内置预设显示其官方默认地址。</summary>
    public string BaseUrlWatermark =>
        AiProviderPresetCatalog.FindPreset(SelectedProfile?.PresetId ?? string.Empty)?.BaseUrl is { Length: > 0 } url
            ? url
            : "https://api.example.com/v1";

    /// <summary>最近一次从当前供应商获取到的模型列表。</summary>
    public ObservableCollection<string> AvailableModels { get; } = [];

    public bool HasAvailableModels => AvailableModels.Count > 0;

    /// <summary>当前预设的模型名称；AiProviderProfile 不发出变更通知，因此经由视图模型同步文本框与下拉框。</summary>
    public string SelectedModel
    {
        get => SelectedProfile?.Model ?? string.Empty;
        set
        {
            if (SelectedProfile is null || SelectedProfile.Model == value) return;
            SelectedProfile.Model = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedAvailableModel));
        }
    }

    /// <summary>从可用模型下拉框中选择后写回当前预设的模型字段。</summary>
    public string? SelectedAvailableModel
    {
        get => SelectedProfile is null
            ? null
            : AvailableModels.FirstOrDefault(model => string.Equals(model, SelectedProfile.Model, StringComparison.Ordinal));
        set
        {
            if (SelectedProfile is null || string.IsNullOrWhiteSpace(value) || SelectedProfile.Model == value) return;
            SelectedProfile.Model = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedModel));
        }
    }

    public IReadOnlyList<LabeledValue<AiProtocol>> Protocols { get; }

    /// <summary>审查失败时可选择的行为；UI 使用带中文标签的 FailureActionOptions。</summary>
    public IReadOnlyList<ReviewFailureAction> FailureActions { get; }

    public IReadOnlyList<LabeledValue<ReviewFailureAction>> FailureActionOptions { get; }

    /// <summary>允许主题的可选项；与禁止主题互斥，同一标签只允许出现在一侧。</summary>
    public ObservableCollection<TagOptionViewModel> AllowedTagOptions { get; } = [];

    /// <summary>禁止主题的可选项；选中即注入提示词并在本地强制拦截。</summary>
    public ObservableCollection<TagOptionViewModel> BlockedTagOptions { get; } = [];

    public ReviewStrictness Strictness
    {
        get => _strictness;
        set
        {
            if (!SetProperty(ref _strictness, value)) return;
            OnPropertiesChanged(nameof(IsStrictMode), nameof(IsBalancedMode), nameof(IsRelaxedMode),
                nameof(StrictnessDescription));
        }
    }

    public bool IsStrictMode
    {
        get => Strictness == ReviewStrictness.Strict;
        set { if (value) Strictness = ReviewStrictness.Strict; }
    }

    public bool IsBalancedMode
    {
        get => Strictness == ReviewStrictness.Balanced;
        set { if (value) Strictness = ReviewStrictness.Balanced; }
    }

    public bool IsRelaxedMode
    {
        get => Strictness == ReviewStrictness.Relaxed;
        set { if (value) Strictness = ReviewStrictness.Relaxed; }
    }

    public string StrictnessDescription => Strictness switch
    {
        ReviewStrictness.Strict => "严格：提示词按从严标准判定，低风险但置信度不足的内容也会转人工复核。",
        ReviewStrictness.Relaxed => "宽松：只拦截明确不当的内容，中风险自动放行（高风险仍会拦截）。",
        _ => "均衡（默认）：低风险放行，中风险转人工复核，高风险直接拦截。"
    };

    /// <summary>标签选择的摘要，随选择变化刷新。</summary>
    public string TagSummary
    {
        get
        {
            int allowed = AllowedTagOptions.Count(option => option.IsSelected);
            int blocked = BlockedTagOptions.Count(option => option.IsSelected);
            return allowed == 0 && blocked == 0
                ? "未选择任何主题标签；提示词只使用默认审查标准。"
                : $"已选：允许 {allowed} 个主题，禁止 {blocked} 个主题。";
        }
    }

    public AiProviderProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!SetProperty(ref _selectedProfile, value)) return;
            AvailableModels.Clear();
            OnPropertyChanged(nameof(HasAvailableModels));
            OnPropertyChanged(nameof(SelectedModel));
            OnPropertyChanged(nameof(SelectedAvailableModel));
            OnPropertyChanged(nameof(SelectedProtocolChoice));
            OnPropertyChanged(nameof(SelectedApiKey));
            OnPropertyChanged(nameof(ShowApiKeyWarning));
            OnPropertyChanged(nameof(BaseUrlWatermark));
        }
    }

    public LabeledValue<AiProtocol>? SelectedProtocolChoice
    {
        get => SelectedProfile is null
            ? null
            : Protocols.FirstOrDefault(item => item.Value == SelectedProfile.Protocol);
        set
        {
            if (SelectedProfile is null || value is null || SelectedProfile.Protocol == value.Value) return;
            SelectedProfile.Protocol = value.Value;
            OnPropertyChanged();
        }
    }

    public string ActiveProfileId { get; private set; } = string.Empty;

    public string SystemPrompt
    {
        get => _systemPrompt;
        set => SetProperty(ref _systemPrompt, value);
    }

    public ReviewFailureAction FailureAction
    {
        get => _failureAction;
        set
        {
            if (!SetProperty(ref _failureAction, value)) return;
            OnPropertyChanged(nameof(FailureActionChoice));
        }
    }

    public LabeledValue<ReviewFailureAction>? FailureActionChoice
    {
        get => FailureActionOptions.FirstOrDefault(item => item.Value == FailureAction);
        set
        {
            if (value is null) return;
            FailureAction = value.Value;
        }
    }
    public int MaxConcurrentReviews
    {
        get => _maxConcurrentReviews;
        set => SetProperty(ref _maxConcurrentReviews, Math.Clamp(value, 1, 10));
    }

    public int ItemsPerReviewRequest
    {
        get => _itemsPerReviewRequest;
        set => SetProperty(ref _itemsPerReviewRequest, Math.Clamp(value, 1, 10));
    }

    public int RefillThreshold
    {
        get => _refillThreshold;
        set => SetProperty(ref _refillThreshold, Math.Clamp(value, 0, 100));
    }

    public int MaxHistoryItems
    {
        get => _maxHistoryItems;
        set => SetProperty(ref _maxHistoryItems, Math.Clamp(value, 10, 100000));
    }

    public int RequestTimeoutSeconds
    {
        get => _requestTimeoutSeconds;
        set => SetProperty(ref _requestTimeoutSeconds, Math.Clamp(value, 5, 180));
    }

    public bool LogFullPrompts
    {
        get => _logFullPrompts;
        set => SetProperty(ref _logFullPrompts, value);
    }

    public bool LogFullResponses
    {
        get => _logFullResponses;
        set => SetProperty(ref _logFullResponses, value);
    }

    public bool LogEnabled
    {
        get => _logEnabled;
        set => SetProperty(ref _logEnabled, value);
    }

    public int RetentionDays
    {
        get => _retentionDays;
        set => SetProperty(ref _retentionDays, Math.Clamp(value, 1, 3650));
    }

    /// <summary>连接测试时送审的文本，用于查看该文本的风险评级。</summary>
    public string TestText
    {
        get => _testText;
        set => SetProperty(ref _testText, value ?? string.Empty);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
        }
    }

    public bool IsIdle => !IsBusy;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>从运行时设置重新加载供应商与全局策略；不会读取或显示完整密钥。</summary>
    public void Reload()
    {
        try
        {
            _settings = PluginRuntime.SettingsStore.Settings;
            Profiles.Clear();
            ThirdPartyProfiles.Clear();
            foreach (AiProviderProfile profile in _settings.Ai.Profiles)
            {
                Profiles.Add(profile);
                if (profile.Id != AiProviderPresetCatalog.DefaultProfileId)
                    ThirdPartyProfiles.Add(profile);
            }

            _customProfile = Profiles.FirstOrDefault(profile => profile.Id == AiProviderPresetCatalog.DefaultProfileId);
            ActiveProfileId = _settings.Ai.ActiveProfileId;
            AiProviderProfile? active = Profiles.FirstOrDefault(profile => profile.Id == ActiveProfileId);
            _selectedThirdPartyProfile = active is not null && ThirdPartyProfiles.Contains(active)
                ? active
                : ThirdPartyProfiles.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedThirdPartyProfile));
            _isCustomMode = active is not null && active == _customProfile
                            || active is null && _selectedThirdPartyProfile is null;
            OnPropertyChanged(nameof(IsCustomMode));
            OnPropertyChanged(nameof(IsThirdPartyMode));
            OnPropertyChanged(nameof(ModeDescription));
            SelectedProfile = _isCustomMode ? _customProfile : _selectedThirdPartyProfile;
            SystemPrompt = _settings.Ai.SystemPrompt;
            RequestTimeoutSeconds = _settings.Ai.RequestTimeoutSeconds;
            Strictness = Enum.IsDefined(_settings.Ai.Strictness)
                ? _settings.Ai.Strictness
                : ReviewStrictness.Balanced;
            LoadTagOptions(_settings.Ai);
            FailureAction = _settings.Review.FailureAction;
            MaxConcurrentReviews = _settings.Review.MaxConcurrentReviews;
            ItemsPerReviewRequest = _settings.Review.ItemsPerReviewRequest;
            RefillThreshold = _settings.Review.RefillThreshold;
            MaxHistoryItems = _settings.Review.MaxHistoryItems;
            LogFullPrompts = _settings.Review.LogFullPrompts;
            LogFullResponses = _settings.Review.LogFullResponses;
            LogEnabled = _settings.Log.Enabled;
            RetentionDays = _settings.Log.RetentionDays;

            StatusMessage = Profiles.Count == 0
                ? "当前没有可用的 AI 供应商预设。请检查插件配置是否已初始化。"
                : $"已加载 {Profiles.Count} 个内置/自定义供应商。保存供应商编辑时会将其设为当前启用配置。";
        }
        catch
        {
            _settings = null;
            Profiles.Clear();
            ThirdPartyProfiles.Clear();
            _customProfile = null;
            SelectedThirdPartyProfile = null;
            SelectedProfile = null;
            StatusMessage = "AI 设置服务尚未就绪。请先启用插件并打开一次设置页面，或重启 ClassIsland。";
        }
    }

    /// <summary>验证并保存当前供应商、审查失败策略、日志与提示词设置。</summary>
    public async Task SaveAsync()
    {
        if (IsBusy) return;

        try
        {
            if (_settings is null) Reload();
            if (_settings is null)
            {
                StatusMessage = "无法保存：AI 设置服务尚未就绪。";
                return;
            }

            if (SelectedProfile is null)
            {
                StatusMessage = "无法保存：请先选择一个 AI 供应商预设。";
                return;
            }

            IsBusy = true;
            StatusMessage = "正在保存设置…";

            AiProviderProfile profile = SelectedProfile;
            if (string.IsNullOrWhiteSpace(profile.Id))
                profile.Id = Guid.NewGuid().ToString("N");

            var warnings = new List<string>();
            if (string.IsNullOrWhiteSpace(profile.DisplayName))
                warnings.Add("供应商名称为空");
            if (string.IsNullOrWhiteSpace(profile.BaseUrl))
                warnings.Add("Base URL 为空");
            else if (!Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out Uri? uri)
                     || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                warnings.Add("Base URL 不是有效的 http/https 地址");
            if (string.IsNullOrWhiteSpace(profile.Model))
                warnings.Add("模型名称为空");
            if (profile.RequiresApiKey
                && string.IsNullOrWhiteSpace(profile.ApiKey))
                warnings.Add("此供应商需要 API Key；仍会保存，但连接测试和 AI 审查将失败（失败即关闭）");
            else if (profile.ApiKey.StartsWith("env:", StringComparison.OrdinalIgnoreCase)
                     && profile.ApiKey.Trim().Length <= 4)
                warnings.Add("环境变量引用缺少变量名");
            if (!string.IsNullOrWhiteSpace(profile.ExtraHeadersJson))
            {
                try
                {
                    using var _ = System.Text.Json.JsonDocument.Parse(profile.ExtraHeadersJson);
                }
                catch
                {
                    warnings.Add("额外请求头不是有效 JSON");
                }
            }

            _settings.Ai.Profiles = Profiles.ToList();
            _settings.Ai.ActiveProfileId = profile.Id;
            _settings.Ai.SystemPrompt = SystemPrompt ?? string.Empty;
            _settings.Ai.RequestTimeoutSeconds = Math.Clamp(RequestTimeoutSeconds, 5, 180);
            _settings.Ai.Strictness = Strictness;
            _settings.Ai.AllowedTags = AllowedTagOptions
                .Where(option => option.IsSelected)
                .Select(option => option.Label)
                .ToList();
            _settings.Ai.BlockedTags = BlockedTagOptions
                .Where(option => option.IsSelected)
                .Select(option => option.Label)
                .ToList();
            _settings.Review.FailureAction = FailureAction;
            _settings.Review.MaxConcurrentReviews = Math.Clamp(MaxConcurrentReviews, 1, 10);
            _settings.Review.ItemsPerReviewRequest = Math.Clamp(ItemsPerReviewRequest, 1, 10);
            _settings.Review.RefillThreshold = Math.Clamp(RefillThreshold, 0, 100);
            _settings.Review.MaxHistoryItems = Math.Clamp(MaxHistoryItems, 10, 100000);
            _settings.Review.LogFullPrompts = LogFullPrompts;
            _settings.Review.LogFullResponses = LogFullResponses;
            _settings.Log.Enabled = LogEnabled;
            _settings.Log.RetentionDays = Math.Clamp(RetentionDays, 1, 3650);

            ActiveProfileId = profile.Id;
            OnPropertyChanged(nameof(ActiveProfileId));
            await PluginRuntime.SettingsStore.SaveAsync();

            StatusMessage = warnings.Count == 0
                ? "设置已保存并设为当前启用供应商。"
                : $"设置已保存，但请注意：{string.Join("；", warnings)}。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败（{ex.GetType().Name}）。请检查插件配置目录权限后重试。";
        }
        finally
        {
            IsBusy = false;
        }
    }
    /// <summary>保存当前配置后，用测试文本调用当前 AI 供应商并显示其评级。</summary>
    public async Task TestAsync()
    {
        if (IsBusy) return;

        await SaveAsync();
        if (_settings is null || SelectedProfile is null) return;

        try
        {
            IsBusy = true;
            StatusMessage = "正在测试当前 AI 供应商连接…";
            AiReviewResult result = await PluginRuntime.Queue.TestActiveAiAsync(TestText);

            if (result.IsSuccess)
            {
                string categories = result.Categories.Count == 0
                    ? string.Empty
                    : $"分类：{string.Join("、", result.Categories)}。";
                StatusMessage = $"连接测试成功：{RiskLabel(result.Risk)}，置信度 {result.Confidence:P0}。{categories}{Sanitize(result.Reason)}";
            }
            else
            {
                StatusMessage = $"连接测试失败：{Sanitize(result.Error)}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"连接测试失败（{ex.GetType().Name}）。请检查网络、Base URL、协议与密钥后重试。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>向当前选择的供应商查询可用模型，结果填入下拉列表供选择。</summary>
    public async Task FetchModelsAsync()
    {
        if (IsBusy || SelectedProfile is null) return;

        try
        {
            IsBusy = true;
            StatusMessage = "正在获取可用模型…";
            IReadOnlyList<string> models = await PluginRuntime.AiReview.ListModelsAsync(SelectedProfile);

            AvailableModels.Clear();
            foreach (string model in models)
                AvailableModels.Add(model);
            OnPropertyChanged(nameof(HasAvailableModels));
            OnPropertyChanged(nameof(SelectedAvailableModel));

            StatusMessage = models.Count == 0
                ? "供应商未返回任何模型。"
                : $"已获取 {models.Count} 个可用模型，可在模型下方的列表中选择。";
        }
        catch (Exception ex)
        {
            StatusMessage = ex is OperationCanceledException
                ? "获取模型列表超时。请检查网络与 Base URL 后重试。"
                : $"获取模型列表失败：{Sanitize(ex.Message)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>使用系统文件管理器打开审计日志目录；通过 Win32 ShellExecuteW 打开，参数显式传递。</summary>
    public void OpenLogDirectory()
    {
        try
        {
            string directory = PluginRuntime.AuditLog.LogDirectory;
            Directory.CreateDirectory(directory);
            // 通过 ShellExecuteW 直接打开目录；参数显式传递，不经过 shell 命令行。
            ShellFolder.OpenInFileManager(directory);
            StatusMessage = $"已请求打开日志目录：{directory}";
        }
        catch
        {
            StatusMessage = "无法打开日志目录。请检查插件是否已初始化，或稍后重试。";
        }
    }

    /// <summary>按内置目录重建主题选项；设置里存在但目录未收录的自定义标签会追加到末尾。</summary>
    private void LoadTagOptions(AiSettings ai)
    {
        var allowed = new HashSet<string>(ai.AllowedTags ?? [], StringComparer.OrdinalIgnoreCase);
        var blocked = new HashSet<string>(ai.BlockedTags ?? [], StringComparer.OrdinalIgnoreCase);

        AllowedTagOptions.Clear();
        BlockedTagOptions.Clear();

        var labels = QuoteTagCatalog.Tags.Select(tag => tag.Label)
            .Concat(allowed)
            .Concat(blocked)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (string label in labels)
        {
            AllowedTagOptions.Add(new TagOptionViewModel(label, isSelected =>
            {
                if (isSelected) Deselect(BlockedTagOptions, label);
                OnPropertyChanged(nameof(TagSummary));
            })
            {
                IsSelected = allowed.Contains(label)
            });
            BlockedTagOptions.Add(new TagOptionViewModel(label, isSelected =>
            {
                if (isSelected) Deselect(AllowedTagOptions, label);
                OnPropertyChanged(nameof(TagSummary));
            })
            {
                IsSelected = blocked.Contains(label)
            });
        }

        OnPropertyChanged(nameof(TagSummary));
    }

    private static void Deselect(ObservableCollection<TagOptionViewModel> options, string label)
    {
        TagOptionViewModel? counterpart = options.FirstOrDefault(option =>
            string.Equals(option.Label, label, StringComparison.OrdinalIgnoreCase));
        if (counterpart is { IsSelected: true }) counterpart.IsSelected = false;
    }

    string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "未提供详细信息（已保持失败即关闭）";

        string text = value.Trim().Replace('\r', ' ').Replace('\n', ' ');
        string secret = SelectedProfile?.ApiKey?.Trim() ?? string.Empty;
        if (secret.Length >= 4)
            text = text.Replace(secret, "[已隐藏]", StringComparison.Ordinal);

        return text.Length <= 500 ? text : $"{text[..500]}…";
    }

    static string RiskLabel(RiskLevel risk) => risk switch
    {
        RiskLevel.Low => "低风险",
        RiskLevel.Medium => "中风险",
        RiskLevel.High => "高风险",
        _ => "风险未知"
    };
}

/// <summary>带中文显示标签的枚举选项，避免引入额外 XAML 转换器。</summary>
public sealed class LabeledValue<T>(T value, string label) where T : struct, Enum
{
    public T Value { get; } = value;
    public string Label { get; } = label;
    public override string ToString() => Label;
}

/// <summary>主题快速控制的单个选项；选中状态变化时回调视图模型做互斥清理与摘要刷新。</summary>
public sealed class TagOptionViewModel : ObservableObject
{
    private readonly Action<bool> _onSelectionChanged;
    private bool _isSelected;

    public TagOptionViewModel(string label, Action<bool> onSelectionChanged)
    {
        Label = label;
        _onSelectionChanged = onSelectionChanged;
    }

    public string Label { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetProperty(ref _isSelected, value)) return;
            _onSelectionChanged(value);
        }
    }
}
