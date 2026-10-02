// Activity strip + Actions page test. Synthetic "underway" activity is injected IN THE BROWSER via
// CDP request interception (Fetch domain) — nothing fake is written to the app, nothing is executed.
// usage: node ops/uitest-activity.mjs <session-cookie> <outdir> [base=http://localhost:18080/]
import { spawn } from 'node:child_process';
import { writeFileSync, mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
const [cookie, out, base = 'http://localhost:18080/'] = process.argv.slice(2);
const port = 9335;
const chrome = spawn('google-chrome', ['--headless=new', `--remote-debugging-port=${port}`, '--no-first-run', `--user-data-dir=${mkdtempSync(tmpdir() + '/vmui-')}`, 'about:blank'], { stdio: 'ignore' });
const sleep = ms => new Promise(r => setTimeout(r, ms));
let tabs; for (let i = 0; i < 40; i++) { try { tabs = await (await fetch(`http://127.0.0.1:${port}/json`)).json(); if (tabs.length) break; } catch {} await sleep(250); }
const ws = new WebSocket(tabs.find(t => t.type === 'page').webSocketDebuggerUrl); await new Promise(r => ws.onopen = r);
let id = 0; const pend = new Map(); const problems = []; let inject = true;

const now = Date.now(), iso = ms => new Date(now - ms).toISOString();
const FAKE = {
  activity: { now: iso(0), active: 2, items: [
    { kind: 'migrate', id: 'fakejob1', title: 'Migrate isxdc1 → atlas19', status: 'running', active: true, phase: 'copying — drive-scsi0: transferred 4.1 GiB of 18.0 GiB (22.78%)', guest: 'isxdc1', node: 'sirius16', target: 'atlas19', startedAt: iso(95000), by: 'admin' },
    { kind: 'power', id: 'fakepow1', title: 'Shutdown gitlab40', status: 'running', active: true, phase: 'shutdown sent to sagan25', guest: 'gitlab40', node: 'sagan25', startedAt: iso(8000), by: 'admin' },
    { kind: 'migrate', id: 'fakejob0', title: 'Migrate old-rdp → atlas19', status: 'ok', active: false, phase: 'arrived on atlas19', guest: 'old-rdp', node: 'sirius16', target: 'atlas19', startedAt: iso(900000), finishedAt: iso(30000), by: 'admin' },
  ] },
  job: { job: { id: 'fakejob1', guestName: 'isxdc1', guestType: 'qemu', vmid: 114, sourceNode: 'sirius16', targetNode: 'atlas19', sourceHostId: 'x', targetHostId: 'y', mode: 'Offline', status: 'Running', phase: 'copying — 22.78%', bytesPlanned: 19327352832, createdAt: iso(100000), createdBy: 'admin', startedAt: iso(95000), upid: 'UPID:sirius16:fake' }, log: 'starting migration of VM 114\ndrive-scsi0: transferred 4.1 GiB of 18.0 GiB (22.78%)', preflight: null },
  power: { id: 'fakepow1', upid: 'UPID:sagan25:fake', hostId: 'z', node: 'sagan25', guest: 'gitlab40', guestType: 'qemu', vmid: 1040, action: 'shutdown', by: 'admin', startedAt: iso(8000), running: true, log: 'shutting down' },
};
ws.onmessage = async e => { const m = JSON.parse(e.data);
  if (m.id && pend.has(m.id)) { pend.get(m.id)(m); pend.delete(m.id); return; }
  if (m.method === 'Fetch.requestPaused') {
    const u = m.params.request.url; let body = null;
    if (inject && u.endsWith('/api/activity')) body = FAKE.activity;
    else if (u.endsWith('/api/fleet/jobs/fakejob1')) body = FAKE.job;
    else if (u.endsWith('/api/activity/power/fakepow1')) body = FAKE.power;
    if (body) send('Fetch.fulfillRequest', { requestId: m.params.requestId, responseCode: 200, responseHeaders: [{ name: 'Content-Type', value: 'application/json' }], body: Buffer.from(JSON.stringify(body)).toString('base64') });
    else send('Fetch.continueRequest', { requestId: m.params.requestId });
    return; }
  if (m.method === 'Runtime.exceptionThrown') problems.push('EXCEPTION ' + (m.params.exceptionDetails.exception?.description || m.params.exceptionDetails.text).slice(0, 300));
  if (m.method === 'Runtime.consoleAPICalled' && m.params.type === 'error') problems.push('console.error ' + JSON.stringify(m.params.args.map(a => a.value)).slice(0, 200)); };
const send = (method, params = {}) => new Promise(r => { const i = ++id; pend.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
const ev = async expr => { const r = await send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true }); if (r.result?.exceptionDetails) throw new Error(expr.slice(0, 80) + ' :: ' + r.result.exceptionDetails.exception?.description); return r.result?.result?.value; };
const shot = async n => { const s = await send('Page.captureScreenshot', { format: 'png' }); writeFileSync(`${out}/${n}.png`, Buffer.from(s.result.data, 'base64')); };
const check = (c, msg) => { console.log((c ? '  PASS ' : '  FAIL ') + msg); if (!c) problems.push('FAIL ' + msg); };
const waitFor = async (expr, ms = 12000) => { const t = Date.now(); while (Date.now() - t < ms) { if (await ev(expr)) return true; await sleep(200); } return false; };
const vp = (w, h, mobile) => send('Emulation.setDeviceMetricsOverride', { width: w, height: h, deviceScaleFactor: 1, mobile });

await send('Runtime.enable'); await send('Page.enable'); await send('Network.enable');
await send('Fetch.enable', { patterns: [{ urlPattern: '*/api/activity*' }, { urlPattern: '*/api/fleet/jobs/fake*' }] });
await send('Network.setCookie', { name: 'vmentory_session', value: cookie, domain: new URL(base).hostname, path: '/' });

console.log('— desktop: strip with work underway');
await vp(1440, 900, false); await send('Page.navigate', { url: base });
check(await waitFor(`document.getElementById('strip').style.display==='flex'`), 'strip appears when something is underway');
check(await ev(`document.querySelectorAll('#strip .sitem').length`) === 3, 'strip shows 2 running + 1 just finished');
check(await ev(`document.getElementById('act-badge').textContent`) === '2', 'header Actions badge shows 2 underway');
check(await waitFor(`[...document.querySelectorAll('.row.guest')].find(r=>r.textContent.includes('isxdc1'))?.querySelector('.spin')!=null`), 'the guest being moved shows a spinner in the tree');
await shot('a1-strip');
await ev(`[...document.querySelectorAll('#strip .sitem')][0].click()`);
check(await waitFor(`location.hash==='#actions/migrate/fakejob1'`), 'clicking a strip item opens it on the Actions page');
check(await waitFor(`document.getElementById('apage').style.display==='flex'&&document.querySelector('.adetail h1')?.textContent.includes('isxdc1')`), 'Actions page shows the job detail');
check(await ev(`document.querySelector('.adetail pre.log').textContent.includes('22.78%')`), 'job detail shows the live task log');
check(await ev(`[...document.querySelectorAll('.adetail .btn')].some(b=>b.textContent.includes('Cancel'))`), 'running job offers Cancel');
check(await ev(`document.querySelectorAll('.aitem').length`) === 3, 'list shows underway + recent');
check(await ev(`document.getElementById('strip').style.display==='flex'`), 'strip stays visible on the Actions page');
await shot('a2-actions-migrate');
await ev(`[...document.querySelectorAll('.aitem')].find(a=>a.textContent.includes('gitlab40')).click()`);
check(await waitFor(`document.querySelector('.adetail h1')?.textContent.includes('Shutdown gitlab40')`), 'power action detail opens');
await shot('a3-actions-power');
await ev(`location.hash=''`);
check(await waitFor(`document.getElementById('layout').style.display==='flex'&&document.querySelectorAll('.row.guest').length>0`), 'back to the inventory');
check(await ev(`document.getElementById('strip').style.display==='flex'`), 'strip persists on the inventory screen');
await ev(`document.getElementById('act-nav').click()`);
check(await waitFor(`location.hash==='#actions'&&document.querySelector('.adetail').textContent.includes('Select an action')`), 'header Actions button opens the page');

console.log('— mobile');
await vp(390, 844, true); await send('Page.navigate', { url: base + '#actions' });
check(await waitFor(`document.querySelectorAll('.aitem').length===3`), 'mobile Actions list');
check(await ev(`document.documentElement.scrollWidth<=390`), 'no horizontal page scroll (strip scrolls inside itself)');
await shot('a4-mobile-list');
await ev(`document.querySelector('.aitem').click()`);
check(await waitFor(`getComputedStyle(document.querySelector('.alist')).display==='none'&&!!document.querySelector('.adetail h1')`), 'tapping an item shows its detail full-width');
await shot('a5-mobile-detail');
await ev(`document.querySelector('.back').click()`);
check(await waitFor(`location.hash==='#actions'&&getComputedStyle(document.querySelector('.alist')).display!=='none'`), 'back returns to the list');

console.log('— real feed (no injection)');
inject = false; await vp(1440, 900, false); await send('Page.navigate', { url: base });
await waitFor(`document.querySelectorAll('.row.guest').length>0`);
await sleep(1500);
check(await ev(`document.getElementById('strip').style.display==='none'`), 'strip hidden when nothing is underway (real /api/activity)');

console.log(problems.length ? 'PROBLEMS:\n  ' + problems.join('\n  ') : 'no exceptions or console errors');
ws.close(); chrome.kill(); process.exit(problems.length ? 1 : 0);
