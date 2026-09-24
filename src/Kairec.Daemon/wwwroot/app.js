// kairec web UI: sessions + clips list, HLS/MP4 player, timeline with markers, range/marker export.
// Plain JS, no build step. Talks to /api on the daemon.

const COLORS = {
  manual: '#f5c542', save: '#4cc9f0', export: '#4cc9f0', kill: '#ff5c5c', multikill: '#ff2d95', headshot: '#ff8c42',
  death: '#8d99ae', round_win: '#43d17a', round_loss: '#c1121f', mvp: '#c77dff', map_start: '#6c757d', map_end: '#6c757d',
};
const colorOf = (t, data) => (t === 'kill' && data && data.count >= 3) ? COLORS.multikill : (COLORS[t] || '#9aa4b2');
const markerName = m => {
  if (m.type === 'kill' && m.data) { const c = m.data.count || 1; return (c >= 2 ? `${c}k` : 'kill') + (m.data.headshot ? ' · HS' : ''); }
  return m.type + (m.label && m.label !== m.type ? ' · ' + m.label : '');
};

const $ = id => document.getElementById(id);
const state = {
  tab: 'sessions', profile: '', sessions: [], clips: [], typeFilter: '',
  current: null,      // { kind: 'session'|'clip', id, data }
  hls: null, details: null, total: 0, inT: null, outT: null, pollTimer: null,
  coverage: [], clipLoaded: null,   // buffer sessions: footage bands and which clip is in the player
  zoom: 1, viewStart: 0,             // timeline zoom: visible span is total/zoom starting at viewStart
  cursorT: 0,                        // last timeline position the user chose; in/out use this, not the player
  playingRange: false,               // true while "play range" runs; pauses at outT
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
    const el = $('status'), txt = el.querySelector('.txt');
    if (s.state === 'recording') {
      el.className = 'status recording';
      txt.textContent = `recording ${s.profile} · ${s.mode} · ${fmt(s.elapsedSeconds)}` + (s.windowSeconds != null ? ` · ${fmt(s.windowSeconds)} in window` : '') + (s.pendingMarks ? ` · ${s.pendingMarks} pending clip` : '');
    } else { el.className = 'status idle'; txt.textContent = s.state; }
    if (s.pause) { el.className = 'status paused'; txt.textContent = (s.state === 'recording' ? txt.textContent + ' · ' : '') + 'auto recording off ' + s.pause.text; }
    $('powerBtn').className = 'switch ' + (s.pause ? 'off' : 'on');
    if (s.usage) {
      const perCore = localStorage.getItem('cpuPerCore') === '1';
      $('uCpu').textContent = (perCore ? s.usage.cpuPercent : s.usage.cpuPercent / (s.usage.cores || 1)).toFixed(1) + '%';
      $('uCpuK').textContent = perCore ? 'cpu (1 core)' : 'cpu (all)';
      $('uRss').textContent = (s.usage.rssBytes / 1048576).toFixed(0) + ' MiB';
      $('uFs').textContent = (s.usage.ramFsBytes / 1e9).toFixed(2) + ' GB';
      $('uFs').title = `RAM footage in tmpfs · ${(s.usage.ramFsFree / 1e9).toFixed(1)} GB free`;
    }
    if (!state.profilesLoaded) {
      const sel = $('profileFilter');
      for (const p of s.profiles) { const o = document.createElement('option'); o.value = p.name; o.textContent = `${p.name} (${p.mode})`; sel.appendChild(o); }
      state.profilesLoaded = true;
    }
    if (state.current && state.current.kind === 'session' && state.current.data.live && s.sessionId !== state.current.id) loadSession(state.current.id);
  } catch { $('status').className = 'status idle'; $('status').querySelector('.txt').textContent = 'daemon unreachable'; }
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
      d.innerHTML = `<div class="l1"><span>${fmtDate(s.startedAt)}</span><span class="r">${s.live ? '<span class="badge live">LIVE</span>' : ''}${s.ram ? `<span class="badge ${s.footageGone ? 'gone' : 'ram'}" title="${s.footageGone ? 'RAM footage has been dropped' : 'segments live in RAM'}">${s.footageGone ? 'gone' : 'RAM'}</span>` : ''}<span class="badge">${s.profile}</span></span></div>
        <div class="l2"><span>${s.map || s.mode}</span>${s.durationSeconds != null ? `<span class="mono">${fmt(s.durationSeconds)}</span>` : ''}<span>${s.markerCount} marker${s.markerCount === 1 ? '' : 's'}</span><span>${s.clipCount} clip${s.clipCount === 1 ? '' : 's'}</span></div>`;
      d.onclick = () => { if (window.closePages) window.closePages(); loadSession(s.id); };
      box.appendChild(d);
    }
    if (!box.children.length) box.innerHTML = '<div class="empty">no sessions yet</div>';
  } else {
    for (const c of state.clips.filter(c => !state.profile || c.profile === state.profile)) {
      const d = document.createElement('div');
      d.className = 'item' + (state.current && state.current.kind === 'clip' && state.current.id === c.profile + '/' + c.file ? ' active' : '');
      const exp = c.expiresAt ? `<span class="badge exp" title="expires ${fmtDate(c.expiresAt)}">⌛</span>` : '';
      d.innerHTML = `<div class="l1"><span>${c.label || c.file.replace('.mp4', '')}</span><span class="r">${c.starred ? '<span class="badge star">★</span>' : ''}${exp}<span class="badge">${c.profile}</span></span></div>
        <div class="l2"><span>${fmtDate(c.createdAt)}</span><span class="mono">${fmt(c.durationSeconds)}</span><span>${gb(c.bytes)}</span><span>${c.source}</span></div>`;
      d.onclick = () => { if (window.closePages) window.closePages(); loadClip(c); };
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
function coverageAt(t) { return state.coverage.find(c => t >= c.start && t <= c.end); }
function toMedia(t) {
  const cur = state.current;
  if (cur.kind !== 'session') return t;
  if (cur.data.mode === 'buffer') {
    const c = coverageAt(t);
    if (!c) return null;
    if (state.clipLoaded !== c.file) {
      state.clipLoaded = c.file;
      $('video').src = `/api/clips/${encodeURIComponent(cur.data.profile)}/${encodeURIComponent(c.file)}`;
    }
    return t - c.start;
  }
  const segs = cur.data.segments.filter(s => s.available);
  const seg = segs.find(s => t >= s.start && t < s.start + s.duration);
  if (!seg) return null;   // purged, or not recorded yet
  const frag = state.details && state.details.fragments.find(f => f.sn === seg.seq);
  return frag ? frag.start + (t - seg.start) : t - segs[0].start;
}
function fromMedia(m) {
  const cur = state.current;
  if (!cur || cur.kind !== 'session') return m;
  if (cur.data.mode === 'buffer') { const c = state.coverage.find(c => c.file === state.clipLoaded); return c ? c.start + m : m; }
  if (state.details) {
    const frag = state.details.fragments.find(f => m >= f.start && m < f.start + f.duration) || state.details.fragments[state.details.fragments.length - 1];
    const seg = frag && cur.data.segments.find(s => s.seq === frag.sn);
    if (frag && seg) return seg.start + (m - frag.start);
  }
  const segs = cur.data.segments.filter(s => s.available);
  return (segs[0] ? segs[0].start : 0) + m;
}
function seek(t) {
  state.playingRange = false;
  state.cursorT = Math.max(0, Math.min(state.total, t));
  placeHead(state.cursorT);
  const m = toMedia(t);
  if (m == null) return toast(state.current.data.mode === 'buffer' ? 'no footage was saved for that moment' : 'that moment is no longer in the timeline window', true);
  const v = $('video'); v.currentTime = m; if (v.paused) v.play().catch(() => {});
}
function currentT() { return state.cursorT; }
function placeHead(t) { $('curTime').textContent = fmt(t) + ' / ' + fmt(state.total); $('tClock').textContent = fmtShort(t) + ' / ' + fmtShort(state.total); $('tlHead').style.left = pct(t); $('tlHeadT').textContent = fmtShort(t); }

// ------------------------------------------------------------------ loading
async function loadSession(id) {
  const data = await api('/sessions/' + id);
  const first = !state.current || state.current.kind !== 'session' || state.current.id !== id;
  state.current = { kind: 'session', id, data };
  state.coverage = (data.clipDetails || []).filter(c => c.sessionOffset != null && c.durationSeconds).map(c => ({ file: c.file, label: c.label, start: c.sessionOffset, end: c.sessionOffset + c.durationSeconds }));
  if (first) {
    destroyPlayer(); state.inT = state.outT = null; state.clipLoaded = null; state.zoom = 1; state.viewStart = 0; state.cursorT = 0;
    if (data.mode === 'timeline') attachHls(`/api/sessions/${id}/playlist.m3u8`);
    if (data.live) state.pollTimer = setInterval(() => loadSession(id), 5000);
  } else if (data.live && data.mode === 'timeline') {
    const t = currentT();
    if (t < data.windowStart && toMedia(data.windowStart + 1) != null) seek(data.windowStart + 1);
  }
  $('title').textContent = `${data.profile} · ${fmtDate(data.startedAt)}` + (data.map ? ` · ${data.map}` : '');
  const covered = state.coverage.reduce((a, c) => a + (c.end - c.start), 0);
  $('starClip').hidden = true;
  $('subtitle').textContent = data.mode === 'timeline'
    ? `${fmt(data.recordedSeconds)} recorded · window ${fmt(data.windowStart)} – ${fmt(data.windowEnd)}` + (data.ram ? ' · RAM' : '') + (data.footageGone ? ' · footage dropped, markers and clips kept' : '') + (data.live ? ' · live' : '')
    : `buffer mode · ${data.markers.length} marks · ${state.coverage.length} clips covering ${fmt(covered)}` + (data.live ? ' · live, nothing is written until a mark or save' : '');
  state.total = data.mode === 'timeline'
    ? Math.max(data.recordedSeconds, data.windowEnd)
    : Math.max(1, data.elapsedSeconds || 0, ...data.markers.map(m => m.tVideo || 0), ...state.coverage.map(c => c.end));
  showViewer(); renderTimeline(); renderMarkers(); renderList();
}
function loadClip(c) {
  destroyPlayer(); state.inT = state.outT = null; state.zoom = 1; state.viewStart = 0; state.cursorT = 0;
  state.current = { kind: 'clip', id: c.profile + '/' + c.file, data: c };
  $('video').src = `/api/clips/${encodeURIComponent(c.profile)}/${encodeURIComponent(c.file)}`;
  $('title').textContent = c.label || c.file;
  const sb = $('starClip'); sb.hidden = false; sb.classList.toggle('on', !!c.starred); sb.querySelector('span').textContent = c.starred ? 'Starred' : 'Star';
  $('subtitle').textContent = `${c.profile} · ${c.source} · ${fmtDate(c.createdAt)} · ${fmt(c.durationSeconds)} · ${gb(c.bytes)}` + (c.expiresAt ? ` · expires ${fmtDate(c.expiresAt)}` : '');
  state.total = c.durationSeconds || 1;
  showViewer(); renderTimeline(); renderMarkers(); renderList();
}
function showViewer() { if ((window.settingsOpen && window.settingsOpen()) || (window.pageOpen && window.pageOpen())) return; $('empty').hidden = true; $('viewer').hidden = false; }

// ------------------------------------------------------------------ timeline
function markersOf() {
  const cur = state.current; if (!cur) return [];
  if (cur.kind === 'session') return cur.data.markers.map(m => ({ id: m.id, type: m.type, label: m.label, t: m.tVideo, starred: m.starred, clip: m.clip, data: m.data }));
  return (cur.data.markers || []).map((m, i) => ({ id: 'c' + i, type: m.type, label: m.label, t: m.t, data: m.data }));
}
function viewLen() { return (state.total || 1) / state.zoom; }
function clampView() { state.viewStart = Math.max(0, Math.min(state.viewStart, (state.total || 1) - viewLen())); }
function pct(t) { return Math.min(200, Math.max(-100, (t - state.viewStart) / viewLen() * 100)) + '%'; }
function xToT(clientX) { const r = $('timeline').getBoundingClientRect(); return state.viewStart + (clientX - r.left) / r.width * viewLen(); }
function setZoom(z, anchorT, centre) {
  const old = state.zoom; state.zoom = Math.max(1, Math.min(64, z));
  if (anchorT != null && centre) state.viewStart = anchorT - viewLen() / 2;
  else if (anchorT != null) { const frac = (anchorT - state.viewStart) / (state.total / old); state.viewStart = anchorT - frac * viewLen(); }
  clampView(); renderTimeline(); $('zoomLbl').textContent = state.zoom === 1 ? 'fit' : state.zoom.toFixed(state.zoom < 4 ? 1 : 0) + '×';
}
function followPlayhead(t) {
  if (state.zoom === 1) return;
  if (t < state.viewStart || t > state.viewStart + viewLen()) { state.viewStart = t - viewLen() * 0.15; clampView(); renderTimeline(); }
}
function renderTimeline() {
  const cur = state.current; if (!cur) return;
  const tl = $('timeline');
  if (cur.kind === 'session' && cur.data.mode === 'timeline') {
    $('tlPurged').style.width = pct(cur.data.windowStart);
    $('tlWindow').style.left = pct(cur.data.windowStart); $('tlWindow').style.width = pct(cur.data.windowEnd - cur.data.windowStart);
  } else if (cur.kind === 'session' && cur.data.mode === 'buffer') {
    // No continuous footage: hatch everything, then draw a band per clip.
    $('tlPurged').style.width = '100%'; $('tlWindow').style.left = '0'; $('tlWindow').style.width = '0';
  } else { $('tlPurged').style.width = '0'; $('tlWindow').style.left = '0'; $('tlWindow').style.width = '100%'; }
  const box = $('tlMarkers'); box.innerHTML = '';
  if (cur.kind === 'session' && cur.data.mode === 'buffer') {
    for (const c of state.coverage) {
      const b = document.createElement('div'); b.className = 'tl-cov'; b.style.left = pct(c.start); b.style.width = pct(c.end - c.start);
      b.title = `${c.label || c.file} · ${fmt(c.start)} – ${fmt(c.end)}`;
      b.onclick = e => { e.stopPropagation(); seek(c.start); };
      box.appendChild(b);
    }
  }
  const types = new Set();
  for (const m of markersOf()) {
    if (m.t == null) continue; types.add(m.type);
    if (state.typeFilter && m.type !== state.typeFilter) continue;
    const d = document.createElement('div'); d.className = 'tl-mark' + (m.starred ? ' starred' : '');
    d.style.left = pct(m.t); d.style.background = colorOf(m.type, m.data); d.title = `${fmt(m.t)} ${markerName(m)}`;
    d.onclick = e => { e.stopPropagation(); seek(m.t); };
    box.appendChild(d);
  }
  $('legend').innerHTML = [...types].map(t => `<span style="--c:${colorOf(t)}">${t}</span>`).join('');
  const bt = $('batchType');
  bt.innerHTML = [...types].map(t => `<option>${t}</option>`).join('');
  renderRuler();
  renderChips(types);
  renderSelection();
  placeHead(state.cursorT);
  tl.onclick = e => seek(xToT(e.clientX));
  tl.onmousemove = e => { const r = tl.getBoundingClientRect(); const t = xToT(e.clientX); const tip = $('tlTip'); tip.style.display = 'block'; tip.style.left = Math.min(e.clientX - r.left + 8, r.width - 70) + 'px'; tip.textContent = fmt(t); };
  tl.onmouseleave = () => $('tlTip').style.display = 'none';
}
function renderRuler() {
  const r = $('tlRuler'); r.innerHTML = '';
  const span = viewLen(), v0 = state.viewStart, v1 = v0 + span, total = state.total || 1;
  const steps = [0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200];
  const step = steps.find(st => span / st <= 12) || 7200;
  const minor = step / (step >= 60 ? 4 : 5);
  for (let t = Math.floor(v0 / minor) * minor; t <= Math.min(total, v1); t += minor) {
    if (t < v0) continue;
    const major = Math.abs(t / step - Math.round(t / step)) < 1e-6;
    const d = document.createElement('div'); d.className = 'tick' + (major ? ' major' : ''); d.style.left = pct(t);
    if (major && t < v1 - span * 0.03) d.innerHTML = `<span>${fmtShort(t)}</span>`;
    r.appendChild(d);
  }
}
const fmtShort = s => { const h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60), sec = Math.floor(s % 60); return h ? `${h}:${String(m).padStart(2, '0')}:${String(sec).padStart(2, '0')}` : `${m}:${String(sec).padStart(2, '0')}`; };
function renderChips(types) {
  const counts = {}; for (const m of markersOf()) counts[m.type] = (counts[m.type] || 0) + 1;
  const box = $('typeChips'); box.innerHTML = '';
  const all = document.createElement('button'); all.className = 'chip' + (state.typeFilter ? '' : ' on'); all.textContent = 'all';
  all.onclick = () => { state.typeFilter = ''; renderTimeline(); renderMarkers(); };
  box.appendChild(all);
  for (const t of types) {
    const c = document.createElement('button'); c.className = 'chip' + (state.typeFilter === t ? ' on' : ''); c.style.setProperty('--c', colorOf(t));
    c.innerHTML = `${t} <span class="n">${counts[t] || 0}</span>`;
    c.onclick = () => { state.typeFilter = state.typeFilter === t ? '' : t; renderTimeline(); renderMarkers(); };
    box.appendChild(c);
  }
}
function renderSelection() {
  const s = $('tlSel');
  if (state.inT != null && state.outT != null && state.outT > state.inT) { s.style.display = 'block'; s.style.left = pct(state.inT); s.style.width = (state.outT - state.inT) / viewLen() * 100 + '%'; }
  else s.style.display = 'none';
  $('inVal').value = state.inT != null ? state.inT.toFixed(1) : ''; $('outVal').value = state.outT != null ? state.outT.toFixed(1) : '';
  const has = state.inT != null && state.outT != null && state.outT > state.inT;
  $('tRange').textContent = has ? `${fmt(state.inT)} → ${fmt(state.outT)} (${(state.outT - state.inT).toFixed(1)}s)` : '';
  $('inBtn').classList.toggle('on', state.inT != null); $('outBtn').classList.toggle('on', state.outT != null);
}
function renderMarkers() {
  const cur = state.current; const box = $('markerList'); box.innerHTML = '';
  const isTimeline = cur.kind === 'session' && cur.data.mode === 'timeline';
  for (const m of markersOf().slice().sort((a, b) => (a.t ?? 0) - (b.t ?? 0))) {
    if (state.typeFilter && m.type !== state.typeFilter) continue;
    const isBuffer = cur.kind === 'session' && cur.data.mode === 'buffer';
    const gone = (isTimeline && m.t != null && m.t < cur.data.windowStart) || (isBuffer && m.t != null && !coverageAt(m.t));
    const d = document.createElement('div'); d.className = 'mk' + (gone ? ' gone' : '');
    d.innerHTML = `<div class="bar" style="background:${colorOf(m.type, m.data)}"></div>
      <div><div class="name">${markerName(m)}</div><div class="t">${fmt(m.t)}${gone ? (isBuffer ? (cur.data.live && !m.clip ? ' · clip pending' : ' · no footage') : ' · purged') : ''}${m.clip ? ' · ' + m.clip : ''}</div></div>
      <div class="acts">${cur.kind === 'session' ? `<button title="star" class="${m.starred ? 'on' : ''}" data-star>★</button>` : ''}${(isTimeline || isBuffer) && !gone ? '<button title="clip around this marker" data-clip>✂</button>' : ''}</div>`;
    d.onclick = () => { if (m.t != null && (!gone)) seek(m.t); };
    const st = d.querySelector('[data-star]');
    if (st) st.onclick = async e => { e.stopPropagation(); await api(`/sessions/${cur.id}/markers/${m.id}/star`, { method: 'POST' }); loadSession(cur.id); };
    const cl = d.querySelector('[data-clip]');
    if (cl) cl.onclick = async e => { e.stopPropagation(); await exportAround(m); };
    box.appendChild(d);
  }
  const n = box.children.length; $('markersCount').textContent = n ? String(n) : '';
  if (!n) box.innerHTML = '<div class="empty">no markers</div>';
}

// ------------------------------------------------------------------ export
async function exportRange() {
  const cur = state.current; if (!cur) return;
  if (state.inT == null || state.outT == null || state.outT <= state.inT) return toast('set in and out points first', true);
  let body = { start: state.inT, end: state.outT, label: $('exportLabel').value || null, exact: $('exact').checked };
  let target = cur.kind === 'session' ? `/sessions/${cur.id}/export` : `/clips/${cur.id}/export`;
  if (cur.kind === 'session' && cur.data.mode === 'buffer') {
    const c = coverageAt(state.inT);
    if (!c || state.outT > c.end + 0.5) return toast('the range must lie inside one saved clip', true);
    target = `/clips/${encodeURIComponent(cur.data.profile)}/${encodeURIComponent(c.file)}/export`;
    body = { ...body, start: state.inT - c.start, end: Math.min(state.outT, c.end) - c.start };
  }
  toast('exporting…');
  try {
    const r = await api(target, { body });
    toast(`exported ${fmt(r.duration)} → ${r.file}`); if (state.tab === 'clips') refreshList();
  } catch (e) { toast('export failed: ' + e.message, true); }
}
async function exportAround(m) {
  const cur = state.current;
  const before = +$('before').value || 0, after = +$('after').value || 0;
  if (cur.data.mode === 'buffer') {
    const c = coverageAt(m.t); if (!c) return toast('no footage for that marker', true);
    state.inT = Math.max(c.start, m.t - before); state.outT = Math.min(c.end, m.t + after); renderSelection();
    return exportRange();
  }
  const body = { markerId: m.id, before, after, label: $('exportLabel').value || null, exact: $('exact').checked };
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
const rangeLen = () => Math.max(1, +$('rangeLen').value || 30);
$('inBtn').onclick = () => {
  state.inT = currentT();
  if (state.outT == null || state.outT <= state.inT) state.outT = Math.min(state.total, state.inT + rangeLen());
  renderSelection();
};
$('outBtn').onclick = () => {
  if (state.inT != null && Math.abs(currentT() - state.inT) < 0.5) return toast('move the playhead to where the range should end first', true);
  state.outT = currentT();
  if (state.inT == null || state.inT >= state.outT) state.inT = Math.max(0, state.outT - rangeLen());
  renderSelection();
};
$('goIn').onclick = () => { if (state.inT != null) seek(state.inT); };
$('starClip').onclick = async () => {
  const cur = state.current; if (!cur || cur.kind !== 'clip') return;
  const r = await api(`/clips/${encodeURIComponent(cur.data.profile)}/${encodeURIComponent(cur.data.file)}/star`, { method: 'POST' });
  cur.data.starred = r.starred; if (r.starred) cur.data.expiresAt = null;
  toast(r.starred ? 'starred: this clip is kept for good' : 'unstarred');
  loadClip(cur.data); refreshList();
};
// export panel: floats over the bottom of the video; the save icon toggles it, Escape or a click outside closes it
const exportPop = $('exportPop');
function showExportPop(on) { exportPop.hidden = !on; $('saveBtn').classList.toggle('on', on); if (on) $('exportLabel').focus(); }
$('saveBtn').onclick = e => { e.stopPropagation(); showExportPop(exportPop.hidden); };
exportPop.onclick = e => e.stopPropagation();
exportPop.addEventListener('keydown', e => { if (e.key === 'Escape') { showExportPop(false); $('video').focus(); } });
document.addEventListener('click', () => { if (!exportPop.hidden) showExportPop(false); });
function playRange() {
  if (state.inT == null || state.outT == null || state.outT <= state.inT) return toast('set in and out points first', true);
  seek(state.inT);
  state.playingRange = true;
}
$('playRange').onclick = playRange;
$('inVal').onchange = e => { state.inT = +e.target.value; renderSelection(); };
$('outVal').onchange = e => { state.outT = +e.target.value; renderSelection(); };
$('exportBtn').onclick = exportRange;
$('batchBtn').onclick = exportBatch;
// markers pane: collapse to a thin strip, remembered across reloads
(() => {
  const layout = document.querySelector('.layout'), btn = $('markersToggle');
  const apply = on => { layout.classList.toggle('markers-collapsed', on); btn.setAttribute('aria-expanded', String(!on)); btn.title = on ? 'expand markers' : 'collapse markers'; };
  const toggle = () => { const on = !layout.classList.contains('markers-collapsed'); localStorage.setItem('markersCollapsed', on ? '1' : '0'); apply(on); };
  btn.onclick = toggle; $('markersTab').onclick = toggle;
  apply(localStorage.getItem('markersCollapsed') === '1');
})();
$('uCpuWrap').onclick = () => { localStorage.setItem('cpuPerCore', localStorage.getItem('cpuPerCore') === '1' ? '0' : '1'); refreshStatus(); };
// on/off switch: off opens a menu of durations, on resumes at once
(() => {
  const menu = $('pauseMenu');
  $('powerBtn').onclick = async e => {
    e.stopPropagation();
    if ($('powerBtn').classList.contains('off')) { try { const r = await api('/resume', { method: 'POST' }); toast(r.message); refreshStatus(); } catch (err) { toast(err.message, true); } return; }
    menu.hidden = !menu.hidden;
  };
  menu.querySelectorAll('[data-pause]').forEach(b => b.onclick = async e => {
    e.stopPropagation(); menu.hidden = true;
    try { const r = await api('/pause', { body: { mode: b.dataset.pause, hours: b.dataset.hours ? +b.dataset.hours : null } }); toast(r.message); refreshStatus(); refreshList(); }
    catch (err) { toast(err.message, true); }
  });
  document.addEventListener('click', () => { menu.hidden = true; });
  menu.onclick = e => e.stopPropagation();
})();
(() => {
  const b = $('clearRam'); let armed = null;
  b.onclick = async () => {
    if (!armed) { armed = setTimeout(() => { armed = null; b.classList.remove('confirm'); b.textContent = 'clear'; }, 3000); b.classList.add('confirm'); b.textContent = 'sure?'; return; }
    clearTimeout(armed); armed = null; b.classList.remove('confirm'); b.textContent = 'clear';
    try { const r = await api('/ram/clear', { method: 'POST' }); toast(`freed ${(r.bytes / 1e9).toFixed(2)} GB of RAM footage`); refreshStatus(); if (state.tab === 'sessions') refreshList(); }
    catch (e) { toast(e.message, true); }
  };
})();
$('markBtn').onclick = async () => { try { const r = await api('/mark', { body: { label: null } }); toast(r.message, !r.ok); } catch (e) { toast(e.message, true); } };
$('video').addEventListener('timeupdate', () => {
  if (!state.current) return;
  const t = fromMedia($('video').currentTime); state.cursorT = t; followPlayhead(t); placeHead(t);
  if (state.playingRange && state.outT != null && t >= state.outT) { $('video').pause(); state.playingRange = false; if (state.inT != null) { state.cursorT = state.outT; placeHead(state.outT); } }
});
// ---------------------------------------------------------------- transport
(() => {
  const v = $('video'), stage = $('stage');
  const nudge = d => seek(state.cursorT + d);
  const toggle = () => { if (!state.current) return; v.paused ? v.play().catch(() => {}) : v.pause(); };
  const jumpMarker = dir => {
    const t = currentT();
    const ts = markersOf().map(m => m.t).filter(x => x != null).sort((a, b) => a - b);
    const next = dir > 0 ? ts.find(x => x > t + 0.3) : [...ts].reverse().find(x => x < t - 0.3);
    if (next == null) return toast(dir > 0 ? 'no later marker' : 'no earlier marker');
    seek(next);
  };
  $('tPlay').onclick = toggle; v.onclick = toggle;
  $('tBack').onclick = () => nudge(-10); $('tFwd').onclick = () => nudge(10);
  $('tPrev').onclick = () => jumpMarker(-1); $('tNext').onclick = () => jumpMarker(1);
  $('tMute').onclick = () => { v.muted = !v.muted; };
  $('tVol').oninput = e => { v.volume = +e.target.value; v.muted = v.volume === 0; };
  $('tFull').onclick = () => { document.fullscreenElement ? document.exitFullscreen() : stage.requestFullscreen().catch(() => {}); };
  v.addEventListener('play', () => stage.classList.add('playing'));
  v.addEventListener('pause', () => stage.classList.remove('playing'));
  v.addEventListener('volumechange', () => { stage.classList.toggle('muted', v.muted || v.volume === 0); if (!v.muted) $('tVol').value = v.volume; });
  let hideTimer;
  stage.addEventListener('mousemove', () => { stage.classList.add('show-ui'); clearTimeout(hideTimer); hideTimer = setTimeout(() => stage.classList.remove('show-ui'), 2000); });
  // Buffer sessions: when the loaded clip runs out, carry on in whichever clip covers the next moment.
  // Markers never stop playback; only a real gap in footage does.
  v.addEventListener('ended', () => {
    const cur = state.current;
    if (!cur || cur.kind !== 'session' || cur.data.mode !== 'buffer') return;
    const t = fromMedia(v.duration);
    const next = state.coverage.find(c => c.file !== state.clipLoaded && t >= c.start - 0.3 && t < c.end - 0.5);
    if (next) { const m = toMedia(Math.max(t, next.start) + 0.05); v.currentTime = m; v.play().catch(() => {}); }
    else toast('end of saved footage');
  });
  // zoom: ctrl+wheel zooms around the cursor, plain wheel pans
  $('timeline').addEventListener('wheel', e => {
    e.preventDefault();
    if (e.ctrlKey || e.metaKey) setZoom(state.zoom * (e.deltaY < 0 ? 1.25 : 0.8), xToT(e.clientX));
    else { state.viewStart += (e.deltaY || e.deltaX) / 100 * viewLen() * 0.1; clampView(); renderTimeline(); }
  }, { passive: false });
  $('zoomIn').onclick = () => setZoom(state.zoom * 1.5, currentT(), true);
  $('zoomOut').onclick = () => setZoom(state.zoom / 1.5, currentT(), true);
  $('zoomFit').onclick = () => setZoom(1);
  window.transport = { nudge, toggle, jumpMarker };
})();
// drag the in/out handles
(() => {
  let drag = null, grabOffset = 0, dragged = false;
  document.querySelectorAll('.tl-sel .handle').forEach(h => h.addEventListener('mousedown', e => { e.stopPropagation(); e.preventDefault(); drag = h.dataset.h; }));
  $('tlSel').addEventListener('mousedown', e => {
    if (e.target.classList.contains('handle')) return;
    e.stopPropagation(); e.preventDefault();
    drag = 'move'; grabOffset = xToT(e.clientX) - state.inT; $('tlSel').classList.add('dragging');
  });
  window.addEventListener('mousemove', e => {
    if (!drag) return;
    dragged = true;
    const t = Math.min(state.total, Math.max(0, xToT(e.clientX)));
    if (drag === 'in') state.inT = Math.min(t, state.outT ?? t);
    else if (drag === 'out') state.outT = Math.max(t, state.inT ?? t);
    else {
      const len = state.outT - state.inT;
      let ni = t - grabOffset; ni = Math.max(0, Math.min(state.total - len, ni));
      state.inT = ni; state.outT = ni + len;
    }
    renderSelection();
  });
  window.addEventListener('mouseup', () => { drag = null; $('tlSel').classList.remove('dragging'); });
  // the click that follows a drag lands on the timeline, not the handle: swallow it
  $('timeline').addEventListener('click', e => { if (dragged) { dragged = false; e.stopImmediatePropagation(); e.preventDefault(); } }, true);
  $('tlSel').addEventListener('click', e => e.stopPropagation());
})();
document.addEventListener('keydown', e => {
  if (e.target.tagName === 'INPUT' || e.target.tagName === 'SELECT') return;
  const v = $('video');
  const { nudge, toggle, jumpMarker } = window.transport;
  switch (e.key) {
    case ' ': e.preventDefault(); toggle(); break;
    case 'ArrowLeft': nudge(e.shiftKey ? -30 : -5); break;
    case 'ArrowRight': nudge(e.shiftKey ? 30 : 5); break;
    case 'ArrowUp': e.preventDefault(); v.volume = Math.min(1, v.volume + 0.05); break;
    case 'ArrowDown': e.preventDefault(); v.volume = Math.max(0, v.volume - 0.05); break;
    case '[': jumpMarker(-1); break; case ']': jumpMarker(1); break;
    case 'f': $('tFull').click(); break;
    case '+': case '=': setZoom(state.zoom * 1.5, currentT(), true); break;
    case '-': setZoom(state.zoom / 1.5, currentT(), true); break;
    case '0': setZoom(1); break;
    case 'j': nudge(-10); break; case 'l': nudge(10); break;
    case ',': nudge(-1 / 60); break; case '.': nudge(1 / 60); break;
    case 'i': $('inBtn').click(); break; case 'o': $('outBtn').click(); break;
    case 'I': $('goIn').click(); break;
    case 'Escape': if (!exportPop.hidden) showExportPop(false); break;
    case 'p': playRange(); break;
    case 'm': $('markBtn').click(); break;
    case 'e': if (exportPop.hidden) showExportPop(true); else exportRange(); break;
  }
});

window.state = state; window.toast = toast;
refreshStatus(); refreshList(); setInterval(refreshStatus, 3000);
