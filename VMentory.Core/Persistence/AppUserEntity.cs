using VMentory.Core.Auth;

namespace VMentory.Core.Persistence;

public class AppUserEntity
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public AppRole Role { get; set; } = AppRole.Viewer;
    public bool MustChangePassword { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
}
