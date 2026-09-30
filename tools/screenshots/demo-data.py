#!/usr/bin/env python3
"""Builds a demo library for screenshots: made-up games, synthetic footage, sessions, markers and clips.

    demo-data.py <dir>

Everything lands under <dir>: a config for a daemon on ports 4555/4556 that records nothing (no profile can
match, and it scans for games once a day), its recordings root, and its data dir. No screen is captured and nothing real
is read: the video is ffmpeg's gradient generator with a caption, the audio is quiet noise.
Run the daemon on it with:
    KAIREC_SOCKET=<dir>/kairec.sock XDG_DATA_HOME=<dir>/data Kairec.Daemon <dir>/config.toml
"""
import json, os, random, subprocess, sys, uuid
from datetime import datetime, timedelta, timezone

OUT = os.path.abspath(sys.argv[1])
ROOT, DATA = f"{OUT}/root", f"{OUT}/data/kairec"
FONT = subprocess.run(["fc-match", "-f", "%{file}", "sans:bold"], capture_output=True, text=True).stdout
TZ = datetime.now().astimezone().tzinfo
NOW = datetime.now(TZ).replace(microsecond=0)
TRACKS = ["mix", "game", "mic", "discord"]
random.seed(7)

GAMES = {   # name: gradient colours
    "Starfall Arena": ("0x16213e", "0xff4a2b", "0x0b0e14", "0x4cc9f0"),
    "Deep Harbor":    ("0x0b3d4f", "0x0b0e14", "0x1f8a70", "0x9ad0ff"),
    "Moss & Mortar":  ("0x2d4a22", "0xf5b544", "0x16181d", "0x8ab17d"),
    "Ion Drift":      ("0x3a0ca3", "0xf72585", "0x0b0e14", "0x4cc9f0"),
}

def run(*args):
    subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", *args], check=True)

def esc(text):   # drawtext escaping
    return text.replace("\\", "\\\\").replace(":", "\\:").replace("'", "’").replace("&", "\\&")

def video_source(game, seconds, size="1280x720", caption=True):
    c = GAMES[game]
    src = f"gradients=s={size}:r=30:duration={seconds}:speed=0.012:nb_colors=4:c0={c[0]}:c1={c[1]}:c2={c[2]}:c3={c[3]},eq=brightness=-0.10:saturation=0.8,vignette=PI/3.2"
    if caption:
        src += (f",drawtext=fontfile={FONT}:text='{esc(game.upper())}':fontsize=58:fontcolor=white@0.9:x=(w-tw)/2:y=h*0.40"
                f",drawtext=fontfile={FONT}:text='demo footage   %{{pts\\:hms}}':fontsize=22:fontcolor=white@0.6:x=(w-tw)/2:y=h*0.40+78")
    return src

def audio_inputs(n, seconds):
    colours = ["pink", "brown", "white", "pink"]
    args = []
    for i in range(n):
        args += ["-f", "lavfi", "-i", f"anoisesrc=color={colours[i % 4]}:amplitude=0.03:duration={seconds}:seed={i + 1}"]
    return args

def encode(game, seconds, tracks, extra):
    maps = ["-map", "0:v"] + sum((["-map", f"{i + 1}:a"] for i in range(tracks)), [])
    run("-f", "lavfi", "-i", video_source(game, seconds), *audio_inputs(tracks, seconds), *maps,
        "-c:v", "h264_nvenc", "-preset", "p4", "-b:v", "2500k", "-g", "30", "-c:a", "aac", "-b:a", "96k", *extra)

def iso(t): return t.isoformat()
def mono(t): return int(t.timestamp() * 1e6)

