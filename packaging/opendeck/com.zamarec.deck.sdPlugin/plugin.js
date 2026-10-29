#!/usr/bin/env node
// ZamaREC keys for OpenDeck (and the Stream Deck app, which speaks the same protocol).
//
// The deck app starts this with -port -pluginUUID -registerEvent -info and talks to it over a WebSocket.
// The plugin talks to the daemon the way the zamarec CLI does: one JSON line over the control socket per
// request (on Windows, where Node cannot open that socket, the same request goes to POST /api/control).
// It asks for the daemon's status once a second while a key is on screen; that is a CPU-side socket call,
// never anything that touches the GPU. Keys are drawn as SVG and only re-sent when what they show changes.
// No dependencies: Node 22+ has WebSocket built in, ws-lite.js stands in on older ones.
"use strict";

const net = require("net");
const http = require("http");
const os = require("os");
const path = require("path");
const fs = require("fs");
const { spawn } = require("child_process");

const args = {};
for (let i = 2; i + 1 < process.argv.length; i += 2) args[process.argv[i].replace(/^-+/, "")] = process.argv[i + 1];
if (!args.port || !args.pluginUUID || !args.registerEvent) {
  console.error("zamarec deck plugin: started without -port, -pluginUUID and -registerEvent (the deck app starts it)");
  process.exit(1);
}

const PREFIX = "com.zamarec.deck.";
const WS = globalThis.WebSocket ?? require("./ws-lite");

// ---------------------------------------------------------------- daemon

let globalSettings = {};

function socketPath() {
  if (process.env.ZAMAREC_SOCKET) return process.env.ZAMAREC_SOCKET;
  const runtime = process.env.XDG_RUNTIME_DIR || (fs.existsSync(`/run/user/${process.getuid?.()}`) ? `/run/user/${process.getuid()}` : os.tmpdir());
  return path.join(runtime, "zamarec.sock");
}

function viaSocket(req, timeoutMs) {
  return new Promise(resolve => {
    let buf = "", done = false;
    const finish = v => { if (!done) { done = true; sock.destroy(); resolve(v); } };
    const sock = net.connect(socketPath());
    sock.setTimeout(timeoutMs, () => finish(null));
    sock.on("connect", () => sock.write(JSON.stringify(req) + "\n"));
    sock.on("data", d => {
      buf += d;
      const nl = buf.indexOf("\n");
      if (nl >= 0) { try { finish(JSON.parse(buf.slice(0, nl))); } catch { finish(null); } }
    });
    sock.on("error", () => finish(null));
    sock.on("close", () => finish(null));
  });
}

function viaHttp(req, timeoutMs) {
  return new Promise(resolve => {
    const base = (globalSettings.url || "http://127.0.0.1:3556").replace(/\/+$/, "");
    const body = JSON.stringify(req);
    let r;
    try {
      r = http.request(base + "/api/control", { method: "POST", timeout: timeoutMs, headers: { "content-type": "application/json", "content-length": Buffer.byteLength(body) } }, res => {
        let text = "";
        res.on("data", d => (text += d));
        res.on("end", () => { try { resolve(res.statusCode === 200 ? JSON.parse(text) : null); } catch { resolve(null); } });
      });
    } catch { return resolve(null); }
    r.on("timeout", () => r.destroy());
    r.on("error", () => resolve(null));
    r.end(body);
  });
}

/** Sends one control request; null when the daemon is not there. */
function control(req, timeoutMs = 20000) {
  const useHttp = process.platform === "win32" || globalSettings.transport === "http";
  return useHttp ? viaHttp(req, timeoutMs) : viaSocket(req, timeoutMs);
}

// ---------------------------------------------------------------- deck connection

const ws = new WS(`ws://127.0.0.1:${args.port}`);
const send = obj => { if (ws.readyState === 1) ws.send(JSON.stringify(obj)); };

ws.onopen = () => {
  send({ event: args.registerEvent, uuid: args.pluginUUID });
  send({ event: "getGlobalSettings", context: args.pluginUUID });
};
ws.onclose = () => process.exit(0);
ws.onmessage = e => {
  let msg;
  try { msg = JSON.parse(typeof e.data === "string" ? e.data : e.data.toString()); } catch { return; }
  handle(msg).catch(err => console.error("zamarec deck plugin:", err));
};

