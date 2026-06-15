# VMentory Phase 2 — Component Specs

Per-component specifications expanding [../ARCHITECTURE.md](../ARCHITECTURE.md) (the spine) and
tracked by [../ROADMAP.md](../ROADMAP.md). Each spec is decision-anchored to the three locked
Phase-2 decisions and references the real Phase-1 source as `path:line`.

| Spec | Covers | Primary milestone |
|---|---|---|
| [provider-abstraction.md](provider-abstraction.md) | `IVirtualizationProvider`, capability model, HyperV vs Proxmox differences, generalizing the Phase-1 domain | [2.0](../ROADMAP.md) |
| [agent-protocol.md](agent-protocol.md) | Core↔Windows-agent transport (gRPC recommended), mTLS auth, RPC surface, where `Scanner`/`Reachability` relocate | [2.0](../ROADMAP.md) |
| [proxmox-integration.md](proxmox-integration.md) | PVE REST surface, API-token auth, REST-vs-SSH boundary, gotchas | [2.1](../ROADMAP.md) |
| [migration-job-model.md](migration-job-model.md) | Persisted resumable DAG, HV→PVE step flow, virt-v2v wrapping, idempotency/rollback, UEFI/virtio edge cases | [2.3](../ROADMAP.md) |
| [persistence-and-security.md](persistence-and-security.md) | Schema sketch, what is/isn't persisted, secret handling, auth model, audit log | [2.0](../ROADMAP.md) → [2.2](../ROADMAP.md) |

## Locked decisions these specs honour

1. **Hyper-V via an agent on the Windows host** (HTTP/gRPC), not remote WinRM from Linux.
2. **Hybrid persistence** — registry/jobs/history persisted; secrets in a vault/secret store,
   never plaintext at rest.
3. **Migration orchestrates proven tools** (virt-v2v, qemu-img, `qm importdisk`), not built from
   scratch.

## Cross-cutting open questions

Each spec records its own "Open questions" section. The ones that need the **project owner** to
decide before implementation are consolidated here:

- **Stable VM identity** across sessions/migration — [provider-abstraction.md](provider-abstraction.md#7-open-questions-need-a-human-decision).
- **Conversion-host placement** (PVE node vs dedicated container) — recurs in
  [agent-protocol.md](agent-protocol.md#7-open-questions-need-a-human-decision),
  [proxmox-integration.md](proxmox-integration.md#5-open-questions-need-a-human-decision),
  [migration-job-model.md](migration-job-model.md#8-open-questions-need-a-human-decision).
- **mTLS PKI ownership** (Core-as-CA vs operator-supplied) — [agent-protocol.md](agent-protocol.md#7-open-questions-need-a-human-decision).
- **PVE TLS trust model** (fingerprint pinning vs proper certs) — [proxmox-integration.md](proxmox-integration.md#5-open-questions-need-a-human-decision).
- **Multi-tenant vs single-operator** (sets auth depth + schema) — [persistence-and-security.md](persistence-and-security.md#7-open-questions-need-a-human-decision).
- **Secret-store v1 target** and **DB at-rest encryption** — [persistence-and-security.md](persistence-and-security.md#7-open-questions-need-a-human-decision).
- **Secure-Boot guests in migration MVP scope** + **quiesce default** — [migration-job-model.md](migration-job-model.md#8-open-questions-need-a-human-decision).
