# ENG-0010 — Core deployment & runtime model (container, network bind, TLS, volumes, "install nothing")

**Status:** Decided (2026-06-16) — **containerized Core, configurable 0.0.0.0 bind, Core-terminated HTTPS (reverse-proxy seam documented), runtime KEK/TLS injection, SQLite on a mounted volume, session-token → login+RBAC**
**Raised:** 2026-06-16 by engineering session (operator re-baseline of the Phase-2 foundation)
**Decided:** 2026-06-16 by operator (owner)
**Affects:** `Program.cs` (bootstrap: loopback + random-port + session-token), `docs/phase2/ARCHITECTURE.md`
(§5 Security, Topology), `docs/phase2/ROADMAP.md` (2.0 "containerize Core"), `docs/phase2/specs/persistence-and-security.md`
**Related:** ENG-0002 (KEK injection at runtime), ENG-0008 (RBAC — the auth that replaces the session token), ENG-0009 (Proxmox transport — no inbound from nodes), ENG-0005 (agent PKI is a *separate* trust domain from dashboard TLS)

## Context

The operator's locked frame makes the runtime model an explicit, first-class requirement, not an
afterthought of "containerize Core" buried in ROADMAP 2.0:

- **Containerized app; the container hosts all the tools.** Core is a single Linux container image; the
  Proxmox CLI dependencies it needs are reached over SSH on the node (ENG-0009), so the *image* carries
  the .NET app + an SSH client, not `qemu`/`qm`.
