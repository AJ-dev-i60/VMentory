# ENG-0013 — Hyper-V monitoring transport from the Linux Core (SSH+PowerShell vs agent vs native WinRM)

**Status:** Decided (2026-08-03) — **A: SSH + PowerShell remoting; one shared `ISshExecutor` seam for both platforms**
**Raised:** 2026-08-03 by engineering session (operator re-focus: monitoring-first for both platforms)
**Decided:** 2026-08-03 by operator (owner)
**Affects:** `HyperVProvider.cs`, `Scanner.cs`, `Reachability.cs`, `Poller.cs`, `Dockerfile`,
`docs/phase2/ROADMAP.md` (slice ordering), `docs/phase2/ARCHITECTURE.md` (Topology), ENG-0007
(pillar weighting), ENG-0001/0003/0004/0005 (agent scope), ENG-0012 (a 2nd per-host SSH secret)
**Related:** ENG-0009 (the same question, answered for Proxmox), ENG-0011/0011a (the trigger), ENG-0012 (credential model)

## Context

The operator has re-focused VMentory's primary purpose: **a monitoring tool for Hyper-V *and*
Proxmox hosts**, ahead of management, Deploy and Migrate. That exposes a blocker that ENG-0011 had
already flagged as a symptom without naming the cause:

**Hyper-V is not reachable from the containerized Core at all — not degraded, not partly working.
Zero.**

Grounded in the current tree:

- `Reachability.cs:169-171` — every remote call funnels through `RunPowerShellAsync`, which does
  `new ProcessStartInfo("powershell.exe", "-NonInteractive -NoProfile -ExecutionPolicy Bypass -File …")`.
  `powershell.exe` does not exist in the Linux image.
- `Reachability.cs:20` — the ICMP check shells out to `ping.exe` (`-n`/`-w` are Windows flags).
- `Reachability.cs:52,80-82` — the Core ensures its **own local** WinRM service is running and mutates
  `WSMan:\localhost\Client\TrustedHosts`. Both are Windows-host-machine operations, meaningless in the
  container.
- `Reachability.cs:113` and `Scanner.cs:216` — the actual inventory hop is
  `Invoke-Command -ComputerName '…' -Credential $cred -Authentication Negotiate`, i.e. **the Core is
  assumed to be a domain-capable Windows box** acting as the WinRM *client*.
- `HyperVProvider.cs` delegates `QuickConnectAsync`/`ScanAsync` straight to `Scanner`, so the provider
  seam inherits all of the above.

The only piece that survives on Linux is `Program.cs:517`'s `TestTcpPortAsync(addr, cfg.WinRmPort)` —
a bare TCP probe. So a live, healthy Hyper-V host presents to the container as: port open, every scan
failed, no inventory. That is exactly the "every HV host reports unreachable with no *why*" that
raised ENG-0011 on 2026-06-17.

Phase-1's Windows single-exe still works because it *was* the WinRM client. The containerization in
re-baselined slice (1) (ENG-0010) silently stranded the Hyper-V provider; the slice-(1) bootstrap even
`IsWindows()`-guards the WinRM ensure, which stops the crash without restoring the capability.

## The question

How does a **Linux container** obtain inventory and health from a **Windows Hyper-V host**, cheaply
enough to be justified on a platform the estate is deliberately migrating *off* (ENG-0007)?

## Options

### A — SSH + PowerShell over SSH

Enable the in-box **OpenSSH Server** on the Hyper-V host (`Add-WindowsCapability -Online -Name
OpenSSH.Server~~~~0.0.1.0`), authorise a VMentory public key, and have the Core execute the existing
inventory scripts **on the host** over SSH, reading JSON from stdout.

- OpenSSH Server is an **on-demand Windows feature** on Server 2019+ / Win10 1809+ — a capability
  toggle, not a third-party install. It does not violate ENG-0003's "no remote push install"; it is
  the same class of prerequisite as enabling WinRM was.
- The container **already ships an SSH client** — the ENG-0010 Dockerfile installs `openssh-client`
  for the Proxmox path.
- Key-based auth means **no Windows domain password at rest**, honouring ENG-0002's explicit bind to
  "prefer scoped keys/tokens over passwords." This was previously listed as a benefit only the
  agent's mTLS identity could deliver (ENG-0001); SSH keys deliver it too, for monitoring.
