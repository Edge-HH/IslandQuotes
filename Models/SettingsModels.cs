using System.Text.Json.Serialization;

namespace IslandQuotes.Models;

public enum RiskLevel
{
    Unknown = 0,
    Low = 1,
    Medium = 2,
    High = 3
}

public enum ReviewItemState
{
    Pending = 0,
    Reviewing = 1,
    AwaitingManualReview = 2,
    Approved = 3,
    Displayed = 4,
    Blocked = 5,
    Rejected = 6,
    ReviewFailed = 7
}

public enum ReviewFailureAction
{
    ManualReview = 0,
    Block = 1
}

public enum AiProtocol
{
    OpenAiCompatible = 0,
    AnthropicMessages = 1,
    GeminiGenerateContent = 2
}

/// <summary>AI 审查的判断严格程度；同时影响提示词限度与审查决策的过滤力度。</summary>
public enum ReviewStrictness
{
    /// <summary>宽松：只拦截明确不适合校园展示的内容，中风险默认放行。</summary>
    Relaxed = 0,

    /// <summary>均衡（默认）：低风险放行、中风险转人工、高风险拦截。</summary>
    Balanced = 1,

    /// <summary>严格：按从严标准提示词审查，低风险且置信度不足时也转人工。</summary>
    Strict = 2
}

/// <summary>One quote returned by an upstream quote provider.</summary>
public sealed class QuoteContent
{
    public string Content { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Catalog { get; set; } = string.Empty;

    /// <summary>Stable id supplied by the upstream source, or a documented content-derived id.</summary>
    public string UpstreamId { get; set; } = string.Empty;

    /// <summary>native, uuid, numeric, content-sha256, etc.</summary>
    public string UpstreamIdKind { get; set; } = "unknown";
    public string ProviderId { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string RequestMethod { get; set; } = "GET";
    public string RequestUrl { get; set; } = string.Empty;
    public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore]
    public string Attribution
    {
        get
        {
            string author = string.IsNullOrWhiteSpace(Author) ? string.Empty : Author.Trim();
            string title = string.IsNullOrWhiteSpace(Title) ? string.Empty : Title.Trim();
            if (author.Length == 0) return title;
            if (title.Length == 0) return author;
            return $"{author} · {title}";
        }
    }
}

public sealed class AiReviewResult
{
    public bool IsSuccess { get; set; }
    public RiskLevel Risk { get; set; } = RiskLevel.Unknown;
    public double Confidence { get; set; }
    public string Reason { get; set; } = string.Empty;
    public List<string> Categories { get; set; } = [];
    public string RawResponse { get; set; } = string.Empty;
    public string RawReasoning { get; set; } = string.Empty;
    public string AiProfileId { get; set; } = string.Empty;
    public string AiProviderName { get; set; } = string.Empty;
    public string AiModel { get; set; } = string.Empty;
    public string AiProtocol { get; set; } = string.Empty;
    public string AiEndpoint { get; set; } = string.Empty;
    public string AiRequestId { get; set; } = string.Empty;
    public string RequestBodyJson { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public string UserPrompt { get; set; } = string.Empty;
    public long DurationMs { get; set; }
    public string Error { get; set; } = string.Empty;
}

public sealed class AiProviderProfile
{
    public string Id { get; set; } = string.Empty;
    public string PresetId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public AiProtocol Protocol { get; set; } = AiProtocol.OpenAiCompatible;
    public string BaseUrl { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public bool RequiresApiKey { get; set; }
    public bool IsBuiltIn { get; set; } = true;
    public bool IsEnabled { get; set; } = true;
    public string Notes { get; set; } = string.Empty;
    public string ExtraHeadersJson { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 45;

    public AiProviderProfile Clone() => (AiProviderProfile)MemberwiseClone();

    [JsonIgnore]
    public string DisplayLabel => DisplayName;
}

public sealed class PluginSettings
{
    public AiSettings Ai { get; set; } = new();
    public ReviewSettings Review { get; set; } = new();
    public LogSettings Log { get; set; } = new();
}

public sealed class AiSettings
{
    public string ActiveProfileId { get; set; } = "custom-openai-compatible";
    public List<AiProviderProfile> Profiles { get; set; } = [];
    public string SystemPrompt { get; set; } = string.Empty;
    public int RequestTimeoutSeconds { get; set; } = 45;

    /// <summary>判断严格程度；写入提示词并影响审查决策的过滤力度。</summary>
    public ReviewStrictness Strictness { get; set; } = ReviewStrictness.Balanced;

    /// <summary>允许的主题标签（快速控制）：提示词中倾向展示，不能压过安全边界。</summary>
    public List<string> AllowedTags { get; set; } = [];

    /// <summary>禁止的主题标签（快速控制）：命中即拦截，本地强制执行。</summary>
    public List<string> BlockedTags { get; set; } = [];
}

public sealed class ReviewSettings
{
    public int MaxConcurrentReviews { get; set; } = 3;

    /// <summary>单次 AI 请求合并审查的条数；多条共用一份系统提示词，设为 1 时逐条请求。</summary>
    public int ItemsPerReviewRequest { get; set; } = 5;

    /// <summary>播放队列剩余条数不高于此值时，在后台提前抓取并审查下一批内容；设为 0 时仅在队列耗尽后才补充。</summary>
    public int RefillThreshold { get; set; } = 1;
    public ReviewFailureAction FailureAction { get; set; } = ReviewFailureAction.ManualReview;
    public int MaxHistoryItems { get; set; } = 500;
    public bool LogFullPrompts { get; set; } = true;
    public bool LogFullResponses { get; set; } = true;
}

public sealed class LogSettings
{
    public bool Enabled { get; set; } = true;
    public int RetentionDays { get; set; } = 30;
}

public sealed class QuoteProviderConfig
{
    public bool IsEnabled { get; set; }
    public int Weight { get; set; } = 1;

    /// <summary>该来源的内容是否先经 AI 审查再播放；关闭时仅应用本地排除列表。</summary>
    public bool IsAiReviewEnabled { get; set; } = true;

    public Dictionary<string, string> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string GetOption(string key, string defaultValue = "")
        => Options.TryGetValue(key, out string? value) ? value : defaultValue;

    public void SetOption(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) Options.Remove(key);
        else Options[key] = value;
    }
}

/// <summary>Inputs supplied by a quote component for one queue/fill operation.</summary>
public sealed class QuoteRequestOptions
{
    public IReadOnlyDictionary<string, QuoteProviderConfig> ProviderSettings { get; init; }
        = new Dictionary<string, QuoteProviderConfig>(StringComparer.OrdinalIgnoreCase);
    public int LengthLimitation { get; init; }
    public int BatchSize { get; init; } = 3;
    public int MaxBufferedItems { get; init; } = 12;
    public IReadOnlyList<string> IgnoreTerms { get; init; } = [];
    public string RefreshSessionId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>组件自带的额外来源（如用户自定义来源），与内置来源一同参与加权选择。</summary>
    public IReadOnlyList<Services.IQuoteProvider> ExtraProviders { get; init; } = [];
}
