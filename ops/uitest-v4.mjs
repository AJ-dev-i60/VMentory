// Drives the v4 UI in headless Chrome over CDP. No write action is ever confirmed: destructive
// menu items are opened to their confirm step and cancelled; migrate runs a DRY-RUN preflight only.
// usage: node uitest.mjs <cookie> <outdir>
import { spawn } from 'node:child_process';
import { writeFileSync } from 'node:fs';
const [cookie, out] = process.argv.slice(2);
const port = 9334, base = 'http://localhost:18080/';
const chrome = spawn('google-chrome', ['--headless=new', `--remote-debugging-port=${port}`, '--no-first-run', '--user-data-dir=/tmp/claude-1000/-home-armandt/25156685-719e-4bdb-a4f3-661a42eb93d5/scratchpad/chrome-prof2', 'about:blank'], { stdio: 'ignore' });
const sleep = ms => new Promise(r => setTimeout(r, ms));
let tabs; for (let i = 0; i < 40; i++) { try { tabs = await (await fetch(`http://127.0.0.1:${port}/json`)).json(); if (tabs.length) break; } catch {} await sleep(250); }
const ws = new WebSocket(tabs.find(t => t.type === 'page').webSocketDebuggerUrl); await new Promise(r => ws.onopen = r);
let id = 0; const pend = new Map(); const problems = [];
ws.onmessage = e => { const m = JSON.parse(e.data); if (m.id && pend.has(m.id)) { pend.get(m.id)(m); pend.delete(m.id); }
  else if (m.method === 'Runtime.exceptionThrown') problems.push('EXCEPTION ' + (m.params.exceptionDetails.exception?.description || m.params.exceptionDetails.text).slice(0, 300));
  else if (m.method === 'Runtime.consoleAPICalled' && m.params.type === 'error') problems.push('console.error ' + JSON.stringify(m.params.args.map(a => a.value)).slice(0, 300));
  else if (m.method === 'Network.responseReceived' && m.params.response.status >= 400 && m.params.response.url.includes('/api/')) problems.push(`HTTP ${m.params.response.status} ${m.params.response.url}`); };
const send = (method, params = {}) => new Promise(r => { const i = ++id; pend.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
const ev = async expr => { const r = await send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true }); if (r.result?.exceptionDetails) throw new Error('eval failed: ' + expr.slice(0, 80) + ' :: ' + r.result.exceptionDetails.exception?.description); return r.result?.result?.value; };
const shot = async name => { const s = await send('Page.captureScreenshot', { format: 'png' }); writeFileSync(`${out}/${name}.png`, Buffer.from(s.result.data, 'base64')); console.log('  shot', name); };
const vp = (w, h, mobile) => send('Emulation.setDeviceMetricsOverride', { width: w, height: h, deviceScaleFactor: 1, mobile });
const check = (cond, msg) => { console.log((cond ? '  PASS ' : '  FAIL ') + msg); if (!cond) problems.push('FAIL ' + msg); };
const waitFor = async (expr, ms = 15000) => { const t = Date.now(); while (Date.now() - t < ms) { if (await ev(expr)) return true; await sleep(200); } return false; };

await send('Runtime.enable'); await send('Network.enable'); await send('Page.enable');
await send('Network.setCookie', { name: 'vmentory_session', value: cookie, domain: 'localhost', path: '/' });

