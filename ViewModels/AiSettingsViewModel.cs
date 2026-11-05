using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Motivation.Core;
using Motivation.Models;

namespace Motivation.ViewModels;

/// <summary>AI 审查与供应商设置页的视图模型。</summary>
public sealed class AiSettingsViewModel : ObservableObject
{
    PluginSettings? _settings;
    AiProviderProfile? _selectedProfile;
    ReviewFailureAction _failureAction = ReviewFailureAction.ManualReview;
    string _systemPrompt = string.Empty;
    int _maxConcurrentReviews = 3;
    int _maxHistoryItems = 500;
    int _requestTimeoutSeconds = 45;
    bool _logFullPrompts = true;
    bool _logFullResponses = true;
    bool _logEnabled = true;
    int _retentionDays = 30;
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

    public IReadOnlyList<LabeledValue<AiProtocol>> Protocols { get; }

    /// <summary>审查失败时可选择的行为；UI 使用带中文标签的 FailureActionOptions。</summary>
    public IReadOnlyList<ReviewFailureAction> FailureActions { get; }

    public IReadOnlyList<LabeledValue<ReviewFailureAction>> FailureActionOptions { get; }

    public AiProviderProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!SetProperty(ref _selectedProfile, value)) return;
            OnPropertyChanged(nameof(SelectedProtocolChoice));
            OnPropertyChanged(nameof(ApiKeyRequirementText));
            OnPropertyChanged(nameof(CategoryPrivacyWarning));
            OnPropertyChanged(nameof(HasCategoryPrivacyWarning));
            OnPropertyChanged(nameof(SelectedProfileSummary));
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

    public string ApiKeyRequirementText => SelectedProfile?.RequiresApiKey == true
        ? "此供应商需要 API Key；可填写 env:环境变量名。留空时仍可保存，但连接测试会失败。"
        : "此供应商通常不需要 API Key；留空即可，也可填写环境变量引用。";

    public string SelectedProfileSummary => SelectedProfile is null
        ? "尚未选择供应商"
        : $"当前选择：{SelectedProfile.DisplayLabel}";

    public bool HasCategoryPrivacyWarning => SelectedProfile?.Category is
        AiProviderCategory.PublicFree or AiProviderCategory.FreeTier;

    public string CategoryPrivacyWarning => SelectedProfile?.Category switch
    {
        AiProviderCategory.PublicFree =>
            "公用免费端点可能记录或复用发送的文本，且可能限流。请勿发送敏感内容；若在意隐私，请改用本地或自备密钥的供应商。",
        AiProviderCategory.FreeTier =>
            "免费额度端点可能记录请求或受限流影响。请先阅读供应商隐私政策。",
        AiProviderCategory.Local =>
            "本地端点通常不离开本机，但仍请确认服务监听地址与访问权限。",
        _ => string.Empty
    };
    /// <summary>从运行时设置重新加载供应商与全局策略；不会读取或显示完整密钥。</summary>
    public void Reload()
    {
        try
        {
            _settings = PluginRuntime.SettingsStore.Settings;
            Profiles.Clear();
            foreach (AiProviderProfile profile in _settings.Ai.Profiles)
                Profiles.Add(profile);

            ActiveProfileId = _settings.Ai.ActiveProfileId;
            SelectedProfile = Profiles.FirstOrDefault(profile => profile.Id == ActiveProfileId)
                ?? Profiles.FirstOrDefault();
            SystemPrompt = _settings.Ai.SystemPrompt;
            RequestTimeoutSeconds = _settings.Ai.RequestTimeoutSeconds;
            FailureAction = _settings.Review.FailureAction;
            MaxConcurrentReviews = _settings.Review.MaxConcurrentReviews;
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
            _settings.Review.FailureAction = FailureAction;
            _settings.Review.MaxConcurrentReviews = Math.Clamp(MaxConcurrentReviews, 1, 10);
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
    /// <summary>保存当前配置后，用内置的良性测试内容调用当前 AI 供应商。</summary>
    public async Task TestAsync()
    {
        if (IsBusy) return;

        await SaveAsync();
        if (_settings is null || SelectedProfile is null) return;

        try
        {
            IsBusy = true;
            StatusMessage = "正在测试当前 AI 供应商连接…";
            AiReviewResult result = await PluginRuntime.Queue.TestActiveAiAsync();

            if (result.IsSuccess)
            {
                StatusMessage = $"连接测试成功：{RiskLabel(result.Risk)}，置信度 {result.Confidence:P0}。{Sanitize(result.Reason)}";
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

    /// <summary>使用系统文件管理器打开审计日志目录。</summary>
    public void OpenLogDirectory()
    {
        try
        {
            string directory = PluginRuntime.AuditLog.LogDirectory;
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            StatusMessage = $"已请求打开日志目录：{directory}";
        }
        catch
        {
            StatusMessage = "无法打开日志目录。请检查插件是否已初始化，或稍后重试。";
        }
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
