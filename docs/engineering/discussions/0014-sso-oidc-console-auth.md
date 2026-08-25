# ENG-0014 — SSO / OIDC sign-in for the console

**Status:** Decided (2026-08-25) · **Owner:** human
**Relates to:** ENG-0008 (RBAC / scoped console auth), ENG-0010 (deployment runtime)

## Trigger

Owner decision, 2026-08-25: consolidate every app on the EdgeStudios estate behind one identity
provider (Pocket-ID, `https://id.edgestudios.co.za`) and stop maintaining a separate account per
app. VMentory was the second app migrated, after Haven.

This is **not a new direction**. ENG-0008 was decided on 2026-06-16 as "local accounts now **and
OIDC seam later**". This lands the deferred half; nothing in ENG-0008 is superseded.

## Decision

**Generic OIDC authorization-code flow with PKCE, sitting alongside the existing cookie session —
not replacing it.**

1. **The issuer is configuration, never a hardcoded provider.** `VMENTORY_OIDC_ISSUER` points at any
   OP. Pocket-ID is what the dev instance happens to use. This matters because VMentory is aimed at
   client estates: a customer deployment must be able to point at the customer's own IdP, and must
   never be wired to the vendor's personal identity provider.
2. **The library does the protocol.** `Microsoft.AspNetCore.Authentication.OpenIdConnect` — the only
   NuGet dependency in the solution, everything else being BCL. Taken deliberately: discovery,
   PKCE, nonce, state and id-token validation are precisely the things a hand-rolled implementation
   gets subtly and silently wrong.
3. **OIDC signs in to the existing cookie scheme, and the provider's principal is discarded.**
   `OnTokenValidated` rebuilds the principal with the same three claims the password path issues
   (`ClaimTypes.Name`, `ClaimTypes.Role`, `must_change_password`) plus `auth_mode`. `RbacCatalog`
   reads `ClaimTypes.Role`; a differently-shaped principal would make every authorization check
   fail closed, which reads as a broken app rather than a broken login.
4. **SSO users are real `AppUserEntity` rows**, auto-provisioned on first sign-in, keyed by email.
   `PasswordHash` is set to the empty string — not a hash of anything. `PasswordHasher.Verify`
   requires `iterations:salt:hash` and returns false for anything else, so an SSO account is
   unreachable through `/api/auth/login` by construction rather than by a flag someone can flip.
5. **First sign-in takes `VMENTORY_OIDC_ROLE` (default `Admin`); later sign-ins take the stored
   role.** An operator who demotes an account by hand must not have that undone by the next login.
6. **SSO accounts are never put in forced password rotation.** They have no password to rotate, and
   the ENG-0008 chokepoint would pin them to `/api/auth/*` permanently.
7. **`VMENTORY_PASSWORD_LOGIN=0` closes the local door.** Default is open. This is the switch that
   makes a deployment SSO-only, and setting it back to `1` is the break-glass.

## Consequence that needed a deliberate call: SameSite

The session cookie was `SameSite=Strict`. Strict withholds the cookie on the first request after an
external redirect — which is exactly what an SSO callback is — so a Strict cookie can leave a user
looking signed-out at the moment they finish signing in.

**When OIDC is configured the cookie drops to `SameSite=Lax`.** The CSRF posture survives: Lax still
never sends the cookie on a cross-site `POST`/`PATCH`/`DELETE`, and every state-changing verb in the
API is one of those. Only top-level GET navigation carries the session, and a cross-origin page
cannot read those responses. With OIDC unconfigured the cookie stays Strict, so no existing
deployment changes behaviour.

## Alternatives rejected

* **Reverse-proxy / forward-auth gate (oauth2-proxy in front of the container).** Zero app changes,
  but it is all-or-nothing on the hostname: it would swallow `/health` (an unauthenticated liveness
  probe ENG-0010 relies on), break the SSE stream's auth model, and leave RBAC with no identity to
  map — VMentory's four roles would collapse to "past the gate or not".
* **Hand-rolled code flow, no NuGet.** Consistent with the BCL-only posture elsewhere, but see (2).
* **Replacing local accounts entirely.** Rejected: the local admin is the recovery path when the IdP
  is unreachable, and it is the only path that does not depend on a second system being healthy.

## Open

* **Group → role mapping is not implemented.** Pocket-ID advertises a `groups` claim and scope, but
  the instance has **zero groups defined** (verified 2026-08-25), so there is nothing to map and a
  mapping written now would be untested speculation. Role comes from env on first login. Revisit
  when groups exist or a deployment has more than one operator.
* **`GetClaimsFromUserInfoEndpoint = true`** is set for portability across OPs. If an OP returns the
  email in the id_token only, this is redundant but harmless.
