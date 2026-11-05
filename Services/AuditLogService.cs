using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IslandQuotes.Core;

namespace IslandQuotes.Services;

/// <summary>按日期写入线程安全的 UTF-8 JSONL 审计日志，并自动脱敏敏感字段。</summary>
public sealed class AuditLogService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly Regex SensitiveAssignment = new(
        @"(?i)\b(authorization|x-api-key|x-goog-api-key|api[_-]?key|access[_-]?token|refresh[_-]?token|secret|cookie|password)\b([""']?)(\s*[:=]\s*)(?:bearer\s+)?[^\s,;""'&}]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BearerValue = new(
        @"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly PluginSettingsStore? _settingsStore;
    private volatile bool _enabled = true;

    /// <summary>审计日志目录。</summary>
    public string LogDirectory { get; }

    /// <summary>是否写入审计事件；关联设置存储时会随 Log.Enabled 自动同步。</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>创建日志目录并清理超出保留期的旧审计文件。</summary>
    public AuditLogService(string pluginConfigFolder, int retentionDays = 30)
        : this(pluginConfigFolder, retentionDays, settingsStore: null)
    {
    }

    /// <summary>创建设置感知的审计服务，并自动跟随「启用日志」开关。</summary>
    public AuditLogService(PluginSettingsStore settingsStore, int retentionDays = 30)
        : this(
            (settingsStore ?? throw new ArgumentNullException(nameof(settingsStore))).ConfigDirectory,
            retentionDays,
            settingsStore)
    {
    }

    private AuditLogService(
        string pluginConfigFolder,
        int retentionDays,
        PluginSettingsStore? settingsStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginConfigFolder);

        LogDirectory = Path.Combine(pluginConfigFolder, "log");
        Directory.CreateDirectory(LogDirectory);
        RemoveExpiredLogs(retentionDays);

        _settingsStore = settingsStore;
        if (settingsStore is not null)
        {
            Enabled = settingsStore.Settings.Log?.Enabled ?? true;
            settingsStore.SettingsChanged += OnSettingsChanged;
        }
    }

    /// <summary>追加一条审计事件；同一实例内的写入不会交错。</summary>
    public async Task WriteAsync(AuditLogEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (!Enabled) return;

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(LogDirectory);

            DateTimeOffset timestamp = evt.Timestamp == default ? DateTimeOffset.Now : evt.Timestamp;
            string date = timestamp.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string path = Path.Combine(LogDirectory, $"audit-{date}.jsonl");
            string line = SerializeAndRedact(evt);

            await File.AppendAllTextAsync(path, line + Environment.NewLine, Utf8NoBom, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (_settingsStore is not null)
            Enabled = _settingsStore.Settings.Log?.Enabled ?? true;
    }
    /// <summary>等待当前实例所有追加写入完成；文件句柄由每次写入自动关闭。</summary>
    public async Task FlushAsync()
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        _writeGate.Release();
    }

    private void RemoveExpiredLogs(int retentionDays)
    {
        int days = Math.Max(1, retentionDays);
        DateTime cutoff = DateTime.Today.AddDays(-(days - 1));

        foreach (string file in Directory.EnumerateFiles(LogDirectory, "audit-*.jsonl", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(file);
            if (!name.StartsWith("audit-", StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string datePart = name["audit-".Length..^".jsonl".Length];
            if (!DateTime.TryParseExact(
                    datePart,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime fileDate) ||
                fileDate >= cutoff)
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // 文件可能正被其他进程读取；保留它，不应让日志初始化失败。
            }
            catch (UnauthorizedAccessException)
            {
                // 无权限删除旧文件时继续保留，避免影响插件启动。
            }
        }
    }

    private static string SerializeAndRedact(AuditLogEvent evt)
    {
        JsonNode? node;
        try
        {
            node = JsonSerializer.SerializeToNode(evt, JsonDefaults.Compact);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            var fallback = new Dictionary<string, object?>
            {
                ["timestamp"] = evt.Timestamp,
                ["stage"] = evt.Stage,
                ["refreshSessionId"] = evt.RefreshSessionId,
                ["itemId"] = evt.ItemId,
                ["providerId"] = evt.ProviderId,
                ["upstreamId"] = evt.UpstreamId,
                ["correlationId"] = evt.CorrelationId,
                ["data"] = $"[无法序列化的审计数据：{ex.GetType().Name}]"
            };
            node = JsonSerializer.SerializeToNode(fallback, JsonDefaults.Compact);
        }

        node = RedactNode(node);
        return node?.ToJsonString(JsonDefaults.Compact) ?? "{}";
    }

    private static JsonNode? RedactNode(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonObject obj)
        {
            var clone = new JsonObject();
            foreach (KeyValuePair<string, JsonNode?> property in obj)
            {
                clone[property.Key] = IsSensitiveName(property.Key)
                    ? JsonValue.Create("[REDACTED]")
                    : RedactNode(property.Value);
            }
            return clone;
        }
        if (node is JsonArray array)
        {
            var clone = new JsonArray();
            foreach (JsonNode? item in array) clone.Add(RedactNode(item));
            return clone;
        }
        if (node is JsonValue value && value.TryGetValue<string>(out string? text))
            return JsonValue.Create(RedactText(text));
        return node.DeepClone();
    }
    private static bool IsSensitiveName(string name)
        => name.Contains("key", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("password", StringComparison.OrdinalIgnoreCase);

    private static string RedactText(string text)
    {
        string result = SensitiveAssignment.Replace(text, "$1$2$3[REDACTED]");
        result = BearerValue.Replace(result, "Bearer [REDACTED]");
        return result;
    }
}