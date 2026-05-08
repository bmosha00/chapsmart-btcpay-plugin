using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.ChapSmart.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Plugins.ChapSmart;

public class PluginMigrationRunner : IHostedService
{
    private readonly ChapSmartDbContextFactory _dbContextFactory;

    public PluginMigrationRunner(ChapSmartDbContextFactory dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        await ctx.Database.MigrateAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
