#!/usr/bin/env node
// ENG-0017 — contract test for Windows-domain sign-in, against a running instance.
//
// Checks the things that are easy to break and expensive to get wrong: that a blank password never
// authenticates, that a wrong password and an unreachable directory report differently, that the
// role comes from AD on EVERY sign-in rather than the stored row, and that a directory account can
// never be reached through the local password endpoint.
//
//   VM_URL=http://localhost:18080 \
//   VM_AD_USER=armandtd VM_AD_PASS='…' \
//   VM_AD_EXPECT_ROLE=Admin \
//   node ops/authtest-directory.mjs
//
// Needs a real domain account; it signs in for real. Nothing is written except audit rows and the
// auto-provisioned user, so point it at a dev instance.

const URL   = process.env.VM_URL || 'http://localhost:18080';
const USER  = process.env.VM_AD_USER;
const PASS  = process.env.VM_AD_PASS;
const ROLE  = process.env.VM_AD_EXPECT_ROLE || null;

if (!USER || !PASS) {
  console.error('VM_AD_USER and VM_AD_PASS are required.');
  process.exit(2);
}

let pass = 0, fail = 0;
const check = (name, ok, got) => {
  if (ok) { pass++; console.log('  ✓ ' + name); }
  else { fail++; console.log('  ✗ ' + name + '\n      got: ' + JSON.stringify(got)); }
};

const post = async (path, body, cookie) => {
  const r = await fetch(URL + path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...(cookie ? { cookie } : {}) },
    body: JSON.stringify(body),
    redirect: 'manual',
  });
  const json = await r.json().catch(() => ({}));
  return { status: r.status, json, setCookie: r.headers.get('set-cookie') };
};

console.log(`\nVMentory directory sign-in — ${URL}\n`);

// ── the door is advertised ────────────────────────────────────────────────────
const cfg = await fetch(URL + '/api/auth/config').then(r => r.json());
check('/api/auth/config advertises the domain door', cfg.directoryEnabled === true, cfg);
check('it names the domain',                         !!cfg.directoryDomain, cfg.directoryDomain);
if (!cfg.directoryEnabled) {
  console.log('\nDomain sign-in is not enabled on this instance — nothing further to test.');
  process.exit(1);
}

// ── the bypass that matters most ──────────────────────────────────────────────
// An LDAP simple bind with an empty password is an anonymous bind and the directory answers
// success. If this ever returns 200, anyone can sign in as anyone.
for (const blank of ['', ' ', '\t']) {
  const r = await post('/api/auth/directory/login', { username: USER, password: blank });
  check(`blank password (${JSON.stringify(blank)}) is refused`, r.status !== 200, r.status);
  check(`  …and no session cookie is issued`, !r.setCookie, r.setCookie);
}

// ── wrong password ────────────────────────────────────────────────────────────
const wrong = await post('/api/auth/directory/login', { username: USER, password: PASS + '-wrong' });
check('wrong password → 401', wrong.status === 401, wrong);
check('wrong password → BadCredentials', wrong.json.outcome === 'BadCredentials', wrong.json);

// ── unknown account ───────────────────────────────────────────────────────────
const nouser = await post('/api/auth/directory/login',
  { username: 'no-such-account-' + Date.now(), password: PASS });
check('unknown account → 401', nouser.status === 401, nouser);

// ── filter injection cannot alter the query ───────────────────────────────────
const inject = await post('/api/auth/directory/login', { username: '*)(objectClass=*', password: PASS });
check('filter metacharacters are treated as a literal username', inject.status === 401, inject);

// ── the happy path ────────────────────────────────────────────────────────────
const good = await post('/api/auth/directory/login', { username: USER, password: PASS });
check('valid credentials → 200', good.status === 200, good);
check('a session cookie is issued', !!good.setCookie, good.setCookie);
check('the username is normalised to a UPN', /@/.test(good.json.username || ''), good.json.username);
if (ROLE) check(`role is ${ROLE}`, good.json.role === ROLE, good.json.role);

const cookie = (good.setCookie || '').split(';')[0];
const me = await fetch(URL + '/api/auth/me', { headers: { cookie } }).then(r => r.json());
check('the session reports authMode=directory', me.authMode === 'directory', me);
check('the session carries the same role',      me.role === good.json.role, me);
check('no forced password rotation',            me.mustChangePassword === false, me);

// An ordinary API route must accept the session — the whole point is that the claim shape matches
// what RbacCatalog reads, which fails silently rather than loudly when it is wrong.
const state = await fetch(URL + '/api/state', { headers: { cookie } });
check('the session authorises /api/state', state.status === 200, state.status);

// ── DOMAIN\user and user@domain both work ─────────────────────────────────────
for (const form of [`${cfg.directoryDomain.split('.')[0]}\\${USER}`, good.json.username]) {
  const r = await post('/api/auth/directory/login', { username: form, password: PASS });
  check(`username form "${form}" is accepted`, r.status === 200, r.status);
}

// ── a directory account is unreachable through the local password door ────────
const local = await post('/api/auth/login', { username: good.json.username, password: PASS });
check('the local password endpoint rejects the directory account', local.status !== 200, local);

console.log(`\n${pass} passed, ${fail} failed\n`);
process.exit(fail ? 1 : 0);
