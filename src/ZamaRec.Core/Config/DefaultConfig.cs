namespace ZamaRec.Core.Config;

public static class DefaultConfig
{
    /// <summary>The default file for the system this runs on: the same text, with the monitor named the way its recorder wants.</summary>
    public static string ForThisSystem() => Platform.IsWindows ? Toml.Replace("monitor = \"DP-1\" ", "monitor = \"0\"    ") : Toml;

    public const string Toml = """
        # ZamaREC configuration. After editing by hand run:
        #   zamarec reload      (or use Settings in the web UI)

        [storage]
        root = "~/Videos/zamarec"          # sessions/ and clips/ live under here

        [capture]
        monitor = "DP-1"                  # gpu-screen-recorder --list-monitors (Windows: the display number, 0 = first)
        gpu = "nvidia"                    # nvidia | amd | intel: whose encoder ffmpeg uses (Windows: also for recording)
        quality = "high"                  # default preset (see [[quality]]); profiles may pick another
        audio = ["default_output"]        # add "default_input" to record the microphone
        cursor = false
        # multitrack = true               # also record the [[capture.tracks]] below as separate audio tracks, for mixing
        #                                 # afterwards. The first track stays the mix of `audio`, so clips play anywhere.
        # fps / codec / bitrate_kbps / keyframe_seconds may still be set here; presets fall back to them.

        # Two capture modes, chosen per profile:
        #   timeline  a rolling window of the last N minutes on disk. Scrub and export from anywhere
        #             in the window, even mid-session. Older footage is deleted as you play.
        #   buffer    a ring buffer in RAM. Nothing touches the disk until you mark or save.
        #             A mark dumps mark_before_seconds + mark_after_seconds around it into a clip.

        [timeline]                        # defaults; profiles override under timeline.*
        window_minutes = 120              # 0 = keep everything
        segment_seconds = 5                # a save waits for the current segment to finish, so keep this short
        storage = "disk"                  # disk | ram. ram = segments on tmpfs (/dev/shm): scrubbable, no disk
                                          # writes, needs window_minutes x bitrate of RAM (60 min @ 20000 = 9 GB)
        ram_keep_hours = 6                # RAM footage is dropped after this, or when the next session starts

        [buffer]                          # defaults; profiles override under buffer.*
        minutes = 30                      # ~150 MB of RAM per minute at 20000 kbps
        storage = "ram"                   # ram | disk

        [clips]                           # defaults; profiles override under clips.*
        save_seconds = 30                 # `zamarec save` with no --last
        mark_before_seconds = 1500        # buffer mode: 25 min before a mark...
        mark_after_seconds = 300          # ...and 5 min after it
        mark_clip_expire_hours = 30       # mark clips are deleted after this unless starred (0 = never)
        # mark_saves = true               # timeline mode: also write a clip around every mark.
                                          # unset = on for RAM timelines, off for disk ones. buffer mode always does.
        event_clips = ["kill:2", "mvp"]   # game events that write a clip like a mark: kill:N, mvp, death, round_win, round_loss

        [gsi]                             # CS2 game state integration
        cfg_dir = ""                      # CS2's csgo/cfg folder; empty = find it via Steam. CS2 reads the cfg at launch.
        capture_raw = true                # keep every payload in the session folder (gsi.jsonl)

        [retention]                       # per profile, sessions + clips; profiles override under retention.*
        max_gb = 100                      # over this, the oldest unprotected item goes first
        max_days = 30                     # older than this goes. 0 = no limit
        # Never deleted: starred clips, the live session, sessions with starred markers or with clips on disk.
        # Mark clips additionally expire after clips.mark_clip_expire_hours.

        [auto_games]                      # Steam games with no profile are recorded too, to RAM only
        enabled = true                    # false = such games are only listed as seen
        ram_gb = 2                        # the rolling window is as long as this much RAM holds at the quality's bitrate
        # quality = "medium"              # unset = capture.quality
        ignore = []                       # Steam app ids never to record this way

        [share]
        # discord_webhook = "https://discord.com/api/webhooks/..."   # a clip's Share menu can post to this channel; keep it secret
        discord_max_mb = 10               # the channel's upload limit; bigger clips are re-encoded to fit first

        [hotkeys]                         # Windows only: system-wide keys the daemon registers itself.
        mark = "ctrl+alt+m"               # Linux binds the zamarec CLI to desktop shortcuts instead.
        save = "ctrl+alt+s"
        ui = "ctrl+alt+k"

        [feedback]
        sound = true                      # short tone on mark/save. Never a popup, never focus.
        volume = 0.5

        [daemon]
        gsi_port = 3555                   # CS2 game state integration POSTs here
        ui_port = 3556                    # web timeline
        bind = "127.0.0.1"
        ui_window = "app"                 # app = the UI gets its own window (Chrome/Chromium app mode, own profile); browser = a tab

        # Quality presets. A profile picks one with quality = "high"; blank means the [capture] values.
        # Fields left out fall back to [capture]. scale limits the output size, e.g. "1280x720".
        [[quality]]
        name = "low"
        fps = 30
        codec = "h264"
        bitrate_kbps = 6000
        scale = "1280x720"
        [[quality]]
        name = "medium"
        fps = 60
        codec = "h264"
        bitrate_kbps = 12000
        [[quality]]
        name = "high"
        fps = 60
        codec = "h264"                    # h264 plays in every browser; hevc/av1 are smaller but Firefox will not play them
        bitrate_kbps = 20000              # ~9 GB per hour
        [[quality]]
        name = "ultra"
        fps = 60
        codec = "h264"
        bitrate_kbps = 35000

        [[profiles]]
        name = "cs2"
        match = "^cs2$"                   # regex on the process name
        mode = "timeline"
        events = "cs2-gsi"
        timeline.window_minutes = 120

        [[profiles]]
        name = "minecraft"
        match_cmdline = "net\\.minecraft|minecraft-launcher|fabric|forge|prismlauncher"
        mode = "buffer"
        buffer.minutes = 30
        """;
}