def banner(name):
    os.makedirs(f"{ROOT}/banners/custom", exist_ok=True)
    slug = "".join(ch if ch.isalnum() else "-" for ch in name.lower()).strip("-")
    while "--" in slug: slug = slug.replace("--", "-")
    c = GAMES[name]
    run("-f", "lavfi", "-i", f"gradients=s=460x215:nb_colors=4:c0={c[0]}:c1={c[1]}:c2={c[2]}:c3={c[3]}:x0=0:y0=0:x1=460:y1=215"
        f",drawtext=fontfile={FONT}:text='{esc(name)}':fontsize=38:fontcolor=white:x=24:y=h-th-26:shadowcolor=black@0.5:shadowx=0:shadowy=2",
        "-frames:v", "1", f"{ROOT}/banners/custom/{slug}.png")

def session(game, started, seconds, mode="timeline", tracks=1, footage=True, ram=False, markers=(), clips=()):
    sid = f"{started:%Y-%m-%d_%H-%M-%S}_{game}"
    d = f"{ROOT}/sessions/{sid}"
    os.makedirs(d, exist_ok=True)
    info = {
        "id": sid, "profile": game, "mode": mode, "trigger": "auto", "started_at": iso(started),
        "ended_at": iso(started + timedelta(seconds=seconds)), "spawn_mono_us": mono(started), "first_frame_mono_us": mono(started),
        "duration_seconds": seconds, "recorded_bytes": int(seconds * 2_600_000 / 8), "clips": list(clips),
    }
    if tracks > 1: info["audio_tracks"] = TRACKS[:tracks]
    if mode == "timeline":
        info["first_frame_realtime_us"] = mono(started)
        info["playlist"] = "live.m3u8"
        if ram: info["media_dir"] = f"{OUT}/ram/{sid}"; info["footage_gone"] = not footage
        if footage:
            media = info.get("media_dir", d); os.makedirs(media, exist_ok=True)
            encode(game, seconds, tracks, ["-f", "hls", "-hls_time", "5", "-hls_list_size", "0", "-hls_segment_filename", f"{media}/seg_%06d.ts", f"{media}/live.m3u8"])
            segs, t, dur, seq = [], 0.0, None, 0
            for line in open(f"{media}/live.m3u8"):
                line = line.strip()
                if line.startswith("#EXTINF:"): dur = float(line[8:].split(",")[0])
                elif line and not line.startswith("#") and dur is not None:
                    segs.append({"seq": seq, "file": line, "start": t, "duration": dur, "bytes": os.path.getsize(f"{media}/{line}")})
                    t += dur; seq += 1; dur = None
            json.dump({"segments": segs}, open(f"{d}/segments.json", "w"))
            info["recorded_bytes"] = sum(s["bytes"] for s in segs)
    else:
        info["bytes_estimated"] = True
    json.dump(info, open(f"{d}/session.json", "w"), indent=1)
    json.dump({"markers": [{
        "id": uuid.uuid4().hex[:12], "type": m["type"], "label": m.get("label"), "t_mono_us": mono(started) + int(m["t"] * 1e6), "t_video": m["t"],
        "at": iso(started + timedelta(seconds=m["t"])), "starred": m.get("starred", False), "clip": m.get("clip"), "data": m.get("data"),
    } for m in markers]}, open(f"{d}/markers.json", "w"), indent=1)
    return sid

def clip(game, name, recorded, seconds, source, label=None, tracks=1, session_id=None, offset=None, starred=False, expires=None, markers=(), bitrate="2500k", size="1280x720"):
    d = f"{ROOT}/clips/{game}"; os.makedirs(d, exist_ok=True)
    file = f"{recorded:%Y-%m-%d_%H-%M-%S}_{name}.mp4"
    maps = ["-map", "0:v"] + sum((["-map", f"{i + 1}:a"] for i in range(tracks)), [])
    run("-f", "lavfi", "-i", video_source(game, seconds, size), *audio_inputs(tracks, seconds), *maps,
        "-c:v", "h264_nvenc", "-preset", "p4", "-b:v", bitrate, "-g", "30", "-c:a", "aac", "-b:a", "96k",
        *sum(([f"-metadata:s:a:{i}", f"title={TRACKS[i]}"] for i in range(tracks)), []), "-movflags", "+faststart", f"{d}/{file}")
    info = {"profile": game, "label": label, "source": source, "session_id": session_id, "session_offset": offset, "duration_seconds": seconds,
            "created_at": iso(recorded), "starred": starred, "expires_at": iso(expires) if expires else None,
            "markers": [{"t": m["t"], "type": m["type"], "label": m.get("label"), "data": m.get("data")} for m in markers]}
    if tracks > 1: info["audio_tracks"] = TRACKS[:tracks]
    json.dump({k: v for k, v in info.items() if v is not None}, open(f"{d}/{file}.json", "w"), indent=1)
    return file