- **Web-UI-first; user installs nothing locally.** The only install action is `docker run` / compose.
- This **contradicts the current bootstrap.** [Program.cs:43](../../../Program.cs#L43) binds
  `http://127.0.0.1:{random-port}`; auth is a single generated **session token**
  ([Program.cs:111-120](../../../Program.cs#L111)); `/api/quit` exists for a desktop process. That is a
  **loopback desktop app**, not a hosted service. It must change for a container nobody runs on localhost.

A topic to confirm: **is ENG-0010 (this record) warranted as its own decision?** Yes — the runtime
model touches network exposure, TLS, secret (KEK) injection, persistence volumes, and the auth model
swap. These are cross-cutting, security-sensitive, and currently mis-specified by the Phase-1 code;
they deserve a recorded decision rather than being implied by "containerize Core."

## The question

What is Core's deployment & runtime contract: bind address/port, TLS termination, secret/KEK injection,
persistence volume layout, and how the loopback/session-token bootstrap is replaced?

## Decision shape (sketch — to refine into options if the operator wants alternatives)

1. **Packaging:** single Linux container image (ASP.NET Core 8 — already cross-platform, containerizes
   cleanly per [ARCHITECTURE.md §1](../../phase2/ARCHITECTURE.md)). Ships the app + an SSH client; **no
   `qemu`/`qm` in the image** (those run on the PVE node, ENG-0009). Published via image tags (re-scope
   `Updater.cs` from GitHub-exe auto-update to image tags, per the ARCHITECTURE carry-over table).
2. **Network bind:** bind **`0.0.0.0:{configurable port}`** (env-driven, e.g. `VMENTORY_HTTP_ADDR`),
   replacing the hardcoded `127.0.0.1:{random}` in [Program.cs:43](../../../Program.cs#L43). Random-port
   discovery (`FindFreePort()`) is a desktop affordance and is dropped.
3. **TLS termination:** browser↔Core is **HTTPS** ([ARCHITECTURE.md §5](../../phase2/ARCHITECTURE.md)).
   Recommend **terminate at Core** by default (operator-supplied cert/key via mounted volume or env;
   self-signed fallback for first run) **with a documented reverse-proxy seam** (run behind nginx/Traefik
   that terminates TLS). This is a **distinct trust domain from the agent mTLS PKI** (ENG-0005 says so
   explicitly) — do not conflate the dashboard cert with the internal CA.
4. **Secret / KEK injection:** the envelope-encryption **KEK is injected at runtime** (ENG-0002) — Docker/
   Podman secret, `LoadCredential`, or env var; operator-passphrase opt-in. The container therefore
   **must not bake any secret into the image**. The DB holds only KEK-wrapped secrets (ENG-0002).
5. **Persistence volumes:** the SQLite DB lives on a **mounted volume** via the existing `VMENTORY_DB`
   env var (already wired, [Program.cs:32](../../../Program.cs#L32) → `ResolveDbPath`; CLAUDE.md gotcha 7).
   TLS cert material + any operator config mount alongside. Postgres remains the opt-in multi-instance path.
6. **Auth model swap:** the single session token is **replaced by login + RBAC** (ENG-0008). A minimal
   admin login lands first (re-baselined as an early slice — see ENG-0008 amendment); the session-token
   middleware and `/api/quit` desktop behavior are removed/replaced. `/api/quit` purging is already
   neutered (slice 3), but the *route* is a desktop affordance to retire.
7. **No inbound from PVE nodes:** Core initiates all Proxmox connections outbound (REST 8006 + SSH 22,
   ENG-0009); nodes never connect *in*. The only *inbound* control-plane listener is the agent gRPC/mTLS
   endpoint **if/when** Hyper-V agents exist (ENG-0009 knock-on: that listener is HV-only and not part of
   the first releases). So the first containerized releases expose **only the web UI port**.

## Open sub-questions

- **Default TLS posture for first run:** self-signed + warn, or refuse to start without a cert? (UX vs
  secure-by-default.) Recommend self-signed-with-warning + easy cert mount.
- **Single image vs split (web/worker):** one process for now (SSE + ops engine in-proc, matching Phase-1
  `EventHub`); revisit only if the ops engine needs isolation. Recommend single image for release 1.
- **Reverse-proxy vs Core-terminated TLS as the *documented default*** — affects what the quickstart shows.
- Interaction with **ENG-0009 SSH egress** — the image needs an SSH client + known-hosts/pinning strategy
  for PVE nodes (ties to the open PVE-node TLS/trust question in proxmox-integration.md §5).

## Decision

**2026-06-16 · Operator (owner) approved the containerized runtime model, with the TLS posture chosen
explicitly.** The deployment & runtime contract for release 1 is:

1. **Packaging:** single Linux container image (ASP.NET Core 8 app + SSH client; **no `qemu`/`qm`** in
   the image — those run on the PVE node, ENG-0009).
2. **Network bind:** bind **`0.0.0.0:{configurable port}`** (env-driven); the hardcoded
   `127.0.0.1:{random}` loopback bootstrap and `FindFreePort()` are dropped.
3. **TLS posture (operator's explicit choice):** **Core-terminated HTTPS — Kestrel serves HTTPS
   directly with a self-signed or operator-provided cert.** This is the **decided default for release 1.**
   A clean **reverse-proxy seam** (run behind nginx/Traefik that terminates TLS) is a **documented future
   seam, NOT required for release 1.** This dashboard TLS is a **distinct trust domain** from the agent
   mTLS PKI (ENG-0005).
4. **Secret / KEK injection:** the envelope-encryption **KEK is injected at runtime** (ENG-0002) via
   container secret / `LoadCredential` / env / operator-passphrase; the image bakes in **no secret**. The
   dashboard TLS cert/key are likewise **injected at runtime** (mounted volume or env), not baked in.
5. **Persistence volumes:** SQLite DB on a **mounted volume** via `VMENTORY_DB`; TLS material + operator
   config mount alongside. Postgres remains the opt-in multi-instance path.
6. **Auth model swap:** the single session token + `/api/quit` desktop bootstrap are **replaced by
   login + RBAC** (ENG-0008). Minimal admin login lands **in this deployment slice** (the first
   re-baselined foundation slice that exposes a hosted UI).
7. **No inbound from PVE nodes:** Core initiates all Proxmox connections outbound (REST 8006 + SSH 22,
   ENG-0009). The first containerized releases expose **only the web UI port**; the agent gRPC/mTLS
   listener is HV-only and arrives later with the migration slice.

### Open-sub-question resolutions
- **First-run TLS posture:** **self-signed-with-warning + easy cert mount** (not refuse-to-start) — the
  pragmatic secure-by-default for a single operator standing up the container.
- **Documented default = Core-terminated TLS** (per the operator choice above); reverse-proxy is the
  documented alternative, not the quickstart default.
- **Single image vs split web/worker:** single image (SSE + ops engine in-proc) for release 1.

These resolutions feed the documentation agent (Program.cs bootstrap, ARCHITECTURE §5/Topology, ROADMAP
2.0, persistence-and-security spec). The image's SSH-client known-hosts/pinning strategy for PVE nodes
remains a spec detail tied to ENG-0009 + proxmox-integration.md §5.
