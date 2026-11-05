using System.Text.Json.Serialization;
using IslandQuotes.Core;

namespace IslandQuotes.Models;

/// <summary>A quote candidate moving through AI review, manual review and playback.</summary>
public sealed class ReviewQueueItem : ObservableObject
{
    string _stateText = string.Empty;
    ReviewItemState _state;
    RiskLevel _risk;
    double _confidence;
    string _aiReason = string.Empty;
    List<string> _categories = [];
    string _aiProviderName = string.Empty;
    string _aiModel = string.Empty;
    string _aiRequestId = string.Empty;
    string _aiRawResponse = string.Empty;
    DateTimeOffset? _reviewedAt;
    DateTimeOffset? _manualReviewedAt;
    DateTimeOffset? _displayedAt;
    string _manualNote = string.Empty;
    bool _isSelected;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string RefreshSessionId { get; set; } = string.Empty;
    public QuoteContent Quote { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public RiskLevel Risk
    {
        get => _risk;
        set
        {
            if (SetProperty(ref _risk, value))
            {
                OnPropertyChanged(nameof(RiskLabel));
                OnPropertyChanged(nameof(RiskGlyph));
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public double Confidence
    {
        get => _confidence;
        set
        {
            if (SetProperty(ref _confidence, Math.Clamp(value, 0, 1)))
                OnPropertyChanged(nameof(ConfidenceLabel));
        }
    }

    public string AiReason
    {
        get => _aiReason;
        set { if (SetProperty(ref _aiReason, value)) OnPropertyChanged(nameof(Summary)); }
    }

    public List<string> Categories
    {
        get => _categories;
        set { if (SetProperty(ref _categories, value ?? [])) OnPropertyChanged(nameof(CategoryLabel)); }
    }

    public string AiProviderName
    {
        get => _aiProviderName;
        set => SetProperty(ref _aiProviderName, value);
    }

    public string AiModel
    {
        get => _aiModel;
        set => SetProperty(ref _aiModel, value);
    }

    public string AiRequestId
    {
        get => _aiRequestId;
        set => SetProperty(ref _aiRequestId, value);
    }

    public string AiRawResponse
    {
        get => _aiRawResponse;
        set => SetProperty(ref _aiRawResponse, value);
    }

    public string AiRequestBodyJson { get; set; } = string.Empty;
    public string AiSystemPrompt { get; set; } = string.Empty;
    public string AiUserPrompt { get; set; } = string.Empty;

    public ReviewItemState State
    {
        get => _state;
        set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertiesChanged(nameof(StateLabel), nameof(IsAwaitingManual), nameof(IsPlayable), nameof(StatusGlyph));
        }
    }

    [JsonIgnore]
    public string StateLabel => State switch
    {
        ReviewItemState.Pending => "待审查",
        ReviewItemState.Reviewing => "审查中",
        ReviewItemState.AwaitingManualReview => "待人工复核",
        ReviewItemState.Approved => "已通过 · 排队中",
        ReviewItemState.Displayed => "已播放",
        ReviewItemState.Blocked => "高风险 · 已拦截",
        ReviewItemState.Rejected => "人工拒绝",
        ReviewItemState.ReviewFailed => "审查失败",
        _ => "未知"
    };

    [JsonIgnore] public bool IsAwaitingManual => State == ReviewItemState.AwaitingManualReview;
    [JsonIgnore] public bool IsPlayable => State == ReviewItemState.Approved;

    [JsonIgnore]
    public string RiskLabel => Risk switch
    {
        RiskLevel.Low => "低风险",
        RiskLevel.Medium => "中风险",
        RiskLevel.High => "高风险",
        _ => "风险未知"
    };

    [JsonIgnore]
    public string RiskGlyph => Risk switch
    {
        RiskLevel.Low => "●",
        RiskLevel.Medium => "◆",
        RiskLevel.High => "■",
        _ => "?"
    };

    [JsonIgnore]
    public string StatusGlyph => State switch
    {
        ReviewItemState.AwaitingManualReview => "◆",
        ReviewItemState.Approved => "▶",
        ReviewItemState.Blocked => "■",
        ReviewItemState.Displayed => "✓",
        ReviewItemState.Rejected => "×",
        ReviewItemState.ReviewFailed => "!",
        _ => "…"
    };

    [JsonIgnore] public string ConfidenceLabel => $"{Confidence:P0}";
    [JsonIgnore] public string CategoryLabel => Categories.Count == 0 ? "未标注" : string.Join(" / ", Categories);

    [JsonIgnore]
    public string Summary
    {
        get
        {
            string reason = string.IsNullOrWhiteSpace(AiReason) ? "AI 未提供理由" : AiReason.Trim();
            return $"{RiskLabel} · {reason}";
        }
    }

    [JsonIgnore]
    public string SourceLabel
    {
        get
        {
            string provider = string.IsNullOrWhiteSpace(Quote.ProviderName) ? Quote.ProviderId : Quote.ProviderName;
            string upstream = string.IsNullOrWhiteSpace(Quote.UpstreamId) ? "无上游 ID" : $"上游 ID: {Quote.UpstreamId}";
            return $"{provider}  ·  {upstream}";
        }
    }

    public DateTimeOffset? ReviewedAt
    {
        get => _reviewedAt;
        set => SetProperty(ref _reviewedAt, value);
    }

    public DateTimeOffset? ManualReviewedAt
    {
        get => _manualReviewedAt;
        set => SetProperty(ref _manualReviewedAt, value);
    }

    /// <summary>来源未启用 AI 审查、仅经本地排除列表检查后直接放行。</summary>
    public bool IsAiReviewSkipped { get; set; }

    public DateTimeOffset? DisplayedAt
    {
        get => _displayedAt;
        set => SetProperty(ref _displayedAt, value);
    }

    public string ManualNote
    {
        get => _manualNote;
        set => SetProperty(ref _manualNote, value);
    }

    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public void ApplyResult(AiReviewResult result)
    {
        Risk = result.Risk;
        Confidence = result.Confidence;
        AiReason = result.Reason;
        Categories = result.Categories.ToList();
        AiProviderName = result.AiProviderName;
        AiModel = result.AiModel;
        AiRequestId = result.AiRequestId;
        AiRawResponse = result.RawResponse;
        AiRequestBodyJson = result.RequestBodyJson;
        AiSystemPrompt = result.SystemPrompt;
        AiUserPrompt = result.UserPrompt;
        ReviewedAt = DateTimeOffset.Now;
    }
}

public sealed class ReviewQueueSnapshot
{
    public int Version { get; set; } = 1;
    public List<ReviewQueueItem> Items { get; set; } = [];
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.Now;
}
