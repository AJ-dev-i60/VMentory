# ENG-0002 — Secret handling: store design & root-key model

**Status:** Decided
**Raised:** 2026-06-15 by engineering session (spun out of ENG-0001 sub-question on secret storage)
**Affects:** `docs/phase2/specs/persistence-and-security.md`, ARCHITECTURE locked decision #2,
the agent mTLS model (ENG-0001), every provider that holds credentials
**Related:** [ENG-0001](0001-hyperv-migration-transport.md) (agent mTLS eliminates the Windows
domain password), ARCHITECTURE decision #2 (hybrid persistence — secrets never plaintext at rest)

## Context

Locked decision #2 says secrets live in a vault/secret store, **never plaintext at rest**. VMentory
Core is .NET 8 in a Linux container, **single-operator**, self-hosted on-prem (Proxmox + Hyper-V
shops, possibly air-gapped), persisting to SQLite/EF Core. The `migrate-vm` skill currently uses
plaintext `/root/.winrmcreds` and `/root/.smbcreds` files — the property we must not regress.

**Secret inventory (after ENG-0001):**

| Secret | Used for | Form |
|---|---|---|
| ~~Windows domain password~~ | — | **eliminated** by ENG-0001 — agent mTLS client cert instead |
| Proxmox API token | REST control (`:8006/api2/json`) | token, scoped to a role, revocable |
| Proxmox SSH key | `qm importdisk` / disk conversion on the node | dedicated key, ideally forced-command |
| SMB/CIFS creds | VHDX share mount during migration | stored cred (eliminate if agent pushes disks) |
| mTLS client cert/key | VMentory → agent auth | scoped, revocable credential |
| Future API keys | additional providers | tokens |

**Framing insight:** every secret store bottoms out at **one root key that unlocks the rest and
cannot itself live in the store.** So this splits into two independent questions — (1) storage
mechanism, (2) root-key source — decided separately below.

## The question

What is the secret-store mechanism for v1, where does its root key come from, and what credential
forms do we commit to storing?

## Options (summary)

**Mechanism:** (i) HashiCorp Vault/OpenBao — gold standard, but a second stateful service to run,
unseal, back up; (ii) cloud managed (Azure Key Vault/AWS SM) — managed & audited, but a cloud
dependency in an on-prem product; (iii) app-native envelope encryption — AES-256-GCM in SQLite,
DEK wrapped by KEK, zero new infra.

**Root key (KEK):** (a) operator passphrase at startup — strongest, but manual per restart;
(b) runtime-injected secret (Docker/Podman secret, systemd `LoadCredential`, env var) — unattended,
security = injection mechanism; (c) hardware-rooted (TPM/KMS) — strongest + unattended, more
plumbing.

## Decision

**2026-06-15 · Owner decision, engineering session.**

1. **`ISecretStore` provider abstraction** — mirrors `IVirtualizationProvider`. The single most
   important choice: call sites depend on the interface, never on the mechanism, so the store can
   evolve without code churn. Interface exposes **`get` / `set` / `rotate` / `delete`** (rotation is
   first-class, not bolted on) and emits an **audit event per access** to the history store.
2. **v1 default impl = app-native envelope encryption.** Secrets encrypted with **AES-256-GCM**
   (.NET `AesGcm` / libsodium) and stored in SQLite; a per-DB **Data Encryption Key (DEK)** encrypts
   values; a **Key Encryption Key (KEK)** wraps the DEK. Only the KEK comes from outside. No second
   service to run. **Vault/OpenBao and Azure Key Vault are optional providers behind the same
   interface, shipped later** — not v1.
3. **KEK source = runtime-injected (b)** by default (Docker/Podman secret, systemd `LoadCredential`,
   or env var) for unattended operation. **Operator-passphrase (a) is an opt-in mode** for
   high-security deployments. (c) hardware-rooting is a future upgrade, same interface.
4. **Binding design principle — prefer scoped keys/tokens over passwords everywhere.** The best
   secret is one we don't store; the second best is scoped and revocable without a human password
   reset. Concretely: Proxmox **API tokens** (not root password); dedicated **SSH keys**, ideally
   forced-command restricted (not SSH passwords); **agent mTLS cert** (not Windows domain password —
   already won in ENG-0001); SMB/CIFS cred only where unavoidable. This is binding, not advisory.

### Rationale
- Credible v1 with **no new infrastructure** and **no plaintext at rest** — directly satisfies
  locked decision #2 without making the single operator run and babysit Vault.
- The provider seam means choosing app-native now costs nothing later: a shop that already runs
  Vault or Azure KV plugs it in without touching providers or call sites.
- Envelope encryption is the same pattern KMS uses internally; built from mature, audited primitives
  (AES-GCM, libsodium), not hand-rolled crypto.
- The "tokens over passwords" principle shrinks both the blast radius and the rotation pain of every
  remaining secret.

### Consequences
- **Persistence schema:** encrypted secret blobs live in SQLite alongside **metadata** (scope/host,
  created/rotated/last-used timestamps) in the registry; the registry references secrets by id, never
  embeds values.
- **Bootstrap/ops doc needed:** how the operator supplies the KEK per deployment target
  (Docker secret vs systemd `LoadCredential` vs env var) and how to switch to passphrase mode.
- **Migration skill regression closed:** `/root/.winrmcreds` is gone (ENG-0001 mTLS);
  `/root/.smbcreds` is replaced by an `ISecretStore`-held SMB cred injected at job runtime — no
  plaintext cred files.
- **Docs to propagate (documentation agent):** rewrite the secret-store section of
  `persistence-and-security.md` around `ISecretStore` + envelope encryption + runtime-injected KEK;
  cite ENG-0002 next to ARCHITECTURE decision #2.
- **Deferred, same interface:** Vault/OpenBao + Azure Key Vault providers; TPM/KMS root-key option;
  automatic rotation scheduling (the `rotate` verb exists in v1; scheduling is later).

## Open sub-questions (non-blocking)
- KEK rotation / re-wrap procedure for the app-native store (rotate KEK without decrypting every
  secret to plaintext — re-wrap the DEK only).
- Whether mTLS client cert issuance/renewal for the agent is owned by `ISecretStore` or a small
  internal CA (ties into the parked "mTLS PKI ownership" question in PROGRESS.md §4).