def match_markers(seconds):
    """A plausible match: rounds of about 75 s with kills, the odd death, a result, now and then an MVP or a mark by hand."""
    out, t, rnd = [{"type": "map_start", "t": 2.0, "label": "foundry"}], 14.0, 0
    while t < seconds - 40:
        rnd += 1; end = min(seconds - 8, t + random.uniform(55, 90)); kills = 0; alive = True; k = t + random.uniform(8, 20)
        while k < end - 6 and alive:
            if random.random() < 0.72:
                kills += 1; out.append({"type": "kill", "t": round(k, 1), "data": {"count": kills, "headshot": random.random() < 0.4, "round": rnd}})
            else:
                alive = False; out.append({"type": "death", "t": round(k, 1), "data": {"round": rnd}})
            k += random.uniform(4, 16)
        won = random.random() < 0.58
        out.append({"type": "round_win" if won else "round_loss", "t": round(end, 1), "label": f"round {rnd}", "data": {"round": rnd}})
        if won and kills >= 2 and random.random() < 0.6: out.append({"type": "mvp", "t": round(end + 1.5, 1), "data": {"round": rnd}})
        t = end + random.uniform(6, 12)
    out.append({"type": "manual", "t": 187.4, "label": "nice flick", "starred": True})
    out.append({"type": "manual", "t": 512.0, "label": "smoke lineup"})
    return sorted(out, key=lambda m: m["t"])

