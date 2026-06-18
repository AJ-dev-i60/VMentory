using System.Security.Claims;
using VMentory.Core;
using VMentory.Core.Auth;

namespace VMentory.Web;

// Static role→permission catalog (ENG-0008). Fixed built-in roles backed by a pillar×verb mapping
// so the enforcement chokepoint is a single call and a future custom-roles UI can swap the catalog
// without reworking the enforcement layer.
public static class RbacCatalog
{
    private static readonly Dictionary<AppRole, ConsolePermission> _console = new()
    {
        [AppRole.Admin]          = ConsolePermission.ViewAudit | ConsolePermission.ManageCredentials
                                   | ConsolePermission.ManageEnrollment | ConsolePermission.ManageUsers,
        [AppRole.VmOperator]     = ConsolePermission.ManageCredentials,
        [AppRole.BackupOperator] = ConsolePermission.None,
        [AppRole.Viewer]         = ConsolePermission.None,
    };

    private static readonly Dictionary<AppRole, ProviderCapability> _provider = new()
    {
        [AppRole.Admin]          = ProviderCapability.Inventory | ProviderCapability.LiveStats
                                   | ProviderCapability.HistoricalStats | ProviderCapability.Start
                                   | ProviderCapability.Stop | ProviderCapability.Reconfigure
                                   | ProviderCapability.Snapshot | ProviderCapability.Reset
                                   | ProviderCapability.ExportDisk | ProviderCapability.ImportDisk
                                   | ProviderCapability.CreateVmShell | ProviderCapability.Provision
                                   | ProviderCapability.Backup | ProviderCapability.Restore,
        [AppRole.VmOperator]     = ProviderCapability.Inventory | ProviderCapability.LiveStats
                                   | ProviderCapability.Start | ProviderCapability.Stop
                                   | ProviderCapability.Reconfigure,
        [AppRole.BackupOperator] = ProviderCapability.Inventory | ProviderCapability.LiveStats
                                   | ProviderCapability.Backup,
        [AppRole.Viewer]         = ProviderCapability.Inventory | ProviderCapability.LiveStats,
    };

    public static AppRole? GetRole(ClaimsPrincipal user)
    {
        var claim = user.FindFirstValue(ClaimTypes.Role);
        return Enum.TryParse<AppRole>(claim, out var r) ? r : null;
    }

    public static bool Can(ClaimsPrincipal user, ConsolePermission permission)
    {
        var role = GetRole(user);
        return role.HasValue && _console.TryGetValue(role.Value, out var p) && p.HasFlag(permission);
    }

    public static bool Can(ClaimsPrincipal user, ProviderCapability capability)
    {
        var role = GetRole(user);
        return role.HasValue && _provider.TryGetValue(role.Value, out var c) && c.HasFlag(capability);
    }

    // Convenience: any authenticated user with at least Viewer access.
    public static bool IsAuthenticated(ClaimsPrincipal user) => GetRole(user).HasValue;
}
