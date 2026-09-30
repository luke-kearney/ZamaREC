// Takes the README screenshots from a daemon serving the demo library (see demo-data.py).
//   node shoot.mjs <chrome-debug-port> <ui-url> <out-dir>
// Chrome must be running headless with --remote-debugging-port and a 1680x910 window. Each shot sets the theme,
// clicks through the real page, waits for it to settle and saves a PNG of the viewport.
import { writeFileSync, mkdirSync } from 'node:fs';
const [port, ui, out] = process.argv.slice(2);
mkdirSync(out, { recursive: true });
const pages = await (await fetch(`http://127.0.0.1:${port}/json`)).json();
const ws = new WebSocket(pages.find(p => p.type === 'page').webSocketDebuggerUrl);
await new Promise(r => ws.addEventListener('open', r));
let next = 1; const waiting = new Map();
ws.addEventListener('message', m => { const d = JSON.parse(m.data); if (waiting.has(d.id)) { waiting.get(d.id)(d); waiting.delete(d.id); } });
const send = (method, params = {}) => new Promise(res => { const id = next++; waiting.set(id, res); ws.send(JSON.stringify({ id, method, params })); });
await send('Emulation.setDeviceMetricsOverride', { width: 1680, height: 910, deviceScaleFactor: 1, mobile: false });   // the size the UI window opens at
const run = async js => {
  const r = await send('Runtime.evaluate', { expression: `(async () => { const wait = ms => new Promise(r => setTimeout(r, ms)); const $ = id => document.getElementById(id); const item = re => [...document.querySelectorAll('.item')].find(i => re.test(i.innerText)); ${js} })()`, awaitPromise: true, returnByValue: true, userGesture: true });
  if (r.result.exceptionDetails) throw new Error(r.result.exceptionDetails.exception?.description || 'page script failed');
  return r.result.result.value;
};
const open = async theme => {
  await send('Page.navigate', { url: ui }); await new Promise(r => setTimeout(r, 1500));
  await run(`localStorage.setItem('theme', '${theme}'); localStorage.setItem('volume', '0.8'); localStorage.setItem('muted', '0'); localStorage.removeItem('mixer');`);
  await send('Page.navigate', { url: ui }); await new Promise(r => setTimeout(r, 2500));
};
const shot = async name => {
  const r = await send('Page.captureScreenshot', { format: 'png' });
  writeFileSync(`${out}/${name}.png`, Buffer.from(r.result.data, 'base64')); console.log('saved', name);
};
// Seeks the video, waits for the frame to show, and leaves it paused there.
const at = t => `seek(${t}); await wait(1800); $('video').pause(); await wait(400);`;
const hover = frac => `{ const tl = $('timeline'), r = tl.getBoundingClientRect(); tl.dispatchEvent(new MouseEvent('mousemove', { clientX: r.left + r.width * ${frac}, clientY: r.top + 20, bubbles: true })); await wait(900); }`;

// 1. the timeline: a match with game events, a range selected, the hover preview
await open('dark');
await run(`item(/Starfall Arena/).click(); await wait(2500); ${at(318)} state.inT = 296; state.outT = 331; renderSelection(); setZoom(2.2, 318); await wait(400);`);
await shot('timeline');

// 2. a clip with the mixer open
await run(`document.querySelector('[data-tab="clips"]').click(); await wait(1000); item(/clutch 1v3(?! 10)/).click(); await wait(2200); $('video').currentTime = 17; await wait(1200);
  $('mixBtn').click(); await wait(200); $('mixOn').click(); await wait(1500);
  const set = (n, v) => { const el = document.querySelector('.mixrow[data-name="' + n + '"] .mixlevel'); el.value = v; el.dispatchEvent(new Event('input')); };
  set('game', 0.7); set('mic', 1.4); document.querySelector('.mixrow[data-name="discord"] [data-mute]').click(); await wait(400);`);
await shot('mixer');

// 3. the share menu on the same clip
await run(`$('mixOn').click(); await wait(300); document.body.click(); await wait(200); $('shareClip').click(); await wait(300);`);
await shot('share');

// 4. the games page, a profile open in the editor
await run(`document.body.click(); $('gamesBtn').click(); await wait(2000); document.querySelector('[data-edit="0"]').click(); await wait(1200);`);
await shot('games');

// 5. the light theme, on a buffer session that plays through its clips
await open('light');
await run(`item(/Deep Harbor/).click(); await wait(2200); seek(1265); await wait(2200); $('video').pause(); await wait(300); ${hover(0.985)}`);
await shot('light');
ws.close(); process.exit(0);
