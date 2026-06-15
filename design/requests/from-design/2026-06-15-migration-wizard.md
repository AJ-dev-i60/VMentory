# Migration wizard (HV→Proxmox) — stepper setup + control-room run

**Mockups:**
- `design/mockups/2026-06-15-migration-wizard-stepper.html` — setup phase (precheck + map resources)
- `design/mockups/2026-06-15-migration-run-controlroom.html` — run phase (job DAG + partial failure + rollback)

**Proposal (rationale + recommendation):** `design/proposals/2026-06-15-migration-wizard.md`
**Status:** Proposed — awaiting human review
**Touches:** `wwwroot/index.html` — a NEW view (route, e.g. `#migrate/<jobId>` and `#migrate/new`). Reuses SSE (`EventHub.cs`) for live step progress. Largely net-new CSS + JS; reuses existing tokens, pill/checklist/log idioms.

**Targets ROADMAP 2.3.** Do not start before the migration job engine (persisted resumable DAG, per-step logs, SSE progress, dry-run) exists. This is the UI spec for that engine.

## What the user sees after this lands

Two connected phases of one flow, with a seam at **Run**:

### Phase 1 — setup (stepper)
A numbered stepper rail: **Precheck · Map resources · Run · Validate · Cutover**.
1. **Precheck** — pass/warn/fail checklist reusing `.health`-style colour semantics. Items: target capability match, target free space, guest-OS support (virt-v2v), power state. Warnings inform; only blockers stop progression. Re-runnable.
2. **Map resources** — three source→target tables:
   - **Compute & firmware:** vCPU, RAM, firmware (Gen2/UEFI → OVMF + EFI disk), machine type (q35 for UEFI). Each row: source value (read-only) → editable target, plus a flag (`direct` / `auto-mapped` / `warn`). Secure-Boot-off-on-import shown as a non-blocking warn flag + callout.
   - **Storage (per disk):** source VHDX + size → target storage / format (raw thin / qcow2) / bus (virtio-scsi recommended).
   - **Network (per NIC):** source switch+VLAN → target bridge / model (virtio recommended) + "preserve MAC" checkbox.
   - Mapping is **persisted to the job** and editable until Run.

### Phase 2 — run (control room) — the seam
On **Run**, hand off to a persisted **job view**: a left-rail step **DAG** (done ✓ / running / failed ✕ / pending / skipped) + a right detail pane for the selected/failed step. Validate and Cutover are **steps in the DAG**, not separate wizard pages (cutover stays gated on green validation). Critical behaviours:
- **Partial failure is first-class.** Job pauses at the failed step; completed steps preserved. Per-step recovery row: **Edit input & retry · Retry as-is · Skip (advanced) · Roll back & abort**.
- **Rollback safety always visible.** Banner shows "Failed — paused, source intact"; a rollback note states exactly what rollback destroys (half-built target only) vs. never touches (HV source). Source is never auto-deleted.
- **Live + persisted per-step log** streamed over SSE and persisted, so refresh / next-day return shows the same history. The job survives closing the tab (resumable).

## Implementation notes

- Reuse existing tokens + the platform accents (`--hv`, `--pve`) from the dashboard spec. The migrate flow uses the HV→PVE badge pair in its header.
- **Stepper rail and DAG rail are the same spine, drawn twice** — the stepper's later nodes (Run/Validate/Cutover) are the DAG's executing steps. Make the Run transition feel like the rail coming alive, not a new screen.
- **SSE:** reuse `EventHub.cs`. Each step emits progress + log lines; the run view subscribes per job. On (re)open, hydrate from persisted job state first, then attach to the live stream.
- **Recovery buttons must be capability-driven**, not hardcoded per step name — render `Retry` / `Skip` / `Rollback` from per-step flags the engine exposes (same pattern as provider verbs). See open question below.
- Log console is a simple monospace panel (`.log` in the mockup) with severity colour classes (`.ok/.warn/.err/.info`) mapped to existing tokens. Keep it append-only + auto-scroll with a pause-on-scroll-up nicety if cheap.
- Idempotency/state (e.g. reserved VMID) is surfaced in the step detail so "retry is safe" is shown, not assumed.

## Out of scope (do NOT also change)

- Same-platform migration (HV↔HV, PVE↔PVE) — same UI applies later; not mocked.
- Multi-VM batch / queued migration UI (ROADMAP 2.4) — the per-job view is the building block; the batch list is a separate pass.
- Linux-guest path, Secure-Boot re-enrolment guidance, UEFI edge cases (2.4).
- Auth/audit attribution of who ran a migration (2.2) — show it later in the banner.
- Dashboard changes (separate spec, same date).

## Open questions

Drop in `requests/from-codebase/` if it blocks you:
1. **Does the job engine expose per-step `canRetry` / `canSkip` / `canRollback` flags**, or must the UI infer recovery affordances from step type? The control-room recovery row should be capability-driven. (Raised in the proposal too.)
2. Is there a **dry-run** mode the wizard should offer as a toggle before Run (ARCHITECTURE §4 mentions it)? If so, where in the stepper — a checkbox on the Run step?
3. Quiesce options: which are configurable (graceful shutdown vs. checkpoint vs. live)? The precheck "power state" warning and a Run-step option depend on the answer.
4. Granularity of progress for long steps (export/convert): byte-level % (drives a determinate bar) or coarse phase updates only (indeterminate spinner)? Mockup assumes a determinate bar is available.