/** context -> { action: short name, settings, image: last image sent, flash: { text, colour, until } } */
const keys = new Map();

async function handle(msg) {
  const { event, context, payload } = msg;
  switch (event) {
    case "willAppear":
      keys.set(context, { action: msg.action.slice(PREFIX.length), settings: payload?.settings ?? {}, image: null, flash: null });
      send({ event: "setTitle", context, payload: { title: "", target: 0 } });
      render(context);
      schedulePoll(0);
      break;
    case "willDisappear":
      keys.delete(context);
      break;
    case "didReceiveSettings":
      if (keys.has(context)) { keys.get(context).settings = payload?.settings ?? {}; keys.get(context).image = null; render(context); }
      break;
    case "didReceiveGlobalSettings":
      globalSettings = payload?.settings ?? {};
      schedulePoll(0);
      break;
    case "sendToPlugin":
      // The settings page asks for the profile list to fill its dropdown.
      if (payload?.request === "profiles")
        send({ event: "sendToPropertyInspector", context, action: msg.action, payload: { profiles: status?.profiles ?? [], online: status !== null } });
      break;
    case "keyDown":
      if (keys.has(context)) keys.get(context).downAt = Date.now();
      break;
    case "keyUp":
      if (keys.has(context)) await press(context, Date.now() - (keys.get(context).downAt ?? Date.now()));
      break;
  }
}

// ---------------------------------------------------------------- status

/** The daemon's DaemonStatus (snake_case, as the CLI sees it), or null while it is not running. */
let status = null;
let polling = false, pollTimer = null, storageAt = 0;
const STORAGE_VIEWS = ["ram", "disk"];

function schedulePoll(ms) {
  clearTimeout(pollTimer);
  pollTimer = setTimeout(poll, ms);
}

async function poll() {
  if (keys.size === 0) return;   // nothing on screen: no requests until a key appears again
  if (!polling) {
    polling = true;
    // Storage figures walk the RAM dir: only for a key that shows them, and at most every 10 s.
    const storage = [...keys.values()].some(k => k.action === "info" && STORAGE_VIEWS.includes(k.settings.show)) && Date.now() - storageAt > 10000;
    const r = await control({ cmd: "status", storage: storage || undefined }, 3000);
    polling = false;
    const last = status?.storage;
    status = r?.status ?? null;
    if (status) {
      status.at = Date.now();
      if (status.storage) storageAt = Date.now();
      else if (last) status.storage = last;
    }
    renderAll();
  }
  schedulePoll(status ? 1000 : 3000);
}

const recording = () => status?.state === "recording";

// ---------------------------------------------------------------- key presses

const LONG_PRESS_MS = 600;

async function press(context, heldMs) {
  const key = keys.get(context);
  const s = key.settings;
  if (key.action === "info") return s.press === "ui" ? openUi(context) : undefined;
  if (heldMs >= LONG_PRESS_MS && s.longPress !== "nothing" && key.action !== "ui") return openUi(context);
  switch (key.action) {
    case "recording": {
      if (!status) return fail(context, "OFFLINE");
      const what = s.press || "toggle";
      if (what === "nothing") return;
      if (what === "ui") return openUi(context);
      if (status.state !== "idle") return run(context, { cmd: "stop" }, "STOPPED");
      if (what === "stop") return;
      if (status.paused) return run(context, { cmd: "resume" }, "AUTO ON");
      // Games start their own sessions. A manual one records the desktop under that profile and drops the RAM
      // footage of the last session, so it happens only when the key names a profile.
      if (!s.profile) return fail(context, "NO GAME");
      return run(context, { cmd: "start", profile: s.profile }, "STARTED");
    }
    case "mark":
      if (!recording()) return fail(context, "NOT REC");
      return run(context, { cmd: "mark", label: s.label || undefined }, "MARKED");
    case "save": {
      if (!recording()) return fail(context, "NOT REC");
      const secs = parseInt(s.seconds, 10);
      flash(context, "SAVING", RED, 30000);
      return run(context, { cmd: "save", seconds: secs > 0 ? secs : undefined, label: s.label || undefined }, "SAVED");
    }
    case "pause":
      if (!status) return fail(context, "OFFLINE");
      if (status.paused) return run(context, { cmd: "resume" }, "AUTO ON");
      return run(context, pauseRequest(s.mode), "AUTO OFF");
    case "ui":
      return openUi(context);
  }
}

