# VMentory Phase 2 — Progress & Resumption

> **Purpose:** pick up Phase 2 from a clean clone on any machine. Read this top-to-bottom and you
> know where we are, what's decided, what's open, and what to do next.
>
> **Last updated:** 2026-06-15 · **Phase:** planning (pre-implementation) · **Branch:** `dev`

---

## 1. Orientation (60 seconds)

VMentory Phase 1 = single-exe, **Windows-only, read-only, ephemeral** Hyper-V inventory tool
(ASP.NET Core 8 + vanilla-JS SPA, namespace `HyperInventory`, in-memory only, WinRM via
`powershell.exe`). It works and ships today.

Phase 2 = turn it into a **hosted, multi-platform (Hyper-V + Proxmox) management + migration
platform**: a Linux-container "Core" that talks to providers, persists state, and runs migrations.

The full target design is in [ARCHITECTURE.md](ARCHITECTURE.md); the milestone plan is in
[ROADMAP.md](ROADMAP.md). **No Phase 2 product code has been written yet** — we are in planning,
docs, and decision-framing.

## 2. Locked decisions (the spine — don't silently revisit)

1. **Hyper-V transport (long-term):** a **Windows agent installed on the host** (HTTP/gRPC), not
   remote WinRM from Linux. *(But see open decision ENG-0001 — the migration MVP may reuse the
   existing winrun.py path first.)*
2. **Persistence: hybrid** — registry/jobs/history persisted (SQLite/EF Core); **secrets in a vault/
   secret store, never plaintext at rest**.
3. **Migration: orchestrate proven tools.** The validated path (from the `migrate-vm` skill) is
   **`qm importdisk` on the Proxmox node** + targeted guest fixes; **virt-v2v is optional**, not the
   baseline. ⚠️ ARCHITECTURE.md and `specs/migration-job-model.md` still over-index on virt-v2v —
   **a docs reconciliation is pending** (held for owner review).
4. **Single-operator** (not multi-tenant) — no `tenant_id` in the schema.
5. **Dashboard:** ship direction **A (unified list)** as default, with **B (grouped)** as a toggle.
6. **Migration quiesce:** **operator choice per job, with explicit caveats** (static frontend →
   favor uptime/checkpoint; database server → favor consistency/graceful shutdown). Not hardcoded.

## 3. What exists right now

### Planning docs (`docs/phase2/`)
- `ARCHITECTURE.md` — target topology, `IVirtualizationProvider` model, persistence, migration engine, carry-over table.
- `ROADMAP.md` — milestones **2.0** foundation/re-architecture → **2.1** Proxmox read → **2.2** management → **2.3** migration MVP → **2.4** scale.
- `specs/` — five component specs (provider-abstraction, agent-protocol, proxmox-integration, migration-job-model, persistence-and-security) + index. *Authored by the documentation agent; **pending owner review**; the migration spec needs the virt-v2v→qm-importdisk correction.*
- `PROGRESS.md` — this file.

### Engineering decision workspace (`docs/engineering/`)
- `README.md` — the RFC/ADR protocol. `REGISTER.md` — the board (read first).
- `discussions/0001-hyperv-migration-transport.md` — **ENG-0001, Awaiting decision**.

### Agents (`.claude/agents/`)
- `ui-design.md` — owns `design/`; produces mockups + specs; never edits `wwwroot/index.html`.
- `documentation.md` — owns `docs/`; authors/maintains specs.
- `engineering.md` — drives architectural decisions in `docs/engineering/`.

### Design output (`design/`) — **pending owner review**
- Mockups (2026-06-15): `dashboard-unified-list`, `dashboard-grouped`, `migration-wizard-stepper`, `migration-run-controlroom`.
- Proposals + from-design requests for the multi-platform dashboard and the migration wizard; `STATUS.md` updated.

### The migration skill (`migrate-vm/`) — validated ground truth
A working Hyper-V→Proxmox **cold-migration** runbook (last proven 2026-06-12, Ubuntu 22.04 Gen2).
`SKILL.md` + `references/runbook.md` + `references/centralized-access.md` + scripts
(`winrun.py`, `hyperv-prep.ps1`, `fix-guest-netplan.sh`). This is effectively the prototype of
VMentory's migration engine — its step graph, safety rules, and scripts feed the design.

## 4. Open decisions / what needs the owner

- **ENG-0001 (Awaiting decision):** how to drive the Hyper-V side of the 2.3 migration MVP — reuse
  `winrun.py` now (A), build the agent first (B), or support both (C). The owner is taking this to a
  dedicated engineering-discussion session. See the discussion file for the full brief.
- **Docs reconciliation (held):** demote virt-v2v, ground `migration-job-model.md` in the skill,
  fold in the safety rules + scripts. Held pending owner review of the agents' first output.
- **Review backlog:** the five specs and the four design mockups are first-drafts awaiting owner review.
- Lower-priority open questions captured by the doc agent: conversion-host placement detail, stable
  VM identity across migration, mTLS PKI ownership, secret-store v1 target, Secure-Boot guest scope.

## 5. Immediate next steps (suggested order)
1. Owner reviews the five specs (`docs/phase2/specs/`) and the four design mockups (`design/`).
2. Resolve **ENG-0001** in the engineering session → record the Decision → documentation agent
   propagates it into `agent-protocol.md` / `migration-job-model.md` / ROADMAP.
3. Reconcile the migration docs (virt-v2v → qm-importdisk, fold in the skill).
4. Begin **2.0 foundation**: rename `HyperInventory` → `VMentory.*`, split projects, define
   `IVirtualizationProvider` + capability model.

## 6. How to resume on a new machine
```
git clone <repo> && cd VMentory && git checkout dev
```
Read in this order: this file → `ARCHITECTURE.md` → `ROADMAP.md` → `docs/engineering/REGISTER.md`
→ the `specs/` you need. The three agents are defined in `.claude/agents/` and can be invoked by
name. CLAUDE.md still describes Phase 1 accurately (plus a Phase 2 pointer at the top).

> Note: agent **memories** live outside the repo (in the local Claude profile) and do **not** travel
> with the clone — this doc is the portable source of truth.
