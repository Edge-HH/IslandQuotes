using ClassIsland.Core;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using Motivation.Components.QuoteAi;
using Motivation.Core;
using Motivation.Services;
using Motivation.SettingsPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Motivation;

/// <summary>
/// Motivation · AI 每日一言 plugin entrance.
/// </summary>
[PluginEntrance]
public sealed class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        if (string.IsNullOrWhiteSpace(PluginConfigFolder))
            throw new InvalidOperationException("ClassIsland did not provide a plugin config folder.");

        PluginRuntime.Initialize(PluginConfigFolder);
        Console.WriteLine($"[Motivation] config: {PluginRuntime.ConfigDirectory}");
        Console.WriteLine($"[Motivation] AI provider: {PluginRuntime.SettingsStore.GetActiveProfile()?.DisplayLabel ?? "not configured"}");
        Console.WriteLine($"[Motivation] audit log: {PluginRuntime.AuditLog.LogDirectory}");

        services.AddSingleton(PluginRuntime.SettingsStore);
        services.AddSingleton(PluginRuntime.AuditLog);
        services.AddSingleton(PluginRuntime.QuoteSources);
        services.AddSingleton(PluginRuntime.AiReview);
        services.AddSingleton(PluginRuntime.Queue);

        services.AddComponent<QuoteAiComponent, QuoteAiSettings>();

        services.AddSettingsPageGroup("motivation", "\uE9D9", "Motivation · AI 每日一言")
            .AddSettingsPage<AiSettingsPage>()
            .AddSettingsPage<ReviewQueuePage>();

        AppBase.Current.AppStopping += (_, _) =>
        {
            try
            {
                PluginRuntime.ShutdownAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Motivation] shutdown failed: {ex.Message}");
            }
        };
    }
}