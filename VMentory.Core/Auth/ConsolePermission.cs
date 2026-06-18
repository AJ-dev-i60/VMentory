namespace VMentory.Core.Auth;

[Flags]
public enum ConsolePermission
{
    None              = 0,
    ViewAudit         = 1 << 0,
    ManageCredentials = 1 << 1,
    ManageEnrollment  = 1 << 2,
    ManageUsers       = 1 << 3,
}
