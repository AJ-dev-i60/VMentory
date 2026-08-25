using Microsoft.EntityFrameworkCore;

namespace VMentory.Core.Persistence;

// The Core datastore. SQLite by default (single file in a mounted volume); Postgres opt-in later.
// Single-operator (ENG-0006): no tenant_id anywhere. The schema grows over later slices
// (operation jobs/steps/logs, secret metadata, agent PKI, audit, users) — kept lean for 2.0.
public class VMentoryDbContext(DbContextOptions<VMentoryDbContext> options) : DbContext(options)
{
    public DbSet<HostRegistrationEntity> Hosts => Set<HostRegistrationEntity>();
    public DbSet<InventorySnapshotEntity> Snapshots => Set<InventorySnapshotEntity>();
    public DbSet<AppUserEntity> Users => Set<AppUserEntity>();
    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();
    public DbSet<SecretEntity> Secrets => Set<SecretEntity>();
    public DbSet<DekEntity> Deks => Set<DekEntity>();

    // ENG-0015 — estate + remediation tracker
    public DbSet<MachineEntity> Machines => Set<MachineEntity>();
    public DbSet<ActionItemEntity> Actions => Set<ActionItemEntity>();
    public DbSet<ActionDependencyEntity> ActionDependencies => Set<ActionDependencyEntity>();
    public DbSet<ActionNoteEntity> ActionNotes => Set<ActionNoteEntity>();
    public DbSet<HardwareSnapshotEntity> HardwareSnapshots => Set<HardwareSnapshotEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // ── ENG-0015 ──
        b.Entity<MachineEntity>(e =>
        {
            e.HasKey(m => m.Key);
            e.Property(m => m.Kind).HasConversion<string>();
            e.HasIndex(m => m.ServiceTag);
            e.HasIndex(m => m.Address);
        });

        b.Entity<ActionItemEntity>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => a.Ref).IsUnique();
            e.HasIndex(a => a.SourceKey);
            e.HasIndex(a => a.Status);
            e.Property(a => a.Priority).HasConversion<string>();
            e.Property(a => a.Class).HasConversion<string>();
            e.Property(a => a.Status).HasConversion<string>();
            e.HasMany(a => a.Notes).WithOne(n => n.Action!).HasForeignKey(n => n.ActionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ActionDependencyEntity>(e =>
        {
            e.HasKey(d => new { d.BlockedId, d.BlockerId });
            e.HasOne(d => d.Blocked).WithMany(a => a.BlockedBy).HasForeignKey(d => d.BlockedId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(d => d.Blocker).WithMany(a => a.Blocks).HasForeignKey(d => d.BlockerId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ActionNoteEntity>(e =>
        {
            e.HasKey(n => n.Id);
            e.HasIndex(n => n.ActionId);
        });

        b.Entity<HardwareSnapshotEntity>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.TakenAt);
        });

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

        b.Entity<AppUserEntity>(e =>
        {
            e.HasKey(u => u.Id);
            e.HasIndex(u => u.Username).IsUnique();
            e.Property(u => u.Role).HasConversion<string>();
        });

        b.Entity<AuditEventEntity>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => new { a.Timestamp, a.Username });
        });

        b.Entity<SecretEntity>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.Key).IsUnique();
        });

        b.Entity<DekEntity>(e =>
        {
            e.HasKey(d => d.Id);
        });
    }
}
