// kairos web UI: sessions + clips list, HLS/MP4 player, timeline with markers, range/marker export.
// Plain JS, no build step. Talks to /api on the daemon.

const COLORS = {
  manual: '#f5c542', save: '#4cc9f0', export: '#4cc9f0', kill: '#ff5c5c', multikill: '#ff2d95', headshot: '#ff8c42',
  death: '#8d99ae', round_win: '#43d17a', round_loss: '#c1121f', mvp: '#c77dff', map_start: '#6c757d', map_end: '#6c757d',
};
const colorOf = t => COLORS[t] || '#9aa4b2';

const $ = id => document.getElementById(id);
const state = {
  tab: 'sessions', profile: '', sessions: [], clips: [], typeFilter: '',
  current: null,      // { kind: 'session'|'clip', id, data }
  hls: null, details: null, total: 0, inT: null, outT: null, pollTimer: null,
};

// ------------------------------------------------------------------ helpers
const fmt = s => {
  if (s == null || isNaN(s)) return '–';
  s = Math.max(0, s); const h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60), sec = s % 60;
  return (h ? h + ':' + String(m).padStart(2, '0') : m) + ':' + sec.toFixed(1).padStart(4, '0');
};
const fmtDate = iso => { const d = new Date(iso); return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' }) + ' ' + d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' }); };
const gb = b => b > 1e9 ? (b / 1e9).toFixed(2) + ' GB' : (b / 1e6).toFixed(0) + ' MB';
async function api(path, opts) {
  const r = await fetch('/api' + path, opts && opts.body ? { method: 'POST', headers: { 'content-type': 'application/json' }, ...opts, body: JSON.stringify(opts.body) } : opts);
  const text = await r.text();
  let data = null; try { data = text ? JSON.parse(text) : null; } catch { data = text; }
  if (!r.ok) throw new Error((data && (data.error || data.message)) || r.statusText);
  return data;
}
let toastTimer;
function toast(msg, err) {
  const t = $('toast'); t.textContent = msg; t.hidden = false; t.classList.toggle('err', !!err);
  clearTimeout(toastTimer); toastTimer = setTimeout(() => t.hidden = true, err ? 7000 : 3500);
}

// ------------------------------------------------------------------ status
async function refreshStatus() {
  try {
    const s = await api('/status');
    const el = $('status');
    if (s.state === 'recording') {
      el.className = 'status recording';
      el.textContent = `recording ${s.profile} (${s.mode}) · ${fmt(s.elapsedSeconds)}` + (s.windowSeconds != null ? ` · ${fmt(s.windowSeconds)} in window` : '') + (s.pendingMarks ? ` · ${s.pendingMarks} pending clip` : '');
    } else { el.className = 'status idle'; el.textContent = s.state; }
    if (!state.profilesLoaded) {
      const sel = $('profileFilter');
      for (const p of s.profiles) { const o = document.createElement('option'); o.value = p.name; o.textContent = `${p.name} (${p.mode})`; sel.appendChild(o); }
      state.profilesLoaded = true;
    }
    if (state.current && state.current.kind === 'session' && state.current.data.live && s.sessionId !== state.current.id) loadSession(state.current.id);
  } catch { $('status').className = 'status idle'; $('status').textContent = 'daemon unreachable'; }
}

// ------------------------------------------------------------------ lists
async function refreshList() {
  if (state.tab === 'sessions') state.sessions = await api('/sessions'); else state.clips = await api('/clips');
  renderList();
}
function renderList() {
  const box = $('list'); box.innerHTML = '';
  if (state.tab === 'sessions') {
    for (const s of state.sessions.filter(s => !state.profile || s.profile === state.profile)) {
      const d = document.createElement('div');
      d.className = 'item' + (state.current && state.current.kind === 'session' && state.current.id === s.id ? ' active' : '');
      d.innerHTML = `<div class="l1"><span>${fmtDate(s.startedAt)}</span><span>${s.live ? '<span class="badge live">LIVE</span>' : ''}<span class="badge">${s.profile}</span></span></div>
        <div class="l2"><span>${s.map || s.mode}</span><span>${s.durationSeconds != null ? fmt(s.durationSeconds) : ''}</span><span>${s.markerCount} markers</span><span>${s.clipCount} clips</span></div>`;
      d.onclick = () => loadSession(s.id);
      box.appendChild(d);
    }
    if (!box.children.length) box.innerHTML = '<div class="empty">no sessions yet</div>';
  } else {
    for (const c of state.clips.filter(c => !state.profile || c.profile === state.profile)) {
      const d = document.createElement('div');
      d.className = 'item' + (state.current && state.current.kind === 'clip' && state.current.id === c.profile + '/' + c.file ? ' active' : '');
      const exp = c.expiresAt ? `<span class="badge exp" title="expires ${fmtDate(c.expiresAt)}">⌛</span>` : '';
      d.innerHTML = `<div class="l1"><span>${c.label || c.file.replace('.mp4', '')}</span><span>${c.starred ? '<span class="badge star">★</span>' : ''}${exp}<span class="badge">${c.profile}</span></span></div>
        <div class="l2"><span>${fmtDate(c.createdAt)}</span><span>${fmt(c.durationSeconds)}</span><span>${gb(c.bytes)}</span><span>${c.source}</span></div>`;
      d.onclick = () => loadClip(c);
      box.appendChild(d);
    }
    if (!box.children.length) box.innerHTML = '<div class="empty">no clips yet</div>';
  }
}

