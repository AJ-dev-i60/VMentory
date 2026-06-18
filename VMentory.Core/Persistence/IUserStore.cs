namespace VMentory.Core.Persistence;

public interface IUserStore
{
    Task<bool> AnyUsersAsync(CancellationToken ct = default);
    Task<AppUserEntity?> FindByUsernameAsync(string username, CancellationToken ct = default);
    Task<List<AppUserEntity>> GetAllAsync(CancellationToken ct = default);
    Task CreateAsync(AppUserEntity user, CancellationToken ct = default);
    Task UpdateAsync(AppUserEntity user, CancellationToken ct = default);
    Task WriteAuditAsync(AuditEventEntity evt, CancellationToken ct = default);
}
