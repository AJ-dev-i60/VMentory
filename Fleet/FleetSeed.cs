using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VMentory.Core;
using VMentory.Core.Persistence;
using VMentory.Core.Secrets;

namespace VMentory.Web.Fleet;

public static class FleetSeed
{
    // VMENTORY_SEED_PVE_HOSTS — same contract as the single VMENTORY_SEED_PVE_HOST seed (ENG-0015),
    // for many nodes: registers unknown addresses, re-arms a token the secret store has lost.
    public static async Task SeedHostsAsync(Store store, IInventoryStore inv, ISecretStore secrets, FleetOptions opt)
    {
        foreach (var (addr, token, name) in opt.SeedHosts)
        {
            var host = store.GetAllHosts().FirstOrDefault(h => h.Address.Equals(addr, StringComparison.OrdinalIgnoreCase));
            if (host == null)
            {
                host = new Host
                {
                    Address = addr, Fqdn = addr, Platform = PlatformKind.Proxmox, UseGlobalCreds = false,
                    SkipTlsVerification = true, DisplayName = name,
                };
                store.AddHost(host);
                await inv.UpsertRegistrationAsync(host);
                DevLog.Ok($"[SEED] Proxmox node {addr} registered from VMENTORY_SEED_PVE_HOSTS");
            }
            if (host.PerHostCreds == null || host.PerHostCreds.GetPassword() != token)
            {
                store.UpdateHost(host.Id, h => { h.PerHostCreds = new Credentials("", token); h.UseGlobalCreds = false; });
                await secrets.SetAsync($"host_cred:{host.Id}", JsonSerializer.Serialize(new { username = "", password = token }));
            }
        }
    }

    // First run only (empty table): the constraints the v3 brief states as already true of this
    // estate. These are facts about which guests must not share a host — not numbers — and every
    // one is editable in the UI and labelled "seed".
    public static async Task SeedRulesAsync(VMentoryDbContext db, FleetOptions opt)
    {
        if (!opt.SeedRules || await db.FleetRules.AnyAsync()) return;
        var now = DateTimeOffset.UtcNow;
        FleetRuleEntity R(FleetRuleKind k, string name, string[] members, string note) => new()
        {
            Kind = k, Name = name, MembersJson = JsonSerializer.Serialize(members), Note = note, Source = "seed", CreatedAt = now, CreatedBy = "seed",
        };
        db.FleetRules.AddRange(
            R(FleetRuleKind.AntiAffinity, "iSixty domain controllers", ["isxdc1", "isxdc2"],
              "Never put both controllers of a domain on one hypervisor (Atlas outage write-up)."),
            R(FleetRuleKind.AntiAffinity, "i60 domain controllers", ["i60dc2", "DC1"],
              "DC1 runs on Rhea (Hyper-V) today; the rule bites if it ever lands on a Proxmox node next to i60dc2."),
            R(FleetRuleKind.Pinned, "Arcserve primary", ["arcserveprimary"],
              "Owns an in-guest iSCSI LUN on 172.0.0.212 — two live copies would corrupt it. Offline moves only."),
            R(FleetRuleKind.Ephemeral, "GitLab restore test", ["gitlab-restore-test"],
              "A disposable copy holding real secrets — a deletion candidate, not something to migrate."),
            R(FleetRuleKind.SelfHost, "EdgeStudios Coolify VM (hosts VMentory)", ["aj-linux-box-71"],
              "Moving it offline stops this console until it is back. Moved last in any drain."));
        await db.SaveChangesAsync();
        DevLog.Ok("[SEED] fleet placement rules seeded (5)");
    }
}