console.log('— desktop 1440×900');
await vp(1440, 900, false);
await send('Page.navigate', { url: base });
check(await waitFor(`document.querySelectorAll('.row.guest').length>0`), 'tree renders guest rows');
check(await ev(`document.querySelectorAll('.row.group').length`) === 5, 'five node rows');
check(await ev(`document.getElementById('panel').textContent.includes('Fleet overview')`), 'fleet overview in the panel by default');
await shot('d1-overview');
// search + filters
await ev(`(()=>{const q=document.getElementById('q');q.value='isxdc';q.dispatchEvent(new Event('input'));})()`);
check(await ev(`document.querySelectorAll('.row.guest').length`) === 2, 'search "isxdc" → 2 guests');
await ev(`(()=>{const q=document.getElementById('q');q.value='172.0.0.101';q.dispatchEvent(new Event('input'));})()`);
check((await ev(`[...document.querySelectorAll('.row.guest')].map(r=>r.textContent).join()`)).includes('isxdc1'), 'search by IP finds isxdc1');
await ev(`(()=>{const q=document.getElementById('q');q.value='';q.dispatchEvent(new Event('input'));})()`);
await ev(`document.querySelector('.chip[data-k=lxc]').click()`);
const ctOnly = await ev(`[...document.querySelectorAll('.row.guest')].every(r=>r.textContent.includes('CT'))`);
check(ctOnly && await ev(`document.querySelectorAll('.row.guest').length`) === 14, 'CT filter → 14 containers only');
await ev(`document.querySelector('.chip[data-k=all]').click()`);
// collapse a node
const before = await ev(`document.querySelectorAll('.row.guest').length`);
await ev(`[...document.querySelectorAll('.row.group')].find(r=>r.textContent.includes('vega14')).querySelector('.caret').click()`);
check(await ev(`document.querySelectorAll('.row.guest').length`) < before, 'caret collapses a node');
await ev(`[...document.querySelectorAll('.row.group')].find(r=>r.textContent.includes('vega14')).querySelector('.caret').click()`);
// select guest
await ev(`[...document.querySelectorAll('.row.guest')].find(r=>r.textContent.includes('isxdc1')).click()`);
check(await waitFor(`document.querySelector('#panel h1')?.textContent==='isxdc1'`), 'clicking a guest opens its side panel');
check(await ev(`location.hash.startsWith('#g/')`), 'selection is in the URL');
check(await ev(`document.getElementById('panel').textContent.includes('172.0.0.101')`), 'panel shows the live IP');
check(await ev(`document.querySelectorAll('#panel .share').length>=3`), 'panel shows CPU/RAM/disk share bars');
await shot('d2-guest');
// tooltip
await ev(`(()=>{const el=document.querySelector('.row.guest .ic');el.dispatchEvent(new PointerEvent('pointerover',{bubbles:true,pointerType:'mouse'}));})()`);
check(await ev(`getComputedStyle(document.getElementById('tip')).display==='block'&&document.getElementById('tip').textContent.length>0`), 'icon hover shows a tooltip');
await ev(`document.querySelector('.row.guest .ic').dispatchEvent(new PointerEvent('pointerout',{bubbles:true,pointerType:'mouse'}))`);
// actions menu
await ev(`document.querySelector('#panel .act').click()`);
check(await waitFor(`!!document.getElementById('menu')`), 'Actions ▾ opens the menu');
const items = await ev(`[...document.querySelectorAll('#menu .mi')].map(b=>b.dataset.a+':'+(b.disabled?'off':'on'))`);
console.log('    menu items', items.join(' '));
check(items.includes('start:off') && items.includes('stop:on') && items.includes('clone:off'), 'Start disabled while running; Stop enabled; Clone disabled (not built)');
await shot('d3-menu');
await ev(`document.querySelector('#menu .mi[data-a=stop]').click()`);
check(await waitFor(`!!document.getElementById('confirm-go')`), 'Stop asks for a one-line confirm');
await shot('d4-confirm');
await ev(`[...document.querySelectorAll('#menu .btn')].find(b=>b.textContent==='Cancel').click()`);
check(await ev(`!!document.querySelector('#menu .mi')`), 'Cancel returns to the menu, nothing sent');
await ev(`document.body.click()`);
check(await ev(`!document.getElementById('menu')`), 'clicking outside closes the menu');
// migrate dry run
await ev(`document.querySelector('#panel .act').click()`); await sleep(200);
await ev(`document.querySelector('#menu .mi[data-a=migrate]').click()`);
check(await waitFor(`document.querySelectorAll('.tgt').length===4`), 'Migrate… lists 4 ranked targets');
await ev(`document.getElementById('pf-btn').click()`);
check(await waitFor(`!!document.getElementById('pf')`, 30000), 'dry-run preflight renders');
check(await ev(`document.getElementById('pf').textContent.includes('Mode')`), 'preflight shows mode/VMID/storage');
await shot('d5-migrate-preflight');
await ev(`closeModal()`);
// node panel
await ev(`document.querySelectorAll('.row.group')[0].click()`);
check(await waitFor(`document.getElementById('panel').textContent.includes('Capacity')`), 'node row opens the node panel');
await shot('d6-node');
// light theme
await ev(`toggleTheme()`); await shot('d7-light'); await ev(`toggleTheme()`);

console.log('— mobile 390×844');
await vp(390, 844, true);
await send('Page.navigate', { url: base });
check(await waitFor(`document.querySelectorAll('.row.guest').length>0`), 'mobile tree renders');
check(await ev(`getComputedStyle(document.getElementById('panel')).display==='none'`), 'no side panel under 900px');
check(await ev(`document.documentElement.scrollWidth<=390`), 'no horizontal scroll at 390px');
check(await ev(`document.querySelector('.row.guest').getBoundingClientRect().height>=44`), 'rows ≥ 44px tall');
await shot('m1-list');
await ev(`(()=>{const el=document.querySelector('.row.guest .ic');el.dispatchEvent(new PointerEvent('pointerdown',{bubbles:true,pointerType:'touch'}));el.click();})()`);
check(await ev(`getComputedStyle(document.getElementById('tip')).display==='block'`), 'tapping an icon shows its tooltip (touch)');
check(await ev(`!document.querySelector('.inl')`), 'tapping an icon does not expand the row');
await ev(`[...document.querySelectorAll('.row.guest')].find(r=>r.textContent.includes('isxdc1')).click()`);
check(await waitFor(`!!document.querySelector('.inl')`), 'tapping a row expands it inline');
await ev(`document.querySelector('.inl').scrollIntoView()`);
await shot('m2-inline');
await ev(`document.querySelector('.inl .act').click()`);
check(await waitFor(`document.querySelector('#menu')?.classList.contains('sheet')`), 'Actions opens a bottom sheet on mobile');
await shot('m3-sheet');
await ev(`document.querySelector('.sheet-ov').click()`);
check(await ev(`!document.getElementById('menu')`), 'tapping the overlay closes the sheet');
await ev(`document.querySelector('.row.fleet').click()`);
check(await waitFor(`document.querySelector('.inl')?.textContent.includes('guests running')`), 'fleet row expands the overview inline');
await shot('m4-fleet');

console.log(problems.length ? 'PROBLEMS:\n  ' + problems.join('\n  ') : 'no exceptions, console errors or failing API calls');
ws.close(); chrome.kill(); process.exit(problems.length ? 1 : 0);