function pauseRequest(mode) {
  if (mode === "forever") return { cmd: "pause", mode: "forever" };
  const hours = parseFloat(mode);
  if (hours > 0) return { cmd: "pause", mode: "until", hours };
  return { cmd: "pause", mode: "session" };
}

async function run(context, req, okText) {
  const r = await control(req);
  if (r?.ok) { flash(context, okText, GREEN, 1200); send({ event: "showOk", context }); }
  else fail(context, r ? "FAILED" : "OFFLINE", r?.message);
  schedulePoll(150);
}

function fail(context, text, why) {
  if (why) console.error(`zamarec deck plugin: ${why}`);
  flash(context, text, AMBER, 1500);
  send({ event: "showAlert", context });
}

function flash(context, text, colour, ms) {
  const key = keys.get(context);
  if (!key) return;
  key.flash = { text, colour, until: Date.now() + ms };
  render(context);
  setTimeout(() => { if (key.flash && Date.now() >= key.flash.until) { key.flash = null; render(context); } }, ms + 20);
}

// Opening the window is only ever done here, on a key press. The CLI knows how to raise an open one.
function openUi(context) {
  const cli = findCli();
  const detached = { detached: true, stdio: "ignore" };
  try {
    if (cli) spawn(cli, ["ui"], detached).unref();
    else if (process.platform === "win32") spawn("cmd", ["/c", "start", "", status?.ui_url || "http://127.0.0.1:3556/"], { ...detached, windowsHide: true }).unref();
    else spawn("xdg-open", [status?.ui_url || "http://127.0.0.1:3556/"], detached).unref();
    flash(context, "OPENING", BLUE, 1000);
  } catch (e) { fail(context, "NO CLI", String(e)); }
}

function findCli() {
  const exe = process.platform === "win32" ? "zamarec.exe" : "zamarec";
  const candidates = [globalSettings.cli, path.join(os.homedir(), ".local", "bin", exe),
    ...(process.env.PATH || "").split(path.delimiter).filter(Boolean).map(d => path.join(d, exe))];
  return candidates.find(p => p && fs.existsSync(p));
}

// ---------------------------------------------------------------- drawing

const { keySvg, infoSvg, wrap, G, BLUE, RED, AMBER, GREY, GREEN, PAPER, MUTED } = require("./draw");

function elapsed(s, seconds) {
  const t = Math.max(0, Math.floor((s.elapsed_seconds ?? 0) + (Date.now() - s.at) / 1000));
  const h = Math.floor(t / 3600), m = Math.floor((t % 3600) / 60), sec = t % 60;
  if (seconds) return h ? `${h}:${String(m).padStart(2, "0")}:${String(sec).padStart(2, "0")}` : `${m}:${String(sec).padStart(2, "0")}`;
  return h ? `${h}h${String(m).padStart(2, "0")}` : `${m} min`;
}

// The daemon's pause descriptions (PauseControl.Describe), cut to fit under a key.
const pauseText = p => p === "for the rest of this game" ? "this game" : p === "until turned back on" ? "until resumed" : short(p, 15);

const short = (text, n = 13) => (!text ? "" : text.length > n ? text.slice(0, n - 1) + "…" : text);

