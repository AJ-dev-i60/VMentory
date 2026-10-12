# ENG-0017 — Windows-domain sign-in for the console

**Status:** Decided + BUILT (2026-10-12) · **Owner:** human
**Relates to:** ENG-0008 (RBAC / scoped console auth), ENG-0014 (OIDC sign-in), ENG-0010 (deployment runtime)

## Trigger

Owner decision, 2026-10-12: VMentory stops being a one-person tool on a personal domain and becomes
a work tool for the iSixty estate — *"I want to make it available to Grant and team, but currently it
only uses pocketid. I want to deploy it to the i60 domain so it's a work thing and no longer
edgestudios. And then I want it to be able to authenticate with windows domain."*

Three asks, of which this document covers the third. The deployment move is operational work, not an
architecture decision, and is recorded in the Outline page rather than here.

The reason the third ask is not simply "add another OIDC issuer": **there is no identity provider on
the i60 estate.** Pocket-ID is EdgeStudios' and holds personal accounts; the only identity system at
i60 is Active Directory (`i60.local`). Grant and the team have domain accounts and nothing else.

## Decision

**A third sign-in door: username and password validated by an LDAPS simple bind against the domain's
own DCs, with the console role read from AD group membership. It sits alongside the local password
path and OIDC, and none of them know about each other.**

1. **The bind *is* the password check.** VMentory never sees a verifiable copy of a domain password:
   the string goes straight into a simple bind and is not stored, hashed or cached. There is no
   service-account bind-then-compare, so a compromised VMentory cannot replay anyone's password.
2. **A directory account's role comes from AD on *every* sign-in.** ⚠️ This is the **deliberate
   opposite** of ENG-0014 rule 5, which keeps a hand-edited role. Here the directory is the
   authority: removing someone from the admin group in AD has to demote them at their next sign-in,
   or the group mapping is decoration. Hand-editing the role of a directory account in VMentory is
   the wrong place to do it, and the next sign-in overwrites it.
3. **Group → role mapping is configuration, and membership is evaluated transitively.** One LDAP
   search per sign-in using the AD extensible matching rule `1.2.840.113556.1.4.1941`
   (`LDAP_MATCHING_RULE_IN_CHAIN`) answers "which of these groups contain this user, directly or
   through nesting". Precedence when several match is fixed and documented:
   **Admin > VmOperator > BackupOperator > Viewer.**
4. **No group match means no sign-in, unless a default role is explicitly configured.** A directory
   account existing is not an authorization decision. `VMENTORY_AD_DEFAULT_ROLE` exists for the
   deployment that genuinely wants "anyone in the company can look", and is unset by default.
5. **LDAPS only, with the domain's issuing CA pinned.** Cleartext LDAP is refused unless
   `VMENTORY_AD_ALLOW_CLEARTEXT=1` is set, and then warns on every start. The CA is supplied as a
   file or as base64 in the environment, and server certificates are validated against *that* trust
   store rather than the system one — which is how a private enterprise CA is trusted without
   installing anything into the image.
6. **Directory users are real `AppUserEntity` rows** with `PasswordHash = ""`, auto-provisioned and
   keyed by UPN. Exactly the ENG-0014 construction: `PasswordHasher.Verify` requires
   `iterations:salt:hash` and returns false for anything else, so a directory account is unreachable
   through `/api/auth/login` **by construction**, not by a flag someone can flip back.
7. **The principal is built by the same `BuildClaims` the other two doors use**, plus
   `auth_mode=directory`. `RbacCatalog` reads `ClaimTypes.Role`; a differently-shaped principal fails
   every authorization check closed, which presents as a broken application rather than a broken
   login.
8. **Directory accounts never enter forced password rotation.** They have no password here to rotate.

## ⭐ An unreachable directory is not a wrong password

This is the single most valuable line in the feature. A dead DC, a TLS failure, a timeout and a
refused certificate all produce `DirectoryUnavailable` → **HTTP 503** with a message that says so.
Only LDAP result 49 produces `BadCredentials` → 401.

Folding the two together is the obvious implementation and it is a trap: it sends an operator
hunting a password problem that does not exist, during an outage, when they are least able to afford
it. The outcomes are also distinguished in the audit trail, so the trail can answer "could anyone
sign in at 14:00?" afterwards.

