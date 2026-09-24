// Settings view: edits the daemon config through /api/config. Form → structured save; raw TOML → text save.
(() => {
  const $ = id => document.getElementById(id);
  let cfg = null, meta = null, open = false;

  const num = (v) => v === '' || v == null ? null : +v;
  const field = (label, html, wide, hint, mode) => `<label class="field${wide ? ' wide' : ''}"${mode ? ` data-mode="${mode}"` : ''}><span>${label}</span>${html}${hint ? `<small class="hint">${hint}</small>` : ''}</label>`;
  const inp = (path, val, type = 'text', extra = '') => `<input type="${type}" data-path="${path}" value="${val ?? ''}" ${extra}>`;
  const sel = (path, val, opts) => `<select data-path="${path}">${opts.map(o => `<option value="${o.v ?? o}" ${(o.v ?? o) === val ? 'selected' : ''}>${o.l ?? o}</option>`).join('')}</select>`;
  const chk = (path, val) => `<span class="toggle"><input type="checkbox" data-path="${path}" ${val ? 'checked' : ''}><span class="track"><span class="knob"></span></span></span>`;

  function render() {
    const c = cfg;
    const monitors = meta.monitors.map(m => ({ v: m.id, l: `${m.id} (${m.description})` }));
    if (!monitors.some(m => m.v === c.capture.monitor)) monitors.unshift({ v: c.capture.monitor, l: c.capture.monitor });
    if (!monitors.some(m => m.v === 'screen')) monitors.push({ v: 'screen', l: 'screen (first monitor)' });
    const audioKnown = new Set(meta.audioDevices.map(a => a.id));
    const audioExtra = c.capture.audio.filter(a => !audioKnown.has(a));

    const presetOpts = (c.quality || []).map(q => ({ v: q.name, l: `${q.name} · ${q.fps ?? c.capture.fps} fps · ${q.bitrateKbps ?? c.capture.bitrateKbps} kbps${q.scale ? ' · ' + q.scale : ''}` }));
    const audioGroup = a => a.id.startsWith('default_') ? 'defaults' : a.id.startsWith('app') ? 'apps' : a.id.includes('.monitor') || a.id.includes('sink') ? 'what you hear (outputs)' : 'microphones and inputs';
    const groups = ['defaults', 'what you hear (outputs)', 'microphones and inputs', 'apps'];
    const audioHtml = groups.map(g => {
      const devs = meta.audioDevices.filter(a => audioGroup(a) === g);
      if (!devs.length) return '';
      return `<div class="agroup"><div class="alabel">${g}</div><div class="achips">${devs.map(a => `<button type="button" class="achip ${c.capture.audio.includes(a.id) ? 'on' : ''}" data-audio="${a.id}" title="${a.id}"><span class="dot"></span>${a.id === 'default_output' ? 'Default output' : a.id === 'default_input' ? 'Default input (mic)' : a.description}</button>`).join('')}</div></div>`;
    }).join('');
    let h = `<div class="settings-cols"><div class="col left"><div class="card"><h3>Capture</h3><div class="help">Applies to every profile. Encoder settings live in the presets below; the recorder always uses the GPU encoder.</div><div class="fields">
      ${field('monitor', sel('capture.monitor', c.capture.monitor, monitors), false, 'the screen the game runs on')}
      ${field('default quality preset', sel('capture.quality', c.capture.quality ?? '', [{ v: '', l: '(built-in defaults: 60 fps h264 20000 kbps)' }, ...presetOpts]), false, 'used by every profile that does not pick its own')}
      ${field('show cursor', chk('capture.cursor', c.capture.cursor), false, 'draw the mouse pointer into the recording')}
      <div class="field wide"><span>audio sources</span><small class="hint">pick what goes into the recording. "Default output" is the game audio; add the mic if you want your voice</small>
        ${audioHtml}
        <label class="other"><span class="muted">other, comma separated (e.g. app:cs2)</span><input type="text" id="audioExtra" value="${audioExtra.join(', ')}"></label>
      </div>
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
      ${field('sound feedback', chk('feedback.sound', c.feedback.sound), false, 'a short tone on mark, save, start and stop; never a popup')}
      ${field('sound volume', inp('feedback.volume', c.feedback.volume, 'number', 'min="0" max="1" step="0.1"'))}
      ${field('storage root', inp('storage.root', c.storage.root))}
      ${field('UI port (restart needed)', inp('daemon.uiPort', c.daemon.uiPort, 'number'))}
      ${field('GSI port (restart needed)', inp('daemon.gsiPort', c.daemon.gsiPort, 'number'))}
      ${field('bind address (restart needed)', inp('daemon.bind', c.daemon.bind))}
    </div></div>`;

    h += `<div class="card"><h3>Quality presets <span class="spacer"></span><button class="btn small" id="addQuality">+ Add preset</button></h3>
      <div class="help">Encoder settings. Blank fields mean 60 fps, h264, 20000 kbps, 1 s keyframes. Scale limits the output size, e.g. 1280x720. h264 plays in every browser.</div>
      <div class="qgrid"><div class="qh">name</div><div class="qh">fps</div><div class="qh">codec</div><div class="qh">bitrate kbps</div><div class="qh">keyframe s</div><div class="qh">scale</div><div class="qh">≈ GB/h</div><div class="qh"></div>
      ${(c.quality || []).map((q, i) => `<input type="text" data-path="quality.${i}.name" value="${q.name ?? ''}"><input type="number" data-path="quality.${i}.fps" value="${q.fps ?? ''}" placeholder="60"><select data-path="quality.${i}.codec"><option value="" ${!q.codec ? 'selected' : ''}>(capture)</option>${meta.codecs.map(x => `<option ${q.codec === x ? 'selected' : ''}>${x}</option>`).join('')}</select><input type="number" data-path="quality.${i}.bitrateKbps" value="${q.bitrateKbps ?? ''}" placeholder="20000" step="500"><input type="number" data-path="quality.${i}.keyframeSeconds" value="${q.keyframeSeconds ?? ''}" placeholder="1" step="0.5"><input type="text" data-path="quality.${i}.scale" value="${q.scale ?? ''}" placeholder="native"><span class="mono muted qrate" data-qrate="${i}"></span><button class="tiny" data-delq="${i}" title="remove preset">✕</button>`).join('')}
      </div></div>`;
    h += `</div><div class="col right">`;
    h += `<div class="card"><h3>Profiles <span class="spacer"></span><span class="switch-wrap"><button class="btn small" id="addProfile">+ Add profile</button><div id="presetMenu" class="menu right" hidden><div class="menu-title">From a game preset</div>${(meta.presets || []).map(g => `<button data-preset="${g.id}">${g.name}<span class="sub">${g.match || g.matchCmdline} · ${g.mode}${g.events !== 'none' ? ' · ' + g.events : ''}</span></button>`).join('')}<div class="menu-title">Or</div><button data-preset="">blank profile</button></div></span></h3>
      <div class="help">A profile is one game. The daemon starts recording when a matching process appears. Blank override fields use the defaults above.</div></div>`;
    c.profiles.forEach((p, i) => {
      const P = `profiles.${i}.`;
      const ov = (o, k) => (o && o[k] != null) ? o[k] : '';
      const open = openProfiles.has(p.name || `#${i}`) || !p.name;
      const st = p.mode === 'buffer' ? (ov(p.buffer, 'storage') || c.buffer.storage) : (ov(p.timeline, 'storage') || c.timeline.storage);
      const len = p.mode === 'buffer' ? `${ov(p.buffer, 'minutes') || c.buffer.minutes} min buffer` : `${ov(p.timeline, 'windowMinutes') === '' ? c.timeline.windowMinutes : ov(p.timeline, 'windowMinutes')} min window`;
      h += `<div class="card profile ${open ? 'open' : 'closed'}" data-profile="${i}"><h3 class="ph" data-toggle="${p.name || `#${i}`}"><span class="chev">${open ? '▾' : '▸'}</span><span class="name">${p.name || 'new profile'}</span><span class="badge">${p.mode}</span><span class="badge">${st}</span><span class="muted sum">${len} · ${p.quality ? p.quality + ' · ' : ''}${p.events === 'none' ? 'manual markers' : p.events} · ${p.match || p.matchCmdline || 'no match yet'}</span><span class="spacer"></span><button class="btn small danger" data-del="${i}">Delete</button></h3>
        <div class="body"><div class="fields">
          ${field('name', inp(P + 'name', p.name), false, 'used for the session folder and the clips subfolder')}
          ${field('game preset', sel(P + 'game', p.game ?? '', [{ v: '', l: '(none)' }, ...(meta.presets || []).map(g => ({ v: g.id, l: g.name }))]), false, 'a built-in game: supplies the banner when nothing below is set. A profile named like a preset is matched automatically')}
          ${field('steam app id', inp(P + 'steamAppId', p.steamAppId, 'number', 'placeholder="730"'), false, 'the banner is Steam\'s header art for this app. Blank: kairec searches the Steam store for the profile name once and uses the first hit')}
          ${field('banner URL', inp(P + 'bannerUrl', p.bannerUrl, 'text', 'placeholder="https://…/image.jpg"'), false, 'any image on the web, for games that are not on Steam. Fetched once and cached')}
          <label class="field wide banner-field"><span>banner</span><div class="banner-row"><img class="banner-preview" data-banner="${i}" alt=""><span class="muted banner-src" data-bsrc="${i}"></span><span class="spacer"></span><button type="button" class="btn small" data-bupload="${i}">Upload image…</button><button type="button" class="btn small" data-bremove="${i}" hidden>Remove upload</button></div><small class="hint">an uploaded JPEG, PNG or WebP beats every other source. Shown behind this profile's cards in the sidebar</small></label>
          ${field('mode', sel(P + 'mode', p.mode, [{ v: 'timeline', l: 'timeline (rolling window on disk)' }, { v: 'buffer', l: 'buffer (RAM ring buffer)' }]), false, 'timeline: scrub the last N minutes any time. buffer: nothing is written until you mark or save')}
          ${field('events', sel(P + 'events', p.events, [{ v: 'none', l: 'none (manual markers only)' }, { v: 'cs2-gsi', l: 'cs2-gsi (CS2 game state)' }]), false, 'cs2-gsi adds kill, death, round and MVP markers from the game itself')}
          ${field('quality preset', sel(P + 'quality', p.quality ?? '', [{ v: '', l: '(capture defaults)' }, ...(c.quality || []).map(q => ({ v: q.name, l: q.name + (q.bitrateKbps ? ` · ${q.bitrateKbps} kbps` : '') + (q.fps ? ` · ${q.fps} fps` : '') }))]), false, 'fps, codec, bitrate and size for this game; presets are defined on the left')}
          <label class="field"><span>process name regex <button class="tiny" data-pick="${i}" title="pick from running processes">pick…</button></span>${inp(P + 'match', p.match, 'text', 'placeholder="^cs2$"')}<small class="hint">matched against the process name (first 15 chars). Recording starts when it appears and stops when it exits</small></label>
          ${field('command line regex', inp(P + 'matchCmdline', p.matchCmdline, 'text', 'placeholder="net\\\\.minecraft|prismlauncher"'), false, 'for games whose process name is generic, like java: matched against the full command line')}
          ${field('fps override', inp(P + 'fps', p.fps, 'number'), false, 'beats the preset and capture fps; blank keeps them')}
        </div>
        <div class="footprint" data-fp="${i}"></div>
        <div class="sub"><div class="lbl">overrides</div><div class="fields">
          ${field('timeline window minutes', inp(P + 'timeline.windowMinutes', ov(p.timeline, 'windowMinutes'), 'number', `placeholder="${c.timeline.windowMinutes}"`), false, 'how far back you can scrub; older footage is deleted as you play. 0 keeps everything', 'timeline')}
          ${field('timeline segment seconds', inp(P + 'timeline.segmentSeconds', ov(p.timeline, 'segmentSeconds'), 'number', `placeholder="${c.timeline.segmentSeconds}"`), false, 'file chunk length; a save waits for the current chunk to finish', 'timeline')}
          ${field('timeline storage', sel(P + 'timeline.storage', ov(p.timeline, 'storage'), [{ v: '', l: '(default)' }, 'disk', 'ram']), false, 'ram keeps the window in tmpfs: no disk writes, needs window × bitrate of memory', 'timeline')}
          ${field('RAM footage kept for hours', inp(P + 'timeline.ramKeepHours', ov(p.timeline, 'ramKeepHours'), 'number', `placeholder="${c.timeline.ramKeepHours}"`), false, 'after a RAM session ends its footage stays this long, or until the next session starts', 'timeline')}
          ${field('buffer minutes', inp(P + 'buffer.minutes', ov(p.buffer, 'minutes'), 'number', `placeholder="${c.buffer.minutes}"`), false, 'ring buffer length; the most a mark or save can reach back', 'buffer')}
          ${field('buffer storage', sel(P + 'buffer.storage', ov(p.buffer, 'storage'), [{ v: '', l: '(default)' }, 'ram', 'disk']), false, 'where the ring lives; ram costs ~150 MB per minute at 20 Mbps', 'buffer')}
          ${field('save seconds', inp(P + 'clips.saveSeconds', ov(p.clips, 'saveSeconds'), 'number', `placeholder="${c.clips.saveSeconds}"`), false, 'what kairec save and the tray Save write when no length is given')}
          ${field('mark: seconds before', inp(P + 'clips.markBeforeSeconds', ov(p.clips, 'markBeforeSeconds'), 'number', `placeholder="${c.clips.markBeforeSeconds}"`), false, 'a mark clip starts this long before the mark')}
          ${field('mark: seconds after', inp(P + 'clips.markAfterSeconds', ov(p.clips, 'markAfterSeconds'), 'number', `placeholder="${c.clips.markAfterSeconds}"`), false, 'and ends this long after it; marks inside that time join the same clip')}
          ${field('mark clips expire hours', inp(P + 'clips.markClipExpireHours', ov(p.clips, 'markClipExpireHours'), 'number', `placeholder="${c.clips.markClipExpireHours}"`), false, 'mark clips are deleted after this unless starred; 0 = never')}
          ${field('clip around each mark', sel(P + 'clips.markSaves', ov(p.clips, 'markSaves') === '' ? '' : String(ov(p.clips, 'markSaves')), [{ v: '', l: '(default)' }, { v: 'true', l: 'on' }, { v: 'false', l: 'off' }]), false, 'timeline mode only: also write a clip on every mark. Default is on for RAM timelines', 'timeline')}
          ${field('retention max GB', inp(P + 'retention.maxGb', ov(p.retention, 'maxGb'), 'number', `placeholder="${c.retention.maxGb ?? ''}"`), false, 'sessions + clips of this profile; oldest unprotected go first')}
          ${field('retention max days', inp(P + 'retention.maxDays', ov(p.retention, 'maxDays'), 'number', `placeholder="${c.retention.maxDays ?? ''}"`), false, 'older sessions and clips are deleted unless starred or protected')}
        </div></div></div></div>`;
    });
    $('cfgForm').innerHTML = h + '</div></div>';
    $('cfgForm').addEventListener('input', updateFootprints);
    updateFootprints();
    document.querySelectorAll('[data-toggle]').forEach(hd => hd.onclick = e => {
      if (e.target.closest('button')) return;
      const key = hd.dataset.toggle; openProfiles.has(key) ? openProfiles.delete(key) : openProfiles.add(key);
      try { localStorage.setItem('openProfiles', JSON.stringify([...openProfiles])); } catch {}
      collect(); render();
    });
    document.querySelectorAll('[data-pick]').forEach(b => b.onclick = e => { e.preventDefault(); openPicker(+b.dataset.pick, b); });
    const applyModes = () => document.querySelectorAll('.profile').forEach(card => {
      const mode = card.querySelector('[data-path$=".mode"]').value;
      card.querySelectorAll('[data-mode]').forEach(f => { f.hidden = f.dataset.mode !== mode; });
    });
    applyModes();
    document.querySelectorAll('.profile [data-path$=".mode"]').forEach(sel => sel.addEventListener('change', applyModes));
    document.querySelectorAll('.achip').forEach(b => b.onclick = () => { b.classList.toggle('on'); updateFootprints(); });
    $('addQuality').onclick = () => { collect(); (cfg.quality ||= []).push({ name: '', fps: null, codec: null, bitrateKbps: null, keyframeSeconds: null, scale: null }); render(); };
    document.querySelectorAll('[data-delq]').forEach(b => b.onclick = () => { collect(); cfg.quality.splice(+b.dataset.delq, 1); render(); });
    const pm = $('presetMenu');
    $('addProfile').onclick = e => { e.stopPropagation(); pm.hidden = !pm.hidden; };
    pm.onclick = e => e.stopPropagation();
    document.addEventListener('click', () => { pm.hidden = true; }, { once: true });
    pm.querySelectorAll('[data-preset]').forEach(b => b.onclick = () => {
      const g = (meta.presets || []).find(x => x.id === b.dataset.preset);
      collect(); openProfiles.add(`#${cfg.profiles.length}`);
      cfg.profiles.push(g
        ? { name: g.name, game: g.id, steamAppId: g.steamAppId, bannerUrl: g.bannerUrl, match: g.match, matchCmdline: g.matchCmdline, mode: g.mode, events: g.events, fps: null, timeline: null, buffer: null, clips: null, retention: null }
        : { name: '', game: null, steamAppId: null, bannerUrl: null, match: '', matchCmdline: null, mode: 'timeline', events: 'none', fps: null, timeline: null, buffer: null, clips: null, retention: null });
      render();
    });
    // banner previews, uploads and removals: keyed by the profile's saved name
    const bannerFor = i => { const p = cfg.profiles[i]; return p && p.name ? p.name : null; };
    const refreshBanner = async i => {
      const name = bannerFor(i); const img = document.querySelector(`[data-banner="${i}"]`), src = document.querySelector(`[data-bsrc="${i}"]`), rm = document.querySelector(`[data-bremove="${i}"]`);
      if (!img) return;
      if (!name) { img.hidden = true; src.textContent = 'save the profile with a name first'; rm.hidden = true; return; }
      img.hidden = false; img.src = `/api/banners/${encodeURIComponent(name)}?v=${Date.now()}`;
      img.onerror = () => { img.hidden = true; };
      try { const d = await (await fetch(`/api/banners/${encodeURIComponent(name)}/source`)).json(); src.textContent = 'source: ' + d.source; rm.hidden = !d.custom; } catch { src.textContent = ''; }
    };
    cfg.profiles.forEach((_, i) => refreshBanner(i));
    document.querySelectorAll('[data-bupload]').forEach(b => b.onclick = () => {
      const i = +b.dataset.bupload; const name = bannerFor(i); if (!name) return window.toast('give the profile a name and save first', true);
      const pick = document.createElement('input'); pick.type = 'file'; pick.accept = 'image/jpeg,image/png,image/webp';
      pick.onchange = async () => {
        const f = pick.files[0]; if (!f) return;
        const r = await fetch(`/api/banners/${encodeURIComponent(name)}`, { method: 'PUT', headers: { 'content-type': f.type }, body: f });
        if (!r.ok) { const d = await r.json().catch(() => ({})); return window.toast('not uploaded: ' + (d.error || r.status), true); }
        window.toast('banner uploaded'); refreshBanner(i); if (window.bannersChanged) window.bannersChanged();
      };
      pick.click();
    });
    document.querySelectorAll('[data-bremove]').forEach(b => b.onclick = async () => {
      const i = +b.dataset.bremove; const name = bannerFor(i); if (!name) return;
      await fetch(`/api/banners/${encodeURIComponent(name)}`, { method: 'DELETE' });
      window.toast('upload removed'); refreshBanner(i); if (window.bannersChanged) window.bannersChanged();
    });
    document.querySelectorAll('[data-del]').forEach(b => b.onclick = () => { collect(); cfg.profiles.splice(+b.dataset.del, 1); render(); });
    $('cfgText').value = meta.text; $('cfgPath').textContent = meta.path;
    const n = $('cfgNotice');
    if (meta.pending) { n.hidden = false; n.textContent = 'a saved change is waiting for the current session to end'; }
    else if (meta.sessionRunning) { n.hidden = false; n.textContent = 'a session is running: saves apply when it ends'; }
    else n.hidden = true;
  }

  let openProfiles = new Set();
  try { openProfiles = new Set(JSON.parse(localStorage.getItem('openProfiles') || '[]')); } catch {}

  // Process picker: lists your running processes; choose the name or the command line as the match.
  async function openPicker(i, anchor) {
    document.querySelectorAll('.picker').forEach(p => p.remove());
    const pk = document.createElement('div'); pk.className = 'picker';
    pk.innerHTML = `<input type="text" class="text" placeholder="filter…"><div class="rows"><div class="muted">loading…</div></div>`;
    anchor.closest('.field').appendChild(pk);
    const rows = pk.querySelector('.rows'), filter = pk.querySelector('input');
    const procs = await (await fetch('/api/processes')).json();
    const esc = s => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const draw = () => {
      const q = filter.value.toLowerCase();
      const seen = new Set();
      rows.innerHTML = procs.filter(p => !q || p.comm.toLowerCase().includes(q) || p.cmdline.toLowerCase().includes(q))
        .filter(p => { if (seen.has(p.comm)) return false; seen.add(p.comm); return true; })   // one row per process name
        .slice(0, 80)
        .map(p => `<div class="prow"><b>${p.comm}</b><span class="muted mono">${p.cmdline.replace(/</g, '&lt;')}</span><span><button class="tiny" data-name="${p.comm}">name</button><button class="tiny" data-cmd="${p.cmdline.split(' ')[0].replace(/"/g, '')}">cmdline</button></span></div>`).join('') || '<div class="muted">no match</div>';
      rows.querySelectorAll('[data-name]').forEach(b => b.onclick = () => { setField(`profiles.${i}.match`, '^' + esc(b.dataset.name) + '$'); pk.remove(); });
      rows.querySelectorAll('[data-cmd]').forEach(b => b.onclick = () => { setField(`profiles.${i}.matchCmdline`, esc(b.dataset.cmd.split('/').pop())); pk.remove(); });
    };
    filter.oninput = draw; draw(); filter.focus();
    const close = ev => { if (!pk.contains(ev.target) && ev.target !== anchor) { pk.remove(); document.removeEventListener('mousedown', close); } };
    setTimeout(() => document.addEventListener('mousedown', close), 0);
  }
  function setField(path, value) { const el = document.querySelector(`#cfgForm [data-path="${path}"]`); if (el) { el.value = value; el.dispatchEvent(new Event('input', { bubbles: true })); } }

  // Footage the profile needs, from the current form values, against what the machine has.
  const gb = b => (b / 1e9).toFixed(1) + ' GB';
  function updateFootprints() {
    const val = path => { const el = document.querySelector(`#cfgForm [data-path="${path}"]`); return el ? (el.type === 'checkbox' ? el.checked : el.value) : ''; };
    const numv = (path, fallback) => { const v = val(path); return v === '' || v == null ? fallback : +v; };
    const presetKbps = name => { const qi = (cfg.quality || []).findIndex(q => q.name === name); return qi >= 0 ? numv(`quality.${qi}.bitrateKbps`, cfg.capture.bitrateKbps) : null; };
    const capKbps = presetKbps(val('capture.quality')) ?? cfg.capture.bitrateKbps;
    document.querySelectorAll('[data-qrate]').forEach(el => { const k = numv(`quality.${el.dataset.qrate}.bitrateKbps`, capKbps); el.textContent = (k * 450 / 1e6).toFixed(1); });
    document.querySelectorAll('.footprint').forEach(el => {
      const i = el.dataset.fp, P = `profiles.${i}.`;
      const mode = val(P + 'mode');
      const qn = val(P + 'quality'); let kbps = capKbps;
      if (qn) kbps = presetKbps(qn) ?? capKbps;
      const perHour = kbps * 1000 / 8 * 3600;
      let need = 0, where = '', note = '', avail = 0, ok = true;
      if (mode === 'buffer') {
        const mins = numv(P + 'buffer.minutes', numv('buffer.minutes', cfg.buffer.minutes));
        const st = val(P + 'buffer.storage') || val('buffer.storage') || 'ram';
        need = mins * 60 * kbps * 125; where = st === 'ram' ? 'RAM' : 'disk'; avail = st === 'ram' ? meta.memTotal : meta.diskFree;
        note = `${mins} min ring buffer at ${kbps} kbps`;
        ok = need < avail * 0.8;
      } else {
        const win = numv(P + 'timeline.windowMinutes', numv('timeline.windowMinutes', cfg.timeline.windowMinutes));
        const st = val(P + 'timeline.storage') || val('timeline.storage') || 'disk';
        where = st === 'ram' ? 'RAM (tmpfs)' : 'disk'; avail = st === 'ram' ? meta.ramFree : meta.diskFree;
        if (win > 0) { need = win * 60 * kbps * 125 * 1.1; note = `${win} min window at ${kbps} kbps`; ok = need <= avail; }
        else { note = `no window: grows ${gb(perHour)} per hour on ${where}`; }
      }
      const extra = mode !== 'buffer' && where.startsWith('RAM') ? ` · tmpfs is ${gb(meta.ramTotal)} of ${gb(meta.memTotal)} RAM` : '';
      el.className = 'footprint ' + (ok ? 'ok' : 'bad');
      el.innerHTML = need
        ? `<b>${gb(need)}</b> of ${where} needed <span class="muted">(${note})</span> · <b>${gb(avail)}</b> available${extra}${ok ? '' : ' · <b>will not start</b>: shorten the window, lower the bitrate, or switch storage'}`
        : `<span class="muted">${note}</span> · ${gb(avail)} available`;
    });
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
    const audio = [...document.querySelectorAll('#cfgForm .achip.on')].map(x => x.dataset.audio);
    ($('audioExtra').value || '').split(',').map(s => s.trim()).filter(Boolean).forEach(a => audio.push(a));
    cfg.capture.audio = audio;
    if (cfg.quality) { for (const q of cfg.quality) for (const k of Object.keys(q)) if (q[k] === '') q[k] = null; cfg.quality = cfg.quality.filter(q => q.name); }
    for (const p of cfg.profiles) {
      if (p.quality === '') p.quality = null;
      if (p.game === '') p.game = null;
      if (p.bannerUrl === '') p.bannerUrl = null;
      for (const g of ['timeline', 'buffer', 'clips', 'retention']) {
        if (p[g] && Object.values(p[g]).every(v => v == null || v === '')) p[g] = null;
        else if (p[g]) for (const k of Object.keys(p[g])) if (p[g][k] === '') p[g][k] = null;
      }
      if (p.match === '') p.match = null;
      if (p.matchCmdline === '') p.matchCmdline = null;
    }
  }

  async function load() {
    const [r, pr] = await Promise.all([fetch('/api/config'), fetch('/api/presets')]); meta = await r.json(); meta.presets = pr.ok ? await pr.json() : []; cfg = meta.config; render();
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
