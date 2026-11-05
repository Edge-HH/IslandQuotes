using System.IO;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Controls;
using Avalonia.Media;
using IslandQuotes.Models;

namespace IslandQuotes.Services;

/// <summary>名句请求失败时，保留可用于审计的请求信息。</summary>
internal sealed class QuoteProviderRequestException : InvalidOperationException
{
    public QuoteProviderRequestException(
        string message,
        string providerId,
        string requestUrl,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ProviderId = providerId;
        RequestUrl = requestUrl;
    }

    public string ProviderId { get; }
    public string RequestUrl { get; }
}

/// <summary>四个名句提供程序共用同一 HttpClient，避免耗尽连接池。</summary>
internal static class RhesisHttp
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    internal static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        HttpClient client = new()
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("IslandQuotes", "1.0"));

        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    internal static async Task<T> GetJsonAsync<T>(
        string providerId,
        string requestUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await Client.GetAsync(
                requestUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                string detail = string.Empty;
                try
                {
                    string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    detail = Truncate(body, 240);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // 状态码已经足够说明失败，忽略读取错误正文时的二次错误。
                }

                string suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $"：{detail}";
                throw new InvalidOperationException(
                    $"上游返回 HTTP {(int)response.StatusCode} {response.ReasonPhrase}{suffix}");
            }

            await using Stream stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            T? result = await JsonSerializer
                .DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return result ?? throw new InvalidOperationException("上游返回了空 JSON 响应。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (QuoteProviderRequestException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new QuoteProviderRequestException(
                $"请求名句来源失败：{ex.Message}",
                providerId,
                requestUrl,
                ex);
        }
    }

    internal static string MakeContentDerivedId(string content)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return $"content-sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    internal static bool TryGetNativeId(
        JsonElement element,
        out string upstreamId,
        out string upstreamIdKind)
    {
        upstreamId = string.Empty;
        upstreamIdKind = "unknown";
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                upstreamId = element.GetRawText();
                upstreamIdKind = "numeric";
                return upstreamId.Length > 0;
            case JsonValueKind.String:
                upstreamId = element.GetString()?.Trim() ?? string.Empty;
                upstreamIdKind = Guid.TryParse(upstreamId, out _) ? "uuid" : "native";
                return upstreamId.Length > 0;
            default:
                return false;
        }
    }

    internal static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value ?? string.Empty;
        return value[..maxLength] + "...";
    }
}

/// <summary>一言（Hitokoto）名句来源。</summary>
public sealed class HitokotoProvider : IQuoteProvider, IQuoteProviderSettingsFactory
{
    public const string ProviderId = "hitokoto";
    public const string QueryOption = "query";

    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    public string Id => ProviderId;
    public string DisplayName => "一言";
    public string Description => "来自一言 API；内容由社区提交，请留意本来源中可能存在的不良内容。";
    public bool IsEnabledByDefault => false;
    public int DefaultWeight => 1;

    public async Task<QuoteContent> FetchAsync(
        QuoteProviderConfig config,
        int lengthLimitation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        await ThrottleAsync(cancellationToken).ConfigureAwait(false);

        string requestUrl = BuildRequestUrl(config, lengthLimitation);
        HitokotoData data = await RhesisHttp.GetJsonAsync<HitokotoData>(
            ProviderId,
            requestUrl,
            cancellationToken).ConfigureAwait(false);

        string content = (data.Hitokoto ?? string.Empty).Trim();
        if (content.Length == 0)
        {
            throw new QuoteProviderRequestException("一言 API 返回了空句子。", ProviderId, requestUrl);
        }

        string upstreamId;
        string upstreamIdKind;
        string uuid = (data.Uuid ?? string.Empty).Trim();
        if (uuid.Length > 0)
        {
            upstreamId = uuid;
            upstreamIdKind = "uuid";
        }
        else if (data.Id != 0)
        {
            upstreamId = data.Id.ToString(CultureInfo.InvariantCulture);
            upstreamIdKind = "numeric";
        }
        else
        {
            upstreamId = RhesisHttp.MakeContentDerivedId(content);
            upstreamIdKind = "content-sha256";
        }

        return new QuoteContent
        {
            Content = content,
            Title = (data.From ?? string.Empty).Trim(),
            Author = (data.FromWho ?? string.Empty).Trim(),
            Source = "一言API",
            Catalog = ConvertTypeToString(data.Type),
            UpstreamId = upstreamId,
            UpstreamIdKind = upstreamIdKind,
            ProviderId = ProviderId,
            ProviderName = DisplayName,
            RequestMethod = "GET",
            RequestUrl = requestUrl,
            FetchedAt = DateTimeOffset.Now
        };
    }


