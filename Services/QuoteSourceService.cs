using IslandQuotes.Models;

namespace IslandQuotes.Services;

/// <summary>负责从已启用的名句来源中按权重挑选并获取句子。</summary>
public sealed class QuoteSourceService
{
    /// <summary>同一来源因内容超长连续重试的上限。</summary>
    private const int MaxAttempts = 4;

    /// <summary>单次获取跨所有来源的总尝试上限。</summary>
    private const int MaxTotalAttempts = 8;
    private readonly AuditLogService _audit;
    private readonly IQuoteProvider[] _providers;
    private readonly object _providerSettingsSync = new();

    public QuoteSourceService(AuditLogService audit)
    {
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _providers =
        [
            new HitokotoProvider(),
            new JinrishiciProvider(),
            new DlystcProvider(),
            new SainticProvider()
        ];
    }

    /// <summary>当前内置的名句来源。顺序稳定，便于设置页展示。</summary>
    public IReadOnlyList<IQuoteProvider> Providers => _providers;

    /// <summary>
    /// 补齐缺失的来源配置。已有配置（包括用户禁用或修改的权重）不会被覆盖。
    /// 只读字典无法写入时保持原样；获取时会使用来源的内置默认值。
    /// </summary>
    public void EnsureProviderSettings(IReadOnlyDictionary<string, QuoteProviderConfig> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings is not IDictionary<string, QuoteProviderConfig> mutable)
        {
            return;
        }

