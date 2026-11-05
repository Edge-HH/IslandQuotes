using IslandQuotes.Models;

namespace IslandQuotes.Services;

/// <summary>A quote source. Every implementation must populate Quote.UpstreamId.</summary>
public interface IQuoteProvider
{
    string Id { get; }
    string DisplayName { get; }
    string Description { get; }
    bool IsEnabledByDefault { get; }
    int DefaultWeight { get; }
    bool IsAiReviewEnabledByDefault => true;
    Task<QuoteContent> FetchAsync(QuoteProviderConfig config, int lengthLimitation, CancellationToken cancellationToken = default);
}

public interface IQuoteProviderSettingsFactory
{
    Avalonia.Controls.Control CreateSettingsControl(QuoteProviderConfig config);
}

/// <summary>A single audit line written to PluginConfigFolder/log/audit-yyyy-MM-dd.jsonl.</summary>
public sealed class AuditLogEvent
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string Stage { get; set; } = string.Empty;
    public string RefreshSessionId { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string UpstreamId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public object? Data { get; set; }
}