function look(key) {
  const s = status, set = key.settings;
  const offline = { sticker: GREY, glyph: G.off, opacity: 0.55, big: "OFFLINE", small: "no daemon", bigColour: MUTED };
  switch (key.action) {
    case "recording":
      if (!s) return offline;
      if (s.state === "recorder-died") return { sticker: RED, glyph: G.died, big: "DIED", small: short(s.profile), bigColour: RED };
      if (s.state === "recording")
        return { sticker: RED, glyph: G.rec, big: elapsed(s, set.seconds), small: short(s.profile) + (s.unconfirmed ? "?" : ""), bigColour: RED,
          badge: set.hideCount ? null : s.marker_count };
      if (s.paused) return { sticker: AMBER, glyph: G.paused, big: "AUTO OFF", small: pauseText(s.paused), bigColour: AMBER };
      return { sticker: BLUE, glyph: G.idle, big: "IDLE", small: "waiting" };
    case "mark":
      if (!s) return { ...offline, glyph: G.flag };
      return recording()
        ? { sticker: RED, glyph: G.flag, big: "MARK", small: short(set.label) || `${s.marker_count} so far`, badge: s.pending_marks > 0 ? "…" : null }
        : { sticker: GREY, glyph: G.flag, opacity: 0.55, big: "MARK", small: "not recording", bigColour: MUTED };
    case "save": {
      const secs = parseInt(set.seconds, 10) > 0 ? parseInt(set.seconds, 10) : s?.save_seconds ?? 30;
      if (!s) return { ...offline, glyph: G.clip };
      return recording()
        ? { sticker: RED, glyph: G.clip, big: `SAVE ${secs}s`, small: short(set.label) || "clip" }
        : { sticker: GREY, glyph: G.clip, opacity: 0.55, big: `SAVE ${secs}s`, small: "not recording", bigColour: MUTED };
    }
    case "pause":
      if (!s) return { ...offline, glyph: G.power };
      return s.paused
        ? { sticker: AMBER, glyph: G.power, big: "AUTO OFF", small: pauseText(s.paused), bigColour: AMBER }
        : { sticker: BLUE, glyph: G.power, big: "AUTO ON", small: "press to pause" };
    case "ui":
      return { sticker: s ? BLUE : GREY, glyph: G.window, opacity: s ? 1 : 0.55, big: "ZamaREC", small: "open window" };
    case "info":
      return { info: true, ...info(set.show || "time", s, set) };
  }
  return offline;
}

// ---------------------------------------------------------------- display keys

const CT = "#7aa5d6", T = "#e0a93f";
const gb = n => n >= 1e12 ? `${(n / 1e12).toFixed(1)} TB` : n >= 1e10 ? `${Math.round(n / 1e9)} GB` : `${(n / 1e9).toFixed(1)} GB`;
const mapName = m => (m || "").replace(/^(de|cs|ar|gd|dz)_/, "");

function ago(ts) {
  const s = Math.max(0, (Date.now() - new Date(ts).getTime()) / 1000);
  return s < 60 ? "just now" : s < 3600 ? `${Math.floor(s / 60)} min ago` : s < 86400 ? `${Math.floor(s / 3600)} h ago` : `${Math.floor(s / 86400)} d ago`;
}

/** The game block when CS2 has spoken in the last two minutes; a stale one is a game that has closed. */
const game = s => (s?.game && s.gsi_last_seconds != null && s.gsi_last_seconds < 120 ? s.game : null);

