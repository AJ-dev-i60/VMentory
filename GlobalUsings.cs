// In VMentory, the unqualified type `Host` always means the domain host (VMentory.Core.Host),
// not Microsoft.Extensions.Hosting.Host (which this app never uses — it builds via
// WebApplication.CreateBuilder). Phase 1 resolved this implicitly because Host lived in the same
// namespace as its callers; after the Core/Web split it arrives via `using VMentory.Core`, so we
// alias it project-wide to keep the ambiguity from resurfacing in every file that touches a host.
global using Host = VMentory.Core.Host;
