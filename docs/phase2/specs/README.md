# VMentory Phase 2 — Component Specs

Per-component specifications expanding [../ARCHITECTURE.md](../ARCHITECTURE.md) (the spine) and
tracked by [../ROADMAP.md](../ROADMAP.md). Each spec is decision-anchored to the engineering
decisions in [`docs/engineering/REGISTER.md`](../../engineering/REGISTER.md) (cited as `ENG-NNNN`)
and references the real Phase-1 source as `path:line`. The platform is **four pillars on a shared
foundation** (ENG-0006) — these specs cover the foundation + the Migrate pillar; Deploy/Backup
surfaces are noted where the foundation must leave room for them.

| Spec | Covers | Primary milestone |
|---|---|---|
| [provider-abstraction.md](provider-abstraction.md) | `IVirtualizationProvider`, capability model (now also gating Deploy/Backup), HyperV vs Proxmox differences, generalizing the Phase-1 domain | [2.0](../ROADMAP.md) |
| [agent-protocol.md](agent-protocol.md) | Core↔agent **gRPC/HTTP2 + mTLS** (ENG-0004), enrollment + private-CA PKI (ENG-0003/0005), constrained verb catalog, self-update, capability negotiation | [2.0](../ROADMAP.md) |
| [proxmox-integration.md](proxmox-integration.md) | PVE REST surface, scoped API-token auth, REST-vs-SSH boundary, gotchas | [2.1](../ROADMAP.md) |
| [migration-job-model.md](migration-job-model.md) | Persisted resumable DAG (the **general operations engine**), HV→PVE step flow on the **`qm importdisk` baseline** (virt-v2v optional), idempotency/rollback, UEFI/virtio edge cases | [2.3](../ROADMAP.md) |
| [persistence-and-security.md](persistence-and-security.md) | Schema sketch, what is/isn't persisted, **`ISecretStore`** + envelope encryption (ENG-0002), agent **private-CA** PKI (ENG-0005), auth model, audit log | [2.0](../ROADMAP.md) → [2.2](../ROADMAP.md) |

## Decisions these specs honour (see [`docs/engineering/REGISTER.md`](../../engineering/REGISTER.md))

- **ENG-0001** — Hyper-V via a **.NET-native agent** (no winrun.py); migration on the proven
  **`qm importdisk`** path (virt-v2v optional); agent is the migration gate.
- **ENG-0002** — **`ISecretStore`** + app-native envelope encryption, runtime-injected KEK,
  tokens-over-passwords.
- **ENG-0003** — agent install is **manual + enrollment token**; no remote push-install.
- **ENG-0004** — agent = **NativeAOT**, **gRPC/HTTP2 + mTLS**, **constrained verb executor**,
  gMSA+local, **mTLS self-update** with watchdog rollback.
- **ENG-0005** — agent PKI = **private CA in Core**, root+intermediate, short-lived certs +
  auto-renew, allow/deny revocation (no CRL/OCSP).
- **ENG-0006** — **four pillars** (Observe→Migrate→Deploy→Backup) on a shared foundation;
  **single-operator**; bidirectional migration end-state, HV→PVE first; backup buy-vs-build deferred.

## Cross-cutting open questions

Each spec records its own "Open / resolved questions" section. The ones still needing the
**project owner** (the rest are now decided — see the register) are consolidated here:

- **Conversion-host placement** for the *optional* virt-v2v enhancement (the baseline `qm importdisk`
  runs on the PVE node regardless) — recurs in
  [agent-protocol.md](agent-protocol.md#9-open--resolved-questions),
  [proxmox-integration.md](proxmox-integration.md#5-open-questions-need-a-human-decision),
  [migration-job-model.md](migration-job-model.md#8-open--resolved-questions).
- **Stable VM identity** across sessions/migration — [provider-abstraction.md](provider-abstraction.md#7-open-questions-need-a-human-decision).
- **PVE node TLS trust model** (fingerprint pinning vs proper certs) — [proxmox-integration.md](proxmox-integration.md#5-open-questions-need-a-human-decision).
- **Snapshot retention/cadence** + **DB at-rest encryption** — [persistence-and-security.md](persistence-and-security.md#7-open--resolved-questions).
- **Secure-Boot guests in migration MVP scope** — [migration-job-model.md](migration-job-model.md#8-open--resolved-questions).
- **Future ENG topics (ENG-0006):** storage/repository placement, guest-customization engine,
  backup buy-vs-build, PVE→HV reverse migration.