    /// <summary>创建一言附加查询参数编辑控件。</summary>
    public Control CreateSettingsControl(QuoteProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Options ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        TextBox queryTextBox = new()
        {
            Text = config.GetOption(QueryOption),
            Watermark = "例如：c=i 或 c=k",
            MinWidth = 180
        };
        queryTextBox.TextChanged += (_, _) => config.SetOption(QueryOption, queryTextBox.Text);

        return new StackPanel
        {
            Spacing = 6,
            Margin = new Avalonia.Thickness(12, 6),
            Children =
            {
                new TextBlock
                {
                    Text = "附加查询参数（不含 ?）。全局字数限制会自动转换为 max_length 参数。",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.Gray
                },
                queryTextBox
            }
        };
    }
    internal static string BuildRequestUrl(QuoteProviderConfig config, int lengthLimitation)
    {
        List<string> queryParts = [];
        if (lengthLimitation > 0)
        {
            queryParts.Add("max_length=" + lengthLimitation.ToString(CultureInfo.InvariantCulture));
        }

        string customQuery = config.GetOption(QueryOption).Trim().TrimStart('?', '&');
        if (customQuery.Length > 0)
        {
            queryParts.Add(customQuery);
        }

        return queryParts.Count == 0
            ? "https://v1.hitokoto.cn/"
            : $"https://v1.hitokoto.cn/?{string.Join("&", queryParts)}";
    }

    private async Task ThrottleAsync(CancellationToken cancellationToken)
    {
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TimeSpan remaining = TimeSpan.FromMilliseconds(700) - (DateTimeOffset.UtcNow - _lastRequestAt);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
            }

            _lastRequestAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private static string ConvertTypeToString(string? type)
    {
        return type switch
        {
            "a" => "动画",
            "b" => "漫画",
            "c" => "游戏",
            "d" => "文学",
            "e" => "原创",
            "f" => "网络",
            "g" => "其他",
            "h" => "影视",
            "i" => "诗词",
            "j" => "网易云",
            "k" => "哲学",
            "l" => "抖机灵",
            _ => string.Empty
        };
    }
}

/// <summary>今日诗词名句来源。</summary>
public sealed class JinrishiciProvider : IQuoteProvider
{
    public const string ProviderId = "jinrishici";
    private const string RequestUrl = "https://v1.jinrishici.com/all.json";

    public string Id => ProviderId;
    public string DisplayName => "今日诗词";
    public string Description => "来自今日诗词 API。";
    public bool IsEnabledByDefault => true;
    public int DefaultWeight => 1;

    public async Task<QuoteContent> FetchAsync(
        QuoteProviderConfig config,
        int lengthLimitation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        JinrishiciData data = await RhesisHttp.GetJsonAsync<JinrishiciData>(
            ProviderId,
            RequestUrl,
            cancellationToken).ConfigureAwait(false);

        string content = (data.Content ?? string.Empty).Trim();
        if (content.Length == 0)
        {
            throw new QuoteProviderRequestException("今日诗词 API 返回了空句子。", ProviderId, RequestUrl);
        }

        string upstreamId;
        string upstreamIdKind;
        if (!RhesisHttp.TryGetNativeId(data.Id, out upstreamId, out upstreamIdKind))
        {
            upstreamId = RhesisHttp.MakeContentDerivedId(content);
            upstreamIdKind = "content-sha256";
        }

        return new QuoteContent
        {
            Content = content,
            Title = (data.Origin ?? string.Empty).Trim(),
            Author = (data.Author ?? string.Empty).Trim(),
            Source = "今日诗词API",
            Catalog = (data.Category ?? string.Empty).Trim(),
            UpstreamId = upstreamId,
            UpstreamIdKind = upstreamIdKind,
            ProviderId = ProviderId,
            ProviderName = DisplayName,
            RequestMethod = "GET",
            RequestUrl = RequestUrl,
            FetchedAt = DateTimeOffset.Now
        };
    }
}

/// <summary>每日一句名句来源。</summary>
public sealed class DlystcProvider : IQuoteProvider
{
    public const string ProviderId = "dlystc";
    private const string RequestUrl = "https://dlystc.unknownmp.top/api/v2/sentence";

    public string Id => ProviderId;
    public string DisplayName => "每日一句";
    public string Description => "来自每日一句 API；有约 30 秒的 CDN 缓存，期间刷新可能不会更新句子。";
    public bool IsEnabledByDefault => false;
    public int DefaultWeight => 1;

    public async Task<QuoteContent> FetchAsync(
        QuoteProviderConfig config,
        int lengthLimitation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        DlystcData data = await RhesisHttp.GetJsonAsync<DlystcData>(
            ProviderId,
            RequestUrl,
            cancellationToken).ConfigureAwait(false);

        string content = (data.Content ?? string.Empty).Trim();
        if (content.Length == 0)
        {
            throw new QuoteProviderRequestException("每日一句 API 返回了空句子。", ProviderId, RequestUrl);
        }

        string upstreamId;
        string upstreamIdKind;
        if (!RhesisHttp.TryGetNativeId(data.Id, out upstreamId, out upstreamIdKind))
        {
            upstreamId = RhesisHttp.MakeContentDerivedId(content);
            upstreamIdKind = "content-sha256";
        }

        return new QuoteContent
        {
            Content = content,
            Title = (data.Source ?? string.Empty).Trim(),
            Author = (data.Author ?? string.Empty).Trim(),
            Source = "每日一句API",
            Catalog = string.Empty,
            UpstreamId = upstreamId,
            UpstreamIdKind = upstreamIdKind,
            ProviderId = ProviderId,
            ProviderName = DisplayName,
            RequestMethod = "GET",
            RequestUrl = RequestUrl,
            FetchedAt = DateTimeOffset.Now
        };
    }
}

