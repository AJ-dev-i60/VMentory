#!/usr/bin/env bash
# ENG-0017 — set the secret-bearing environment of the i60 VMentory app.
#
# Everything non-secret is already set. This covers the six values that carry credentials, which a
# permission classifier (correctly) would not let the agent write into a secret store. It holds no
# secrets itself: it reads them from your own dotfiles and from the live EdgeStudios instance, and
# generates the two new ones.
#
#   bash ops/seed-i60-secrets.sh
#
# Idempotent: re-running replaces the values rather than adding duplicates.
# After it finishes, trigger a deploy (the script prints the command).

set -euo pipefail

APP=nlojvlsvudikfiavax0eeuhn
I60=https://deploy.i60.co.za
ES=https://coolify.edgestudios.co.za
ES_APP=myoe1w37oii1lxkfjwrlzw9x

I60_TOKEN=$(cat ~/.coolify-i60-token)
ES_TOKEN=$(cat ~/.config/coolify/edgestudios.token)
TMP=$(mktemp -d); trap 'rm -rf "$TMP"' EXIT; chmod 700 "$TMP"

need() { [ -s "$1" ] || { echo "missing: $1" >&2; exit 1; }; }
need ~/.coolify-i60-token
need ~/.config/coolify/edgestudios.token

# ── carry the proven node + OME wiring across verbatim ────────────────────────
# Reused rather than reassembled from the token dotfiles, so a node address cannot be mistyped.
# -A is required: the EdgeStudios Coolify is behind Cloudflare, which 403s a default urllib/curl UA.
curl -fsS -H "Authorization: Bearer $ES_TOKEN" -A 'Mozilla/5.0' \
     "$ES/api/v1/applications/$ES_APP/envs" > "$TMP/es.json"

# ── the two new ones: this is a separate deployment, so it gets its own ───────
# A fresh database means no existing ciphertext needs the old KEK, and the PVE tokens re-arm from
# the env seed on every start.
python3 -c "
import base64, secrets, pathlib
kek = base64.b64encode(secrets.token_bytes(32)).decode()
pw  = secrets.token_urlsafe(18)
pathlib.Path('$TMP/kek').write_text(kek)
pathlib.Path('$TMP/pw').write_text(pw)
h = pathlib.Path.home()
(h/'.vmentory-i60-kek').write_text(kek + chr(10));        (h/'.vmentory-i60-kek').chmod(0o600)
(h/'.vmentory-i60-breakglass').write_text('breakglass' + chr(10) + pw + chr(10))
(h/'.vmentory-i60-breakglass').chmod(0o600)
print('  generated KEK      -> ~/.vmentory-i60-kek')
print('  generated admin pw -> ~/.vmentory-i60-breakglass')
"

# ── apply ─────────────────────────────────────────────────────────────────────
# PATCH updates an existing key; POST creates it. Try PATCH, fall back to POST.
python3 - "$TMP" "$APP" "$I60" <<'PY'
import json, sys, pathlib, subprocess

tmp, app, base = pathlib.Path(sys.argv[1]), sys.argv[2], sys.argv[3]
token = pathlib.Path.home().joinpath('.coolify-i60-token').read_text().strip()

CARRY = ("VMENTORY_SEED_PVE_HOSTS", "VMENTORY_PVE_MIGRATE_TOKENS", "VMENTORY_PVE_PROBE_KEY",
         "VMENTORY_OME_PASSWORD")
src = {}
for e in json.loads((tmp/'es.json').read_text()):
    if e.get('key') in CARRY and e.get('value'):
        src.setdefault(e['key'], e['value'])

env = dict(src)
env['VMENTORY_KEK']            = (tmp/'kek').read_text().strip()
env['VMENTORY_ADMIN_PASSWORD'] = (tmp/'pw').read_text().strip()

missing = [k for k in CARRY if k not in env]
if missing:
    print(f"  !! not found on the live instance: {missing}", file=sys.stderr)
    sys.exit(1)

def call(method, path, body):
    body_file = tmp/'body.json'
    body_file.write_text(json.dumps(body))
    out = subprocess.run(
        ['curl', '-sS', '-o', str(tmp/'resp'), '-w', '%{http_code}', '-X', method,
         '-H', f'Authorization: Bearer {token}', '-H', 'Content-Type: application/json',
         f'{base}/api/v1/applications/{app}/envs', '-d', f'@{body_file}'],
        capture_output=True, text=True, timeout=60)
    return out.stdout.strip(), (tmp/'resp').read_text()[:120]

ok = True
for k, v in env.items():
    code, resp = call('PATCH', '', {'key': k, 'value': v})
    if code not in ('200', '201'):
        code, resp = call('POST', '', {'key': k, 'value': v})
    status = 'ok' if code in ('200', '201') else f'FAILED {code} {resp}'
    if status != 'ok': ok = False
    print(f"  {k:<32} {status}")

sys.exit(0 if ok else 1)
PY

echo
echo "Done. Deploy with:"
echo "  curl -s -X POST -H \"Authorization: Bearer \$(cat ~/.coolify-i60-token)\" \\"
echo "    '$I60/api/v1/deploy?uuid=$APP&force=true'"
