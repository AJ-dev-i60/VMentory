using Microsoft.EntityFrameworkCore;

namespace VMentory.Core.Persistence;

public class EfUserStore(VMentoryDbContext db) : IUserStore
{
    public Task<bool> AnyUsersAsync(CancellationToken ct = default)
        => db.Users.AnyAsync(ct);

    public Task<AppUserEntity?> FindByUsernameAsync(string username, CancellationToken ct = default)
        => db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);

    public Task<List<AppUserEntity>> GetAllAsync(CancellationToken ct = default)
        => db.Users.ToListAsync(ct);

    public async Task CreateAsync(AppUserEntity user, CancellationToken ct = default)
    {
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(AppUserEntity user, CancellationToken ct = default)
    {
        db.Users.Update(user);
        await db.SaveChangesAsync(ct);
    }

    public async Task WriteAuditAsync(AuditEventEntity evt, CancellationToken ct = default)
    {
        db.AuditEvents.Add(evt);
        await db.SaveChangesAsync(ct);
    }
}