/// <summary>诏预（Saintic）名句来源。</summary>
public sealed class SainticProvider : IQuoteProvider, IQuoteProviderSettingsFactory
{
    public const string ProviderId = "saintic";
    public const string PathOption = "path";

    public string Id => ProviderId;
    public string DisplayName => "诏预";
    public string Description => "来自 Saintic 句子 API。";
    public bool IsEnabledByDefault => true;
    public int DefaultWeight => 1;

    public async Task<QuoteContent> FetchAsync(
        QuoteProviderConfig config,
        int lengthLimitation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        string requestUrl = BuildRequestUrl(config);
        SainticData data = await RhesisHttp.GetJsonAsync<SainticData>(
            ProviderId,
            requestUrl,
            cancellationToken).ConfigureAwait(false);

        SainticQuoteData? quote = data.Data;
        string content = (quote?.Sentence ?? string.Empty).Trim();
        if (content.Length == 0)
        {
            string upstreamMessage = string.IsNullOrWhiteSpace(data.Message)
                ? "Saintic API 返回了空句子。"
                : $"Saintic API 返回了空句子：{data.Message.Trim()}";
            throw new QuoteProviderRequestException(upstreamMessage, ProviderId, requestUrl);
        }

        string upstreamId;
        string upstreamIdKind;
        if (quote!.Id != 0)
        {
            upstreamId = quote.Id.ToString(CultureInfo.InvariantCulture);
            upstreamIdKind = "numeric";
        }
        else
        {
            upstreamId = RhesisHttp.MakeContentDerivedId(content);
            upstreamIdKind = "content-sha256";
        }

        string catalog = string.Join(
            "-",
            new[] { quote.Theme, quote.Catalog }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim()));

        return new QuoteContent
        {
            Content = content,
            Title = (quote.Name ?? string.Empty).Trim(),
            Author = (quote.Author ?? string.Empty).Trim(),
            Source = "诏预API",
            Catalog = catalog,
            UpstreamId = upstreamId,
            UpstreamIdKind = upstreamIdKind,
            ProviderId = ProviderId,
            ProviderName = DisplayName,
            RequestMethod = "GET",
            RequestUrl = requestUrl,
            FetchedAt = DateTimeOffset.Now
        };
    }


    /// <summary>创建诏预接口路径编辑控件。</summary>
    public Control CreateSettingsControl(QuoteProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Options ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        TextBox pathTextBox = new()
        {
            Text = config.GetOption(PathOption),
            Watermark = "all",
            MinWidth = 180
        };
        pathTextBox.TextChanged += (_, _) => config.SetOption(PathOption, pathTextBox.Text);

        return new StackPanel
        {
            Spacing = 6,
            Margin = new Avalonia.Thickness(12, 6),
            Children =
            {
                new TextBlock
                {
                    Text = "接口路径：https://hub.saintic.com/openservice/sentence/{路径}.json",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.Gray
                },
                pathTextBox
            }
        };
    }
    internal static string BuildRequestUrl(QuoteProviderConfig config)
    {
        string path = config.GetOption(PathOption, "all").Trim().Trim('/').TrimEnd('.');
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^5];
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            path = "all";
        }

        return $"https://hub.saintic.com/openservice/sentence/{path}.json";
    }
}

internal sealed class HitokotoData
{
    [JsonPropertyName("id")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long Id { get; set; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; set; }

    [JsonPropertyName("hitokoto")]
    public string? Hitokoto { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("from")]
    public string? From { get; set; }

    [JsonPropertyName("from_who")]
    public string? FromWho { get; set; }
}

internal sealed class JinrishiciData
{
    [JsonPropertyName("id")]
    public JsonElement Id { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("origin")]
    public string? Origin { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }
}

internal sealed class DlystcData
{
    [JsonPropertyName("id")]
    public JsonElement Id { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }
}

internal sealed class SainticData
{
    [JsonPropertyName("code")]
    public int StatusCode { get; set; } = -1;

    [JsonPropertyName("data")]
    public SainticQuoteData? Data { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

internal sealed class SainticQuoteData
{
    [JsonPropertyName("id")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long Id { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("catalog")]
    public string? Catalog { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("sentence")]
    public string? Sentence { get; set; }

    [JsonPropertyName("src_url")]
    public string? SrcUrl { get; set; }

    [JsonPropertyName("theme")]
    public string? Theme { get; set; }
}
