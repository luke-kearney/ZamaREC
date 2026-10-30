// zamarec web UI: sessions + clips list, HLS/MP4 player, timeline with markers, range/marker export.
// Plain JS, no build step. Talks to /api on the daemon.

const COLORS = {
  manual: '#f5c542', save: '#4cc9f0', export: '#4cc9f0', kill: '#ff5c5c', multikill: '#ff2d95', headshot: '#ff8c42',
  death: '#8d99ae', round_win: '#43d17a', round_loss: '#c1121f', mvp: '#c77dff', map_start: '#6c757d', map_end: '#6c757d',
};
const colorOf = (t, data) => (t === 'kill' && data && data.count >= 3) ? COLORS.multikill : (COLORS[t] || '#9aa4b2');
// The UI's small icons, as inline SVG in the same stroke style as the header and transport ones (shapes from
// Feather/Lucide, ISC licence). No icon font or library to load: icon('star') returns the markup.
const ICONS = {
  star: '<polygon points="12 2 15.09 8.26 22 9.27 17 14.14 18.18 21.02 12 17.77 5.82 21.02 7 14.14 2 9.27 8.91 8.26 12 2"/>',
  scissors: '<circle cx="6" cy="6" r="3"/><circle cx="6" cy="18" r="3"/><path d="M20 4L8.12 15.88M14.47 14.48L20 20M8.12 8.12L12 12"/>',
  clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
  x: '<path d="M18 6L6 18M6 6l12 12"/>',
};
const icon = name => `<svg class="ic ic-${name}" viewBox="0 0 24 24" aria-hidden="true">${ICONS[name]}</svg>`;
window.icon = icon;

// " · 4 audio tracks (mix, game, mic, discord)" for a multi-track recording, nothing for an ordinary one.
const tracksNote = t => t && t.length > 1 ? ` · ${t.length} audio tracks (${t.join(', ')})` : '';

// How long until a mark clip expires, short enough for a badge: 3d, 29h, 40m.
const timeLeft = iso => { const m = (new Date(iso) - Date.now()) / 60000; return m <= 0 ? 'now' : m >= 2880 ? Math.floor(m / 1440) + 'd' : m >= 60 ? Math.floor(m / 60) + 'h' : Math.ceil(m) + 'm'; };
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
const fmtDay = iso => new Date(iso).toLocaleDateString(undefined, { day: 'numeric', month: 'long', year: 'numeric' });
const fmtTime = iso => new Date(iso).toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit', hour12: true });
const gb = b => b > 1e9 ? (b / 1e9).toFixed(2) + ' GB' : (b / 1e6).toFixed(0) + ' MB';
async function api(path, opts) {
  const r = await fetch('/api' + path, opts && opts.body ? { method: 'POST', headers: { 'content-type': 'application/json' }, ...opts, body: JSON.stringify(opts.body) } : opts);
  const text = await r.text();
  let data = null; try { data = text ? JSON.parse(text) : null; } catch { data = text; }
  if (!r.ok) throw new Error((data && (data.error || data.message)) || r.statusText);
  return data;
}
let toastTimer;
function toast(msg, err, ms) {
  const t = $('toast'); t.textContent = msg; t.hidden = false; t.classList.toggle('err', !!err);
  clearTimeout(toastTimer); toastTimer = setTimeout(() => t.hidden = true, ms || (err ? 7000 : 3500));
}

// ------------------------------------------------------------------ status
let statusBusy = false;
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
    // when nothing records the pill starts a recording; stopping is in the switch's menu
    state.recording = s.state === 'recording';
    el.classList.toggle('can', !state.recording);
    el.classList.toggle('busy', statusBusy);
    el.title = state.recording ? '' : 'click to start recording: the game that is running, else the desktop';
    state.shareTargets = s.shareTargets || [];
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
  } catch { $('status').className = 'status idle'; $('status').querySelector('.txt').textContent = 'daemon unreachable'; $('status').title = ''; }
}