Related: on result 49 the authenticator stops rather than trying the next DC. Replaying a known-wrong
password at every controller only multiplies the hit on the account's lockout counter.

## Why the LDAP wire format is hand-written

ENG-0014 argued *against* hand-rolling a protocol, and took a NuGet dependency for OIDC on exactly
that reasoning. This decision goes the other way, and the distinction is worth stating because it
looks like an inconsistency.

**`System.DirectoryServices.Protocols` was the obvious choice and was rejected.** On Linux it is a
P/Invoke shim over OpenLDAP: it needs `libldap` added to the runtime image, and — the deciding point
— it hands the TLS trust decision to the native library, where pinning a private enterprise CA and
checking the server name are awkward and version-dependent. A `Novell.Directory.Ldap` style managed
port avoids the native dependency but is a third-party dependency for a security path.

**What this feature actually needs of LDAP is small and completely specified:** a simple bind, two
searches, and the result code. The security does not rest on the encoding — it rests on TLS, which is
an ordinary .NET `SslStream` here, with `RemoteCertificateValidationCallback` and
`X509ChainTrustMode.CustomRootTrust` doing the pinning in managed code that is easy to read and was
tested directly. Unlike OIDC there is no cryptographic token to validate, so the realistic failure
mode of a parsing bug is *a sign-in that does not work*, not *a sign-in that wrongly works*.

Two consequences follow from building the filters as BER rather than rendering them to the RFC 4515
string form:

- **Filter injection cannot happen, and there is deliberately no escaping function.** A filter's
  structure lives in its BER tags; an assertion value is a raw octet string the server never
  re-parses. A username of `*)(objectClass=*` is matched as that literal string. (Tested.)