// ------------------------------------------------------------------ player
function destroyPlayer() {
  clearInterval(state.pollTimer); state.pollTimer = null;
  if (state.hls) { state.hls.destroy(); state.hls = null; }
  state.details = null;
  const v = $('video'); v.pause(); v.removeAttribute('src'); v.load();
}
function attachHls(url) {
  const v = $('video');
  if (Hls.isSupported()) {
    const hls = new Hls({ enableWorker: true, lowLatencyMode: false, liveDurationInfinity: false, maxBufferLength: 60, backBufferLength: 30 });
    hls.on(Hls.Events.LEVEL_LOADED, (_, d) => { state.details = d.details; });
    hls.on(Hls.Events.LEVEL_UPDATED, (_, d) => { state.details = d.details; });
    hls.on(Hls.Events.ERROR, (_, d) => { if (d.fatal) toast('playback error: ' + d.details, true); });
    hls.loadSource(url); hls.attachMedia(v); state.hls = hls;
  } else if (v.canPlayType('application/vnd.apple.mpegurl')) v.src = url;
  else toast('this browser cannot play HLS', true);
}
// session timeline seconds  <->  media element seconds (only differs for HLS sessions)
function toMedia(t) {
  const cur = state.current;
  if (cur.kind !== 'session') return t;
  const segs = cur.data.segments.filter(s => s.available);
  const seg = segs.find(s => t >= s.start && t < s.start + s.duration);
  if (!seg) return null;   // purged, or not recorded yet
  const frag = state.details && state.details.fragments.find(f => f.sn === seg.seq);
  return frag ? frag.start + (t - seg.start) : t - segs[0].start;
}
function fromMedia(m) {
  const cur = state.current;
  if (!cur || cur.kind !== 'session') return m;
  if (state.details) {
    const frag = state.details.fragments.find(f => m >= f.start && m < f.start + f.duration) || state.details.fragments[state.details.fragments.length - 1];
    const seg = frag && cur.data.segments.find(s => s.seq === frag.sn);
    if (frag && seg) return seg.start + (m - frag.start);
  }
  const segs = cur.data.segments.filter(s => s.available);
  return (segs[0] ? segs[0].start : 0) + m;
}
function seek(t) {
  const m = toMedia(t);
  if (m == null) return toast('that moment is no longer in the timeline window', true);
  const v = $('video'); v.currentTime = m; if (v.paused) v.play().catch(() => {});
}
function currentT() { return fromMedia($('video').currentTime); }

// ------------------------------------------------------------------ loading
async function loadSession(id) {
  const data = await api('/sessions/' + id);
  const first = !state.current || state.current.kind !== 'session' || state.current.id !== id;
  state.current = { kind: 'session', id, data };
  if (first) {
    destroyPlayer(); state.inT = state.outT = null;
    if (data.mode === 'timeline') attachHls(`/api/sessions/${id}/playlist.m3u8`);
    else toast('buffer-mode session: its footage lives in the clips it produced');
    if (data.live) state.pollTimer = setInterval(() => loadSession(id), 5000);
  } else if (data.live && data.mode === 'timeline') {
    const t = currentT();
    if (t < data.windowStart && toMedia(data.windowStart + 1) != null) seek(data.windowStart + 1);
  }
  $('title').textContent = `${data.profile} · ${fmtDate(data.startedAt)}` + (data.map ? ` · ${data.map}` : '');
  $('subtitle').textContent = data.mode === 'timeline'
    ? `${fmt(data.recordedSeconds)} recorded · window ${fmt(data.windowStart)} – ${fmt(data.windowEnd)}` + (data.live ? ' · live' : '')
    : `buffer mode · ${data.markers.length} marks`;
  state.total = data.mode === 'timeline' ? Math.max(data.recordedSeconds, data.windowEnd) : Math.max(1, ...data.markers.map(m => m.tVideo || 0));
  showViewer(); renderTimeline(); renderMarkers(); renderList();
}
function loadClip(c) {
  destroyPlayer(); state.inT = state.outT = null;
  state.current = { kind: 'clip', id: c.profile + '/' + c.file, data: c };
  $('video').src = `/api/clips/${encodeURIComponent(c.profile)}/${encodeURIComponent(c.file)}`;
  $('title').textContent = (c.label || c.file) + (c.starred ? ' ★' : '');
  $('subtitle').textContent = `${c.profile} · ${c.source} · ${fmtDate(c.createdAt)} · ${fmt(c.durationSeconds)} · ${gb(c.bytes)}` + (c.expiresAt ? ` · expires ${fmtDate(c.expiresAt)}` : '');
  state.total = c.durationSeconds || 1;
  showViewer(); renderTimeline(); renderMarkers(); renderList();
}
function showViewer() { $('empty').hidden = true; $('viewer').hidden = false; }

