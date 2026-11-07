using ClassIsland.Core;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using IslandQuotes.Components.QuoteAi;
using IslandQuotes.Core;
using IslandQuotes.Services;
using IslandQuotes.SettingsPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IslandQuotes;

/// <summary>
/// IslandQuotes · AI 每日一言 plugin entrance.
/// </summary>
[PluginEntrance]
public sealed class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        if (string.IsNullOrWhiteSpace(PluginConfigFolder))
            throw new InvalidOperationException("ClassIsland did not provide a plugin config folder.");

        PluginRuntime.Initialize(PluginConfigFolder);
        Console.WriteLine($"[IslandQuotes] config: {PluginRuntime.ConfigDirectory}");
        Console.WriteLine($"[IslandQuotes] AI provider: {PluginRuntime.SettingsStore.GetActiveProfile()?.DisplayLabel ?? "not configured"}");
        Console.WriteLine($"[IslandQuotes] audit log: {PluginRuntime.AuditLog.LogDirectory}");

        services.AddSingleton(PluginRuntime.SettingsStore);
        services.AddSingleton(PluginRuntime.AuditLog);
        services.AddSingleton(PluginRuntime.QuoteSources);
        services.AddSingleton(PluginRuntime.AiReview);
        services.AddSingleton(PluginRuntime.Queue);

        services.AddComponent<QuoteAiComponent, QuoteAiSettings>();

        services.AddSettingsPageGroup("islandquotes", "\uF307", "IslandQuotes")
            .AddSettingsPage<AiSettingsPage>()
            .AddSettingsPage<ReviewQueuePage>()
            .AddSettingsPage<ReviewHistoryPage>();

        AppBase.Current.AppStopping += (_, _) =>
        {
            try
            {
                PluginRuntime.ShutdownAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[IslandQuotes] shutdown failed: {ex.Message}");
            }
        };
    }
}