- ⚠️ **Applying RFC 4515 escaping here would be the actual bug.** `\` and `(` are ordinary characters
  inside a distinguished name, so escaping a user's DN before the group-membership query turns a
  correct query into one that silently matches nothing — i.e. everyone loses their role. This was
  written, caught and removed during the build; do not add it back.

## ⚠️ The empty-password bypass

**An LDAP simple bind with a DN and an empty password is an *unauthenticated* bind** (RFC 4513
§5.1.2) **and the directory answers success.** Left unguarded, a blank password authenticates anyone
whose username exists — a complete authentication bypass, and the most likely way to get this feature
catastrophically wrong.

It is guarded twice: `DirectoryAuthenticator` refuses before connecting, and `LdapClient.BindSimpleAsync`
throws rather than trusting its caller. `ops/authtest-directory.mjs` asserts it for `""`, `" "` and
`"\t"`, and asserts no session cookie is issued.

## Configuration

| Variable | Meaning |
|---|---|
| `VMENTORY_AD_HOSTS` | Comma/semicolon list of `host[:port][\|certificateName]`, tried in order. **Required.** |
| `VMENTORY_AD_DOMAIN` | AD DNS domain, e.g. `i60.local`. Completes a bare username into a UPN. **Required.** |
| `VMENTORY_AD_CA_PEM` / `VMENTORY_AD_CA_B64` | The domain's issuing CA, as a file path or base64 PEM. |
| `VMENTORY_AD_GROUP_ADMIN` / `_VMOPERATOR` / `_BACKUPOPERATOR` / `_VIEWER` | `sAMAccountName` of the group granting each role. |
| `VMENTORY_AD_DEFAULT_ROLE` | Role for an account in none of those groups. Unset = refuse. |
| `VMENTORY_AD_NAME` | Label on the sign-in form. Defaults to `{domain} domain`. |
| `VMENTORY_AD_BASE_DN` | Search base. Read from the rootDSE when unset. |
| `VMENTORY_AD_TRANSPORT` | `ldaps` (default) or `plain`. StartTLS is **not implemented**. |
| `VMENTORY_AD_ALLOW_CLEARTEXT` | `1` to accept `plain`, which puts domain passwords on the wire. |
| `VMENTORY_AD_TLS_INSECURE` | `1` skips certificate validation. Development only; warns on every start. |
| `VMENTORY_AD_TIMEOUT_SECONDS` | Per-server, 1–120, default 10. |

At least one of `VMENTORY_AD_HOSTS` / `VMENTORY_AD_DOMAIN` present turns the feature on; with
**neither** set it does not register and reports nothing, exactly like OIDC. Any configuration error
**disables the door and prints why at startup in red** — a config mistake must not wait to be
discovered by the first person who tries to use it.

⚠️ **`certificateName` is not optional in practice.** The DCs are reached by address while their
LDAPS certificates are issued to their hostnames (`CN=DC1.i60.local` answering on `172.0.0.10`), and
connecting by name instead is not available: `systemd-resolved` refuses `*.local` over unicast DNS,
so the name may not resolve from a Linux container at all. Write the entry as
`172.0.0.10|DC1.i60.local`. An IP with no name is reported as a config error rather than allowed to
fail later in the handshake.

## Verified live against i60.local, 2026-10-12

The domain presented more than was expected: **`i60.local` already runs an enterprise CA**
(`i60-DC1-CA`, self-signed, valid to 2029-10-03, published in AD under
`CN=Certification Authorities,CN=Public Key Services,CN=Services,CN=Configuration`), and both DCs
carry auto-enrolled LDAPS certificates (`CN=DC1.i60.local` / `CN=DC2.i60.local`, valid to
2027-07-12). So LDAPS needed no PKI work at all — only the CA in the container. Domain and forest
functional level are 7 (Windows Server 2016).

Exercised end to end against the real DCs, not a simulator:

| | Result |
|---|---|
| Valid credentials, CA pinned | Success, 271 ms cold / 31 ms warm |
| `user`, `DOMAIN\user`, `user@domain` | all three accepted, normalised to the UPN |
| Group → role | `Domain Admins` → Admin, read transitively |
| In no role group, no default | `NoRole` → 403, sign-in refused |
| Wrong password | `BadCredentials` → 401 (LDAP 49) |
| Empty password | refused before binding, no cookie |
| Username with filter metacharacters | treated as a literal, 401 |
| CA not configured | TLS refused → `DirectoryUnavailable`, **not** trusted silently |
| Wrong expected certificate name | refused |
| Dead DC first, live DC second | failed over and signed in |
| All DCs dead | `DirectoryUnavailable` → 503, never `BadCredentials` |
| Cleartext without the opt-in | config error, door disabled |
| Stored role hand-edited to Viewer | next sign-in restored Admin from AD |
| Directory account on `/api/auth/login` | rejected |

`ops/authtest-directory.mjs` re-runs the HTTP-level half of this against any instance (23 checks).
The login screen was checked in headless Chrome (15 checks): the domain form renders as the primary
action, the console-account button is demoted to secondary, and a failed sign-in shows the domain's
own message.

## What is NOT in this decision

- **Kerberos / SPNEGO (Integrated Windows Authentication).** The nicer experience for a domain-joined
  Windows client — no password typed at all — and a reasonable follow-on. Deliberately not now: it
  needs a service account, an SPN, a keytab in the container and a GPO to put the hostname in the
  browsers' Local Intranet zone, and it cannot stand alone because the owner's own workstation is
  Linux and not domain-joined. ⚠️ Note for whoever does it: **Kerberos survives a reverse proxy and
  NTLM does not** — NTLM is connection-oriented, and Traefik fronts this app.
- **An IdP in front of AD** (Keycloak/Authentik federating LDAP, reusing the ENG-0014 OIDC seam).
  Correct answer the moment a second i60 app needs sign-in; overkill for one, and a new stateful
  service to run and back up.
- **Writing to the directory.** VMentory reads AD and never modifies it. Group membership is managed
  in AD by whoever manages AD.
- **The legacy `iSixty.co.za` forest** (`172.0.0.101` / `.100`), which has no trust with `i60.local`.
  Accounts there cannot sign in and are out of scope.

## Files

`VMentory.Core/Auth/Ldap/Ber.cs` (BER codec) · `VMentory.Core/Auth/Ldap/LdapClient.cs` (bind, search,
`LdapFilter`) · `VMentory.Core/Auth/DirectoryOptions.cs` (env contract + validation) ·
`VMentory.Core/Auth/DirectoryAuthenticator.cs` (the flow, role resolution, TLS trust) ·
`Program.cs` (registration, `/api/auth/directory/login`, `/api/auth/config`, audit, startup banner) ·
`wwwroot/index.html` (the second form) · `ops/authtest-directory.mjs`.

No schema change and no EF migration: `AppUserEntity` already carries everything needed.
