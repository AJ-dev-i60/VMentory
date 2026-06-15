# Migration wizard (Hyper-V → Proxmox) — two directions

**Mockups:**
- `mockups/2026-06-15-migration-wizard-stepper.html` — Direction A: linear stepper (setup phase)
- `mockups/2026-06-15-migration-run-controlroom.html` — Direction B: control room / job DAG (run phase)

## The problem

The headline Phase 2 flow (ROADMAP 2.3): **precheck → map resources → run → watch live progress → validate → cutover.** ARCHITECTURE §4 frames a migration as a *persisted, resumable DAG of steps* orchestrating proven tools (virt-v2v, `qm importdisk`, PVE API) over SSE. The design must serve a **long-running, failure-prone, resumable** journey — minutes-to-hours, with partial failure and rollback as normal states, not edge cases. The happy-path linear wizard alone is a trap.

These two directions are **not competing alternatives** — they're the two *phases* of the same flow, and the key design question is where the seam between them sits.

## Direction A — linear stepper (the setup phase)

A classic numbered stepper rail (Precheck · Map resources · Run · Validate · Cutover) with each step as a card. Mocked at **Step 2 (Map resources)** because that's the hardest screen:

- **Precheck** renders as a pass/warn/fail checklist (reusing the `.health` / `.chk-ico` colour semantics) — capability match, target free space, guest-OS support, power state. Warnings (e.g. "VM is Running") inform but don't block.
- **Map resources** is three small source→target tables: Compute & firmware, Storage (per-disk: target storage / format / bus), Network (bridge / model / MAC preserve). Each mapping row shows the **HV source value**, an arrow, an **editable PVE target**, and a flag (`direct` / `auto-mapped` / `warn`). This is where Gen2→OVMF, virtio-scsi bus choice, and Secure-Boot caveats surface — the platform differences the unification can't paper over.

**Wins:** familiar, low-anxiety, great for *configuring* the migration. The mapping table is the honest place to expose firmware/disk/NIC translation.

**Loses:** a linear stepper implies "click Next to finish." It badly models the *run* — a 9-minute disk export, a step that fails on attempt 2 of 3, a job you close the tab on and resume tomorrow. Steppers hide history and have no vocabulary for partial failure / rollback.

## Direction B — control room / job DAG (the run phase)

Once the operator hits **Run**, the UI becomes a **persisted job view**: a left-rail DAG of steps (done ✓ / running / failed ✕ / pending / skipped) and a right pane detailing the selected/failed step. Mocked at a **realistic partial failure** — provisioning fails on a storage/EFI-disk content-type mismatch:

- **Partial failure is first-class.** Completed steps (export, convert) are preserved; the job pauses at the failed step. Per-step recovery actions: **edit input & retry · retry as-is · skip · roll back & abort**. Idempotency + a reserved VMID make retry safe.
- **Rollback safety is always on screen.** "Failed — paused, source intact" in the banner; an explicit note spells out what rollback destroys (only the half-built target) and what it never touches (the HV source). Directly serves ARCHITECTURE §4's "leave rollback intact / don't auto-delete source."
- **Live + persisted logs** per step over SSE (reusing `EventHub.cs`), so a browser refresh or a next-day return shows the same history. This is what "resumable" *looks like*.

**Wins:** honestly models a long-running, resumable, failure-prone job. Reuses the SSE infra. Gives operators real recovery verbs instead of a dead "Failed" toast.

**Loses:** heavier than a wizard; overkill for the *setup* phase where the user is just filling in a form.

## My recommendation

**Use both, with a clean seam at "Run."** A is the **setup** UX (steps 1–2: precheck + map), B is the **execution** UX (steps 3–8: run · watch · validate · cutover). The stepper rail in A and the DAG rail in B are the *same spine* drawn two ways — the stepper's later nodes (Run/Validate/Cutover) are exactly the DAG's executing steps, so the transition at "Run" should feel like the rail coming alive, not a context switch.

Concretely: keep the numbered stepper for Precheck + Map (where a form is right), and the moment the job is submitted, hand off to the control-room view (where a persisted DAG is right). Validate and Cutover are *steps in the DAG* with their own gates (cutover stays gated on a green validation) rather than separate wizard pages — this keeps "the job" as one persisted object the operator can leave and return to.

Batch/queued migrations (ROADMAP 2.4) then become a list of these job objects — the control-room view is already the per-job drill-down.

## Open question for the codebase side

Logged separately in `requests/from-codebase/` is moot; I'm raising one for the codebase side in `requests/from-design/` notes: **does the job engine expose per-step `canRetry` / `canSkip` / `canRollback` capability flags, or does the UI infer recovery affordances from step type?** The control-room recovery buttons should be capability-driven (same pattern as provider verbs), not hardcoded per step name. See the request file.

## Out of scope

- Same-platform migration (HV↔HV, PVE↔PVE) — a simpler subgraph (ARCHITECTURE §4); same UI applies.
- Linux-guest path and Secure-Boot re-enrolment UX (ROADMAP 2.4 edge cases).
- Multi-VM batch orchestration UI (ROADMAP 2.4) — out of this pass; the per-job view is the building block.
- Auth/audit surfacing of who ran the migration (ROADMAP 2.2).
