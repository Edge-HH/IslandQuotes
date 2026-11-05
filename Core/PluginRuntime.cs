using System.IO;
using Motivation.Services;

namespace Motivation.Core;

/// <summary>
/// 插件进程级运行时。Initialize 负责按依赖顺序构造服务且只构造一次；
/// 其它组件只依赖这里暴露的稳定实例。
/// </summary>
public static class PluginRuntime
{
    private static readonly object LifecycleSync = new();
    private static readonly SemaphoreSlim ShutdownGate = new(1, 1);
    private static string _configDirectory = string.Empty;
    private static PluginSettingsStore? _settingsStore;
    private static QuoteSourceService? _quoteSources;
    private static AiReviewService? _aiReview;
    private static AuditLogService? _auditLog;
    private static ReviewQueueService? _queue;
    private static bool _initialized;
    private static bool _shutdown;

    public static string ConfigDirectory
    {
        get
        {
            lock (LifecycleSync)
            {
                if (!_initialized)
                {
                    throw new InvalidOperationException("PluginRuntime 尚未初始化。");
                }

                return _configDirectory;
            }
        }
    }

    public static PluginSettingsStore SettingsStore =>
        GetRequired(_settingsStore, nameof(SettingsStore));

    public static QuoteSourceService QuoteSources =>
        GetRequired(_quoteSources, nameof(QuoteSources));

    public static AiReviewService AiReview =>
        GetRequired(_aiReview, nameof(AiReview));

    public static AuditLogService AuditLog =>
        GetRequired(_auditLog, nameof(AuditLog));

    public static ReviewQueueService Queue =>
        GetRequired(_queue, nameof(Queue));

    /// <summary>
    /// 加载插件设置、创建所有服务并恢复审查/播放队列。重复调用为空操作。
    /// </summary>
    public static void Initialize(string pluginConfigFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginConfigFolder);

        lock (LifecycleSync)
        {
            if (_initialized)
            {
                return;
            }

            if (_shutdown)
            {
                throw new InvalidOperationException("PluginRuntime 已关闭，不能再次初始化。");
            }

            string configDirectory = Path.GetFullPath(pluginConfigFolder);
            Directory.CreateDirectory(configDirectory);

            PluginSettingsStore settingsStore = new(configDirectory);
            settingsStore.Load();
            settingsStore.EnsureDefaults();

            AuditLogService auditLog = new(
                settingsStore,
                settingsStore.Settings.Log.RetentionDays);
            QuoteSourceService quoteSources = new(auditLog);
            AiReviewService aiReview = new(settingsStore, auditLog);
            ReviewQueueService queue = new(settingsStore, quoteSources, aiReview, auditLog);

            queue.InitializeAsync().GetAwaiter().GetResult();

            _configDirectory = configDirectory;
            _settingsStore = settingsStore;
            _quoteSources = quoteSources;
            _aiReview = aiReview;
            _auditLog = auditLog;
            _queue = queue;
            _initialized = true;
        }
    }

    /// <summary>刷新队列快照、设置与审计日志。重复调用为空操作。</summary>
    public static async Task ShutdownAsync()
    {
        await ShutdownGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_shutdown)
            {
                return;
            }

            ReviewQueueService? queue;
            PluginSettingsStore? settingsStore;
            AuditLogService? auditLog;
            lock (LifecycleSync)
            {
                queue = _queue;
                settingsStore = _settingsStore;
                auditLog = _auditLog;
            }

            if (queue is not null)
            {
                try
                {
                    await queue.FlushAsync().ConfigureAwait(false);
                }
                catch
                {
                    // 关闭流程必须继续刷新其它组件。
                }
            }

            if (settingsStore is not null)
            {
                try
                {
                    await settingsStore.SaveAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // 设置保存失败不应阻止审计日志刷新。
                }
            }

            if (auditLog is not null)
            {
                try
                {
                    await auditLog.FlushAsync().ConfigureAwait(false);
                }
                catch
                {
                    // 关闭阶段没有可安全呈现错误的 UI 上下文。
                }
            }

            _shutdown = true;
        }
        finally
        {
            ShutdownGate.Release();
        }
    }

    private static T GetRequired<T>(T? instance, string propertyName) where T : class
    {
        lock (LifecycleSync)
        {
            if (!_initialized || instance is null)
            {
                throw new InvalidOperationException(
                    $"PluginRuntime 尚未初始化，无法访问 {propertyName}。");
            }

            return instance;
        }
    }
}
