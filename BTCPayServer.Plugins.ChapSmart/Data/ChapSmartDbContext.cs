using BTCPayServer.Plugins.ChapSmart.Data;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.ChapSmart;

public class ChapSmartDbContext : DbContext
{
    private readonly bool _designTime;

    public ChapSmartDbContext(DbContextOptions<ChapSmartDbContext> options, bool designTime = false)
        : base(options)
    {
        _designTime = designTime;
    }

    public DbSet<ChapSmartPayout> Payouts { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("BTCPayServer.Plugins.ChapSmart");

        modelBuilder.Entity<ChapSmartPayout>(entity =>
        {
            entity.HasIndex(e => e.InvoiceId);
            entity.HasIndex(e => e.StoreId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CreatedAt);
        });
    }
}