// ------------------------------------------------------------------ lists
async function refreshList() {
  if (state.tab === 'sessions') state.sessions = await api('/sessions'); else state.clips = await api('/clips');
  renderList();
}
// game banner behind a sidebar card; the daemon caches the image, the page only ever asks the daemon
const noBanner = new Set();
let bannerVersion = 0;
window.bannersChanged = () => { bannerVersion++; noBanner.clear(); banner.checked = new Set(); renderList(); };
function bannerUrl(profile) { return `/api/banners/${encodeURIComponent(profile)}${bannerVersion ? '?v=' + bannerVersion : ''}`; }
function banner(el, profile) {
  if (!profile || noBanner.has(profile)) return false;
  el.classList.add('has-banner'); el.style.setProperty('--banner', `url(${bannerUrl(profile)})`);
  if (!banner.checked) banner.checked = new Set();
  if (!banner.checked.has(profile)) {
    banner.checked.add(profile);
    const probe = new Image();
    probe.onerror = () => { noBanner.add(profile); renderList(); };   // no banner after all: fall back to badges
    probe.src = bannerUrl(profile);
  }
  return true;
}
function renderList() {
  const box = $('list'); box.innerHTML = '';
  select.order = [];
  if (state.tab === 'sessions') {
    let day = null;
    for (const s of state.sessions.filter(s => !state.profile || s.profile === state.profile)) {
      const dayOf = fmtDay(s.startedAt);
      if (dayOf !== day) { day = dayOf; const h = document.createElement('div'); h.className = 'list-head'; h.textContent = dayOf; box.appendChild(h); }
      const d = document.createElement('div');
      d.className = 'item' + (state.current && state.current.kind === 'session' && state.current.id === s.id ? ' active' : '') + (s.footageGone ? ' gone' : '');
      const hasBanner = banner(d, s.profile);   // the banner already says which game, and greyscale says the footage is gone
      d.innerHTML = `<div class="l1"><span>${s.gameName || s.profile}</span><span class="r">${s.live ? '<span class="badge live">LIVE</span>' : ''}${s.unconfirmed ? '<span class="badge new" title="a Steam game without a profile, recorded with the defaults for unconfirmed games. Keep or ignore it on the Games page">new</span>' : ''}${s.ram && !(hasBanner && s.footageGone) ? `<span class="badge ${s.footageGone ? 'gone' : 'ram'}" title="${s.footageGone ? 'RAM footage has been dropped' : 'segments live in RAM'}">${s.footageGone ? 'gone' : 'RAM'}</span>` : ''}<span class="badge time mono" title="${fmtDate(s.startedAt)}">${fmtTime(s.startedAt)}</span></span></div>
        <div class="l2"><span>${s.map || s.mode}</span>${s.durationSeconds != null ? `<span class="mono">${fmt(s.durationSeconds)}</span>` : ''}<span>${s.markerCount} marker${s.markerCount === 1 ? '' : 's'}</span><span>${s.clipCount} clip${s.clipCount === 1 ? '' : 's'}</span></div>`;
      d.onclick = () => { if (window.closePages) window.closePages(); loadSession(s.id); };
      selectable(d, s.id, !s.live);
      box.appendChild(d);
    }
    if (!box.children.length) box.innerHTML = '<div class="empty">no sessions yet</div>';
  } else {
    for (const c of state.clips.filter(c => !state.profile || c.profile === state.profile)) {
      const d = document.createElement('div');
      d.className = 'item' + (state.current && state.current.kind === 'clip' && state.current.id === c.profile + '/' + c.file ? ' active' : '');
      const hasBanner = banner(d, c.profile);
      const exp = c.expiresAt ? `<span class="badge exp" title="a mark clip: deleted ${fmtDate(c.expiresAt)} unless you star it">${icon('clock')}${timeLeft(c.expiresAt)}</span>` : '';
      d.innerHTML = `<div class="l1"><span>${c.label || c.file.replace('.mp4', '')}</span><span class="r">${c.starred ? `<span class="badge star" title="starred: never deleted">${icon('star')}</span>` : ''}${exp}${hasBanner ? '' : `<span class="badge">${c.profile}</span>`}</span></div>
        <div class="l2"><span>${fmtDate(c.recordedAt || c.createdAt)}</span><span class="mono">${fmt(c.durationSeconds)}</span><span>${gb(c.bytes)}</span><span>${c.source}</span></div>`;
      d.onclick = () => { if (window.closePages) window.closePages(); loadClip(c); };
      selectable(d, c.profile + '/' + c.file, true);
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
// buffer sessions, and timeline sessions whose RAM footage is gone, play through their clips
function clipPlayback(cur = state.current) { return !!cur && cur.kind === 'session' && (cur.data.mode === 'buffer' || !!cur.data.footageGone); }
function coverageAt(t) { return state.coverage.find(c => t >= c.start && t <= c.end); }
function toMedia(t) {
  const cur = state.current;
  if (cur.kind !== 'session') return t;
  if (clipPlayback(cur)) {
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
  if (clipPlayback(cur)) { const c = state.coverage.find(c => c.file === state.clipLoaded); return c ? c.start + m : m; }
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
  if (m == null) {
    if (clipPlayback()) { $('video').pause(); return playerMessage(holeMessage()); }
    return toast('that moment is no longer in the timeline window', true);
  }
  playerMessage(null);
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
  // nothing to play: say so where the video would be instead of a black box and a playback error
  const noFootage = data.footageGone || (data.mode === 'timeline' && !data.live && !data.segments.some(s => s.available)) || (data.mode === 'buffer' && !data.live && !state.coverage.length);
  if (first) playerMessage(!noFootage ? null
    : data.footageGone ? ['Footage is gone', `This session lived in RAM and its footage has been dropped. ${data.markers.length} marker${data.markers.length === 1 ? '' : 's'} kept.` + (state.coverage.length ? ` The ${state.coverage.length} saved clip${state.coverage.length === 1 ? '' : 's'} still play: click a highlighted band on the timeline or a marker with a clip.` : '')]
    : data.mode === 'buffer' ? ['No footage was saved', 'Buffer sessions write nothing unless you mark or save.']
    : ['No footage on disk', 'Every segment of this session has been removed by retention.']);
  if (first) {
    destroyPlayer(); state.inT = state.outT = null; state.clipLoaded = null; state.zoom = 1; state.viewStart = 0; state.cursorT = 0;
    if (data.mode === 'timeline' && !data.footageGone && !noFootage) attachHls(`/api/sessions/${id}/playlist.m3u8`);
    if (data.live) state.pollTimer = setInterval(() => loadSession(id), 5000);
  } else if (data.live && data.mode === 'timeline') {
    const t = currentT();
    if (t < data.windowStart && toMedia(data.windowStart + 1) != null) seek(data.windowStart + 1);
  }
  $('title').textContent = `${data.profile} · ${fmtDate(data.startedAt)}` + (data.map ? ` · ${data.map}` : '');
  const covered = state.coverage.reduce((a, c) => a + (c.end - c.start), 0);
  titleButtons();
  $('subtitle').textContent = data.mode === 'timeline'
    ? `${fmt(data.recordedSeconds)} recorded · window ${fmt(data.windowStart)} – ${fmt(data.windowEnd)}` + (data.ram ? ' · RAM' : '') + (data.footageGone ? ' · footage dropped, markers and clips kept' : '') + (data.live ? ' · live' : '')
    : `buffer mode · ${data.markers.length} marks · ${state.coverage.length} clips covering ${fmt(covered)}` + (data.live ? ' · live, nothing is written until a mark or save' : '');
  $('subtitle').textContent += tracksNote(data.audioTracks);
  state.total = data.mode === 'timeline'
    ? Math.max(data.recordedSeconds, data.windowEnd)
    : Math.max(1, data.elapsedSeconds || 0, ...data.markers.map(m => m.tVideo || 0), ...state.coverage.map(c => c.end));
  showViewer(); renderTimeline(); renderMarkers(); renderList();
}
function loadClip(c) {
  destroyPlayer(); playerMessage(null); state.inT = state.outT = null; state.zoom = 1; state.viewStart = 0; state.cursorT = 0;
  state.current = { kind: 'clip', id: c.profile + '/' + c.file, data: c };
  $('video').src = `/api/clips/${encodeURIComponent(c.profile)}/${encodeURIComponent(c.file)}`;
  $('title').textContent = c.label || c.file;
  const sb = $('starClip'); sb.hidden = false; sb.classList.toggle('on', !!c.starred); sb.querySelector('span').textContent = c.starred ? 'Starred' : 'Star';
  titleButtons();
  $('subtitle').textContent = `${c.profile} · ${c.source} · ${fmtDate(c.recordedAt || c.createdAt)} · ${fmt(c.durationSeconds)} · ${gb(c.bytes)}` + (c.expiresAt ? ` · expires ${fmtDate(c.expiresAt)}` : '') + tracksNote(c.audioTracks);
  state.total = c.durationSeconds || 1;
  showViewer(); renderTimeline(); renderMarkers(); renderList();
}
function holeMessage() { const d = state.current.data; return d.footageGone ? ['Footage is gone', 'No clip covers this moment. The highlighted bands on the timeline are the clips that were saved.'] : ['No footage here', 'Nothing was saved for this moment. The highlighted bands on the timeline are the saved clips.']; }
function playerMessage(m) { const box = $('playerMsg'); box.hidden = !m; if (m) { $('playerMsgTitle').textContent = m[0]; $('playerMsgText').textContent = m[1]; } }
function showViewer() {
  $('tlTip').style.display = 'none'; hoverThumb(null); if ((window.settingsOpen && window.settingsOpen()) || (window.pageOpen && window.pageOpen())) return; $('empty').hidden = true; $('viewer').hidden = false; }

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
  clampView(); renderTimeline();
}
function followPlayhead(t) {
  if (state.zoom === 1) return;
  if (t < state.viewStart || t > state.viewStart + viewLen()) { state.viewStart = t - viewLen() * 0.15; clampView(); renderTimeline(); }
}
function renderTimeline() {
  const cur = state.current; if (!cur) return;
  $('zoomLbl').textContent = state.zoom === 1 ? 'fit' : state.zoom.toFixed(state.zoom < 4 ? 1 : 0) + '×';
  const tl = $('timeline');
  if (cur.kind === 'session' && cur.data.mode === 'timeline' && !cur.data.footageGone) {
    $('tlPurged').style.width = pct(cur.data.windowStart);
    $('tlWindow').style.left = pct(cur.data.windowStart); $('tlWindow').style.width = pct(cur.data.windowEnd - cur.data.windowStart);
  } else if (clipPlayback(cur)) {
    // No continuous footage: hatch everything, then draw a band per clip.
    $('tlPurged').style.width = '100%'; $('tlWindow').style.left = '0'; $('tlWindow').style.width = '0';
  } else { $('tlPurged').style.width = '0'; $('tlWindow').style.left = '0'; $('tlWindow').style.width = '100%'; }
  const box = $('tlMarkers'); box.innerHTML = '';
  if (clipPlayback(cur)) {
    for (const c of state.coverage) {
      const b = document.createElement('div'); b.className = 'tl-cov'; b.style.left = pct(c.start); b.style.width = (c.end - c.start) / viewLen() * 100 + '%';
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
  tl.onmousemove = e => { const r = tl.getBoundingClientRect(); const t = xToT(e.clientX); const tip = $('tlTip'); tip.style.display = 'block'; tip.style.left = Math.min(e.clientX - r.left + 8, r.width - 70) + 'px'; tip.textContent = fmt(t); hoverThumb(t, e.clientX - r.left, r.width); };
  tl.onmouseleave = () => { $('tlTip').style.display = 'none'; hoverThumb(null); };
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
  const isBuffer = clipPlayback(cur);
  const isTimeline = cur.kind === 'session' && cur.data.mode === 'timeline' && !isBuffer;
  for (const m of markersOf().slice().sort((a, b) => (a.t ?? 0) - (b.t ?? 0))) {
    if (state.typeFilter && m.type !== state.typeFilter) continue;
    const gone = (isTimeline && m.t != null && m.t < cur.data.windowStart) || (isBuffer && m.t != null && !coverageAt(m.t));
    const d = document.createElement('div'); d.className = 'mk' + (gone ? ' gone' : '');
    d.innerHTML = `<div class="bar" style="background:${colorOf(m.type, m.data)}"></div>
      <div><div class="name">${markerName(m)}</div><div class="t">${fmt(m.t)}${gone ? (isBuffer ? (cur.data.live && !m.clip ? ' · clip pending' : ' · no footage') : ' · purged') : ''}${m.clip ? ' · ' + m.clip : ''}</div></div>
      <div class="acts">${cur.kind === 'session' ? `<button title="star" class="${m.starred ? 'on' : ''}" data-star>${icon('star')}</button>` : ''}${(isTimeline || isBuffer) && !gone ? `<button title="clip around this marker" data-clip>${icon('scissors')}</button>` : ''}</div>`;
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

// ------------------------------------------------------------------ hover preview
// A thumbnail of the moment under the cursor, above the timeline. The daemon makes one per whole second and only
// on request, so the cursor has to rest for a moment before a new one is asked for; one already fetched shows
// at once. One request at a time: where the cursor ends up is what gets loaded next.
const thumbs = { dwellMs: 64, cache: new Map(), timer: null, busy: false, queued: null, want: null, shown: null, x: 0 };
function thumbUrl(t) {
  const cur = state.current; if (!cur || t < 0 || t > state.total) return null;
  const enc = encodeURIComponent;
  if (cur.kind === 'clip') return `/api/clips/${enc(cur.data.profile)}/${enc(cur.data.file)}/thumb?t=${Math.floor(t)}`;
  if (clipPlayback(cur)) { const c = coverageAt(t); return c ? `/api/clips/${enc(cur.data.profile)}/${enc(c.file)}/thumb?t=${Math.max(0, Math.floor(t - c.start))}` : null; }
  if (cur.data.mode !== 'timeline') return null;
  return cur.data.segments.some(s => s.available && t >= s.start && t < s.start + s.duration) ? `/api/sessions/${enc(cur.id)}/thumb?t=${Math.floor(t)}` : null;
}
// Shows what is known for the spot under the cursor: its picture if fetched, else the last picture while the cursor moves.
function drawThumb() {
  const box = $('tlThumb'), img = box.querySelector('img'), url = thumbs.want;
  const have = url && thumbs.cache.get(url);
  if (have && thumbs.shown !== url) { img.src = have; thumbs.shown = url; }
  if (!url || thumbs.cache.get(url) === '' || !thumbs.shown) { box.hidden = true; if (!url) thumbs.shown = null; return; }
  box.hidden = false;
  const w = box.offsetWidth, wrap = box.parentElement.clientWidth;
  box.style.left = Math.max(0, Math.min($('timeline').offsetLeft + thumbs.x - w / 2, wrap - w)) + 'px';
}
async function loadThumb(url) {
  if (thumbs.busy) { thumbs.queued = url; return; }
  thumbs.busy = true;
  try {
    const r = await fetch(url);
    thumbs.cache.set(url, r.ok ? URL.createObjectURL(await r.blob()) : '');   // '' = nothing to show there
    if (thumbs.cache.size > 400) { const old = thumbs.cache.keys().next().value; URL.revokeObjectURL(thumbs.cache.get(old)); thumbs.cache.delete(old); }
  } catch { /* the daemon went away: the next rest tries again */ }
  thumbs.busy = false;
  drawThumb();
  const next = thumbs.queued; thumbs.queued = null;
  if (next && next === thumbs.want && !thumbs.cache.has(next)) loadThumb(next);
}
function hoverThumb(t, x) {
  clearTimeout(thumbs.timer);
  const url = t == null ? null : thumbUrl(t);
  thumbs.want = url; thumbs.x = x || 0;
  drawThumb();
  if (url && !thumbs.cache.has(url)) thumbs.timer = setTimeout(() => loadThumb(url), thumbs.dwellMs);
}

// ------------------------------------------------------------------ export
async function exportRange() {
  const cur = state.current; if (!cur) return;
  if (state.inT == null || state.outT == null || state.outT <= state.inT) return toast('set in and out points first', true);
  const maxMb = +$('exportSize').value || null;
  let body = { start: state.inT, end: state.outT, label: $('exportLabel').value || null, exact: $('exact').checked, mix: exportMix(), maxMb };
  let target = cur.kind === 'session' ? `/sessions/${cur.id}/export` : `/clips/${cur.id}/export`;
  if (clipPlayback(cur)) {
    const c = coverageAt(state.inT);
    if (!c || state.outT > c.end + 0.5) return toast('the range must lie inside one saved clip', true);
    target = `/clips/${encodeURIComponent(cur.data.profile)}/${encodeURIComponent(c.file)}/export`;
    body = { ...body, start: state.inT - c.start, end: Math.min(state.outT, c.end) - c.start };
  }
  // The panel closes at once; the range and label are cleared once the clip exists. A failed export keeps
  // them so it can be retried. The playhead is never moved.
  const inT = state.inT, outT = state.outT, label = $('exportLabel').value;
  showExportPop(false);
  toast(maxMb ? `re-encoding to fit under ${maxMb} MB…` : 'exporting…', false, maxMb ? 120000 : 0);
  try {
    const r = await api(target, { body });
    $('exportSize').value = '';   // a size is a one-off: the next export is an ordinary copy again
    if (state.current && state.current.kind === cur.kind && state.current.id === cur.id && state.inT === inT && state.outT === outT) { state.inT = state.outT = null; renderSelection(); }
    if ($('exportLabel').value === label) $('exportLabel').value = '';
    toast(`export complete${body.mix ? ' with your mix' : ''}: ${fmt(r.duration)}${maxMb && r.bytes ? ', ' + (r.bytes / 1e6).toFixed(1) + ' MB' : ''} → ${r.file}`); if (state.tab === 'clips') refreshList();
  } catch (e) { toast('export failed: ' + e.message, true); }
}
// The mixer's levels, when it is on and the "mixer levels" box is ticked: the export's mix is remade from them.
function exportMix() { return !$('exportMixWrap').hidden && $('exportMix').checked && window.mixerLevels ? window.mixerLevels() : null; }
async function exportAround(m) {
  const cur = state.current;
  const before = +$('before').value || 0, after = +$('after').value || 0;
  if (clipPlayback(cur)) {
    const c = coverageAt(m.t); if (!c) return toast('no footage for that marker', true);
    state.inT = Math.max(c.start, m.t - before); state.outT = Math.min(c.end, m.t + after); renderSelection();
    return exportRange();
  }
  const body = { markerId: m.id, before, after, label: $('exportLabel').value || null, exact: $('exact').checked, mix: exportMix() };
  toast('exporting…');
  try { const r = await api(`/sessions/${cur.id}/export-around`, { body }); toast(`exported ${fmt(r.duration)} → ${r.file}`); }
  catch (e) { toast('export failed: ' + e.message, true); }
}
async function exportBatch() {
  const cur = state.current; if (!cur || cur.kind !== 'session') return;
  const type = $('batchType').value; if (!type) return;
  const body = { type, before: +$('before').value || 0, after: +$('after').value || 0, exact: $('exact').checked, mix: exportMix() };
  toast(`exporting every "${type}" marker…`);
  try { const r = await api(`/sessions/${cur.id}/export-batch`, { body }); toast(`${r.filter(x => x.ok).length}/${r.length} clips exported`); }
  catch (e) { toast('batch failed: ' + e.message, true); }
}

// ------------------------------------------------------------------ wiring
document.querySelectorAll('.tab').forEach(b => b.onclick = () => { document.querySelectorAll('.tab').forEach(x => x.classList.toggle('active', x === b)); state.tab = b.dataset.tab; $('asideTabLabel').textContent = b.textContent; select.keys.clear(); select.last = null; selectBar(); refreshList(); });
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
// delete a clip: first click arms the button for a few seconds, second click deletes
let deleteTimer;
function disarmDelete() { clearTimeout(deleteTimer); const b = $('deleteClip'); b.classList.remove('arm'); b.querySelector('span').textContent = 'Delete'; }
$('deleteClip').onclick = async e => {
  e.stopPropagation();
  const cur = state.current; if (!cur) return;
  const clip = cur.kind === 'clip';
  const b = $('deleteClip');
  if (!b.classList.contains('arm')) { b.classList.add('arm'); b.querySelector('span').textContent = clip ? 'Click again to delete' : 'Click again: deletes the session, keeps its clips'; deleteTimer = setTimeout(disarmDelete, 4000); return; }
  disarmDelete();
  try { await api(clip ? `/clips/${encodeURIComponent(cur.data.profile)}/${encodeURIComponent(cur.data.file)}` : `/sessions/${encodeURIComponent(cur.id)}`, { method: 'DELETE' }); }
  catch (err) { return toast('not deleted: ' + err.message, true); }
  toast(clip ? `deleted ${cur.data.label || cur.data.file}` : 'session deleted; its clips are kept');
  closeViewer();
  refreshList();
};
function closeViewer() {
  destroyPlayer(); playerMessage(null); state.current = null; state.inT = state.outT = null;
  $('viewer').hidden = true; $('empty').hidden = false; $('markerList').innerHTML = ''; $('typeChips').innerHTML = ''; $('markersCount').textContent = '';
}

// ------------------------------------------------------------------ the clip in the viewer: rename, share, show in folder
// Which title-bar buttons apply: everything for a clip, only Delete for a session that has ended.
function titleButtons() {
  const cur = state.current, clip = !!cur && cur.kind === 'clip';
  for (const id of ['renameClip', 'revealClip', 'shareWrap']) $(id).hidden = !clip;
  if (!clip) $('starClip').hidden = true;
  $('deleteClip').hidden = !(clip || (cur && cur.kind === 'session' && !cur.data.live));
  $('deleteClip').title = clip ? 'delete this clip' : 'delete this session: its markers and any footage still in RAM. Its clips are kept';
  disarmDelete(); $('shareMenu').hidden = true;
}
const clipPath = c => `/clips/${encodeURIComponent(c.profile)}/${encodeURIComponent(c.file)}`;

// Rename edits the label in place; the file keeps its name, since sessions and markers refer to it.
$('renameClip').onclick = () => {
  const cur = state.current; if (!cur || cur.kind !== 'clip' || $('title').querySelector('input')) return;
  const inp = document.createElement('input'); inp.className = 'text title-edit'; inp.value = cur.data.label || ''; inp.placeholder = cur.data.file;
  $('title').textContent = ''; $('title').appendChild(inp); inp.focus(); inp.select();
  let finished = false;
  const finish = async save => {
    if (finished) return; finished = true;
    const label = inp.value.trim();
    if (save && label !== (cur.data.label || '')) {
      try { const r = await api(clipPath(cur.data), { method: 'PATCH', body: { label } }); cur.data.label = r.label; toast(r.label ? `renamed to "${r.label}"` : 'name cleared'); refreshList(); }
      catch (e) { toast('not renamed: ' + e.message, true); }
    }
    if (state.current === cur) $('title').textContent = cur.data.label || cur.data.file;
  };
  inp.onkeydown = e => { e.stopPropagation(); if (e.key === 'Enter') finish(true); else if (e.key === 'Escape') finish(false); };
  inp.onblur = () => finish(true);
};
$('revealClip').onclick = async () => {
  const cur = state.current; if (!cur || cur.kind !== 'clip') return;
  try { await api(clipPath(cur.data) + '/reveal', { method: 'POST' }); } catch (e) { toast('could not open the folder: ' + e.message, true); }
};
$('shareClip').onclick = e => {
  e.stopPropagation();
  const menu = $('shareMenu'); menu.hidden = !menu.hidden;
  // one entry per share target: Discord, then the modules from [[share.targets]]
  const box = $('shareTargets'); box.textContent = '';
  for (const t of state.shareTargets || []) {
    const b = document.createElement('button'), sub = document.createElement('span');
    b.textContent = 'Send to ' + t.name; b.disabled = !t.ready;
    sub.className = 'sub'; sub.textContent = !t.ready ? t.problem : t.maxMb > 0 ? `re-encoded first if over ${t.maxMb} MB` : 'sent as it is';
    b.append(sub); b.onclick = () => shareTo(t); box.append(b);
  }
};
$('shareMenu').onclick = e => e.stopPropagation();
document.addEventListener('click', () => { $('shareMenu').hidden = true; });
// A share runs in the daemon; the toast follows it and a click on the toast cancels it.
let shareJob = null;
async function shareTo(target) {
  const cur = state.current; if (!cur || cur.kind !== 'clip' || !target.ready) return;
  $('shareMenu').hidden = true;
  if (shareJob) return toast('still sending the last clip; click the message to cancel it', true);
  const t = $('toast');
  try {
    let job = shareJob = await api(`${clipPath(cur.data)}/share/${encodeURIComponent(target.id)}`, { body: { message: cur.data.label || null } });
    t.onclick = () => api('/share/jobs/' + job.id, { method: 'DELETE' }).catch(() => {});
    t.classList.add('clickable');
    while (job.stage === 'encoding' || job.stage === 'sending') {
      const pct = job.progress != null ? ` ${Math.round(job.progress * 100)}%` : '';
      toast((job.stage === 'encoding' ? `re-encoding to fit under ${target.maxMb} MB…` : `sending to ${target.name}…${pct}` + (job.note ? ' · ' + job.note : '')) + ' (click to cancel)', false, 300000);
      await new Promise(r => setTimeout(r, 500));
      job = await api('/share/jobs/' + job.id);
    }
    if (job.stage === 'done') {
      let copied = false;
      if (job.url) try { await navigator.clipboard.writeText(job.url); copied = true; } catch { }
      toast(`sent to ${target.name}: ${(job.bytes / 1e6).toFixed(1)} MB` + (job.reencoded ? ', re-encoded to fit' : '') + (copied ? ' · link copied' : job.url ? ' · ' + job.url : ''), false, copied || !job.url ? 0 : 15000);
    }
    else if (job.stage === 'cancelled') toast('not sent: cancelled');
    else toast('not sent: ' + job.error, true);
    if (job.reencoded && state.tab === 'clips') refreshList();
  } catch (e) { toast('not sent: ' + e.message, true); }
  finally { shareJob = null; t.onclick = null; t.classList.remove('clickable'); }
}
document.querySelectorAll('[data-share-mb]').forEach(b => b.onclick = async () => {
  const cur = state.current; if (!cur || cur.kind !== 'clip') return;
  const mb = +b.dataset.shareMb; $('shareMenu').hidden = true;
  if (cur.data.bytes <= mb * 1e6) return toast(`this clip is already under ${mb} MB (${(cur.data.bytes / 1e6).toFixed(1)} MB)`);
  toast(`re-encoding to fit under ${mb} MB…`, false, 300000);
  try {
    const r = await api(clipPath(cur.data) + '/export', { body: { start: 0, end: cur.data.durationSeconds || state.total, label: `${cur.data.label || cur.data.file.replace('.mp4', '')} ${mb}mb`, exact: false, maxMb: mb } });
    toast(`copy made: ${(r.bytes / 1e6).toFixed(1)} MB → ${r.file}`); if (state.tab === 'clips') refreshList();
  } catch (e) { toast('no copy made: ' + e.message, true); }
});

// ------------------------------------------------------------------ selecting several in the sidebar
// Select mode turns a click on a card into a tick. Shift+click ticks everything between it and the last one.
const select = { on: false, keys: new Set(), order: [], last: null };
function selectable(el, key, allowed) {
  select.order.push(allowed ? key : null);
  if (!select.on) return;
  el.classList.add('selecting'); el.classList.toggle('picked', select.keys.has(key)); el.classList.toggle('unpickable', !allowed);
  const box = document.createElement('span'); box.className = 'pick'; el.prepend(box);
  el.onclick = e => {
    if (!allowed) return toast('the session being recorded cannot be selected');
    const keys = select.order.filter(Boolean);
    if (e.shiftKey && select.last && keys.includes(select.last)) {
      const [a, b] = [keys.indexOf(select.last), keys.indexOf(key)].sort((x, y) => x - y);
      for (const k of keys.slice(a, b + 1)) select.keys.add(k);
    } else { select.keys.has(key) ? select.keys.delete(key) : select.keys.add(key); }
    select.last = key; renderList(); selectBar();
  };
}
let selDeleteTimer;
function selectBar() {
  const clips = state.tab === 'clips', n = select.keys.size;
  $('selectBar').hidden = !select.on; $('selectBtn').classList.toggle('on', select.on);
  $('selectCount').textContent = n ? `${n} selected` : 'pick some';
  $('selStar').hidden = $('selUnstar').hidden = !clips;
  for (const id of ['selStar', 'selUnstar', 'selDelete']) $(id).disabled = !n;
  clearTimeout(selDeleteTimer); $('selDelete').classList.remove('arm'); $('selDelete').querySelector('span').textContent = 'Delete';
}
function selectMode(on) { select.on = on; select.keys.clear(); select.last = null; renderList(); selectBar(); }
$('selectBtn').onclick = () => selectMode(!select.on);
$('selDone').onclick = () => selectMode(false);
$('selAll').onclick = () => { const all = select.order.filter(Boolean); const every = all.every(k => select.keys.has(k)); select.keys = new Set(every ? [] : all); renderList(); selectBar(); };
// One request per item, in turn; the list is reloaded once at the end.
async function forSelected(what, call) {
  const keys = [...select.keys]; let done = 0, failed = 0;
  for (const k of keys) { try { await call(k); done++; } catch { failed++; } }
  const open = state.current && keys.includes(state.current.kind === 'clip' ? state.current.id : state.current.id);
  toast(`${done} ${what}` + (failed ? `, ${failed} failed` : ''), failed > 0);
  return { open };
}
const clipKeyPath = k => { const i = k.indexOf('/'); return `/clips/${encodeURIComponent(k.slice(0, i))}/${encodeURIComponent(k.slice(i + 1))}`; };
$('selStar').onclick = async () => { await forSelected('starred', k => api(clipKeyPath(k) + '/star?on=true', { method: 'POST' })); select.keys.clear(); await refreshList(); selectBar(); };
$('selUnstar').onclick = async () => { await forSelected('unstarred', k => api(clipKeyPath(k) + '/star?on=false', { method: 'POST' })); select.keys.clear(); await refreshList(); selectBar(); };
$('selDelete').onclick = async () => {
  const b = $('selDelete'), clips = state.tab === 'clips', n = select.keys.size;
  if (!b.classList.contains('arm')) { b.classList.add('arm'); b.querySelector('span').textContent = `Delete ${n}? Click again`; selDeleteTimer = setTimeout(selectBar, 4000); return; }
  const { open } = await forSelected(clips ? 'clips deleted' : 'sessions deleted (their clips are kept)', k => api(clips ? clipKeyPath(k) : `/sessions/${encodeURIComponent(k)}`, { method: 'DELETE' }));
  if (open) closeViewer();
  select.keys.clear(); await refreshList(); selectBar();
};
$('starClip').onclick = async () => {
  const cur = state.current; if (!cur || cur.kind !== 'clip') return;
  const r = await api(`/clips/${encodeURIComponent(cur.data.profile)}/${encodeURIComponent(cur.data.file)}/star`, { method: 'POST' });
  cur.data.starred = r.starred; if (r.starred) cur.data.expiresAt = null;
  toast(r.starred ? 'starred: this clip is kept for good' : 'unstarred');
  loadClip(cur.data); refreshList();
};
// export panel: floats over the bottom of the video; the save icon toggles it, Escape or a click outside closes it
const exportPop = $('exportPop');
function showExportPop(on) { if (on && window.closeMixer) window.closeMixer(); exportPop.hidden = !on; $('saveBtn').classList.toggle('on', on); if (on) $('exportLabel').focus(); }
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
// side panes collapse to thin strips, remembered across reloads. Hovering the collapsed sidebar
// floats it over the content (a "peek") until the mouse leaves; clicking its strip pins it open.
(() => {
  const layout = document.querySelector('.layout');
  const mkBtn = $('markersToggle'), asBtn = $('asideToggle'), aside = $('aside');
  const apply = (cls, btn, on, what) => {
    layout.classList.toggle(cls, on);
    if (btn === asBtn) { btn.classList.toggle('on', !on); btn.setAttribute('aria-pressed', String(!on)); btn.title = on ? 'pin sidebar open' : 'unpin sidebar: it collapses and shows on hover'; }
    else { btn.setAttribute('aria-expanded', String(!on)); btn.title = (on ? 'expand ' : 'collapse ') + what; }
  };
  const toggle = (cls, btn, key, what) => () => { const on = !layout.classList.contains(cls); localStorage.setItem(key, on ? '1' : '0'); apply(cls, btn, on, what); aside.classList.remove('peek'); };
  const tMarkers = toggle('markers-collapsed', mkBtn, 'markersCollapsed', 'markers');
  const tAside = toggle('aside-collapsed', asBtn, 'asideCollapsed', 'sidebar');
  mkBtn.onclick = tMarkers; $('markersTab').onclick = tMarkers;
  asBtn.onclick = tAside; $('asideTab').onclick = tAside;
  let peekTimer;
  aside.addEventListener('mouseenter', () => { if (!layout.classList.contains('aside-collapsed')) return; peekTimer = setTimeout(() => aside.classList.add('peek'), 120); });
  aside.addEventListener('mouseleave', () => { clearTimeout(peekTimer); aside.classList.remove('peek'); });
  apply('markers-collapsed', mkBtn, localStorage.getItem('markersCollapsed') === '1', 'markers');
  apply('aside-collapsed', asBtn, localStorage.getItem('asideCollapsed') === '1', 'sidebar');
})();
$('uCpuWrap').onclick = () => { localStorage.setItem('cpuPerCore', localStorage.getItem('cpuPerCore') === '1' ? '0' : '1'); refreshStatus(); };
// status pill: click to start (the running game, else the desktop)
(() => {
  const el = $('status'), act = el.querySelector('.act');
  const go = async () => {
    if (statusBusy || !el.classList.contains('can')) return;
    statusBusy = true; el.classList.add('busy'); act.textContent = 'starting…';
    try { const r = await api('/control', { body: { cmd: 'start' } }); toast(r.message, !r.ok); }
    catch (err) { toast(err.message, true); }
    finally { statusBusy = false; act.textContent = 'record'; refreshStatus(); refreshList(); }
  };
  el.onclick = go;
  el.onkeydown = e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); go(); } };
})();
// on/off switch: its menu stops a running session and turns auto recording off for a while;
// switched off with nothing recording, a click turns it back on at once
(() => {
  const menu = $('pauseMenu');
  const resume = async () => { menu.hidden = true; try { const r = await api('/resume', { method: 'POST' }); toast(r.message); refreshStatus(); } catch (err) { toast(err.message, true); } };
  $('powerBtn').onclick = async e => {
    e.stopPropagation();
    const off = $('powerBtn').classList.contains('off');
    if (off && !state.recording) return resume();
    $('stopGroup').hidden = !state.recording; $('offGroup').hidden = off; $('onGroup').hidden = !off;
    menu.hidden = !menu.hidden;
  };
  $('resumeBtn').onclick = e => { e.stopPropagation(); resume(); };
  $('stopBtn').onclick = async e => {
    e.stopPropagation(); menu.hidden = true;
    try { const r = await api('/control', { body: { cmd: 'stop' } }); toast(r.message, !r.ok); refreshStatus(); refreshList(); }
    catch (err) { toast(err.message, true); }
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
  // volume and mute survive reloads
  try { const sv = localStorage.getItem('volume'); if (sv != null) { v.volume = Math.max(0, Math.min(1, +sv)); $('tVol').value = v.volume; } v.muted = localStorage.getItem('muted') === '1'; stage.classList.toggle('muted', v.muted || v.volume === 0); } catch {}
  v.addEventListener('volumechange', () => {
    stage.classList.toggle('muted', v.muted || v.volume === 0); if (!v.muted) $('tVol').value = v.volume;
    try { localStorage.setItem('volume', String(v.volume)); localStorage.setItem('muted', v.muted ? '1' : '0'); } catch {}
  });
  // frame-exact is on unless it was turned off here
  try { if (localStorage.getItem('exact') === '0') $('exact').checked = false; } catch {}
  $('exact').addEventListener('change', () => { try { localStorage.setItem('exact', $('exact').checked ? '1' : '0'); } catch {} });
  let hideTimer;
  stage.addEventListener('mousemove', () => { stage.classList.add('show-ui'); clearTimeout(hideTimer); hideTimer = setTimeout(() => stage.classList.remove('show-ui'), 2000); });
  // Buffer sessions: when the loaded clip runs out, carry on in whichever clip covers the next moment.
  // Markers never stop playback; only a real gap in footage does.
  v.addEventListener('ended', () => {
    const cur = state.current;
    if (!clipPlayback(cur)) return;
    const t = fromMedia(v.duration);
    const next = state.coverage.find(c => c.file !== state.clipLoaded && t >= c.start - 0.3 && t < c.end - 0.5);
    if (next) { const m = toMedia(Math.max(t, next.start) + 0.05); v.currentTime = m; v.play().catch(() => {}); }
    else playerMessage(cur.data.footageGone ? ['Footage is gone', 'The clip ended and nothing was saved after this point.'] : ['End of saved footage', 'Nothing was saved after this point.']);
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

// theme button: auto (follow the OS/browser) → light → dark
{
  const b = $('themeBtn'), order = ['auto', 'light', 'dark'];
  const show = t => { b.dataset.theme = t; b.title = t === 'auto' ? 'theme: follows the system. Click for light' : t === 'light' ? 'theme: light. Click for dark' : 'theme: dark. Click to follow the system'; };
  show(window.zamarecTheme.get());
  b.onclick = () => show(window.zamarecTheme.set(order[(order.indexOf(b.dataset.theme) + 1) % 3]));
}
// header logo: lockup ⇄ bottle
{
  const b = $('brandLogo');
  const show = v => { b.title = v === 'bottle' ? 'ZamaREC. Click for the logo' : 'ZamaREC. Click for the bottle'; };
  show(window.zamarecLogo.get());
  b.onclick = () => show(window.zamarecLogo.set(window.zamarecLogo.get() === 'bottle' ? 'lockup' : 'bottle'));
}
window.state = state; window.toast = toast; window.showExportPop = showExportPop;
refreshStatus(); refreshList(); setInterval(refreshStatus, 3000);
