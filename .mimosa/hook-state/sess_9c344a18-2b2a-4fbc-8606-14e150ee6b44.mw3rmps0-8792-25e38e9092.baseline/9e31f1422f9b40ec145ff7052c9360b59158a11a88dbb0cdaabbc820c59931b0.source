using System.IO;
using System.Text;
using System.Text.Json;
using IslandQuotes.Core;
using IslandQuotes.Models;

namespace IslandQuotes.Services;

/// <summary>线程安全地加载、补全并原子保存插件设置。</summary>
public sealed class PluginSettingsStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private PluginSettings _settings = new();

    /// <summary>插件配置目录。</summary>
    public string ConfigDirectory { get; }

    /// <summary>settings.json 的完整路径。</summary>
    public string SettingsPath { get; }

    /// <summary>当前设置对象；对象本身供界面直接编辑。</summary>
    public PluginSettings Settings
    {
        get
        {
            lock (_gate) return _settings;
        }
    }

    /// <summary>设置成功加载、补全或保存后触发。</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>初始化配置路径并建立内存中的默认设置，不主动覆盖磁盘文件。</summary>
    public PluginSettingsStore(string pluginConfigFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginConfigFolder);

        ConfigDirectory = Path.GetFullPath(pluginConfigFolder);
        SettingsPath = Path.Combine(ConfigDirectory, "settings.json");
        EnsureDefaultsCore();
    }

    /// <summary>从 settings.json 加载设置；损坏文件会备份后回退到默认设置。</summary>
    public void Load()
    {
        _saveGate.Wait();
        bool changed = false;
        try
        {
            Directory.CreateDirectory(ConfigDirectory);

            PluginSettings loaded;
            if (File.Exists(SettingsPath))
            {
                try
                {
                    string json = File.ReadAllText(SettingsPath, Encoding.UTF8);
                    loaded = JsonSerializer.Deserialize<PluginSettings>(json, JsonDefaults.Compact)
                             ?? new PluginSettings();
                }
                catch (JsonException)
                {
                    TryBackupInvalidSettings();
                    loaded = new PluginSettings();
                    changed = true;
                }
                catch (NotSupportedException)
                {
                    TryBackupInvalidSettings();
                    loaded = new PluginSettings();
                    changed = true;
                }
            }
            else
            {
                loaded = new PluginSettings();
                changed = true;
            }

            lock (_gate)
            {
                _settings = loaded;
                changed |= EnsureDefaultsCore();
            }
        }
        finally
        {
            _saveGate.Release();
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>原子写入当前设置并触发设置变更事件。</summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string json;
            lock (_gate)
            {
                EnsureDefaultsCore();
                json = JsonSerializer.Serialize(_settings, JsonDefaults.Indented);
            }

            Directory.CreateDirectory(ConfigDirectory);
            string tempPath = Path.Combine(
                ConfigDirectory,
                $".settings.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

            try
            {
                await File.WriteAllTextAsync(tempPath, json, Utf8NoBom, ct).ConfigureAwait(false);
                ReplaceAtomically(tempPath, SettingsPath);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch (IOException)
                    {
                        // 保存失败的临时文件清理不应掩盖原始异常。
                    }
                }
            }
        }
        finally
        {
            _saveGate.Release();
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>添加缺失的内置供应商、修正范围并确保活动供应商引用有效。</summary>
    public void EnsureDefaults()
    {
        bool changed;
        lock (_gate)
        {
            changed = EnsureDefaultsCore();
        }

        if (changed) SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>返回活动供应商的副本；活动 ID 为空或不存在时返回 null。</summary>
    public AiProviderProfile? GetActiveProfile()
    {
        lock (_gate)
        {
            AiSettings? ai = _settings.Ai;
            string activeId = (ai?.ActiveProfileId ?? string.Empty).Trim();
            if (activeId.Length == 0 || ai?.Profiles is null) return null;

            AiProviderProfile? profile = ai.Profiles.FirstOrDefault(candidate =>
                candidate is not null &&
                string.Equals(candidate.Id, activeId, StringComparison.OrdinalIgnoreCase));
            return profile?.Clone();
        }
    }

    /// <summary>验证活动供应商；有效时返回 null，否则返回可展示的中文原因。</summary>
    public string? ValidateActiveProfile()
    {
        lock (_gate)
        {
            AiSettings? ai = _settings.Ai;
            if (ai is null) return "AI 设置缺失，请重新加载或恢复默认设置。";

            string activeId = (ai.ActiveProfileId ?? string.Empty).Trim();
            if (activeId.Length == 0) return "未选择活动 AI 供应商。";

            AiProviderProfile? profile = ai.Profiles?.FirstOrDefault(candidate =>
                candidate is not null &&
                string.Equals(candidate.Id, activeId, StringComparison.OrdinalIgnoreCase));
            if (profile is null) return $"活动 AI 供应商「{activeId}」不存在，请重新选择。";

            return ValidateProfile(profile);
        }
    }

    /// <summary>解析直接 API Key 或 env:环境变量名；环境变量不存在时返回空字符串。</summary>
    public static string ResolveSecret(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        string trimmed = value.Trim();
        if (!trimmed.StartsWith("env:", StringComparison.OrdinalIgnoreCase)) return trimmed;

        string variableName = trimmed[4..].Trim();
        if (variableName.Length == 0) return string.Empty;
        return Environment.GetEnvironmentVariable(variableName)?.Trim() ?? string.Empty;
    }

    private bool EnsureDefaultsCore()
    {
        bool changed = false;

        if (_settings.Ai is null)
        {
            _settings.Ai = new AiSettings();
            changed = true;
        }

        if (_settings.Review is null)
        {
            _settings.Review = new ReviewSettings();
            changed = true;
        }

        if (_settings.Log is null)
        {
            _settings.Log = new LogSettings();
            changed = true;
        }

        AiSettings ai = _settings.Ai;
        if (ai.Profiles is null)
        {
            ai.Profiles = [];
            changed = true;
        }

        for (int i = ai.Profiles.Count - 1; i >= 0; i--)
        {
            if (ai.Profiles[i] is null)
            {
                ai.Profiles.RemoveAt(i);
                changed = true;
            }
        }

        if (string.IsNullOrWhiteSpace(ai.SystemPrompt))
        {
            ai.SystemPrompt = AiProviderPresetCatalog.DefaultSystemPrompt;
            changed = true;
        }

        int timeout = Math.Clamp(ai.RequestTimeoutSeconds, 5, 180);
        if (timeout != ai.RequestTimeoutSeconds)
        {
            ai.RequestTimeoutSeconds = timeout;
            changed = true;
        }

        int retired = ai.Profiles.RemoveAll(static profile =>
            profile.IsBuiltIn &&
            AiProviderPresetCatalog.RetiredPresetIds.Contains(
                string.IsNullOrWhiteSpace(profile.PresetId) ? profile.Id : profile.PresetId,
                StringComparer.OrdinalIgnoreCase));
        if (retired > 0) changed = true;

        foreach (AiProviderProfile preset in AiProviderPresetCatalog.CreateDefaultProfiles())
        {
            bool exists = ai.Profiles.Any(profile =>
                profile is not null &&
                (string.Equals(profile.Id, preset.Id, StringComparison.OrdinalIgnoreCase) ||
                 (!string.IsNullOrWhiteSpace(profile.PresetId) &&
                  string.Equals(profile.PresetId, preset.PresetId, StringComparison.OrdinalIgnoreCase))));

            if (!exists)
            {
                ai.Profiles.Add(preset.Clone());
                changed = true;
            }
        }

        EnsureUniqueProfileIds(ai.Profiles, ref changed);

        AiProviderProfile? active = ai.Profiles.FirstOrDefault(profile =>
            profile.IsEnabled &&
            string.Equals(profile.Id, ai.ActiveProfileId, StringComparison.OrdinalIgnoreCase));

        if (active is null)
        {
            AiProviderProfile? fallback = ai.Profiles.FirstOrDefault(static profile =>
                                              profile.IsEnabled &&
                                              string.Equals(profile.Id, AiProviderPresetCatalog.DefaultProfileId, StringComparison.OrdinalIgnoreCase))
                                          ?? ai.Profiles.FirstOrDefault(static profile => profile.IsEnabled)
                                          ?? ai.Profiles.FirstOrDefault();
            string fallbackId = fallback?.Id ?? string.Empty;
            if (!string.Equals(ai.ActiveProfileId, fallbackId, StringComparison.Ordinal))
            {
                ai.ActiveProfileId = fallbackId;
                changed = true;
            }
        }
        else if (!string.Equals(ai.ActiveProfileId, active.Id, StringComparison.Ordinal))
        {
            ai.ActiveProfileId = active.Id;
            changed = true;
        }

        ReviewSettings review = _settings.Review;
        int concurrency = Math.Clamp(review.MaxConcurrentReviews, 1, 10);
        if (concurrency != review.MaxConcurrentReviews)
        {
            review.MaxConcurrentReviews = concurrency;
            changed = true;
        }

        int itemsPerRequest = Math.Clamp(review.ItemsPerReviewRequest, 1, 10);
        if (itemsPerRequest != review.ItemsPerReviewRequest)
        {
            review.ItemsPerReviewRequest = itemsPerRequest;
            changed = true;
        }

        int refillThreshold = Math.Clamp(review.RefillThreshold, 0, 100);
        if (refillThreshold != review.RefillThreshold)
        {
            review.RefillThreshold = refillThreshold;
            changed = true;
        }

        int historyItems = Math.Clamp(review.MaxHistoryItems, 0, 10_000);
        if (historyItems != review.MaxHistoryItems)
        {
            review.MaxHistoryItems = historyItems;
            changed = true;
        }

        LogSettings log = _settings.Log;
        int retentionDays = Math.Clamp(log.RetentionDays, 1, 3650);
        if (retentionDays != log.RetentionDays)
        {
            log.RetentionDays = retentionDays;
            changed = true;
        }

        return changed;
    }

    private static void EnsureUniqueProfileIds(List<AiProviderProfile> profiles, ref bool changed)
    {
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (AiProviderProfile profile in profiles)
        {
            string requestedId = (profile.Id ?? string.Empty).Trim();
            if (requestedId.Length == 0)
            {
                requestedId = (profile.PresetId ?? string.Empty).Trim();
                if (requestedId.Length == 0) requestedId = $"profile-{Guid.NewGuid():N}";
            }

            string uniqueId = requestedId;
            int suffix = 2;
            while (!usedIds.Add(uniqueId))
            {
                uniqueId = $"{requestedId}-{suffix++}";
            }

            if (!string.Equals(profile.Id, uniqueId, StringComparison.Ordinal))
            {
                profile.Id = uniqueId;
                changed = true;
            }
        }
    }

    private static string? ValidateProfile(AiProviderProfile profile)
    {
        if (!profile.IsEnabled) return "当前活动 AI 供应商已禁用，请启用或选择其他供应商。";

        if (!Enum.IsDefined(typeof(AiProtocol), profile.Protocol))
            return "当前活动 AI 供应商协议无效。";

        string baseUrl = (profile.BaseUrl ?? string.Empty).Trim();
        if (baseUrl.Length == 0) return "当前活动 AI 供应商未填写 Base URL。";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "当前活动 AI 供应商的 Base URL 必须是有效的 HTTP 或 HTTPS 地址。";
        }

        if (string.IsNullOrWhiteSpace(profile.Model))
            return "当前活动 AI 供应商未填写模型名称。";

        if (profile.RequiresApiKey && string.IsNullOrWhiteSpace(ResolveSecret(profile.ApiKey ?? string.Empty)))
            return "当前活动 AI 供应商需要 API Key，可直接填写或使用 env:环境变量名。";

        string extraHeaders = (profile.ExtraHeadersJson ?? string.Empty).Trim();
        if (extraHeaders.Length > 0)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(extraHeaders, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return "额外请求头 JSON 必须是对象。";
            }
            catch (JsonException)
            {
                return "额外请求头 JSON 格式无效。";
            }
        }

        return null;
    }

    private void TryBackupInvalidSettings()
    {
        try
        {
            string backupPath = Path.Combine(
                ConfigDirectory,
                $"settings.invalid-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
            File.Move(SettingsPath, backupPath);
        }
        catch (IOException)
        {
            // 备份失败时仍使用默认设置，避免插件因损坏配置无法启动。
        }
        catch (UnauthorizedAccessException)
        {
            // 只读目录中无法备份，仍继续使用内存默认值。
        }
    }

    private static void ReplaceAtomically(string tempPath, string destinationPath)
    {
        if (!File.Exists(destinationPath))
        {
            File.Move(tempPath, destinationPath);
            return;
        }

        try
        {
            File.Replace(tempPath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        catch (PlatformNotSupportedException)
        {
            File.Move(tempPath, destinationPath, overwrite: true);
        }
    }
}