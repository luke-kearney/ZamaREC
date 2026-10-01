// Stats and Log pages. Both are read-only views over /api/stats and /api/events.
(() => {
  const $ = id => document.getElementById(id);
  const fmtB = b => b >= 1e12 ? (b / 1e12).toFixed(2) + ' TB' : b >= 1e9 ? (b / 1e9).toFixed(2) + ' GB' : b >= 1e6 ? (b / 1e6).toFixed(0) + ' MB' : (b / 1e3).toFixed(0) + ' kB';
  const fmtT = s => { s = Math.max(0, s || 0); const h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60); return h ? `${h}h ${m}m` : `${m}m ${Math.floor(s % 60)}s`; };
  const fmtD = iso => { const d = new Date(iso); return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' }) + ' ' + d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' }); };
  const fmtClock = iso => new Date(iso).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  let current = null, timer = null, logKind = '';

  function show(id) {
    if (window.settingsOpen && window.settingsOpen()) $('cfgClose').click();
    for (const p of ['stats', 'log']) $(p).hidden = p !== id;
    current = id;
    document.querySelector('.layout').classList.toggle('settings-open', !!id);
    $('viewer').hidden = !!id || !window.state.current;
    $('empty').hidden = !!id || !!window.state.current;
    clearInterval(timer); timer = null;
    if (id === 'stats') { loadStats(); timer = setInterval(loadStats, 5000); }
    if (id === 'log') { loadLog(); timer = setInterval(() => { if ($('logFollow').checked) loadLog(); }, 3000); }
  }
  window.pageOpen = () => current;

  async function loadStats() {
    const d = await (await fetch('/api/stats')).json();
    const t = d.totals, o = d.onDisk;
    const tile = (k, v, s, cls = '') => `<div class="tile ${cls}"><div class="k">${k}</div><div class="v">${v}</div>${s ? `<div class="s">${s}</div>` : ''}</div>`;
    $('tiles').innerHTML =
      tile('footage to disk', fmtB(t.diskBytes), 'timeline sessions on disk, lifetime', 'disk') +
      tile('footage to RAM', fmtB(t.ramBytes), 'RAM timelines + buffer churn, never hit the disk', 'ram') +
      tile('clips on disk', fmtB(t.clipBytes), `${t.clips} clips · ${t.saves} saves · ${t.exports} exports`, 'clips') +
      tile('sessions', t.sessions, `${fmtT(t.secondsRecorded)} recorded · ${t.marks} marks`) +
      tile('sessions folder now', fmtB(o.sessionsBytes), 'segments and metadata currently kept') +
      tile('clips folder now', fmtB(o.clipsBytes)) +
      tile('RAM footage now', fmtB(o.ramDirBytes), 'in tmpfs') +
      tile('disk free', fmtB(o.freeBytes));
    const rows = d.sessions.map(s => `<tr class="${s.live ? 'live' : ''}">
      <td>${fmtD(s.startedAt)}</td><td>${s.profile}</td><td>${s.mode}</td>
      <td><span class="badge ${s.storage === 'ram' ? 'ram' : ''}">${s.storage}</span>${s.footageGone ? ' <span class="badge gone">gone</span>' : ''}</td>
      <td class="n">${fmtT(s.durationSeconds)}</td>
      <td class="n">${fmtB(s.recordedBytes)}${s.estimated ? '<span title="estimated from elapsed time x bitrate">≈</span>' : ''}</td>
      <td class="n">${s.clipCount ? fmtB(s.clipBytes) + ' <span class="muted">(' + s.clipCount + ')</span>' : '–'}</td>
      <td class="n">${s.markerCount}</td></tr>`).join('');
    $('statsTable').innerHTML = `<thead><tr><th>started</th><th>profile</th><th>mode</th><th>storage</th><th class="n">length</th><th class="n">footage</th><th class="n">clips</th><th class="n">marks</th></tr></thead><tbody>${rows || '<tr><td colspan="8" class="muted">no sessions yet</td></tr>'}</tbody>`;
    $('statsNote').textContent = 'footage = bytes the recorder produced; ≈ means estimated for a buffer session';
  }

  const KINDS = ['session', 'mark', 'clip', 'export', 'purge', 'config', 'daemon', 'error'];
  function renderChips() {
    $('logChips').innerHTML = ['', ...KINDS].map(k => `<button class="chip ${logKind === k ? 'on' : ''}" data-k="${k}">${k || 'all'}</button>`).join('');
    $('logChips').querySelectorAll('.chip').forEach(b => b.onclick = () => { logKind = b.dataset.k; renderChips(); loadLog(); });
  }
  async function loadLog() {
    const ev = await (await fetch('/api/events?limit=300' + (logKind ? '&kind=' + logKind : ''))).json();
    $('logList').innerHTML = ev.slice().reverse().map(e => `<div class="ev k-${e.kind} l-${e.level}">
      <span class="t" title="${e.ts}">${fmtClock(e.ts)}</span><span class="k">${e.kind}</span>
      <span class="m">${esc(e.message)}${e.profile ? `<small>${e.profile}</small>` : ''}${e.file ? `<small>${e.file}</small>` : ''}</span></div>`).join('') || '<div class="muted" style="padding:10px">nothing logged yet</div>';
  }
  const esc = s => String(s).replace(/[&<>]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]));

  $('statsBtn').onclick = () => show(current === 'stats' ? null : 'stats');
  $('logBtn').onclick = () => show(current === 'log' ? null : 'log');
  $('statsClose').onclick = () => show(null);
  $('logClose').onclick = () => show(null);
  renderChips();
  window.closePages = () => { if (current) show(null); };
})();
