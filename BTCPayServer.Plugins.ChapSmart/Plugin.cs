using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Plugins.ChapSmart.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.ChapSmart;

public class Plugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new IBTCPayServerPlugin.PluginDependency { Identifier = nameof(BTCPayServer), Condition = ">=2.0.1" }
    };

    public override void Execute(IServiceCollection services)
    {
        // UI: Add ChapSmart to store sidebar navigation
        services.AddUIExtension("store-integrations-nav", "ChapSmartNav");

        // Services
        services.AddSingleton<ChapSmartSettingsRepository>();
        services.AddSingleton<ChapSmartService>();
        services.AddHostedService<ChapSmartInvoiceHandler>();

        // Database
        services.AddSingleton<ChapSmartDbContextFactory>();
        services.AddDbContext<ChapSmartDbContext>((provider, o) =>
        {
            var factory = provider.GetRequiredService<ChapSmartDbContextFactory>();
            factory.ConfigureBuilder(o);
        });
        services.AddHostedService<PluginMigrationRunner>();
    }
}
