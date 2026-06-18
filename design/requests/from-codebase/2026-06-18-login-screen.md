# from-codebase request — Login + password-change screen

**Date:** 2026-06-18
**Raised by:** codebase (slice-2 login + RBAC build)
**Priority:** high — blocks the retiring of the interim token banner and enables the first real multi-user deploy

---

## What exists today

Slice (2) — login + RBAC (ENG-0008) — is now built on the backend. The app has:
- `POST /api/auth/login` → sets an HttpOnly Secure cookie, returns `{ role, mustChangePassword }`
- `GET /api/auth/me` → current user info
- `POST /api/auth/logout`
- `POST /api/auth/change-password`

The SPA has two **functional but unstyled** auth screens sitting on top of the existing app shell:
1. **Login wall** (`#auth-login`) — a centred card with username + password inputs and a "Sign in" button. Shown on load if `/api/auth/me` returns 401.
2. **Change-password wall** (`#auth-chpw`) — shown immediately after first login when `mustChangePassword: true`, before the main app is visible. 8-char minimum enforced client-side.

A **Logout** button has been added to the header action bar (hidden until authenticated).

---

## What we need from design

### 1. Login screen
A styled login wall. The screen is full-viewport (`position:fixed;inset:0;background:var(--bg)`) with a centred card. Current interim markup:

```html
<div class="auth-wall" id="auth-login">
  <div class="auth-box">
    <div class="auth-logo">…VMentory logo + wordmark…</div>
    <div class="auth-title">Sign in</div>
    <div class="auth-sub">…</div>
    <div class="auth-err" id="login-err">…error message…</div>
    <div class="fgrp"><label>Username</label><input id="login-user" …></div>
    <div class="fgrp"><label>Password</label><input id="login-pass" …></div>
    <button id="login-btn">Sign in</button>
    <div class="auth-foot" id="login-role-hint">…</div>
  </div>
</div>
```

Design needs:
- Visual treatment for the full-page wall and the centred card (blur backdrop vs solid, gradient, or flat dark)
- Logo + wordmark refinement (current icon is a monitor SVG)
- Input and button styling consistent with the rest of the app
- Error state (red inline message, input highlight)
- Loading state on the button ("Signing in…" + spinner)

### 2. Change-password screen
Same full-viewport wall, different card. Current interim markup:

```html
<div class="auth-wall" id="auth-chpw">
  <div class="auth-box">
    <div class="auth-logo">…lock icon + VMentory…</div>
    <div class="auth-title">Set a new password</div>
    <div class="auth-sub">…</div>
    <div class="auth-err" id="chpw-err">…</div>
    <div class="fgrp"><label>New password</label><input id="chpw-new" …></div>
    <div class="fgrp"><label>Confirm new password</label><input id="chpw-confirm" …></div>
    <button id="chpw-btn">Set password</button>
  </div>
</div>
```

Design needs:
- Same visual language as login screen (they share `.auth-wall` / `.auth-box` classes)
- Password strength indicator (optional but desired — at minimum show length feedback)
- Distinct heading/icon to make it clear this is a forced rotation, not regular settings

### 3. Logout button in the header
A **Logout** button (`#btn-logout`) is now in the header actions bar (right side). Shown only when authenticated. Current treatment: `btn btn-ghost btn-sm` with a log-out icon.

Design needs to confirm or propose alternative placement/styling — e.g. a user avatar/chip that opens a dropdown with "Change password" + "Logout" options.

---

## Constraints

- All markup already exists and is wired up — the JS functions `submitLogin()`, `submitChangePassword()`, `logout()`, `showLogin()`, `showChangePassword()`, `hideAuthWalls()` are live. **Design only needs to produce a styled version of these elements.**
- Do not change element IDs (they're referenced by JS).
- Use the existing CSS custom properties (`--bg`, `--bg-card`, `--border`, `--blue`, `--text`, etc.) for theme compatibility.
- The `finp` class (existing form input style) and `fgrp` / `flbl` classes are already defined — reuse or extend them.
- Both screens must work in dark mode (default) and light mode (`[data-theme="light"]`).

---

## Out of scope for this request

- Role-indicator UI in the header (e.g. showing the logged-in user's name/role) — a follow-up request
- "Change password" from within the settings (not forced rotation) — follow-up
- Forgot password / account management screens — not in release 1 (local accounts, single operator)