// ------------------------------------------------------------------ timeline
function markersOf() {
  const cur = state.current; if (!cur) return [];
  if (cur.kind === 'session') return cur.data.markers.map(m => ({ id: m.id, type: m.type, label: m.label, t: m.tVideo, starred: m.starred, clip: m.clip, data: m.data }));
  return (cur.data.markers || []).map((m, i) => ({ id: 'c' + i, type: m.type, label: m.label, t: m.t, data: m.data }));
}
function pct(t) { return Math.min(100, Math.max(0, t / state.total * 100)) + '%'; }
function renderTimeline() {
  const cur = state.current; if (!cur) return;
  const tl = $('timeline');
  if (cur.kind === 'session' && cur.data.mode === 'timeline') {
    $('tlPurged').style.width = pct(cur.data.windowStart);
    $('tlWindow').style.left = pct(cur.data.windowStart); $('tlWindow').style.width = pct(cur.data.windowEnd - cur.data.windowStart);
  } else { $('tlPurged').style.width = '0'; $('tlWindow').style.left = '0'; $('tlWindow').style.width = '100%'; }
  const box = $('tlMarkers'); box.innerHTML = '';
  const types = new Set();
  for (const m of markersOf()) {
    if (m.t == null) continue; types.add(m.type);
    if (state.typeFilter && m.type !== state.typeFilter) continue;
    const d = document.createElement('div'); d.className = 'tl-mark' + (m.starred ? ' starred' : '');
    d.style.left = pct(m.t); d.style.background = colorOf(m.type); d.title = `${fmt(m.t)} ${m.type}${m.label ? ' · ' + m.label : ''}`;
    d.onclick = e => { e.stopPropagation(); seek(m.t); };
    box.appendChild(d);
  }
  $('legend').innerHTML = [...types].map(t => `<span style="--c:${colorOf(t)}">${t}</span>`).join('');
  const tf = $('typeFilter'), bt = $('batchType'); const keep = tf.value;
  tf.innerHTML = '<option value="">all</option>' + [...types].map(t => `<option>${t}</option>`).join(''); tf.value = keep;
  bt.innerHTML = [...types].map(t => `<option>${t}</option>`).join('');
  renderSelection();
  tl.onclick = e => { const r = tl.getBoundingClientRect(); seek((e.clientX - r.left) / r.width * state.total); };
  tl.onmousemove = e => { const r = tl.getBoundingClientRect(); const t = (e.clientX - r.left) / r.width * state.total; const tip = $('tlTip'); tip.style.display = 'block'; tip.style.left = Math.min(e.clientX - r.left + 8, r.width - 70) + 'px'; tip.textContent = fmt(t); };
  tl.onmouseleave = () => $('tlTip').style.display = 'none';
}
function renderSelection() {
  const s = $('tlSel');
  if (state.inT != null && state.outT != null && state.outT > state.inT) { s.style.display = 'block'; s.style.left = pct(state.inT); s.style.width = pct(state.outT - state.inT); }
  else s.style.display = 'none';
  $('inVal').value = state.inT != null ? state.inT.toFixed(1) : ''; $('outVal').value = state.outT != null ? state.outT.toFixed(1) : '';
}
function renderMarkers() {
  const cur = state.current; const box = $('markerList'); box.innerHTML = '';
  const isTimeline = cur.kind === 'session' && cur.data.mode === 'timeline';
  for (const m of markersOf().slice().sort((a, b) => (a.t ?? 0) - (b.t ?? 0))) {
    if (state.typeFilter && m.type !== state.typeFilter) continue;
    const gone = isTimeline && m.t != null && m.t < cur.data.windowStart;
    const d = document.createElement('div'); d.className = 'mk' + (gone ? ' gone' : '');
    const extra = m.data && m.data.count ? ` ×${m.data.count}` : '';
    d.innerHTML = `<div class="bar" style="background:${colorOf(m.type)}"></div>
      <div><div class="name">${m.type}${extra}${m.label ? ' · ' + m.label : ''}</div><div class="t">${fmt(m.t)}${gone ? ' · purged' : ''}${m.clip ? ' · ' + m.clip : ''}</div></div>
      <div class="acts">${cur.kind === 'session' ? `<button title="star" class="${m.starred ? 'on' : ''}" data-star>★</button>` : ''}${isTimeline && !gone ? '<button title="clip around this marker" data-clip>✂</button>' : ''}</div>`;
    d.onclick = () => { if (m.t != null && (!gone)) seek(m.t); };
    const st = d.querySelector('[data-star]');
    if (st) st.onclick = async e => { e.stopPropagation(); await api(`/sessions/${cur.id}/markers/${m.id}/star`, { method: 'POST' }); loadSession(cur.id); };
    const cl = d.querySelector('[data-clip]');
    if (cl) cl.onclick = async e => { e.stopPropagation(); await exportAround(m); };
    box.appendChild(d);
  }
  if (!box.children.length) box.innerHTML = '<div class="empty">no markers</div>';
}

