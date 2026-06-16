using Microsoft.EntityFrameworkCore;

namespace VMentory.Core.Persistence;

// The Core datastore. SQLite by default (single file in a mounted volume); Postgres opt-in later.
// Single-operator (ENG-0006): no tenant_id anywhere. The schema grows over later slices
// (operation jobs/steps/logs, secret metadata, agent PKI, audit, users) — kept lean for 2.0.
public class VMentoryDbContext(DbContextOptions<VMentoryDbContext> options) : DbContext(options)
{
    public DbSet<HostRegistrationEntity> Hosts => Set<HostRegistrationEntity>();
    public DbSet<InventorySnapshotEntity> Snapshots => Set<InventorySnapshotEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<HostRegistrationEntity>(e =>
        {
            e.HasKey(h => h.Id);
            e.Property(h => h.Platform).HasConversion<string>();
            e.HasMany(h => h.Snapshots)
                .WithOne(s => s.Host!)
                .HasForeignKey(s => s.HostId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<InventorySnapshotEntity>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => new { s.HostId, s.TakenAt });
        });
    }
}