- **Identical transport idiom to Proxmox** (ENG-0009's constrained SSH key), so both platforms are
  served by **one** `ISshExecutor` seam instead of two unrelated transports.
- The existing PowerShell payloads survive nearly verbatim: `Scanner.cs`'s scripts already emit JSON
  to stdout (`Scanner.cs:251,265`). What changes is **delivery** — they stop being wrapped in a
  local→remote `Invoke-Command` hop (`Scanner.cs:216`) and simply run where they already intended to.
- Cost: a per-host prerequisite (feature on + pubkey), and Windows admin rights are still required for
  `Get-VM`/WMI.

### B — Pull the Hyper-V agent forward (ENG-0001/0003/0004/0005)

Build the designed NativeAOT + gRPC/mTLS agent and the private CA now, and monitor through it.

- Fully designed already, and it is the eventual migration source, so the work is not wasted.
- But it is **the single largest build in the plan**, it front-loads the private-CA/PKI foundation that
  ENG-0009 deliberately demoted off the critical path, and it spends that budget on a **read-only
  monitoring** need for a platform the estate is retiring. Directly contradicts the operator's
  "monitor it well while it declines" framing.

### C — Native WinRM / WS-Man client in .NET

Drop `powershell.exe` and speak WS-Man SOAP to port 5985/5986 directly from managed code.

- Preserves the current semantics most exactly and needs nothing new on the Windows host.
- But NTLM/Negotiate from Linux requires GSSAPI/Kerberos plumbing, there is no first-party .NET WS-Man
  client, and we would own bespoke protocol code indefinitely. This is the well-known painful path,
  and it buys nothing over A except avoiding the OpenSSH prerequisite.

## Recommendation

**A.** It is the cheapest adequate option, it consolidates rather than adds a transport, and it is the
same reasoning ENG-0009 applied to Proxmox: a host that already exposes a first-class remote-execution
surface does not need an agent built for it. Hyper-V, once OpenSSH is enabled, stops being the "black
box that needs an agent to become reachable" that ENG-0009 assumed it was.

## Decision

> Decided 2026-08-03 (owner). Hyper-V monitoring from the Linux Core is delivered over **SSH with
> PowerShell executed on the host** (option A). A single **`ISshExecutor`** seam in `VMentory.Core`
> serves **both** platforms — Hyper-V inventory/health commands and the Proxmox on-node residue
> (ENG-0009) — and is built **once, for monitoring**, rather than being introduced later by the
> Proxmox write-verb slice. Authentication is by **key**, stored as a per-host secret; no Windows
> domain password is persisted. The Hyper-V agent + private CA (ENG-0001/0003/0004/0005) is **removed
> from the monitoring path entirely** and narrows to the migration source only.

### Consequences (in effect)

1. **New `ISshExecutor` seam in `VMentory.Core`**, with the Proxmox and Hyper-V providers as its first
   two consumers. This was scheduled as "Proxmox SSH executor" in re-baselined slice (5); it is now
   pulled forward and generalised.
2. **`Scanner.cs` loses its remote wrapper.** `BuildRemoteWrapper` / `Invoke-Command -ComputerName`
   (`Scanner.cs:216`) goes away; the inventory and quick-info scripts become payloads executed on the
   target. Their WMI/`Get-VM` bodies are unchanged.
3. **`Reachability.cs` loses its Windows-host assumptions.** `RunPowerShellAsync`
   (`Reachability.cs:158-200`), the local WinRM-service ensure (`:52`) and the `TrustedHosts`
   mutation (`:80-82`) are all deleted rather than ported — they exist only to make a *Windows Core*
   act as a WinRM client, a role the Core no longer plays.
4. **ICMP becomes genuinely optional.** `ping.exe` (`Reachability.cs:20`) is Windows-only, and the
   container runs non-root (uid 10001, ENG-0010) so raw ICMP sockets are unavailable anyway. This is
   consistent with **ENG-0011a**, which already demoted ICMP to informational-only and never folds it
   into the health tier: a host with no ICMP result simply renders the neutral grey **P** badge.
5. **Hyper-V gains an SSH key as a per-host secret** — the same shape as the Proxmox SSH key. This
   *doubles down* on ENG-0012's sequencing argument: the named/reusable credential model must land
   **before** this slice, not after, or two platforms simultaneously grow untyped second secrets.
6. **The failure taxonomy gains real HV coverage.** ENG-0011a's per-stage codes were defined largely
   against the Proxmox path; SSH gives Hyper-V honest equivalents at last —
   `MGMT_PORT_CLOSED` (22 shut), `AUTH_REJECTED` (key refused), `AUTH_INSUFFICIENT_PRIV` (SSH fine,
   `Get-VM` denied), `SCAN_FAILED` (script threw). "Unreachable with no why" is answerable.
7. **ENG-0001/0003/0004/0005 narrow again.** After ENG-0009 removed the agent from Proxmox, this
   removes it from Hyper-V *monitoring*. Its remaining justification is the migration step-graph and
   guest control (ENG-0001's 2.3 gate) — and only if migration is still wanted. The private CA is no
   longer on any near-term path.
8. **Onboarding gains a documented prerequisite:** OpenSSH Server capability enabled + VMentory pubkey
   in `administrators_authorized_keys`, with the correct restrictive ACL on that file. Needs a runbook
   alongside the existing Proxmox token onboarding.

### Open sub-questions (build-time)

- **Transport flavour:** plain `ssh host "powershell.exe -Command …"` exec vs PowerShell 7 **PSRP over
  SSH** (`New-PSSession -HostName -KeyFilePath`). Plain exec works against the **in-box Windows
  PowerShell 5.1** and needs nothing further installed; PSRP needs pwsh 7 deployed on every HV host and
  registered as an sshd subsystem. **Start with plain exec**; revisit only if object fidelity or
  session reuse demands it.
- **SSH user model:** dedicated local account vs domain account vs the existing admin, and whether the
  key can be constrained (Windows OpenSSH has no `forced-command` equivalent as clean as Linux's).
  Parallel to the unresolved slice-(5) "root vs per-node" question ENG-0012 left open for Proxmox —
  resolve both together.
- **Verification gate:** none of this is proven against a live Hyper-V host yet. Slice acceptance
  requires an end-to-end read from a real HV host over SSH, the way vega14 proved the Proxmox path.