def main():
    os.makedirs(DATA, exist_ok=True)
    for g in GAMES: banner(g)

    # tonight: the long match with game events and separate audio tracks, footage on disk
    t0 = NOW - timedelta(hours=1, minutes=12)
    markers = match_markers(750)
    ace = next(m for m in markers if m["type"] == "kill" and m["data"]["count"] >= 3) if any(m["type"] == "kill" and m["data"]["count"] >= 3 for m in markers) else markers[5]
    c1 = clip("Starfall Arena", "triple-on-foundry", t0 + timedelta(seconds=ace["t"] + 20), 45, "mark", "triple on Foundry", tracks=4, offset=ace["t"] - 25,
              expires=NOW + timedelta(hours=29), markers=[{"t": 25, "type": "kill", "data": ace["data"]}])
    c2 = clip("Starfall Arena", "clutch-1v3", t0 + timedelta(seconds=470), 38, "export", "clutch 1v3", tracks=4, offset=432, starred=True,
              markers=[{"t": 9, "type": "kill", "data": {"count": 1}}, {"t": 17, "type": "kill", "data": {"count": 2, "headshot": True}}, {"t": 29, "type": "kill", "data": {"count": 3}}, {"t": 33, "type": "round_win"}])
    ace["clip"] = c1
    sid = session("Starfall Arena", t0, 750, tracks=4, markers=markers, clips=[c1, c2])
    for f in (c1, c2):
        p = f"{ROOT}/clips/Starfall Arena/{f}.json"; d = json.load(open(p)); d["session_id"] = sid; json.dump(d, open(p, "w"), indent=1)
    clip("Starfall Arena", "clutch-1v3-10mb", t0 + timedelta(seconds=470), 38, "share", "clutch 1v3 10mb", session_id=sid, offset=432, bitrate="1700k")

    # earlier today: a buffer-mode session, which keeps only what was marked or saved
    t1 = NOW - timedelta(hours=5, minutes=40)
    b1 = clip("Deep Harbor", "wreck-found", t1 + timedelta(seconds=1310), 120, "mark", "wreck found", offset=1190, markers=[{"t": 90, "type": "manual", "label": "wreck found"}])
    b2 = clip("Deep Harbor", "leviathan", t1 + timedelta(seconds=2890), 75, "save", "leviathan", offset=2815, starred=True, markers=[{"t": 75, "type": "save", "label": "leviathan"}])
    bsid = session("Deep Harbor", t1, 3420, mode="buffer", markers=[{"type": "manual", "t": 1280, "label": "wreck found", "clip": b1}, {"type": "save", "t": 2890, "label": "leviathan", "clip": b2}], clips=[b1, b2])
    for f in (b1, b2):
        p = f"{ROOT}/clips/Deep Harbor/{f}.json"; d = json.load(open(p)); d["session_id"] = bsid; json.dump(d, open(p, "w"), indent=1)

    # yesterday: a RAM timeline whose footage has been dropped, and a short one still on disk
    t2 = NOW - timedelta(days=1, hours=2)
    session("Moss & Mortar", t2, 2710, ram=True, footage=False, markers=[{"type": "manual", "t": 640, "label": "base finished"}, {"type": "manual", "t": 2101, "label": "creeper"}])
    session("Starfall Arena", NOW - timedelta(days=1, hours=4), 180, markers=match_markers(180)[:9])
    clip("Moss & Mortar", "base-tour", t2 + timedelta(seconds=700), 60, "export", "base tour", starred=True)

    # a Steam game seen without a profile (the Games page lists it), and lifetime counters for the Stats page
    seen = lambda app, name, n, ago: {"app_id": app, "name": name, "first_seen": iso(NOW - timedelta(days=9)), "last_seen": iso(NOW - ago), "launches": n}
    json.dump([seen(910440, "Ion Drift", 3, timedelta(days=2)), seen(910120, "Starfall Arena", 14, timedelta(hours=1)), seen(910260, "Deep Harbor", 6, timedelta(hours=5))], open(f"{DATA}/games.json", "w"))
    json.dump({"sessions": 31, "seconds_recorded": 118800, "disk_bytes": 41_200_000_000, "ram_bytes": 96_400_000_000, "clip_bytes": 3_900_000_000, "clips": 84, "marks": 212, "saves": 37, "exports": 19},
              open(f"{DATA}/stats.json", "w"))

    open(f"{OUT}/config.toml", "w").write(f'''[storage]
root = "{ROOT}"
[capture]
monitor = "DP-1"
quality = "high"
audio = ["default_output", "default_input"]
multitrack = true
[[capture.tracks]]
name = "game"
sources = ["app-inverse:WEBRTC VoiceEngine"]
[[capture.tracks]]
name = "mic"
sources = ["default_input"]
[[capture.tracks]]
name = "discord"
sources = ["app:WEBRTC VoiceEngine"]
[timeline]
ram_dir = "{OUT}/ram"
[feedback]
sound = false
[daemon]
gsi_port = 4555
ui_port = 4556
process_scan_seconds = 86400      # one look at /proc when it starts, then none: this daemon never records
[gsi]
cfg_dir = "{OUT}/no-gsi"
capture_raw = false
[share]
discord_webhook = "https://discord.com/api/webhooks/0/demo"
[[profiles]]
name = "Starfall Arena"
steam_app_id = 910120
match = "^starfall-demo$"
mode = "timeline"
events = "cs2-gsi"
timeline.window_minutes = 45
timeline.storage = "ram"
clips.mark_before_seconds = 25
clips.mark_after_seconds = 20
[[profiles]]
name = "Deep Harbor"
steam_app_id = 910260
match = "^deepharbor-demo$"
mode = "buffer"
quality = "medium"
buffer.minutes = 20
[[profiles]]
name = "Moss & Mortar"
match = "^mossmortar-demo$"
mode = "timeline"
quality = "medium"
timeline.window_minutes = 60
timeline.storage = "ram"
''')
    print("demo library in", OUT)

main()
