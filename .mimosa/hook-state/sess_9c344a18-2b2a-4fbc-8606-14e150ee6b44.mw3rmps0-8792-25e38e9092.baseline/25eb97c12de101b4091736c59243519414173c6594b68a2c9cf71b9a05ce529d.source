using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IslandQuotes.Core;

/// <summary>插件统一使用的 System.Text.Json 配置。</summary>
public static class JsonDefaults
{
    /// <summary>紧凑 JSON，适合 JSONL 和 HTTP 请求体。</summary>
    public static JsonSerializerOptions Compact { get; } = Create(indented: false);

    /// <summary>缩进 JSON，适合面向用户的设置与快照文件。</summary>
    public static JsonSerializerOptions Indented { get; } = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = indented,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 64
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: true));
        return options;
    }
}