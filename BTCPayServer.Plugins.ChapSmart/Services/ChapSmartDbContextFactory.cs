using System;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace BTCPayServer.Plugins.ChapSmart.Services;

public class ChapSmartDbContextFactory : BaseDbContextFactory<ChapSmartDbContext>
{
    public ChapSmartDbContextFactory(IOptions<DatabaseOptions> options) :
        base(options, "BTCPayServer.Plugins.ChapSmart")
    {
    }

    public override ChapSmartDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder> npgsqlOptionsAction = null)
    {
        var builder = new DbContextOptionsBuilder<ChapSmartDbContext>();
        ConfigureBuilder(builder, npgsqlOptionsAction);
        return new ChapSmartDbContext(builder.Options);
    }
}
