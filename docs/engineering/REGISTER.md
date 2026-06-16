# Engineering Decision Register

**Read this first.** Every architectural/implementation discussion and its current state.
Protocol in [README.md](README.md). Don't re-open a **Decided** topic without superseding it.

| ID | Topic | Status | Affects | Owner |
|----|-------|--------|---------|-------|
| [ENG-0001](discussions/0001-hyperv-migration-transport.md) | Hyper-V migration transport for the 2.3 MVP (winrun.py vs Windows agent vs both) | **Decided** (B — agent first, .NET) | ROADMAP 2.0/2.2/2.3, agent-protocol spec, migrate-vm skill | human |
| [ENG-0002](discussions/0002-secret-store.md) | Secret handling: store design & root-key model | **Decided** (ISecretStore + app-native envelope enc, runtime-injected KEK, tokens-over-passwords) | persistence-and-security spec, ARCHITECTURE decision #2, agent mTLS | human |
| [ENG-0003](discussions/0003-agent-install-model.md) | Agent install / onboarding model (remote push vs manual) | **Decided** (manual/org-managed only + enrollment token; no remote install) | agent-protocol spec, ROADMAP 2.0/2.2 | human |
| [ENG-0004](discussions/0004-agent-runtime-lifecycle.md) | Agent runtime, packaging, self-update & security model (lightweight/updatable/secure/robust) | **Decided** (NativeAOT, gRPC+mTLS, constrained-verb, gMSA+local, mTLS self-update) | agent-protocol spec, ROADMAP 2.0/2.2 | human |
| [ENG-0005](discussions/0005-mtls-pki-ownership.md) | mTLS PKI ownership (agent enrollment & trust) | **Decided** (private CA in Core + external seam; root+intermediate; short-lived certs + auto-renew) | agent-protocol spec, persistence-and-security spec, ROADMAP 2.0 | human |
| [ENG-0006](discussions/0006-product-scope-four-pillars.md) | Product scope: four-pillar platform (Observe/Migrate/Deploy/Backup) & sequencing | **Decided** (4 pillars on shared foundation; Observe→Migrate→Deploy→Backup; bidirectional migration HV→PVE first; backup buy-vs-build deferred) | ARCHITECTURE, ROADMAP, all specs, design | human |
| [ENG-0007](discussions/0007-product-strategy-release-scope.md) | Product strategy & release-1 scope: Proxmox-first, HV-as-managed-source, incremental shipping | **Decided** (Proxmox-first super-tool; incremental per-milestone releases, v2.0=planted Observe; HV=light management not parity → provider capability model must allow HV management verbs; weighting Observe→Proxmox mgmt/Deploy→HV→PVE migration; PVE→HV reverse + Backup out of release 1; refines/sequences ENG-0006) | ROADMAP, ARCHITECTURE, PROGRESS, design, ENG-0006 | human |

<!-- Add new rows at the bottom. Keep IDs sequential. -->

## Legend
**Open** → raised, not analyzed · **In discussion** → being worked · **Awaiting decision** → needs the human owner · **Decided** → relied upon downstream · **Superseded** → replaced (link the successor)
