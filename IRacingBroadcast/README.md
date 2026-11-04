# IRacingBroadcast

Turns an iRacing replay (`.rpy`) of a race into a narrated highlights video for YouTube, about 5 minutes long, in the style of Formula 1's race highlights. The video has:

- the start, the race's best moments in race order, and the finish: lead changes, overtakes, contact, and your own race;
- one narrator who tells the story of the race over the clips, written by Claude and voiced locally (Kokoro's am_michael by default);
- camera cuts chosen like a TV director would, on the attacker before the move and on the car in trouble;
- broadcast graphics: a standings tower, a name tag on every cut, event banners, an intro card, a wipe between clips and a results table;
- the sim's own sound, dipped under the voice. Drivers' voice chat from the replay is left out: the app presses iRacing's *Mute Voice Chat* key (`MuteVoiceChatKeys`, Ctrl+Shift+Alt+M) when the recording starts and again when it ends. As a backup, the edit also mutes the sim sound wherever iRacing reports someone talking.

It works with any iRacing race replay and any driver in it.

## Use it

Double-click `run.cmd` (or drag an `.rpy` onto it). It builds the app and opens the window:

1. **Replay**: pick the race. The list shows your saved replays, newest first; Browse finds others.
2. Open the same replay in iRacing (watch it from the race results, or under Replays). The app checks that iRacing has the right one open.
3. **Follow** the driver whose race you want featured (default: whoever saved the replay), and choose what the **Highlights** are of:
   - **The race, plus my car**: the race's story, with about a third of the time on the featured driver.
   - **Mostly my race**: the featured driver's start, battles and finish, with the leaders for context.
   - **The race only**: TV highlights, with no special treatment for anyone.
4. Pick how the **Narration** is written and which **Voice** reads it.
5. **Make video**. Finished stages are skipped, so you can close the app at any time and carry on later; **Stop** keeps what's finished. **Redo** on a stage runs it again; the stages after it that depend on it become "out of date" and are redone by the next Make video. A new script, for example, doesn't need a new recording.

| Stage | What happens | Time |
|---|---|---|
| 1 Scan | The whole race plays at 16× while the app records every car from the SDK. | ~3 min for a 45-minute race |
| 2 Plan | It finds the story (overtakes, battles, incidents, pit stops, lead changes, every car taking the flag), picks the highlights and plans their camera shots. | seconds |
| 3 Narration | Claude writes the voiceover and it's voiced. | ~1–2 min |
| 4 Record | For each highlight, the replay jumps there and plays at 1× with the replay UI hidden. The app cuts the cameras and records the screen and sound with ffmpeg. | ~10 min |
| 5 Edit | The clips, graphics, narration and sound are combined into the mp4. | ~2 min |

**While it records, leave iRacing in front and don't touch the PC.** It's a screen recording, so anything on top of the sim ends up in the video. The window minimises itself. If the replay stops (a click in the sim), the app presses play again.

### Narration without Claude Code (paste mode)

With **Narration: Paste into Claude yourself**, the app writes everything Claude needs into a prompt after the plan stage:

1. **Copy the prompt** and paste it into a new Claude chat (claude.ai, or Claude Code).
2. Paste Claude's whole reply into the box and click **Use this reply**. The app checks it and voices it.

The recording goes ahead meanwhile; only the edit waits for the reply. The files are `narration-prompt.txt` and `narration-reply.json` in the work folder.

### From a terminal

```
IRacingBroadcast.exe "C:\Users\you\Documents\iRacing\replay\subses12345678.rpy" --cli [options]
```

You get these files in `Videos\iRacing Broadcasts\`:

- `<replay>-highlights.mp4`: the video;
- `<replay>-highlights.srt`: subtitles for YouTube;
- `<replay>-highlights.txt`: a title, a description and a chapter for each highlight, ready to paste.

### Options

```
--featured "Name"     driver to follow (default: whoever saved the replay)
--focus mine          what the highlights are of: mixed (the race plus your car, default) | mine | race
--out video.mp4       where the video goes
--from plan           redo from a stage: scan | plan | commentary | shoot | edit (finished stages are otherwise skipped)
--only plan           run just one stage
--claude              Claude writes the narration through the Claude Code CLI (your subscription)
--paste               write a prompt to paste into Claude yourself; the edit waits for the reply
--no-claude           simple built-in narration, no Claude
--demo                offline test with a made-up race (no iRacing needed)
```

Each stage saves its work in `Videos\iRacing Broadcasts\work\<replay>\`. Some examples:

- Don't like the script? Redo Narration (or `--from commentary`). You get a new script and the same recording, without iRacing.
- Want other highlights? Change **Highlights** (or `--focus`): the plan, narration and recording are redone; the scan is kept.
- Want to change the graphics or the mix? Redo Edit (`--from edit`).
- The clip recordings (`clip-NN.mkv`) take a few GB. Delete the folder once you're happy with the video.

## What it needs

- **iRacing** with the replay. The app refuses to run against a live session: it never drives the cameras or records while you're racing.
- **ffmpeg** for recording and editing: `winget install Gyan.FFmpeg`, or set `"Ffmpeg"` in `broadcast.json` to the full path of `ffmpeg.exe`.
- **Claude Code** for Claude's narration (**Narration: Claude Code**): the app runs `claude -p` headless with its tools off, on your Claude subscription. Install it and run `claude` once to sign in. The app removes `ANTHROPIC_API_KEY` and `ANTHROPIC_AUTH_TOKEN` from the CLI's environment, so it never bills an API key. If the CLI is missing or signed out, the narration stage stops and says so; use paste mode or the simple script instead.
- **Kokoro** (the voice, **Voice: Kokoro**): the sherpa-onnx Kokoro model `kokoro-multi-lang-v1_0` in `..\IRacingVoice\voices`, shared with the IRacingVoice app and downloaded the first time either app needs it (~330 MB). Natural, on the CPU, about a quarter of real time. am_michael narrates; bm_george is the colour voice in two-voice scripts.
- **Chatterbox** (optional; **Voice: Chatterbox** in the window): a much more natural, excited voice on an NVIDIA GPU. It needs its own Python, about 7 GB, in `%USERPROFILE%\.iracingbroadcast\chatterbox`. Install it from PowerShell:

  ```
  $root = "$env:USERPROFILE\.iracingbroadcast\chatterbox"; $env:UV_PYTHON_INSTALL_DIR = "$root\python"; $env:UV_CACHE_DIR = "$root\cache"
  # uv from https://github.com/astral-sh/uv/releases, unzipped into $root\bin
  & "$root\bin\uv.exe" venv "$root\venv" --python 3.11
  "torch==2.8.0+cu128`ntorchaudio==2.8.0+cu128" | Set-Content "$root\overrides.txt"   # RTX 50-series needs CUDA 12.8
  & "$root\bin\uv.exe" pip install --python "$root\venv\Scripts\python.exe" --override "$root\overrides.txt" --index https://download.pytorch.org/whl/cu128 --index-strategy unsafe-best-match chatterbox-tts "setuptools<81"
  ```

  (`setuptools<81`: Chatterbox's watermarker still imports `pkg_resources`, which newer setuptools dropped.)

  The model (~3 GB) downloads into `hf\` the first time it voices. The narrator's voice is copied from a ~10 s clip: by default, one spoken by their Kokoro voice (`KokoroVoice`). To change it, pick another Kokoro voice, set `PlayByPlayClip` to `builtin` for Chatterbox's own voice, or set it to your own recording, using only voices you have permission to use. Stop keeps the lines already voiced.

## Settings (`broadcast.json`, created on first run next to the exe)

| Setting | Default | |
|---|---|---|
| `ShowName` | iRacing Broadcast | The name on the graphics and the wipes. |
| `FeaturedDriver` | (replay owner) | The driver to follow. |
| `HighlightMinutes`, `HighlightFocus` | 5, mixed (the window remembers your choice) | The video's length (roughly) and what it's of: `mixed`, `mine` or `race`. |
| `Commentary` | claude (the window remembers your choice) | How the narration is written: `claude` (the Claude Code CLI on your subscription; an old `api` setting reads as this), `paste` (paste the prompt into Claude and the reply back), or `simple`. |
| `Model`, `Effort` | claude-opus-5-5, high | The model that writes the script and how hard it thinks (`claude --model`, `--effort low/medium/high`). |
| `PlayByPlayName` | Alan | The narrator's name. |
| `VoiceEngine` | kokoro (the window remembers your choice) | `kokoro` or `chatterbox`. Changing it (or a Kokoro voice) re-voices the narration, keeping the script and the recording. |
| `KokoroVoice`, `KokoroColourVoice` | am_michael, bm_george | The narrator's and colour commentator's Kokoro voices (American am_*/af_*, British bm_*/bf_*). (`ColourName` and `ColourClip` are for two-voice scripts too.) |
| `VoiceSpeed`, `KokoroModel`, `VoicesDir` | 1.08, kokoro-multi-lang-v1_0, `..\IRacingVoice\voices` | Speaking speed, and the Kokoro model folder (shared with IRacingVoice). |
| `ChatterboxDir`, `PlayByPlayClip`, `Excitement` | `%USERPROFILE%\.iracingbroadcast\chatterbox`, (from Kokoro), 0.5 | Chatterbox's install, the voice clip it copies, and how animated ordinary lines are (0.25–0.85; exclamations get more). |
| `Encoder` | h264_nvenc | `hevc_nvenc`, `h264_amf` (AMD), `h264_qsv` (Intel) or `libx264` (CPU); falls back to libx264 if missing. |
| `Width`, `Height`, `Fps`, `GraphicsFps` | 1920, 1080, 60, 30 | Output size and frame rates. |
| `Monitor` | -1 | The screen to record; -1 = the one iRacing is on. |
| `GameAudio`, `GameAudioDb`, `GameAudioDevice` | true, -12, (auto) | Sim sound under the voice, and the sound device to record it from. Auto = the device iRacing plays on (from its open sound session, else its own sound settings), which may not be the Windows default. |
| `MuteVoiceChatKeys` | Ctrl+Shift+Alt+M | The keys bound to iRacing's *Mute Voice Chat* control (Settings > Controls > Voice Chat Controls). Pressed when the recording starts and ends; empty = leave voice chat alone. |
| `WorkDir`, `OutputDir` | Videos\iRacing Broadcasts\work, Videos\iRacing Broadcasts | Keep `WorkDir` off OneDrive: recordings are big. Not under AppData either: packaged apps (like Claude's desktop app) see a private copy of it. |

## How it works

- **Replay control** (`Sdk/Sim.cs`): the iRacing SDK broadcast messages handle seeking (`ReplaySearchSessionTime`), speed, camera switches (`CamSwitchNum`, by car number and camera group) and hiding the UI (`CamSetState`). Camera groups are matched by name (TV1, Chase, Blimp, …) so any track works.
- **Story** (`Story/`): the .ibt telemetry has only your own car, so the whole field comes from the live SDK while the replay plays. `FieldTracker` keeps each car's race distance, the running order and timing-screen gaps. `Detector` finds the following:
  - passes (confirmed after 3 s, pit-cycle swaps ignored);
  - battles (under 1 s for 20 s or more);
  - incidents (off track, a sudden loss of speed, stopped; simultaneous ones merged into "contact");
  - pit stops, lead changes, fastest laps, the start, the last lap and the finish.
- **Director** (`Direct/Director.cs`): scores each moment (lead changes and contact highest, the featured driver weighted by the focus) and picks the best until the video is full. The start, the winner's flag and the featured driver's finish are always in. Moments close together become one clip, and a long back-and-forth between two cars is shown at most twice. Each clip gets its camera shots, never the same camera twice in a row.
- **Narration** (`Talk/`): one Claude request through `claude -p` (`Talk/ClaudeCli.cs`) with the field, the clips (timed in the video), every event and the lap-by-lap order. Claude returns JSON lines timed in video seconds. The mixer places each line at its moment and drops filler that would come too late.
- **Shoot** (`Shoot/Recorder.cs`): for each clip, the replay jumps a moment before it and plays; ffmpeg `ddagrab` (Desktop Duplication) with NVENC records the screen, and WASAPI loopback records the sim's sound device. Every SDK frame is logged with its time in the finished video.
- **Edit** (`Edit/`): ffmpeg trims and joins the clips. GDI+ draws the graphics frame by frame from the frame log (with a wipe hiding each cut), and they are piped into the same ffmpeg run. It overlays them, mixes the narration over the sim sound (sidechain-ducked), and encodes the mp4.

## Limits

- Incidents involving other cars are a best guess: iRacing doesn't publish other drivers' incident points live. Per-car totals come from the final results.
- The scan still plays the whole race (at 16×), because the highlights can only be picked once the whole story is known.
- There's no "let's see that again" replay of a moment from a second angle.