// ------------------------------------------------------------------ export
async function exportRange() {
  const cur = state.current; if (!cur) return;
  if (state.inT == null || state.outT == null || state.outT <= state.inT) return toast('set in and out points first', true);
  const body = { start: state.inT, end: state.outT, label: $('exportLabel').value || null, exact: $('exact').checked };
  toast('exporting…');
  try {
    const r = cur.kind === 'session' ? await api(`/sessions/${cur.id}/export`, { body }) : await api(`/clips/${cur.id}/export`, { body });
    toast(`exported ${fmt(r.duration)} → ${r.file}`); if (state.tab === 'clips') refreshList();
  } catch (e) { toast('export failed: ' + e.message, true); }
}
async function exportAround(m) {
  const cur = state.current;
  const body = { markerId: m.id, before: +$('before').value || 0, after: +$('after').value || 0, label: $('exportLabel').value || null, exact: $('exact').checked };
  toast('exporting…');
  try { const r = await api(`/sessions/${cur.id}/export-around`, { body }); toast(`exported ${fmt(r.duration)} → ${r.file}`); }
  catch (e) { toast('export failed: ' + e.message, true); }
}
async function exportBatch() {
  const cur = state.current; if (!cur || cur.kind !== 'session') return;
  const type = $('batchType').value; if (!type) return;
  const body = { type, before: +$('before').value || 0, after: +$('after').value || 0, exact: $('exact').checked };
  toast(`exporting every "${type}" marker…`);
  try { const r = await api(`/sessions/${cur.id}/export-batch`, { body }); toast(`${r.filter(x => x.ok).length}/${r.length} clips exported`); }
  catch (e) { toast('batch failed: ' + e.message, true); }
}

// ------------------------------------------------------------------ wiring
document.querySelectorAll('.tab').forEach(b => b.onclick = () => { document.querySelectorAll('.tab').forEach(x => x.classList.toggle('active', x === b)); state.tab = b.dataset.tab; refreshList(); });
$('profileFilter').onchange = e => { state.profile = e.target.value; renderList(); };
$('typeFilter').onchange = e => { state.typeFilter = e.target.value; renderTimeline(); renderMarkers(); };
$('inBtn').onclick = () => { state.inT = currentT(); if (state.outT != null && state.outT <= state.inT) state.outT = null; renderSelection(); };
$('outBtn').onclick = () => { state.outT = currentT(); renderSelection(); };
$('inVal').onchange = e => { state.inT = +e.target.value; renderSelection(); };
$('outVal').onchange = e => { state.outT = +e.target.value; renderSelection(); };
$('exportBtn').onclick = exportRange;
$('batchBtn').onclick = exportBatch;
$('markBtn').onclick = async () => { try { const r = await api('/mark', { body: { label: null } }); toast(r.message, !r.ok); } catch (e) { toast(e.message, true); } };
$('video').addEventListener('timeupdate', () => { if (!state.current) return; const t = currentT(); $('curTime').textContent = fmt(t) + ' / ' + fmt(state.total); $('tlHead').style.left = pct(t); });
document.addEventListener('keydown', e => {
  if (e.target.tagName === 'INPUT' || e.target.tagName === 'SELECT') return;
  const v = $('video');
  const nudge = d => { v.currentTime = Math.max(0, v.currentTime + d); };
  switch (e.key) {
    case ' ': e.preventDefault(); v.paused ? v.play() : v.pause(); break;
    case 'ArrowLeft': nudge(e.shiftKey ? -30 : -5); break;
    case 'ArrowRight': nudge(e.shiftKey ? 30 : 5); break;
    case 'j': nudge(-10); break; case 'l': nudge(10); break;
    case ',': nudge(-1 / 60); break; case '.': nudge(1 / 60); break;
    case 'i': $('inBtn').click(); break; case 'o': $('outBtn').click(); break;
    case 'm': $('markBtn').click(); break;
    case 'e': exportRange(); break;
  }
});

refreshStatus(); refreshList(); setInterval(refreshStatus, 3000);