/** What a display key shows, by its "show" setting. */
function info(show, s, set) {
  if (!s) return { label: "ZAMAREC", accent: GREY, big: "OFFLINE", bigColour: MUTED, small: "no daemon", dim: true };
  const rec = s.state === "recording";
  const noGame = label => ({ label, accent: GREY, big: "—", bigColour: MUTED, small: "not in a match", dim: true });
  switch (show) {
    case "time":
      if (!rec) return { label: "SESSION", accent: s.paused ? AMBER : BLUE, big: s.paused ? "AUTO OFF" : "IDLE", bigColour: s.paused ? AMBER : PAPER, small: s.paused ? pauseText(s.paused) : "waiting" };
      return { label: short(s.profile, 12).toUpperCase(), accent: RED, big: elapsed(s, set.seconds), bigColour: RED, small: `${s.marker_count} markers` };
    case "window": {
      if (!rec) return { label: "WINDOW", accent: GREY, big: "—", bigColour: MUTED, small: "not recording", dim: true };
      const have = Math.floor((s.window_seconds ?? s.recorded_seconds ?? 0) / 60), limit = s.window_limit_seconds;
      return { label: s.mode === "buffer" ? "BUFFER" : "WINDOW", accent: RED, big: limit ? `${have}/${Math.round(limit / 60)}m` : `${have} min`,
        bar: limit ? { frac: (s.window_seconds ?? 0) / limit, colour: RED } : null, small: s.in_ram ? "in RAM" : "on disk" };
    }
    case "markers":
      if (!rec) return { label: "MARKERS", accent: GREY, big: "—", bigColour: MUTED, small: "not recording", dim: true };
      return { label: "MARKERS", accent: RED, big: String(s.marker_count), small: s.pending_marks > 0 ? `${s.pending_marks} clip pending` : short(s.profile, 14) };
    case "score": {
      const g = game(s);
      if (!g || g.score_ct == null) return noGame("SCORE");
      const mine = g.team === "CT" ? g.score_ct : g.team === "T" ? g.score_t : null, theirs = g.team === "CT" ? g.score_t : g.score_ct;
      return { label: mapName(g.map).toUpperCase() || "CS2", accent: g.team === "T" ? T : CT,
        parts: mine == null
          ? [{ text: String(g.score_ct), colour: CT }, { text: " : ", colour: MUTED }, { text: String(g.score_t), colour: T }]
          : [{ text: String(mine), colour: g.team === "CT" ? CT : T }, { text: " : ", colour: MUTED }, { text: String(theirs), colour: g.team === "CT" ? T : CT }],
        small: g.phase === "warmup" ? "warmup" : g.phase === "gameover" ? "game over" : `round ${(g.round ?? 0) + 1}` + (g.team ? ` · ${g.team}` : "") };
    }
    case "kd": {
      const g = game(s);
      if (!g || g.kills == null) return noGame("K / A / D");
      const kd = g.deaths ? (g.kills / g.deaths).toFixed(2) : String(g.kills);
      return { label: "K / A / D", accent: g.team === "T" ? T : CT, big: `${g.kills}/${g.assists ?? 0}/${g.deaths ?? 0}`, small: `KD ${kd}` + (g.mvps ? ` · ${g.mvps} MVP` : "") };
    }
    case "map": {
      const g = game(s);
      if (!g) return noGame("MAP");
      return { label: "MAP", accent: g.team === "T" ? T : CT, big: mapName(g.map), small: [g.mode, g.phase].filter(Boolean).join(" · ") };
    }
    case "last": {
      const e = s.last_event;
      if (!e) return { label: "LAST EVENT", accent: GREY, big: "—", bigColour: MUTED, small: "nothing yet", dim: true };
      return { label: e.kind.toUpperCase(), accent: e.level === "error" ? RED : e.level === "warn" ? AMBER : BLUE, lines: wrap(e.message, 15, 3), small: ago(e.ts) };
    }
    case "ram": {
      const st = s.storage;
      if (!st) return { label: "RAM", accent: GREY, big: "…", bigColour: MUTED };
      const total = st.ram_used_bytes + st.ram_free_bytes;
      return { label: "RAM FOOTAGE", accent: AMBER, big: gb(st.ram_used_bytes), bar: { frac: total ? st.ram_used_bytes / total : 0, colour: AMBER }, small: `${gb(st.ram_free_bytes)} free` };
    }
    case "disk": {
      const st = s.storage;
      if (!st) return { label: "DISK", accent: GREY, big: "…", bigColour: MUTED };
      const used = st.disk_total_bytes - st.disk_free_bytes;
      return { label: "DISK FREE", accent: BLUE, big: gb(st.disk_free_bytes), bar: { frac: st.disk_total_bytes ? used / st.disk_total_bytes : 0, colour: used / st.disk_total_bytes > 0.9 ? RED : BLUE }, small: `of ${gb(st.disk_total_bytes)}` };
    }
  }
  return { label: "?", big: "—" };
}

function render(context) {
  const key = keys.get(context);
  if (!key) return;
  let l = look(key);
  if (key.flash) l = { ...l, big: key.flash.text, parts: null, lines: null, bigColour: key.flash.colour, ring: key.flash.colour, small: l.small };
  const image = l.info ? infoSvg(l) : keySvg(l);
  if (image === key.image) return;   // the deck app writes every image it is sent; send only changes
  key.image = image;
  send({ event: "setImage", context, payload: { image, target: 0 } });
}

function renderAll() { for (const c of keys.keys()) render(c); }
