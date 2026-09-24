// Settings view: edits the daemon config through /api/config. Form → structured save; raw TOML → text save.
(() => {
  const $ = id => document.getElementById(id);
  let cfg = null, meta = null, open = false;

  const num = (v) => v === '' || v == null ? null : +v;
  const field = (label, html, wide) => `<label class="field${wide ? ' wide' : ''}"><span>${label}</span>${html}</label>`;
  const inp = (path, val, type = 'text', extra = '') => `<input type="${type}" data-path="${path}" value="${val ?? ''}" ${extra}>`;
  const sel = (path, val, opts) => `<select data-path="${path}">${opts.map(o => `<option value="${o.v ?? o}" ${(o.v ?? o) === val ? 'selected' : ''}>${o.l ?? o}</option>`).join('')}</select>`;
  const chk = (path, val) => `<input type="checkbox" data-path="${path}" ${val ? 'checked' : ''}>`;

  function render() {
    const c = cfg;
    const monitors = meta.monitors.map(m => ({ v: m.id, l: `${m.id} (${m.description})` }));
    if (!monitors.some(m => m.v === c.capture.monitor)) monitors.unshift({ v: c.capture.monitor, l: c.capture.monitor });
    if (!monitors.some(m => m.v === 'screen')) monitors.push({ v: 'screen', l: 'screen (first monitor)' });
    const audioKnown = new Set(meta.audioDevices.map(a => a.id));
    const audioExtra = c.capture.audio.filter(a => !audioKnown.has(a));

    let h = `<div class="card"><h3>Capture</h3><div class="help">Applies to every profile. The recorder always uses the GPU encoder.</div><div class="fields">
      ${field('monitor', sel('capture.monitor', c.capture.monitor, monitors))}
      ${field('fps', inp('capture.fps', c.capture.fps, 'number', 'min="10" max="240"'))}
      ${field('codec', sel('capture.codec', c.capture.codec, meta.codecs.map(x => ({ v: x, l: x + (x === 'h264' ? ' (plays in every browser)' : '') }))))}
      ${field('bitrate kbps', inp('capture.bitrateKbps', c.capture.bitrateKbps, 'number', 'min="1000" step="500"'))}
      ${field('keyframe seconds', inp('capture.keyframeSeconds', c.capture.keyframeSeconds, 'number', 'min="0.5" step="0.5"'))}
      ${field('show cursor', chk('capture.cursor', c.capture.cursor))}
      <div class="field wide"><span>audio sources</span><div class="audio-list">
        ${meta.audioDevices.map(a => `<label><input type="checkbox" data-audio="${a.id}" ${c.capture.audio.includes(a.id) ? 'checked' : ''}> ${a.description} <code>${a.id}</code></label>`).join('')}
        <label class="other"><span class="muted">other, comma separated (e.g. app:cs2)</span><input type="text" id="audioExtra" value="${audioExtra.join(', ')}"></label>
      </div></div>
    </div></div>`;

    h += `<div class="card"><h3>Defaults</h3><div class="help">Used by every profile unless the profile overrides the value.</div><div class="fields">
      ${field('timeline window minutes (0 = keep all)', inp('timeline.windowMinutes', c.timeline.windowMinutes, 'number', 'min="0"'))}
      ${field('timeline segment seconds', inp('timeline.segmentSeconds', c.timeline.segmentSeconds, 'number', 'min="2"'))}
      ${field('timeline storage', sel('timeline.storage', c.timeline.storage, [{ v: 'disk', l: 'disk' }, { v: 'ram', l: 'ram (tmpfs, needs window × bitrate of RAM)' }]))}
      ${field('RAM footage kept for hours', inp('timeline.ramKeepHours', c.timeline.ramKeepHours, 'number', 'min="0" step="0.5"'))}
      ${field('RAM directory (tmpfs)', inp('timeline.ramDir', c.timeline.ramDir))}
      ${field('buffer minutes', inp('buffer.minutes', c.buffer.minutes, 'number', 'min="1"'))}
      ${field('buffer storage', sel('buffer.storage', c.buffer.storage, ['ram', 'disk']))}
      ${field('save seconds (kairec save)', inp('clips.saveSeconds', c.clips.saveSeconds, 'number', 'min="1"'))}
      ${field('mark: seconds before', inp('clips.markBeforeSeconds', c.clips.markBeforeSeconds, 'number', 'min="0"'))}
      ${field('mark: seconds after', inp('clips.markAfterSeconds', c.clips.markAfterSeconds, 'number', 'min="0"'))}
      ${field('mark clips expire after hours (0 = never)', inp('clips.markClipExpireHours', c.clips.markClipExpireHours, 'number', 'min="0"'))}
      ${field('game events that write a clip (comma list: kill:2, mvp, death, round_win, round_loss)', inp('clips.eventClips', (c.clips.eventClips || []).join(', ')), true)}
      ${field('timeline: write a clip around each mark', sel('clips.markSaves', c.clips.markSaves == null ? '' : String(c.clips.markSaves), [{ v: '', l: 'auto (on for RAM timelines)' }, { v: 'true', l: 'on' }, { v: 'false', l: 'off' }]))}
      ${field('retention max GB', inp('retention.maxGb', c.retention.maxGb, 'number', 'min="0" step="1"'))}
      ${field('retention max days', inp('retention.maxDays', c.retention.maxDays, 'number', 'min="0"'))}
      ${field('sound feedback', chk('feedback.sound', c.feedback.sound))}
      ${field('sound volume', inp('feedback.volume', c.feedback.volume, 'number', 'min="0" max="1" step="0.1"'))}
      ${field('storage root', inp('storage.root', c.storage.root))}
      ${field('UI port (restart needed)', inp('daemon.uiPort', c.daemon.uiPort, 'number'))}
      ${field('GSI port (restart needed)', inp('daemon.gsiPort', c.daemon.gsiPort, 'number'))}
      ${field('bind address (restart needed)', inp('daemon.bind', c.daemon.bind))}
    </div></div>`;

    h += `<div class="card"><h3>Profiles <span class="spacer"></span><button class="btn small" id="addProfile">+ Add profile</button></h3>
      <div class="help">A profile is one game. The daemon starts recording when a matching process appears. Blank override fields use the defaults above.</div></div>`;
    c.profiles.forEach((p, i) => {
      const P = `profiles.${i}.`;
      const ov = (o, k) => (o && o[k] != null) ? o[k] : '';
      h += `<div class="card profile"><h3><span class="name">${p.name || 'new profile'}</span><span class="badge">${p.mode}</span><span class="spacer"></span><button class="btn small danger" data-del="${i}">Delete</button></h3>
        <div class="fields">
          ${field('name', inp(P + 'name', p.name))}
          ${field('mode', sel(P + 'mode', p.mode, [{ v: 'timeline', l: 'timeline (rolling window on disk)' }, { v: 'buffer', l: 'buffer (RAM ring buffer)' }]))}
          ${field('events', sel(P + 'events', p.events, [{ v: 'none', l: 'none (manual markers only)' }, { v: 'cs2-gsi', l: 'cs2-gsi (CS2 game state)' }]))}
          ${field('process name regex (comm, max 15 chars)', inp(P + 'match', p.match, 'text', 'placeholder="^cs2$"'))}
          ${field('command line regex (for Java games etc.)', inp(P + 'matchCmdline', p.matchCmdline, 'text', 'placeholder="net\\\\.minecraft|prismlauncher"'))}
          ${field('fps override', inp(P + 'fps', p.fps, 'number'))}
        </div>
        <div class="sub"><div class="lbl">overrides</div><div class="fields">
          ${field('timeline window minutes', inp(P + 'timeline.windowMinutes', ov(p.timeline, 'windowMinutes'), 'number'))}
          ${field('timeline segment seconds', inp(P + 'timeline.segmentSeconds', ov(p.timeline, 'segmentSeconds'), 'number'))}
          ${field('timeline storage', sel(P + 'timeline.storage', ov(p.timeline, 'storage'), [{ v: '', l: '(default)' }, 'disk', 'ram']))}
          ${field('RAM footage kept for hours', inp(P + 'timeline.ramKeepHours', ov(p.timeline, 'ramKeepHours'), 'number'))}
          ${field('buffer minutes', inp(P + 'buffer.minutes', ov(p.buffer, 'minutes'), 'number'))}
          ${field('buffer storage', sel(P + 'buffer.storage', ov(p.buffer, 'storage'), [{ v: '', l: '(default)' }, 'ram', 'disk']))}
          ${field('save seconds', inp(P + 'clips.saveSeconds', ov(p.clips, 'saveSeconds'), 'number'))}
          ${field('mark: seconds before', inp(P + 'clips.markBeforeSeconds', ov(p.clips, 'markBeforeSeconds'), 'number'))}
          ${field('mark: seconds after', inp(P + 'clips.markAfterSeconds', ov(p.clips, 'markAfterSeconds'), 'number'))}
          ${field('mark clips expire hours', inp(P + 'clips.markClipExpireHours', ov(p.clips, 'markClipExpireHours'), 'number'))}
          ${field('clip around each mark', sel(P + 'clips.markSaves', ov(p.clips, 'markSaves') === '' ? '' : String(ov(p.clips, 'markSaves')), [{ v: '', l: '(default)' }, { v: 'true', l: 'on' }, { v: 'false', l: 'off' }]))}
          ${field('retention max GB', inp(P + 'retention.maxGb', ov(p.retention, 'maxGb'), 'number'))}
          ${field('retention max days', inp(P + 'retention.maxDays', ov(p.retention, 'maxDays'), 'number'))}
        </div></div></div>`;
    });
    $('cfgForm').innerHTML = h;
    $('addProfile').onclick = () => { collect(); cfg.profiles.push({ name: '', match: '', matchCmdline: null, mode: 'timeline', events: 'none', fps: null, timeline: null, buffer: null, clips: null, retention: null }); render(); };
    document.querySelectorAll('[data-del]').forEach(b => b.onclick = () => { collect(); cfg.profiles.splice(+b.dataset.del, 1); render(); });
    $('cfgText').value = meta.text; $('cfgPath').textContent = meta.path;
    const n = $('cfgNotice');
    if (meta.pending) { n.hidden = false; n.textContent = 'a saved change is waiting for the current session to end'; }
    else if (meta.sessionRunning) { n.hidden = false; n.textContent = 'a session is running: saves apply when it ends'; }
    else n.hidden = true;
  }

  // Reads the form back into cfg. Blank numbers become null (= use default); empty override groups become null.
  function collect() {
    const setPath = (obj, path, v) => { const ks = path.split('.'); let o = obj; for (let i = 0; i < ks.length - 1; i++) { const k = isNaN(ks[i]) ? ks[i] : +ks[i]; if (o[k] == null) o[k] = isNaN(ks[i + 1]) ? {} : []; o = o[k]; } o[ks[ks.length - 1]] = v; };
    document.querySelectorAll('#cfgForm [data-path]').forEach(el => {
      const path = el.dataset.path;
      let v = el.type === 'checkbox' ? el.checked : el.type === 'number' ? num(el.value) : (el.value === '' ? null : el.value);
      if (path.endsWith('markSaves') && v != null) v = v === 'true';
      if (path.endsWith('eventClips')) v = (v || '').split(',').map(x => x.trim()).filter(Boolean);
      setPath(cfg, path, v);
    });
    const audio = [...document.querySelectorAll('#cfgForm [data-audio]')].filter(x => x.checked).map(x => x.dataset.audio);
    ($('audioExtra').value || '').split(',').map(s => s.trim()).filter(Boolean).forEach(a => audio.push(a));
    cfg.capture.audio = audio;
    for (const p of cfg.profiles) {
      for (const g of ['timeline', 'buffer', 'clips', 'retention']) {
        if (p[g] && Object.values(p[g]).every(v => v == null || v === '')) p[g] = null;
        else if (p[g]) for (const k of Object.keys(p[g])) if (p[g][k] === '') p[g][k] = null;
      }
      if (p.match === '') p.match = null;
      if (p.matchCmdline === '') p.matchCmdline = null;
    }
  }

  async function load() {
    const r = await fetch('/api/config'); meta = await r.json(); cfg = meta.config; render();
  }
  async function saveStructured() {
    collect();
    const r = await fetch('/api/config/structured', { method: 'PUT', headers: { 'content-type': 'application/json' }, body: JSON.stringify(cfg) });
    const d = await r.json();
    if (!r.ok) return window.toast('not saved: ' + d.error, true);
    window.toast(d.result === 'applied' ? 'saved and applied' : 'saved; applies when the current session ends');
    await load();
  }
  async function saveRaw() {
    const r = await fetch('/api/config', { method: 'PUT', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ text: $('cfgText').value }) });
    const d = await r.json();
    if (!r.ok) return window.toast('not saved: ' + d.error, true);
    window.toast(d.result === 'applied' ? 'saved and applied' : 'saved; applies when the current session ends');
    await load();
  }
  function show(on) {
    if (on && window.closePages) window.closePages();
    open = on;
    $('settings').hidden = !on;
    document.querySelector('.layout').classList.toggle('settings-open', on);
    $('viewer').hidden = on || !window.state.current;
    $('empty').hidden = on || !!window.state.current;
    if (on) load();
  }
  $('settingsBtn').onclick = () => show(!open);
  $('cfgClose').onclick = () => show(false);
  $('cfgSave').onclick = saveStructured;
  $('cfgSaveRaw').onclick = saveRaw;
  $('cfgRawToggle').onclick = () => { const r = $('cfgRaw'); r.hidden = !r.hidden; $('cfgForm').hidden = !r.hidden; $('cfgRawToggle').textContent = r.hidden ? 'Raw TOML' : 'Form'; };
  window.settingsOpen = () => open;
})();
