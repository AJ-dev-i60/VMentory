namespace VMentory.Core.Auth;

[Flags]
public enum ConsolePermission
{
    None              = 0,
    ViewAudit         = 1 << 0,
    ManageCredentials = 1 << 1,
    ManageEnrollment  = 1 << 2,
    ManageUsers       = 1 << 3,
    // ENG-0015: tick off, create, schedule and link remediation actions. Reading the estate and
    // the action list needs only an authenticated session.
    ManageActions     = 1 << 4,
}
