using System.Net.Http;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IslandQuotes.Core;
using IslandQuotes.Models;

namespace IslandQuotes.Services;

/// <summary>调用 OpenAI、Anthropic 或 Gemini 协议执行失败关闭的 AI 内容审查。</summary>
public sealed class AiReviewService
{
    private static readonly HttpClient SharedHttpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static readonly Regex SensitiveAssignment = new(
        @"(?i)\b(authorization|x-api-key|x-goog-api-key|api[_-]?key|access[_-]?token|refresh[_-]?token|secret|cookie|password)\b([""']?)(\s*[:=]\s*)(?:bearer\s+)?[^\s,;""'&}]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BearerValue = new(
        @"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FencedJson = new(
        @"```(?:json)?\s*(?<content>.*?)```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private readonly PluginSettingsStore _settings;
    private readonly AuditLogService _audit;

    /// <summary>使用设置存储和审计日志服务构造 AI 审查客户端。</summary>
    public AiReviewService(PluginSettingsStore settings, AuditLogService audit)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <summary>使用当前活动供应商审查一条第三方引用。</summary>
    public async Task<AiReviewResult> ReviewAsync(
        QuoteContent quote,
        string refreshSessionId,
        string itemId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(quote);
        ct.ThrowIfCancellationRequested();

        AiProviderProfile? profile = _settings.GetActiveProfile();
        if (profile is null)
        {
            string correlationId = Guid.NewGuid().ToString("N");
            AuditOptions auditOptions = ReadAuditOptions();
            string error = _settings.ValidateActiveProfile() ?? "未找到有效的活动 AI 供应商配置。";
            var failure = CreateFailureResult(error);
            await TryWriteAiRequestAuditAsync(
                quote,
                refreshSessionId ?? string.Empty,
                itemId ?? string.Empty,
                correlationId,
                failure,
                requestSent: false,
                preparationError: error,
                auditOptions,
                secrets: [],
                ct).ConfigureAwait(false);
            await TryWriteAiReviewAuditAsync(
                quote,
                refreshSessionId ?? string.Empty,
                itemId ?? string.Empty,
                correlationId,
                failure,
                responseBody: string.Empty,
                rawContent: string.Empty,
                rawReasoning: string.Empty,
                auditOptions,
                secrets: [],
                ct).ConfigureAwait(false);
            return failure;
        }

        return await ReviewCoreAsync(
            quote,
            profile,
            refreshSessionId ?? string.Empty,
            itemId ?? string.Empty,
            ct).ConfigureAwait(false);
    }

    /// <summary>使用指定供应商审查一条第三方引用，不改变活动供应商。</summary>
    public Task<AiReviewResult> ReviewAsync(
        QuoteContent quote,
        AiProviderProfile profile,
        string refreshSessionId,
        string itemId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(quote);
        ArgumentNullException.ThrowIfNull(profile);
        return ReviewCoreAsync(
            quote,
            profile,
            refreshSessionId ?? string.Empty,
            itemId ?? string.Empty,
            ct);
    }

    /// <summary>用一条内置安全样例测试指定供应商的连通性和 JSON 输出能力。</summary>
    public Task<AiReviewResult> TestAsync(AiProviderProfile profile, string? content = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var quote = new QuoteContent
        {
            Content = string.IsNullOrWhiteSpace(content) ? "路虽远，行则将至；事虽难，做则必成。" : content.Trim(),
            Title = "连接测试",
            Author = "IslandQuotes",
            Source = "内置审查测试",
            Catalog = "connectivity-test",
            UpstreamId = "ai-connectivity-test",
            UpstreamIdKind = "fixture",
            ProviderId = "builtin-test",
            ProviderName = "内置测试",
            RequestMethod = "LOCAL",
            RequestUrl = string.Empty,
            FetchedAt = DateTimeOffset.Now
        };

        return ReviewCoreAsync(
            quote,
            profile,
            refreshSessionId: "ai-connectivity-test",
            itemId: $"ai-test-{Guid.NewGuid():N}",
            ct);
    }

    /// <summary>向指定供应商查询可用模型列表；不写入审计日志，失败时抛出带中文说明的异常。</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(AiProviderProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        string baseUrl = (profile.BaseUrl ?? string.Empty).Trim();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Base URL 必须是有效的 HTTP 或 HTTPS 地址。");
        }

        var secrets = new List<string>();
        Dictionary<string, string> extraHeaders = ParseExtraHeaders(profile.ExtraHeadersJson);
        AddSensitiveHeaderValues(extraHeaders, secrets);
        AddSecret(secrets, PluginSettingsStore.ResolveSecret(profile.ApiKey ?? string.Empty));