        lock (_providerSettingsSync)
        {
            foreach (IQuoteProvider provider in _providers)
            {
                if (!mutable.TryGetValue(provider.Id, out QuoteProviderConfig? config) || config is null)
                {
                    mutable[provider.Id] = CreateDefaultConfig(provider);
                }
            }
        }
    }

    /// <summary>
    /// 从加权随机的可用来源获取一条满足长度限制的句子。
    /// 内容超长时在同一来源重试（最多四次），请求失败时换来源，总计最多八次。
    /// 指定 <paramref name="preferredProviderId"/> 时首选该来源而不重新抽签。
    /// 每次请求成功或失败都会写入 quote_request 审计事件。
    /// </summary>
    public async Task<QuoteContent> FetchOneAsync(
        QuoteRequestOptions options,
        string refreshSessionId,
        string itemId,
        CancellationToken ct = default,
        string? preferredProviderId = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        string sessionId = !string.IsNullOrWhiteSpace(refreshSessionId)
            ? refreshSessionId
            : options.RefreshSessionId;
        EnsureProviderSettings(options.ProviderSettings);

        List<(IQuoteProvider Provider, QuoteProviderConfig Config)> candidates =
            GetEnabledCandidates(options.ProviderSettings, options.ExtraProviders);

        if (candidates.Count == 0)
        {
            const string error = "没有启用且权重大于 0 的名句来源。";
            await WriteAuditSafeAsync(new AuditLogEvent
            {
                Stage = "quote_request",
                RefreshSessionId = sessionId,
                ItemId = itemId,
                Data = new
                {
                    success = false,
                    providerId = string.Empty,
                    providerName = string.Empty,
                    requestUrl = string.Empty,
                    upstreamId = string.Empty,
                    error,
                    attempts = 0
                }
            }).ConfigureAwait(false);
            throw new InvalidOperationException(error);
        }

        List<string> errors = [];
        Exception? lastException = null;

        // 来源只按权重抽一次：内容超长时在同一来源内重试，不重新抽签，
        // 否则通过率低的来源会把份额让给其它来源（尤其是不受长度限制的自定义来源），
        // 实际出现比例变成「权重 × 通过率」而不是权重。
        // 只有请求失败、或在该来源上用尽重试时才排除它，在剩余来源中按权重重抽。
        List<(IQuoteProvider Provider, QuoteProviderConfig Config)> remaining = [.. candidates];
        // 调用方因内容重复而重抓时指定原来源：已抽中的份额留在该来源，
        // 否则内容池小、容易重复的来源会在重抓时被换成别的来源。
        (IQuoteProvider Provider, QuoteProviderConfig Config)? current = string.IsNullOrWhiteSpace(preferredProviderId)
            ? null
            : remaining.FirstOrDefault(candidate => string.Equals(
                candidate.Provider.Id, preferredProviderId, StringComparison.OrdinalIgnoreCase)) is { Provider: not null } preferred
                ? preferred
                : null;
        int attemptsOnCurrent = 0;
        int attempt = 0;

        while (attempt < MaxTotalAttempts && remaining.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (current is null || attemptsOnCurrent >= MaxAttempts)
            {
                if (current is { } exhausted) remaining.Remove(exhausted);
                if (remaining.Count == 0) break;
                current = SelectWeighted(remaining);
                attemptsOnCurrent = 0;
            }

            attempt++;
            attemptsOnCurrent++;
            (IQuoteProvider provider, QuoteProviderConfig config) = current.Value;
            string requestUrl = string.Empty;
            QuoteContent? quote = null;
            string error;

            try
            {
                quote = await provider.FetchAsync(config, options.LengthLimitation, ct)
                    .ConfigureAwait(false);
                if (quote is null)
                {
                    throw new InvalidOperationException("来源返回了空引用。");
                }

                requestUrl = quote.RequestUrl ?? string.Empty;
                string content = (quote.Content ?? string.Empty).Trim();
                if (content.Length == 0)
                {
                    throw new InvalidOperationException("来源返回了空句子。");
                }

                quote.Content = content;
                NormalizeIdentity(provider, quote);

                if (options.LengthLimitation > 0
                    && quote.Content.Length > options.LengthLimitation
                    && provider is not CustomQuoteProvider)
                {
                    error = $"内容长度 {quote.Content.Length} 超过限制 {options.LengthLimitation}。";
                    errors.Add($"{provider.Id}: {error}");
                    await WriteAttemptAuditAsync(
                        sessionId,
                        itemId,
                        provider,
                        quote,
                        requestUrl,
                        false,
                        error,
                        attempt).ConfigureAwait(false);
                    continue;
                }

                await WriteAttemptAuditAsync(
                    sessionId,
                    itemId,
                    provider,
                    quote,
                    requestUrl,
                    true,
                    string.Empty,
                    attempt).ConfigureAwait(false);
                return quote;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await WriteAttemptAuditAsync(
                    sessionId,
                    itemId,
                    provider,
                    quote,
                    requestUrl,
                    false,
                    "操作已取消。",
                    attempt).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                requestUrl = ex is QuoteProviderRequestException requestException
                    ? requestException.RequestUrl
                    : requestUrl;
                error = ex.Message;
                errors.Add($"{provider.Id}: {error}");
                await WriteAttemptAuditAsync(
                    sessionId,
                    itemId,
                    provider,
                    quote,
                    requestUrl,
                    false,
                    error,
                    attempt).ConfigureAwait(false);

                // 请求失败（含第三方实现直接抛出的 HttpRequestException 或 JSON 异常）
                // 说明该来源当前不可用：排除它，下一轮在剩余来源中按权重重抽。
                remaining.Remove(current.Value);
                current = null;
            }
        }

        string summary = errors.Count == 0
            ? $"尝试 {attempt} 次后仍未取得满足长度限制的句子。"
            : $"尝试 {attempt} 次后仍无法取得句子：{string.Join("；", errors.TakeLast(4))}";
        await WriteAuditSafeAsync(new AuditLogEvent
        {
            Stage = "quote_request",
            RefreshSessionId = sessionId,
            ItemId = itemId,
            Data = new
            {
                success = false,
                providerId = string.Empty,
                providerName = string.Empty,
                requestUrl = string.Empty,
                upstreamId = string.Empty,
                error = summary,
                attempts = attempt
            }
        }).ConfigureAwait(false);
        throw new InvalidOperationException(summary, lastException);
    }

    /// <summary>按给定设置参与加权选择（启用且权重大于 0）的来源 ID 集合。</summary>
    public HashSet<string> GetEligibleProviderIds(QuoteRequestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return GetEnabledCandidates(options.ProviderSettings, options.ExtraProviders)
            .Select(candidate => candidate.Provider.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private List<(IQuoteProvider Provider, QuoteProviderConfig Config)> GetEnabledCandidates(
        IReadOnlyDictionary<string, QuoteProviderConfig> settings,
        IReadOnlyList<IQuoteProvider>? extraProviders)
    {
        List<(IQuoteProvider Provider, QuoteProviderConfig Config)> candidates = [];
        lock (_providerSettingsSync)
        {
            foreach (IQuoteProvider provider in _providers.Concat(extraProviders ?? []))
            {
                QuoteProviderConfig config = settings.TryGetValue(provider.Id, out QuoteProviderConfig? existing)
                    && existing is not null
                        ? SnapshotConfig(existing)
                        : CreateDefaultConfig(provider);

                if (config.IsEnabled && config.Weight > 0)
                {
                    candidates.Add((provider, config));
                }
            }
        }

        return candidates;
    }

    private static QuoteProviderConfig CreateDefaultConfig(IQuoteProvider provider)
    {
        return new QuoteProviderConfig
        {
            IsEnabled = provider.IsEnabledByDefault,
            Weight = provider.DefaultWeight,
            IsAiReviewEnabled = provider.IsAiReviewEnabledByDefault,
            Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static QuoteProviderConfig SnapshotConfig(QuoteProviderConfig source)
    {
        return new QuoteProviderConfig
        {
            IsEnabled = source.IsEnabled,
            Weight = source.Weight,
            IsAiReviewEnabled = source.IsAiReviewEnabled,
            Options = new Dictionary<string, string>(
                source.Options ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase)
        };
    }

    private static (IQuoteProvider Provider, QuoteProviderConfig Config) SelectWeighted(
        IReadOnlyList<(IQuoteProvider Provider, QuoteProviderConfig Config)> candidates)
    {
        long totalWeight = candidates.Sum(item => (long)Math.Max(0, item.Config.Weight));
        long ticket = Random.Shared.NextInt64(totalWeight);
        foreach ((IQuoteProvider provider, QuoteProviderConfig config) in candidates)
        {
            if (ticket < config.Weight)
            {
                return (provider, config);
            }

            ticket -= config.Weight;
        }

        return candidates[^1];
    }

    private static void NormalizeIdentity(IQuoteProvider provider, QuoteContent quote)
    {
        quote.ProviderId = string.IsNullOrWhiteSpace(quote.ProviderId) ? provider.Id : quote.ProviderId;
        quote.ProviderName = string.IsNullOrWhiteSpace(quote.ProviderName)
            ? provider.DisplayName
            : quote.ProviderName;
        quote.RequestMethod = string.IsNullOrWhiteSpace(quote.RequestMethod) ? "GET" : quote.RequestMethod;
        quote.RequestUrl ??= string.Empty;
        if (string.IsNullOrWhiteSpace(quote.UpstreamId))
        {
            quote.UpstreamId = RhesisHttp.MakeContentDerivedId(quote.Content);
            quote.UpstreamIdKind = "content-sha256";
        }
        else if (string.IsNullOrWhiteSpace(quote.UpstreamIdKind))
        {
            quote.UpstreamIdKind = "native";
        }
    }

    private async Task WriteAttemptAuditAsync(
        string refreshSessionId,
        string itemId,
        IQuoteProvider provider,
        QuoteContent? quote,
        string requestUrl,
        bool success,
        string error,
        int attempt)
    {
        await WriteAuditSafeAsync(new AuditLogEvent
        {
            Stage = "quote_request",
            RefreshSessionId = refreshSessionId,
            ItemId = itemId,
            ProviderId = quote?.ProviderId ?? provider.Id,
            UpstreamId = quote?.UpstreamId ?? string.Empty,
            CorrelationId = itemId,
            Data = new
            {
                success,
                attempt,
                providerId = quote?.ProviderId ?? provider.Id,
                providerName = quote?.ProviderName ?? provider.DisplayName,
                requestUrl,
                upstreamId = quote?.UpstreamId ?? string.Empty,
                upstreamIdKind = quote?.UpstreamIdKind ?? string.Empty,
                contentLength = quote?.Content?.Length ?? 0,
                error
            }
        }).ConfigureAwait(false);
    }

    private async Task WriteAuditSafeAsync(AuditLogEvent auditEvent)
    {
        try
        {
            await _audit.WriteAsync(auditEvent, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // 审计失败不应阻断名句获取或搞崩宿主。
        }
    }
}
