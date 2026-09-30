// Mixer: set the level of each separate audio track while watching.
// A recording made with separate tracks carries the mix as track 1 (what the video element plays) and the
// tracks themselves after it. Browsers cannot reach those, so with the mixer on each one is fetched as its own
// audio stream (/audio/N), played in a hidden <audio> kept in step with the video, and summed through Web
// Audio gains, while the video's own sound is turned down to nothing. Levels are remembered per track name.
(() => {
  const v = $('video'), pop = $('mixPop'), btn = $('mixBtn');
  const enc = encodeURIComponent;
  let saved = { on: false, levels: {}, mutes: {} };
  try { saved = { ...saved, ...JSON.parse(localStorage.getItem('mixer') || '{}') }; } catch {}
  const persist = () => { try { localStorage.setItem('mixer', JSON.stringify(saved)); } catch {} };
  const solo = new Set();

  // ---- Web Audio graph, built the first time the mixer is used (it needs a click to be allowed to sound)
  let ctx = null, videoGain = null, master = null;
  function graph() {
    if (ctx) return;
    ctx = new AudioContext();
    videoGain = ctx.createGain(); ctx.createMediaElementSource(v).connect(videoGain).connect(ctx.destination);
    master = ctx.createGain(); master.connect(ctx.destination);
    const wake = () => { if (ctx.state === 'suspended') ctx.resume().catch(() => {}); };
    for (const ev of ['pointerdown', 'keydown']) document.addEventListener(ev, wake, true);
    v.addEventListener('play', wake);
    v.addEventListener('volumechange', applyGains);
  }

  // ---- which tracks the thing in the player has, and where they come from
  function source() {
    const cur = state.current;
    const names = cur && cur.data.audioTracks;
    if (!names || names.length < 2) return null;
    const stems = names.slice(1).map((name, i) => ({ name, n: i + 1 }));
    if (cur.kind === 'clip') return { key: 'clip:' + cur.id, stems, url: n => `/api/clips/${enc(cur.data.profile)}/${enc(cur.data.file)}/audio/${n}` };
    if (clipPlayback(cur)) return state.clipLoaded ? { key: 'clip:' + cur.data.profile + '/' + state.clipLoaded, stems, url: n => `/api/clips/${enc(cur.data.profile)}/${enc(state.clipLoaded)}/audio/${n}` } : { key: null, stems };
    if (cur.data.live) return { key: null, stems, why: 'the mixer works once the session has ended; a live one plays the recorded mix' };
    return { key: 'session:' + cur.id, stems, hls: true, url: n => `/api/sessions/${enc(cur.id)}/audio/${n}/playlist.m3u8` };
  }

  // ---- the per-track players
  let loaded = null;   // { key, stems: [{ name, el, hls, gain }] }
  function unload() {
    if (!loaded) return;
    for (const s of loaded.stems) { s.el.pause(); if (s.hls) s.hls.destroy(); s.el.removeAttribute('src'); s.el.load(); try { s.node.disconnect(); s.gain.disconnect(); } catch {} }
    loaded = null;
  }
  function load(src) {
    unload(); graph();
    loaded = { key: src.key, stems: src.stems.map(t => {
      const el = new Audio(); el.preload = 'auto';
      let hls = null;
      if (src.hls && Hls.isSupported()) { hls = new Hls({ enableWorker: true, maxBufferLength: 60, backBufferLength: 30 }); hls.loadSource(src.url(t.n)); hls.attachMedia(el); }
      else el.src = src.url(t.n);
      const node = ctx.createMediaElementSource(el), gain = ctx.createGain();
      node.connect(gain).connect(master);
      return { name: t.name, el, hls, node, gain };
    }) };
  }

  const level = name => saved.levels[name] ?? 1;
  const audible = name => !saved.mutes[name] && (solo.size === 0 || solo.has(name));
  function applyGains() {
    if (!ctx) return;
    // The tracks only take over once every one of them can play; until then the recorded mix carries on.
    const ready = !!loaded && loaded.stems.every(s => s.el.readyState >= 2);
    videoGain.gain.value = ready ? 0 : 1;
    master.gain.value = ready ? (v.muted ? 0 : v.volume) : 0;
    if (loaded) for (const s of loaded.stems) s.gain.gain.value = audible(s.name) ? level(s.name) : 0;
  }

  // ---- keeping the tracks in step with the video: nudge their speed for small offsets, jump for large ones
  let lead = 0.08, startedAt = 0, measured = false;
  function follow() {
    const src = source();
    btn.hidden = !src;
    if (!src) { if (!pop.hidden) show(false); }
    const want = saved.on && src && src.key ? src : null;
    if ((want ? want.key : null) !== (loaded ? loaded.key : null)) { want ? load(want) : unload(); if (!pop.hidden) render(); }
    if (loaded) {
      const hold = v.paused || v.seeking || v.readyState < 3;
      const now = performance.now();
      if (hold) startedAt = 0; else if (!startedAt) { startedAt = now; measured = false; }
      for (const s of loaded.stems) {
        const a = s.el;
        // Parked a little ahead of the video: a track takes that long to get going once told to play.
        if (hold) { if (!a.paused) a.pause(); if (Math.abs(a.currentTime - (v.currentTime + lead)) > 0.02 && a.readyState >= 1) a.currentTime = v.currentTime + lead; continue; }
        if (a.paused) a.play().catch(() => {});
        const d = a.currentTime - v.currentTime;
        if (Math.abs(d) > 0.3) { a.currentTime = v.currentTime; a.playbackRate = v.playbackRate; }
        else a.playbackRate = v.playbackRate * Math.min(1.05, Math.max(0.95, 1 - d * 0.5));
      }
      // Learn the start-up delay from where the first track is shortly after each start.
      if (!hold && !measured && now - startedAt > 400 && loaded.stems[0].el.readyState >= 3) {
        measured = true;
        lead = Math.min(0.25, Math.max(0, lead - (loaded.stems[0].el.currentTime - v.currentTime) * 0.7));
      }
    }
    applyGains();
  }
  setInterval(follow, 200);
  for (const ev of ['play', 'pause', 'seeking', 'seeked', 'loadedmetadata', 'emptied']) v.addEventListener(ev, follow);

  // ---- the panel
  const esc = s => String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  function render() {
    const src = source(); if (!src) return;
    $('mixOn').checked = saved.on;
    $('mixRows').innerHTML = src.stems.map(t => `<div class="mixrow ${saved.on ? '' : 'off'}" data-name="${esc(t.name)}">
      <span class="mixname" title="${esc(t.name)}">${esc(t.name)}</span>
      <input type="range" class="vol mixlevel" min="0" max="2" step="0.01" value="${level(t.name)}" title="level">
      <span class="mixval mono">${Math.round(level(t.name) * 100)}%</span>
      <button class="tbtn sm ${saved.mutes[t.name] ? 'on' : ''}" data-mute title="mute this track">M</button>
      <button class="tbtn sm ${solo.has(t.name) ? 'on' : ''}" data-solo title="solo: hear only this track">S</button></div>`).join('');
    $('mixNote').textContent = !saved.on ? 'off: playing the mix as it was recorded' : src.why || (src.key ? 'the volume slider is the master' : 'pick a moment with footage');
    $('mixRows').querySelectorAll('.mixrow').forEach(row => {
      const name = row.dataset.name;
      row.querySelector('.mixlevel').oninput = e => { saved.levels[name] = +e.target.value; row.querySelector('.mixval').textContent = Math.round(e.target.value * 100) + '%'; persist(); applyGains(); };
      row.querySelector('.mixlevel').ondblclick = () => { delete saved.levels[name]; persist(); render(); applyGains(); };
      row.querySelector('[data-mute]').onclick = () => { saved.mutes[name] = !saved.mutes[name]; persist(); render(); applyGains(); };
      row.querySelector('[data-solo]').onclick = () => { solo.has(name) ? solo.delete(name) : solo.add(name); render(); applyGains(); };
    });
  }
  function show(on) {
    pop.hidden = !on; btn.classList.toggle('on', on || saved.on);
    if (on) { if (window.showExportPop) window.showExportPop(false); render(); }
  }
  btn.onclick = e => { e.stopPropagation(); show(pop.hidden); };
  pop.onclick = e => e.stopPropagation();
  pop.addEventListener('keydown', e => { if (e.key === 'Escape') show(false); });
  document.addEventListener('click', () => { if (!pop.hidden) show(false); });
  $('mixOn').onchange = e => { saved.on = e.target.checked; persist(); if (saved.on) graph(); follow(); render(); btn.classList.add('on'); if (!saved.on) btn.classList.toggle('on', !pop.hidden); };
  $('mixReset').onclick = () => { saved.levels = {}; saved.mutes = {}; solo.clear(); persist(); render(); applyGains(); };
  btn.classList.toggle('on', saved.on);
  window.closeMixer = () => show(false);
  window.mixerState = () => ({ on: saved.on, lead, loaded: loaded && { key: loaded.key, stems: loaded.stems.map(s => ({ name: s.name, t: s.el.currentTime, ready: s.el.readyState, paused: s.el.paused, gain: s.gain.gain.value, bytes: s.el.webkitAudioDecodedByteCount })) }, ctx: ctx && ctx.state, videoGain: videoGain && videoGain.gain.value, master: master && master.gain.value });
})();