        int timeoutSeconds = Math.Clamp(profile.TimeoutSeconds > 0 ? profile.TimeoutSeconds : 45, 5, 180);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildModelsEndpoint(uri, profile.Protocol));
        AddRequestHeaders(request, profile, extraHeaders);

        using HttpResponseMessage response = await SharedHttpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
            .ConfigureAwait(false);
        string responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string apiMessage = ExtractApiErrorMessage(responseBody);
            string detail = string.IsNullOrWhiteSpace(apiMessage) ? string.Empty : $"：{apiMessage}";
            throw new InvalidOperationException(SanitizeText(
                $"获取模型列表返回 HTTP {(int)response.StatusCode} ({response.ReasonPhrase}){detail}",
                secrets));
        }

        using JsonDocument document = JsonDocument.Parse(responseBody);
        JsonElement root = document.RootElement;
        var models = new List<string>();
        foreach (string arrayName in new[] { "data", "models" })
        {
            if (!TryGetPropertyIgnoreCase(root, arrayName, out JsonElement array) ||
                array.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement entry in array.EnumerateArray())
            {
                string? id = entry.ValueKind == JsonValueKind.String
                    ? entry.GetString()
                    : GetStringProperty(entry, "id") ?? GetStringProperty(entry, "name");
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (id.StartsWith("models/", StringComparison.OrdinalIgnoreCase)) id = id["models/".Length..];
                models.Add(id.Trim());
            }
        }

        return models
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Uri BuildModelsEndpoint(Uri baseUri, AiProtocol protocol)
    {
        string basePath = baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        foreach (string suffix in new[] { "/chat/completions", "/messages" })
        {
            if (basePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                basePath = basePath[..^suffix.Length];
                break;
            }
        }

        if (protocol == AiProtocol.GeminiGenerateContent)
        {
            int modelsIndex = basePath.LastIndexOf("/models", StringComparison.OrdinalIgnoreCase);
            if (modelsIndex >= 0) basePath = basePath[..modelsIndex];
        }

        return new Uri(basePath + "/models" + baseUri.Query, UriKind.Absolute);
    }

    /// <summary>
    /// 把多条引用合并到一次请求中审查，所有条目共用同一份系统提示词；返回结果与输入顺序一一对应。
    /// 只有一条时与 <see cref="ReviewAsync(QuoteContent, string, string, CancellationToken)"/> 等价。
    /// </summary>
    public async Task<IReadOnlyList<AiReviewResult>> ReviewBatchAsync(
        IReadOnlyList<(string ItemId, QuoteContent Quote)> items,
        string refreshSessionId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ct.ThrowIfCancellationRequested();
        if (items.Count == 0) return [];

        AiProviderProfile? profile = _settings.GetActiveProfile();
        if (items.Count == 1 || profile is null)
        {
            var results = new AiReviewResult[items.Count];
            for (int i = 0; i < items.Count; i++)
                results[i] = await ReviewAsync(items[i].Quote, refreshSessionId, items[i].ItemId, ct).ConfigureAwait(false);
            return results;
        }

        ReviewTarget[] targets = items
            .Select(static item => new ReviewTarget(
                item.ItemId ?? string.Empty,
                item.Quote ?? throw new ArgumentException("待审引用不能为空。", nameof(items))))
            .ToArray();
        return await ReviewCoreAsync(targets, profile, refreshSessionId ?? string.Empty, ct).ConfigureAwait(false);
    }

    private async Task<AiReviewResult> ReviewCoreAsync(
        QuoteContent quote,
        AiProviderProfile profile,
        string refreshSessionId,
        string itemId,
        CancellationToken ct)
    {
        AiReviewResult[] results = await ReviewCoreAsync(
            [new ReviewTarget(itemId, quote)],
            profile,
            refreshSessionId,
            ct).ConfigureAwait(false);
        return results[0];
    }

    private async Task<AiReviewResult[]> ReviewCoreAsync(
        IReadOnlyList<ReviewTarget> targets,
        AiProviderProfile profile,
        string refreshSessionId,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        bool isBatch = targets.Count > 1;
        string correlationId = Guid.NewGuid().ToString("N");
        AuditOptions auditOptions = ReadAuditOptions();
        PluginSettings settingsSnapshot = _settings.Settings;
        AiSettings? aiSettings = settingsSnapshot.Ai;
        AiPromptOptions promptOptions = BuildPromptOptions(aiSettings);
        int settingsTimeout = aiSettings?.RequestTimeoutSeconds ?? 45;
        int timeoutSeconds = Math.Clamp(
            profile.TimeoutSeconds > 0 ? profile.TimeoutSeconds : settingsTimeout,
            5,
            180);

        (string systemPrompt, string userPrompt) = isBatch
            ? AiReviewPrompt.CreateBatch(targets.Select(static target => target.Quote).ToArray(), promptOptions)
            : AiReviewPrompt.Create(targets[0].Quote, promptOptions);
        AiReviewResult result = CreateBaseResult(profile, systemPrompt, userPrompt);
        var secrets = new List<string>();
        var extraHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string requestBody = string.Empty;
        string? preparationError = null;

        try
        {
            string? validationError = ValidateProfileForRequest(profile);
            if (validationError is not null) throw new InvalidOperationException(validationError);

            Uri endpoint = BuildEndpoint(profile);
            result.AiEndpoint = SanitizeEndpoint(endpoint);

            extraHeaders = ParseExtraHeaders(profile.ExtraHeadersJson);
            AddSensitiveHeaderValues(extraHeaders, secrets);

            string resolvedKey = PluginSettingsStore.ResolveSecret(profile.ApiKey ?? string.Empty);
            AddSecret(secrets, resolvedKey);

            requestBody = BuildRequestBody(profile, systemPrompt, userPrompt, GetMaxOutputTokens(targets.Count));
            result.RequestBodyJson = requestBody;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or JsonException or FormatException)
        {
            preparationError = SanitizeText(ex.Message, secrets);
            result.Error = preparationError;
            AiReviewResult[] failed = SplitResult(result, targets.Count);
            await TryWriteAiRequestAuditAsync(
                targets,
                refreshSessionId,
                correlationId,
                result,
                requestSent: false,
                preparationError,
                auditOptions,
                secrets,
                ct).ConfigureAwait(false);
            await TryWriteAiReviewAuditAsync(
                targets,
                refreshSessionId,
                correlationId,
                failed,
                responseBody: string.Empty,
                rawContent: string.Empty,
                rawReasoning: string.Empty,
                auditOptions,
                secrets,
                ct).ConfigureAwait(false);
            return failed;
        }

        await TryWriteAiRequestAuditAsync(
            targets,
            refreshSessionId,
            correlationId,
            result,
            requestSent: true,
            preparationError: null,
            auditOptions,
            secrets,
            ct).ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();
        string responseBody = string.Empty;
        string rawContent = string.Empty;
        string rawReasoning = string.Empty;
        AiReviewResult[]? parsedResults = null;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(profile));
            request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
            AddRequestHeaders(request, profile, extraHeaders);

            using HttpResponseMessage response = await SharedHttpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            string requestId = GetRequestId(response);
            result.AiRequestId = requestId;
            responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                string apiMessage = ExtractApiErrorMessage(responseBody);
                string detail = string.IsNullOrWhiteSpace(apiMessage)
                    ? string.Empty
                    : $"：{apiMessage}";
                result.Error = SanitizeText(
                    $"AI 返回 HTTP {(int)response.StatusCode} ({response.ReasonPhrase}){detail}",
                    secrets);
                result.RawResponse = SanitizeText(responseBody, secrets);
            }
            else if (!TryExtractProtocolContent(
                         profile.Protocol,
                         responseBody,
                         out rawContent,
                         out rawReasoning,
                         out string extractionError))
            {
                result.Error = SanitizeText(extractionError, secrets);
                result.RawResponse = SanitizeText(responseBody, secrets);
            }
            else if (isBatch)
            {
                IReadOnlyList<AiReviewResult> parsed = ParseBatchReviewResponse(rawContent, targets.Count, rawReasoning);
                parsedResults = new AiReviewResult[targets.Count];
                for (int i = 0; i < parsed.Count; i++)
                {
                    parsedResults[i] = CloneBase(result);
                    CopyParsedResult(parsed[i], parsedResults[i]);
                }
            }
            else
            {
                AiReviewResult parsed = ParseReviewResponse(rawContent, rawReasoning);
                CopyParsedResult(parsed, result);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            result.IsSuccess = false;
            result.Risk = RiskLevel.Unknown;
            result.Confidence = 0;
            result.Error = "AI 请求已取消。";
            result.DurationMs = stopwatch.ElapsedMilliseconds;

            await TryWriteAiReviewAuditAsync(
                targets,
                refreshSessionId,
                correlationId,
                SplitResult(result, targets.Count),
                responseBody,
                rawContent,
                rawReasoning,
                auditOptions,
                secrets,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            result.IsSuccess = false;
            result.Risk = RiskLevel.Unknown;
            result.Confidence = 0;
            result.Error = $"AI 请求超时（{timeoutSeconds} 秒）。";
        }
        catch (HttpRequestException ex)
        {
            result.IsSuccess = false;
            result.Risk = RiskLevel.Unknown;
            result.Confidence = 0;
            result.Error = SanitizeText($"AI 网络请求失败：{ex.Message}", secrets);
        }
        catch (Exception ex)
        {
            result.IsSuccess = false;
            result.Risk = RiskLevel.Unknown;
            result.Confidence = 0;
            result.Error = SanitizeText($"AI 请求失败：{ex.GetType().Name}: {ex.Message}", secrets);
        }

        stopwatch.Stop();
        result.DurationMs = stopwatch.ElapsedMilliseconds;
        AiReviewResult[] results = parsedResults ?? SplitResult(result, targets.Count);
        foreach (AiReviewResult item in results)
        {
            item.DurationMs = result.DurationMs;
            item.AiRequestId = result.AiRequestId;
        }

        await TryWriteAiReviewAuditAsync(
            targets,
            refreshSessionId,
            correlationId,
            results,
            responseBody,
            rawContent,
            rawReasoning,
            auditOptions,
            secrets,
            ct).ConfigureAwait(false);

        return results;
    }

    private readonly record struct ReviewTarget(string ItemId, QuoteContent Quote);

    /// <summary>批量审查需要为每条结果预留输出长度；单条保持原有上限。</summary>
    private static int GetMaxOutputTokens(int itemCount)
        => itemCount <= 1 ? 1024 : Math.Min(8192, 512 + itemCount * 384);

    /// <summary>把整次请求的失败结果复制给每个条目，保证批量请求失败时每条都按失败关闭处理。</summary>
    private static AiReviewResult[] SplitResult(AiReviewResult result, int count)
    {
        if (count == 1) return [result];

        var results = new AiReviewResult[count];
        for (int i = 0; i < count; i++)
        {
            results[i] = CloneBase(result);
            CopyParsedResult(result, results[i]);
        }
        return results;
    }

    private static AiReviewResult CloneBase(AiReviewResult source)
    {
        return new AiReviewResult
        {
            IsSuccess = false,
            Risk = RiskLevel.Unknown,
            Confidence = 0,
            AiProfileId = source.AiProfileId,
            AiProviderName = source.AiProviderName,
            AiModel = source.AiModel,
            AiProtocol = source.AiProtocol,
            AiEndpoint = source.AiEndpoint,
            AiRequestId = source.AiRequestId,
            RequestBodyJson = source.RequestBodyJson,
            SystemPrompt = source.SystemPrompt,
            UserPrompt = source.UserPrompt,
            DurationMs = source.DurationMs
        };
    }

    private static AiReviewResult CreateBaseResult(
        AiProviderProfile profile,
        string systemPrompt,
        string userPrompt)
    {
        return new AiReviewResult
        {
            IsSuccess = false,
            Risk = RiskLevel.Unknown,
            Confidence = 0,
            AiProfileId = profile.Id ?? string.Empty,
            AiProviderName = profile.DisplayName ?? string.Empty,
            AiModel = profile.Model ?? string.Empty,
            AiProtocol = profile.Protocol.ToString(),
            AiEndpoint = string.Empty,
            RequestBodyJson = string.Empty,
            SystemPrompt = systemPrompt,
            UserPrompt = userPrompt,
            DurationMs = 0
        };
    }

    private static AiReviewResult CreateFailureResult(string error)
    {
        return new AiReviewResult
        {
            IsSuccess = false,
            Risk = RiskLevel.Unknown,
            Confidence = 0,
            Error = error,
            RawResponse = string.Empty,
            RawReasoning = string.Empty
        };
    }
    private static string? ValidateProfileForRequest(AiProviderProfile profile)
    {
        if (!profile.IsEnabled) return "当前 AI 供应商已禁用。";
        if (!Enum.IsDefined(typeof(AiProtocol), profile.Protocol)) return "AI 协议无效。";

        string baseUrl = (profile.BaseUrl ?? string.Empty).Trim();
        if (baseUrl.Length == 0) return "Base URL 不能为空。";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "Base URL 必须是有效的 HTTP 或 HTTPS 地址。";
        }

        if (string.IsNullOrWhiteSpace(profile.Model)) return "模型名称不能为空。";
        if (profile.RequiresApiKey &&
            string.IsNullOrWhiteSpace(PluginSettingsStore.ResolveSecret(profile.ApiKey ?? string.Empty)))
        {
            return "当前 AI 供应商需要 API Key；可直接填写或使用 env:环境变量名。";
        }

        return null;
    }

    private static Uri BuildEndpoint(AiProviderProfile profile)
    {
        string baseUrl = (profile.BaseUrl ?? string.Empty).Trim();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Base URL 必须是有效的 HTTP 或 HTTPS 地址。");
        }

        string path = uri.AbsolutePath.TrimEnd('/');
        return profile.Protocol switch
        {
            AiProtocol.OpenAiCompatible => BuildOpenAiEndpoint(uri, path),
            AiProtocol.AnthropicMessages => BuildAnthropicEndpoint(uri, path),
            AiProtocol.GeminiGenerateContent => BuildGeminiEndpoint(uri, path, profile.Model),
            _ => throw new InvalidOperationException("不支持的 AI 协议。")
        };
    }

    private static Uri BuildOpenAiEndpoint(Uri baseUri, string path)
    {
        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return baseUri;

        return AppendPath(baseUri, "chat/completions");
    }

    private static Uri BuildAnthropicEndpoint(Uri baseUri, string path)
    {
        return path.EndsWith("/messages", StringComparison.OrdinalIgnoreCase)
            ? baseUri
            : AppendPath(baseUri, "messages");
    }

    private static Uri BuildGeminiEndpoint(Uri baseUri, string path, string model)
    {
        if (path.EndsWith(":generateContent", StringComparison.OrdinalIgnoreCase))
            return baseUri;

        string normalizedModel = NormalizeGeminiModel(model);
        string encodedModel = Uri.EscapeDataString(normalizedModel);

        if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
            return AppendPath(baseUri, encodedModel + ":generateContent");

        int modelsIndex = path.LastIndexOf("/models/", StringComparison.OrdinalIgnoreCase);
        if (modelsIndex >= 0)
        {
            string modelSegment = path[(modelsIndex + "/models/".Length)..];
            if (modelSegment.Length > 0 &&
                !modelSegment.Contains('/') &&
                !modelSegment.Contains(':'))
            {
                return AppendRawSuffix(baseUri, ":generateContent");
            }
        }

        return AppendPath(baseUri, "models/" + encodedModel + ":generateContent");
    }

    private static string NormalizeGeminiModel(string model)
    {
        string normalized = (model ?? string.Empty).Trim().TrimStart('/');
        if (normalized.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["models/".Length..];
        if (normalized.Length == 0) throw new InvalidOperationException("Gemini 模型名称不能为空。");
        return normalized;
    }

    private static Uri AppendPath(Uri baseUri, string pathSuffix)
    {
        string basePath = baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        string value = basePath + "/" + pathSuffix.TrimStart('/');
        if (baseUri.Query.Length > 0) value += baseUri.Query;
        if (baseUri.Fragment.Length > 0) value += baseUri.Fragment;
        return new Uri(value, UriKind.Absolute);
    }

    private static Uri AppendRawSuffix(Uri baseUri, string suffix)
    {
        string basePath = baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        string value = basePath + suffix;
        if (baseUri.Query.Length > 0) value += baseUri.Query;
        if (baseUri.Fragment.Length > 0) value += baseUri.Fragment;
        return new Uri(value, UriKind.Absolute);
    }

    private static Dictionary<string, string> ParseExtraHeaders(string? json)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return headers;

        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new FormatException("额外请求头 JSON 必须是对象。");

        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            string? value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => null,
                _ => throw new FormatException($"额外请求头「{property.Name}」只支持字符串、数字或布尔值。")
            };

            if (value is not null) headers[property.Name] = value;
        }

        return headers;
    }

    private static void AddSensitiveHeaderValues(
        IReadOnlyDictionary<string, string> headers,
        ICollection<string> secrets)
    {
        foreach ((string name, string value) in headers)
        {
            if (IsSensitiveName(name)) AddSecret(secrets, value);
        }
    }

    private static void AddSecret(ICollection<string> secrets, string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return;
        string value = secret.Trim();
        if (!secrets.Contains(value, StringComparer.Ordinal)) secrets.Add(value);
    }

    private static bool IsSensitiveName(string name)
        => name.Contains("key", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("password", StringComparison.OrdinalIgnoreCase);

    private static string BuildRequestBody(
        AiProviderProfile profile,
        string systemPrompt,
        string userPrompt,
        int maxOutputTokens)
    {
        object body = profile.Protocol switch
        {
            AiProtocol.OpenAiCompatible => new Dictionary<string, object?>
            {
                ["model"] = profile.Model,
                ["messages"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["role"] = "system",
                        ["content"] = systemPrompt
                    },
                    new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = userPrompt
                    }
                },
                ["temperature"] = 0.1
            },
            AiProtocol.AnthropicMessages => new Dictionary<string, object?>
            {
                ["model"] = profile.Model,
                ["max_tokens"] = maxOutputTokens,
                ["temperature"] = 0.1,
                ["system"] = systemPrompt,
                ["messages"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = userPrompt
                    }
                }
            },
            AiProtocol.GeminiGenerateContent => new Dictionary<string, object?>
            {
                ["system_instruction"] = new Dictionary<string, object?>
                {
                    ["parts"] = new object[]
                    {
                        new Dictionary<string, object?> { ["text"] = systemPrompt }
                    }
                },
                ["contents"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["parts"] = new object[]
                        {
                            new Dictionary<string, object?> { ["text"] = userPrompt }
                        }
                    }
                },
                ["generationConfig"] = new Dictionary<string, object?>
                {
                    ["temperature"] = 0.1,
                    ["maxOutputTokens"] = maxOutputTokens,
                    ["responseMimeType"] = "application/json"
                }
            },
            _ => throw new InvalidOperationException("不支持的 AI 协议。")
        };

        return JsonSerializer.Serialize(body, JsonDefaults.Compact);
    }

    private static void AddRequestHeaders(
        HttpRequestMessage request,
        AiProviderProfile profile,
        IReadOnlyDictionary<string, string> extraHeaders)
    {
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        foreach ((string name, string value) in extraHeaders)
        {
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(name, value))
                throw new FormatException($"无法添加额外请求头「{name}」。");
        }

        string resolvedKey = PluginSettingsStore.ResolveSecret(profile.ApiKey ?? string.Empty);
        switch (profile.Protocol)
        {
            case AiProtocol.OpenAiCompatible when resolvedKey.Length > 0:
                request.Headers.Remove("Authorization");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", resolvedKey);
                break;

            case AiProtocol.AnthropicMessages:
                request.Headers.Remove("anthropic-version");
                request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                if (resolvedKey.Length > 0)
                {
                    request.Headers.Remove("x-api-key");
                    request.Headers.TryAddWithoutValidation("x-api-key", resolvedKey);
                }
                break;

            case AiProtocol.GeminiGenerateContent when resolvedKey.Length > 0:
                request.Headers.Remove("x-goog-api-key");
                request.Headers.TryAddWithoutValidation("x-goog-api-key", resolvedKey);
                break;
        }
    }

    private static string GetRequestId(HttpResponseMessage response)
    {
        string[] headerNames = ["x-request-id", "request-id", "cf-ray"];
        foreach (string headerName in headerNames)
        {
            string value = GetFirstHeaderValue(response.Headers, headerName);
            if (value.Length == 0) value = GetFirstHeaderValue(response.Content.Headers, headerName);
            if (value.Length > 0) return value;
        }

        return string.Empty;
    }

    private static string GetFirstHeaderValue(HttpHeaders headers, string name)
    {
        if (!headers.TryGetValues(name, out IEnumerable<string>? values)) return string.Empty;
        string? value = values.FirstOrDefault(static item => !string.IsNullOrWhiteSpace(item));
        return value?.Trim() ?? string.Empty;
    }

    private static string ExtractApiErrorMessage(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody)) return string.Empty;

        try
        {
            using JsonDocument document = JsonDocument.Parse(responseBody, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (TryGetPropertyIgnoreCase(root, "error", out JsonElement error))
                {
                    if (error.ValueKind == JsonValueKind.String)
                        return error.GetString() ?? string.Empty;

                    if (error.ValueKind == JsonValueKind.Object &&
                        TryGetPropertyIgnoreCase(error, "message", out JsonElement errorMessage) &&
                        errorMessage.ValueKind == JsonValueKind.String)
                    {
                        return errorMessage.GetString() ?? string.Empty;
                    }
                }

                if (TryGetPropertyIgnoreCase(root, "message", out JsonElement message) &&
                    message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString() ?? string.Empty;
                }
            }
        }
        catch (JsonException)
        {
            // 非 JSON 错误正文会退回为截断后的纯文本。
        }

        string compact = responseBody.Trim().Replace('\r', ' ').Replace('\n', ' ');
        return compact.Length <= 500 ? compact : compact[..500] + "...";
    }

    private static string SanitizeEndpoint(Uri endpoint)
    {
        var builder = new UriBuilder(endpoint);
        bool changed = builder.UserName.Length > 0 || builder.Password.Length > 0;
        builder.UserName = string.Empty;
        builder.Password = string.Empty;

        if (!string.IsNullOrEmpty(builder.Query))
        {
            string[] parts = builder.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                int separator = parts[i].IndexOf('=');
                string name = separator >= 0 ? parts[i][..separator] : parts[i];
                string decodedName;
                try
                {
                    decodedName = Uri.UnescapeDataString(name);
                }
                catch (UriFormatException)
                {
                    decodedName = name;
                }

                if (IsSensitiveName(decodedName))
                {
                    parts[i] = separator >= 0 ? name + "=[REDACTED]" : "[REDACTED]";
                    changed = true;
                }
            }

            if (changed) builder.Query = string.Join("&", parts);
        }

        return changed ? builder.Uri.ToString() : endpoint.ToString();
    }

    private static string SanitizeText(string? text, IEnumerable<string>? secrets)
    {
        string result = text ?? string.Empty;
        if (secrets is not null)
        {
            foreach (string secret in secrets.Distinct(StringComparer.Ordinal))
            {
                if (secret.Length > 0)
                    result = result.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            }
        }

        result = SensitiveAssignment.Replace(result, "$1$2$3[REDACTED]");
        result = BearerValue.Replace(result, "Bearer [REDACTED]");
        return result;
    }

    private AuditOptions ReadAuditOptions()
    {
        PluginSettings settings = _settings.Settings;
        return new AuditOptions(
            Enabled: settings.Log?.Enabled ?? true,
            FullPrompts: settings.Review?.LogFullPrompts ?? true,
            FullResponses: settings.Review?.LogFullResponses ?? true);
    }

    /// <summary>从设置构造审查提示词选项；严格度与标签在读取时归一化，异常值按均衡/空处理。</summary>
    private static AiPromptOptions BuildPromptOptions(AiSettings? aiSettings)
    {
        if (aiSettings is null) return AiPromptOptions.Default;

        ReviewStrictness strictness = Enum.IsDefined(aiSettings.Strictness)
            ? aiSettings.Strictness
            : ReviewStrictness.Balanced;
        return new AiPromptOptions(
            aiSettings.SystemPrompt,
            strictness,
            QuoteTagCatalog.Normalize(aiSettings.BlockedTags));
    }

    private static void CopyParsedResult(AiReviewResult source, AiReviewResult destination)
    {
        destination.IsSuccess = source.IsSuccess;
        destination.Risk = source.Risk;
        destination.Confidence = source.Confidence;
        destination.Reason = source.Reason;
        destination.Categories = source.Categories.ToList();
        destination.RawResponse = source.RawResponse;
        destination.RawReasoning = source.RawReasoning;
        destination.Error = source.Error;
    }

    private async Task TryWriteAiRequestAuditAsync(
        QuoteContent quote,
        string refreshSessionId,
        string itemId,
        string correlationId,
        AiReviewResult result,
        bool requestSent,
        string? preparationError,
        AuditOptions options,
        IEnumerable<string> secrets,
        CancellationToken ct)
        => await TryWriteAiRequestAuditAsync(
            [new ReviewTarget(itemId, quote)],
            refreshSessionId,
            correlationId,
            result,
            requestSent,
            preparationError,
            options,
            secrets,
            ct).ConfigureAwait(false);

    /// <summary>每次请求只写一条 ai_request；批量请求的条目 id 以逗号连接。</summary>
    private async Task TryWriteAiRequestAuditAsync(
        IReadOnlyList<ReviewTarget> targets,
        string refreshSessionId,
        string correlationId,
        AiReviewResult result,
        bool requestSent,
        string? preparationError,
        AuditOptions options,
        IEnumerable<string> secrets,
        CancellationToken ct)
    {
        if (!options.Enabled) return;

        var data = new Dictionary<string, object?>
        {
            ["correlationId"] = correlationId,
            ["aiProfileId"] = result.AiProfileId,
            ["aiProviderName"] = result.AiProviderName,
            ["protocol"] = result.AiProtocol,
            ["model"] = result.AiModel,
            ["endpoint"] = result.AiEndpoint,
            ["requestSent"] = requestSent,
            ["batchSize"] = targets.Count,
            ["requestBody"] = options.FullPrompts
                ? SanitizeText(result.RequestBodyJson, secrets)
                : "[omitted]",
            ["systemPrompt"] = options.FullPrompts
                ? SanitizeText(result.SystemPrompt, secrets)
                : "[omitted]",
            ["userPrompt"] = options.FullPrompts
                ? SanitizeText(result.UserPrompt, secrets)
                : "[omitted]",
            ["preparationError"] = SanitizeText(preparationError, secrets)
        };

        var evt = new AuditLogEvent
        {
            Stage = "ai_request",
            RefreshSessionId = refreshSessionId,
            ItemId = string.Join(",", targets.Select(static target => target.ItemId)),
            ProviderId = string.Join(",", targets.Select(static target => target.Quote.ProviderId ?? string.Empty).Distinct()),
            UpstreamId = string.Join(",", targets.Select(static target => target.Quote.UpstreamId ?? string.Empty)),
            CorrelationId = correlationId,
            Data = data
        };

        await TryWriteAuditBestEffortAsync(evt, ct).ConfigureAwait(false);
    }

    private async Task TryWriteAiReviewAuditAsync(
        QuoteContent quote,
        string refreshSessionId,
        string itemId,
        string correlationId,
        AiReviewResult result,
        string responseBody,
        string rawContent,
        string rawReasoning,
        AuditOptions options,
        IEnumerable<string> secrets,
        CancellationToken ct)
        => await TryWriteAiReviewAuditAsync(
            [new ReviewTarget(itemId, quote)],
            refreshSessionId,
            correlationId,
            [result],
            responseBody,
            rawContent,
            rawReasoning,
            options,
            secrets,
            ct).ConfigureAwait(false);

    /// <summary>每个条目各写一条 ai_review，同一请求的条目共享 correlationId。</summary>
    private async Task TryWriteAiReviewAuditAsync(
        IReadOnlyList<ReviewTarget> targets,
        string refreshSessionId,
        string correlationId,
        IReadOnlyList<AiReviewResult> results,
        string responseBody,
        string rawContent,
        string rawReasoning,
        AuditOptions options,
        IEnumerable<string> secrets,
        CancellationToken ct)
    {
        if (!options.Enabled) return;

        for (int i = 0; i < targets.Count; i++)
        {
            AiReviewResult result = results[i];
            var data = new Dictionary<string, object?>
            {
                ["correlationId"] = correlationId,
                ["aiProfileId"] = result.AiProfileId,
                ["aiProviderName"] = result.AiProviderName,
                ["protocol"] = result.AiProtocol,
                ["model"] = result.AiModel,
                ["isSuccess"] = result.IsSuccess,
                ["risk"] = result.Risk,
                ["confidence"] = result.Confidence,
                ["categories"] = result.Categories,
                ["reason"] = SanitizeText(result.Reason, secrets),
                ["error"] = SanitizeText(result.Error, secrets),
                ["requestId"] = result.AiRequestId,
                ["durationMs"] = result.DurationMs,
                ["batchSize"] = targets.Count,
                ["batchIndex"] = i + 1,
                ["responseBody"] = options.FullResponses
                    ? SanitizeText(responseBody, secrets)
                    : "[omitted]",
                ["rawContent"] = options.FullResponses
                    ? SanitizeText(rawContent, secrets)
                    : "[omitted]",
                ["rawReasoning"] = options.FullResponses
                    ? SanitizeText(rawReasoning, secrets)
                    : "[omitted]"
            };

            var evt = new AuditLogEvent
            {
                Stage = "ai_review",
                RefreshSessionId = refreshSessionId,
                ItemId = targets[i].ItemId,
                ProviderId = targets[i].Quote.ProviderId ?? string.Empty,
                UpstreamId = targets[i].Quote.UpstreamId ?? string.Empty,
                CorrelationId = correlationId,
                Data = data
            };

            await TryWriteAuditBestEffortAsync(evt, ct).ConfigureAwait(false);
        }
    }

    private async Task TryWriteAuditBestEffortAsync(AuditLogEvent evt, CancellationToken ct)
    {
        try
        {
            await _audit.WriteAsync(evt, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 调用方取消时不再阻塞关闭流程。
        }
        catch (Exception)
        {
            // 审计故障不能改变失败关闭的审查结论，也不能让宿主崩溃。
        }
    }

    private readonly record struct AuditOptions(bool Enabled, bool FullPrompts, bool FullResponses);
    private static bool TryExtractProtocolContent(
        AiProtocol protocol,
        string responseBody,
        out string content,
        out string reasoning,
        out string error)
    {
        content = string.Empty;
        reasoning = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(responseBody))
        {
            error = "AI 响应正文为空。";
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(responseBody, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "AI 响应 JSON 顶层必须是对象。";
                return false;
            }

            string apiError = GetApiErrorMessage(root);
            if (apiError.Length > 0)
            {
                error = apiError;
                return false;
            }

            return protocol switch
            {
                AiProtocol.OpenAiCompatible => TryExtractOpenAiContent(root, out content, out reasoning, out error),
                AiProtocol.AnthropicMessages => TryExtractAnthropicContent(root, out content, out reasoning, out error),
                AiProtocol.GeminiGenerateContent => TryExtractGeminiContent(root, out content, out reasoning, out error),
                _ => FailExtraction("不支持的 AI 协议。", out error)
            };
        }
        catch (JsonException)
        {
            error = "AI 响应不是有效 JSON。";
            return false;
        }
    }

    private static bool TryExtractOpenAiContent(
        JsonElement root,
        out string content,
        out string reasoning,
        out string error)
    {
        content = string.Empty;
        reasoning = string.Empty;
        error = string.Empty;

        if (!TryGetPropertyIgnoreCase(root, "choices", out JsonElement choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            error = "OpenAI 兼容响应中缺少 choices。";
            return false;
        }

        JsonElement choice = choices[0];
        if (TryGetPropertyIgnoreCase(choice, "message", out JsonElement message) &&
            message.ValueKind == JsonValueKind.Object)
        {
            if (TryGetPropertyIgnoreCase(message, "content", out JsonElement contentElement))
                content = ExtractTextValue(contentElement);
            reasoning = JoinTextProperties(message, "reasoning_content", "reasoning", "thinking");
        }
        else if (TryGetPropertyIgnoreCase(choice, "text", out JsonElement textElement))
        {
            content = ExtractTextValue(textElement);
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            error = "OpenAI 兼容响应的 message.content 为空。";
            return false;
        }

        return true;
    }

    private static bool TryExtractAnthropicContent(
        JsonElement root,
        out string content,
        out string reasoning,
        out string error)
    {
        content = string.Empty;
        reasoning = string.Empty;
        error = string.Empty;

        if (!TryGetPropertyIgnoreCase(root, "content", out JsonElement contentArray) ||
            contentArray.ValueKind != JsonValueKind.Array)
        {
            error = "Anthropic 响应中缺少 content 数组。";
            return false;
        }

        var contentBuilder = new StringBuilder();
        var reasoningBuilder = new StringBuilder();
        foreach (JsonElement part in contentArray.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                AppendText(contentBuilder, part.GetString());
                continue;
            }

            if (part.ValueKind != JsonValueKind.Object) continue;

            string type = GetStringProperty(part, "type") ?? string.Empty;
            if (type.Equals("thinking", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("reasoning", StringComparison.OrdinalIgnoreCase))
            {
                string thinking = GetStringProperty(part, "thinking")
                                  ?? GetStringProperty(part, "text")
                                  ?? string.Empty;
                AppendText(reasoningBuilder, thinking);
                continue;
            }

            AppendText(contentBuilder, GetStringProperty(part, "text"));
        }

        content = contentBuilder.ToString().Trim();
        reasoning = reasoningBuilder.ToString().Trim();
        if (content.Length == 0)
        {
            string stopReason = GetStringProperty(root, "stop_reason") ?? string.Empty;
            error = stopReason.Length > 0
                ? $"Anthropic 未返回文本，stop_reason={stopReason}。"
                : "Anthropic 响应的 content 中没有文本。";
            return false;
        }

        return true;
    }

    private static bool TryExtractGeminiContent(
        JsonElement root,
        out string content,
        out string reasoning,
        out string error)
    {
        content = string.Empty;
        reasoning = string.Empty;
        error = string.Empty;

        if (!TryGetPropertyIgnoreCase(root, "candidates", out JsonElement candidates) ||
            candidates.ValueKind != JsonValueKind.Array ||
            candidates.GetArrayLength() == 0)
        {
            string blockReason = TryGetPropertyIgnoreCase(root, "promptFeedback", out JsonElement feedback)
                ? GetStringProperty(feedback, "blockReason") ?? string.Empty
                : string.Empty;
            error = blockReason.Length > 0
                ? $"Gemini 拒绝生成内容，blockReason={blockReason}。"
                : "Gemini 响应中缺少 candidates。";
            return false;
        }

        JsonElement candidate = candidates[0];
        var contentBuilder = new StringBuilder();
        var reasoningBuilder = new StringBuilder();

        if (TryGetPropertyIgnoreCase(candidate, "content", out JsonElement candidateContent) &&
            candidateContent.ValueKind == JsonValueKind.Object &&
            TryGetPropertyIgnoreCase(candidateContent, "parts", out JsonElement parts) &&
            parts.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement part in parts.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object) continue;

                string text = GetStringProperty(part, "text") ?? string.Empty;
                bool thought = TryGetPropertyIgnoreCase(part, "thought", out JsonElement thoughtElement) &&
                               thoughtElement.ValueKind == JsonValueKind.True;
                if (thought) AppendText(reasoningBuilder, text);
                else AppendText(contentBuilder, text);
            }
        }

        content = contentBuilder.ToString().Trim();
        reasoning = reasoningBuilder.ToString().Trim();
        if (content.Length == 0)
        {
            string finishReason = GetStringProperty(candidate, "finishReason") ?? string.Empty;
            error = finishReason.Length > 0
                ? $"Gemini 未返回文本，finishReason={finishReason}。"
                : "Gemini 响应的 candidate.content.parts 中没有文本。";
            return false;
        }

        return true;
    }

    private static string ExtractTextValue(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString() ?? string.Empty;

            case JsonValueKind.Array:
                var parts = new List<string>();
                foreach (JsonElement item in value.EnumerateArray())
                {
                    string text = ExtractTextValue(item);
                    if (!string.IsNullOrWhiteSpace(text)) parts.Add(text);
                }
                return string.Join("\n", parts);

            case JsonValueKind.Object:
                foreach (string propertyName in new[] { "text", "content", "output_text", "value" })
                {
                    if (TryGetPropertyIgnoreCase(value, propertyName, out JsonElement nested))
                    {
                        string text = ExtractTextValue(nested);
                        if (!string.IsNullOrWhiteSpace(text)) return text;
                    }
                }
                return string.Empty;

            default:
                return string.Empty;
        }
    }

    private static string JoinTextProperties(JsonElement element, params string[] propertyNames)
    {
        var parts = new List<string>();
        foreach (string propertyName in propertyNames)
        {
            if (!TryGetPropertyIgnoreCase(element, propertyName, out JsonElement value)) continue;
            string text = ExtractTextValue(value);
            if (!string.IsNullOrWhiteSpace(text)) parts.Add(text);
        }
        return string.Join("\n", parts);
    }

    private static void AppendText(StringBuilder builder, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (builder.Length > 0) builder.Append('\n');
        builder.Append(text.Trim());
    }

    private static string GetApiErrorMessage(JsonElement root)
    {
        if (TryGetPropertyIgnoreCase(root, "error", out JsonElement error))
        {
            if (error.ValueKind == JsonValueKind.String)
                return error.GetString() ?? string.Empty;

            if (error.ValueKind == JsonValueKind.Object)
            {
                string message = GetStringProperty(error, "message") ?? string.Empty;
                if (message.Length > 0) return message;
            }
        }

        return GetStringProperty(root, "message") ?? string.Empty;
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        return TryGetPropertyIgnoreCase(element, propertyName, out JsonElement value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetAnyProperty(
        JsonElement element,
        out JsonElement value,
        params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (TryGetPropertyIgnoreCase(element, propertyName, out value)) return true;
        }

        value = default;
        return false;
    }

    private static bool FailExtraction(string message, out string error)
    {
        error = message;
        return false;
    }
    /// <summary>解析模型文本中的审查 JSON；失败时返回 IsSuccess=false 和 RiskLevel.Unknown。</summary>
    public static AiReviewResult ParseReviewResponse(string rawContent, string? rawReasoning = null)
    {
        var result = new AiReviewResult
        {
            IsSuccess = false,
            Risk = RiskLevel.Unknown,
            Confidence = 0,
            Categories = [],
            RawResponse = rawContent ?? string.Empty,
            RawReasoning = rawReasoning ?? string.Empty
        };

        if (string.IsNullOrWhiteSpace(rawContent))
        {
            result.Error = "AI 返回内容为空。";
            return result;
        }

        bool foundJsonObject = false;
        string? lastError = null;

        foreach (string candidate in GetJsonCandidates(rawContent))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(candidate, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                    MaxDepth = 64
                });
                if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                foundJsonObject = true;

                if (!TryFindReviewObject(document.RootElement, depth: 0, out JsonElement reviewObject))
                {
                    lastError = "JSON 中缺少 risk 字段。";
                    continue;
                }

                if (!TryParseReviewObject(reviewObject, out RiskLevel risk, out double confidence, out string reason,
                        out List<string> categories, out string objectError))
                {
                    lastError = objectError;
                    continue;
                }

                result.IsSuccess = true;
                result.Risk = risk;
                result.Confidence = confidence;
                result.Reason = reason;
                result.Categories = categories;
                result.Error = string.Empty;
                return result;
            }
            catch (JsonException)
            {
                lastError = "JSON 格式无效。";
            }
        }

        result.Error = foundJsonObject
            ? lastError ?? "AI 输出不符合审查 JSON 结构。"
            : "AI 输出中未找到有效 JSON 对象。";
        return result;
    }

    /// <summary>校验单个审查对象的 risk、confidence、reason、categories 字段。</summary>
    private static bool TryParseReviewObject(
        JsonElement reviewObject,
        out RiskLevel risk,
        out double confidence,
        out string reason,
        out List<string> categories,
        out string error)
    {
        risk = RiskLevel.Unknown;
        confidence = 0;
        reason = string.Empty;
        categories = [];
        error = string.Empty;

        if (!TryGetPropertyIgnoreCase(reviewObject, "risk", out JsonElement riskElement))
        {
            error = "JSON 中缺少 risk 字段。";
            return false;
        }

        if (riskElement.ValueKind == JsonValueKind.Object &&
            TryGetAnyProperty(riskElement, out JsonElement nestedRisk, "level", "value", "risk", "label", "rating"))
        {
            riskElement = nestedRisk;
        }

        if (!TryParseRisk(riskElement, out risk))
        {
            error = "无法识别 risk，必须是 low、medium 或 high。";
            return false;
        }

        if (risk == RiskLevel.Unknown)
        {
            error = "risk 不能为 unknown 或不确定。";
            return false;
        }

        if (!TryGetPropertyIgnoreCase(reviewObject, "confidence", out JsonElement confidenceElement) ||
            !TryParseConfidence(confidenceElement, out confidence))
        {
            error = "JSON 缺少有效的 confidence。";
            return false;
        }

        if (!TryGetPropertyIgnoreCase(reviewObject, "reason", out JsonElement reasonElement))
        {
            error = "JSON 缺少 reason 字段。";
            return false;
        }

        reason = ExtractTextValue(reasonElement).Trim();
        if (reason.Length == 0)
        {
            error = "reason 不能为空。";
            return false;
        }

        if (!TryGetAnyProperty(
                reviewObject,
                out JsonElement categoriesElement,
                "categories",
                "category",
                "tags"))
        {
            error = "JSON 缺少 categories 字段。";
            return false;
        }

        if (categoriesElement.ValueKind is not JsonValueKind.Array and not JsonValueKind.String)
        {
            error = "categories 必须是字符串数组。";
            return false;
        }

        confidence = Math.Clamp(confidence, 0d, 1d);
        categories = ExtractCategories(categoriesElement);
        return true;
    }

    /// <summary>
    /// 解析批量审查响应，按 id（1 起的序号）把结果分配回各条目；
    /// 缺失、重复或格式无效的条目单独判为失败，不影响其他条目。
    /// </summary>
    public static IReadOnlyList<AiReviewResult> ParseBatchReviewResponse(
        string rawContent,
        int count,
        string? rawReasoning = null)
    {
        var results = new AiReviewResult[count];
        for (int i = 0; i < count; i++)
        {
            results[i] = new AiReviewResult
            {
                IsSuccess = false,
                Risk = RiskLevel.Unknown,
                Confidence = 0,
                Categories = [],
                RawResponse = rawContent ?? string.Empty,
                RawReasoning = rawReasoning ?? string.Empty,
                Error = "批量响应中缺少该条目的审查结果。"
            };
        }

        if (string.IsNullOrWhiteSpace(rawContent))
        {
            foreach (AiReviewResult result in results) result.Error = "AI 返回内容为空。";
            return results;
        }

        foreach (string candidate in GetJsonCandidates(rawContent, includeArrays: true))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(candidate, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                    MaxDepth = 64
                });
                JsonElement root = document.RootElement;
                JsonElement array = root;
                if (root.ValueKind == JsonValueKind.Object &&
                    !TryGetAnyProperty(root, out array, "results", "items", "reviews"))
                {
                    continue;
                }

                if (array.ValueKind != JsonValueKind.Array) continue;

                var seen = new HashSet<int>();
                foreach (JsonElement entry in array.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object ||
                        !TryGetAnyProperty(entry, out JsonElement idElement, "id", "index") ||
                        !TryParseBatchId(idElement, out int id) ||
                        id < 1 || id > count)
                    {
                        continue;
                    }

                    AiReviewResult result = results[id - 1];
                    if (!seen.Add(id))
                    {
                        // 同一 id 出现多次时无法判断哪条可信，整条按失败处理。
                        result.IsSuccess = false;
                        result.Risk = RiskLevel.Unknown;
                        result.Confidence = 0;
                        result.Reason = string.Empty;
                        result.Categories = [];
                        result.Error = "批量响应中该条目出现了多个结果。";
                        continue;
                    }

                    if (TryParseReviewObject(entry, out RiskLevel risk, out double confidence, out string reason,
                            out List<string> categories, out string error))
                    {
                        result.IsSuccess = true;
                        result.Risk = risk;
                        result.Confidence = confidence;
                        result.Reason = reason;
                        result.Categories = categories;
                        result.Error = string.Empty;
                    }
                    else
                    {
                        result.Error = error;
                    }
                }

                if (seen.Count == 0) continue;
                return results;
            }
            catch (JsonException)
            {
                // 尝试下一个候选片段。
            }
        }

        foreach (AiReviewResult result in results) result.Error = "AI 输出中未找到有效的批量审查 JSON。";
        return results;
    }

    private static bool TryParseBatchId(JsonElement element, out int id)
    {
        id = 0;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out id),
            JsonValueKind.String => int.TryParse(
                (element.GetString() ?? string.Empty).Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out id),
            _ => false
        };
    }

    private static List<string> GetJsonCandidates(string rawContent, bool includeArrays = false)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddCandidate(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;
            string trimmed = candidate.Trim().TrimStart('\uFEFF');
            if (trimmed.Length > 0 && seen.Add(trimmed)) candidates.Add(trimmed);
        }

        string normalized = rawContent.Trim().TrimStart('\uFEFF');
        AddCandidate(normalized);

        foreach (Match match in FencedJson.Matches(rawContent))
        {
            string fencedContent = match.Groups["content"].Value;
            AddCandidate(fencedContent);
            foreach (string balanced in FindBalancedJsonObjects(fencedContent)) AddCandidate(balanced);
            if (includeArrays)
                foreach (string balanced in FindBalancedJsonObjects(fencedContent, '[', ']')) AddCandidate(balanced);
        }

        foreach (string balanced in FindBalancedJsonObjects(rawContent)) AddCandidate(balanced);
        if (includeArrays)
            foreach (string balanced in FindBalancedJsonObjects(rawContent, '[', ']')) AddCandidate(balanced);
        return candidates;
    }

    private static List<string> FindBalancedJsonObjects(string text, char open = '{', char close = '}')
    {
        var objects = new List<string>();

        for (int start = 0; start < text.Length; start++)
        {
            if (text[start] != open) continue;

            int depth = 0;
            bool inString = false;
            bool escaped = false;

            for (int i = start; i < text.Length; i++)
            {
                char current = text[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (current == '\\') escaped = true;
                    else if (current == '"') inString = false;
                    continue;
                }

                if (current == '"')
                {
                    inString = true;
                    continue;
                }

                if (current == open)
                {
                    depth++;
                }
                else if (current == close)
                {
                    depth--;
                    if (depth == 0)
                    {
                        objects.Add(text[start..(i + 1)]);
                        start = i;
                        break;
                    }

                    if (depth < 0) break;
                }
            }
        }

        return objects;
    }

    private static bool TryFindReviewObject(
        JsonElement element,
        int depth,
        out JsonElement reviewObject)
    {
        reviewObject = default;
        if (depth > 8) return false;

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryGetPropertyIgnoreCase(element, "risk", out _))
            {
                reviewObject = element;
                return true;
            }

            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Value.ValueKind is not JsonValueKind.Object and not JsonValueKind.Array)
                    continue;

                if (TryFindReviewObject(property.Value, depth + 1, out reviewObject)) return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (item.ValueKind is not JsonValueKind.Object and not JsonValueKind.Array)
                    continue;

                if (TryFindReviewObject(item, depth + 1, out reviewObject)) return true;
            }
        }

        return false;
    }

    private static readonly HashSet<string> LowRiskAliases =
    [
        "low", "lowrisk", "safe", "none", "minimal", "minimalrisk", "benign",
        "pass", "passed", "approve", "approved", "acceptable", "green",
        "低", "低风险", "安全", "无风险", "绿色", "通过", "可展示"
    ];

    private static readonly HashSet<string> MediumRiskAliases =
    [
        "medium", "mediumrisk", "moderate", "mid", "caution", "review",
        "questionable", "sensitive", "warn", "warning", "manualreview",
        "中", "中风险", "中等", "中等风险", "需复核", "人工复核", "谨慎", "黄色"
    ];

    private static readonly HashSet<string> HighRiskAliases =
    [
        "high", "highrisk", "severe", "critical", "unsafe", "danger", "dangerous",
        "block", "blocked", "reject", "unacceptable", "veryhigh", "veryhighrisk",
        "高", "高风险", "危险", "严重", "拦截", "红色", "不可展示", "极高", "极高风险"
    ];

    private static readonly HashSet<string> UnknownRiskAliases =
    [
        "unknown", "uncertain", "unsure", "unclear", "未知", "不确定", "无法判断"
    ];

    private static bool TryParseRisk(JsonElement element, out RiskLevel risk)
    {
        risk = RiskLevel.Unknown;

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int numericRisk))
        {
            risk = numericRisk switch
            {
                1 => RiskLevel.Low,
                2 => RiskLevel.Medium,
                3 => RiskLevel.High,
                _ => RiskLevel.Unknown
            };
            return numericRisk is 1 or 2 or 3;
        }

        if (element.ValueKind != JsonValueKind.String) return false;

        string normalized = NormalizeRiskToken(element.GetString() ?? string.Empty);
        if (normalized.Length == 0) return false;

        if (normalized.StartsWith("risklevel", StringComparison.Ordinal)) normalized = normalized["risklevel".Length..];
        else if (normalized.StartsWith("risk", StringComparison.Ordinal)) normalized = normalized["risk".Length..];
        else if (normalized.StartsWith("level", StringComparison.Ordinal)) normalized = normalized["level".Length..];
        else if (normalized.StartsWith("风险等级", StringComparison.Ordinal)) normalized = normalized["风险等级".Length..];
        else if (normalized.StartsWith("风险", StringComparison.Ordinal)) normalized = normalized["风险".Length..];
        else if (normalized.StartsWith("等级", StringComparison.Ordinal)) normalized = normalized["等级".Length..];

        if (LowRiskAliases.Contains(normalized))
        {
            risk = RiskLevel.Low;
            return true;
        }

        if (MediumRiskAliases.Contains(normalized))
        {
            risk = RiskLevel.Medium;
            return true;
        }

        if (HighRiskAliases.Contains(normalized))
        {
            risk = RiskLevel.High;
            return true;
        }

        if (UnknownRiskAliases.Contains(normalized))
        {
            risk = RiskLevel.Unknown;
            return true;
        }

        bool negated = normalized.Contains("not", StringComparison.Ordinal) ||
                       normalized.Contains('非') ||
                       normalized.Contains('不');
        if (!negated)
        {
            if (normalized.Contains("high", StringComparison.Ordinal) ||
                normalized.Contains("高风险", StringComparison.Ordinal) ||
                normalized.Contains("严重", StringComparison.Ordinal))
            {
                risk = RiskLevel.High;
                return true;
            }

            if (normalized.Contains("medium", StringComparison.Ordinal) ||
                normalized.Contains("moderate", StringComparison.Ordinal) ||
                normalized.Contains("中风险", StringComparison.Ordinal) ||
                normalized.Contains("中等", StringComparison.Ordinal))
            {
                risk = RiskLevel.Medium;
                return true;
            }

            if (normalized.Contains("lowrisk", StringComparison.Ordinal) ||
                normalized.Contains("低风险", StringComparison.Ordinal) ||
                normalized.Contains("安全", StringComparison.Ordinal))
            {
                risk = RiskLevel.Low;
                return true;
            }
        }

        return false;
    }

    private static string NormalizeRiskToken(string raw)
    {
        var builder = new StringBuilder(raw.Length);
        foreach (char character in raw.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character) ||
                (character >= '\u4e00' && character <= '\u9fff'))
            {
                builder.Append(character);
            }
        }
        return builder.ToString();
    }

    private static bool TryParseConfidence(JsonElement element, out double confidence)
    {
        confidence = 0;

        if (element.ValueKind == JsonValueKind.Number)
        {
            if (!element.TryGetDouble(out double numeric)) return false;
            if (!double.IsFinite(numeric)) return false;
            confidence = Math.Clamp(numeric, 0d, 1d);
            return true;
        }

        if (element.ValueKind == JsonValueKind.Object &&
            TryGetAnyProperty(element, out JsonElement nested, "value", "score", "confidence") &&
            TryParseConfidence(nested, out confidence))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String) return false;

        string text = (element.GetString() ?? string.Empty).Trim();
        if (text.Length == 0) return false;

        bool percent = text.EndsWith('%') || text.EndsWith('％');
        if (percent) text = text[..^1].Trim();

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ||
            !double.IsFinite(parsed))
        {
            return false;
        }

        if (percent) parsed /= 100d;
        confidence = Math.Clamp(parsed, 0d, 1d);
        return true;
    }

    private static List<string> ExtractCategories(JsonElement element)
    {
        var categories = new List<string>();

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                string? text = item.ValueKind switch
                {
                    JsonValueKind.String => item.GetString(),
                    JsonValueKind.Number => item.GetRawText(),
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(text)) categories.Add(text.Trim());
            }
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            string text = element.GetString() ?? string.Empty;
            categories.AddRange(text.Split(
                [',', '，', ';', '；', '、', '|', '/'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return categories
            .Where(static category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
    }
}