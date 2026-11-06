# iRacingTools

Tools for racing in [iRacing](https://www.iracing.com) on Windows: overlays, a voice agent, a lap coach, a
highlights-video maker, and a tray app that ties them together.

| Folder | What it is |
|---|---|
| [IRacingOverlays](IRacingOverlays/README.md) | Standings overlay: car badges, tyres, projected iRating / SR, live gaps (formation lap included), and a relative view when you crash |
| [IRacingVoice](IRacingVoice/README.md) | Push-to-talk voice agent: pit stop changes, fuel, questions about the race, corner coaching |
| [IRacingCoach](IRacingCoach/README.md) | Live corner coaching against a reference lap, plus a race engineer powered by Claude |
| [IRacingBroadcast](IRacingBroadcast/README.md) | Turns a race replay into a narrated highlights video |
| IRacingCommandCenter | Tray app: switches the rig's power (a Shelly smart plug) with iRacing, starts and stops the other apps, pauses OneDrive while you race, and swaps the iRacing sign-in between drivers sharing the rig (team race pit stops) |
| IRacingOverlays.Tests | Tests for the overlays (`dotnet test IRacingOverlays.Tests`) |

## Getting started

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download).
2. Clone this repo, then run any app from its folder with its `run.cmd` (see each README).
3. Start the command center with `IRacingCommandCenter\run.cmd`: it builds it and runs it from its build output
   (`bin\Release`), where it finds the other apps in this folder. Tick *Start with Windows* in its tray menu to have it
   start on its own. To update it after a change, exit it from the tray and run `run.cmd` again.

## What isn't in the repo, and where it comes from

Large files, things that belong to others, and anything personal stay on your PC (see `.gitignore`). The apps
fetch or create what they need the first time:

| What | How you get it |
|---|---|
| Car badges (`IRacingOverlays\logos`) | Downloaded from Wikipedia in the background when the overlay first starts, or `run.cmd logos-download`. They're the car makers' trademarks, so they aren't committed |
| Speech recognition model (Whisper `ggml-*.bin`) | Downloaded by IRacingVoice on first run (~150 MB) |
| The Kokoro voice model (`IRacingVoice\voices`) | Downloaded by IRacingVoice or IRacingBroadcast the first time either speaks (~330 MB, shared) |
| Settings (`overlays.json`, `voice.json`, `coach.json`, `broadcast.json`) | Written with defaults on first run; then they hold your own window positions, controller buttons and choices |
| The Shelly plug's address | Found on your network by the command center the first time (with several Shelly devices, put the plug's `ip` and `mac` in its `config.json`) |
| Corner maps and reference laps (`IRacingVoice\coach`, `IRacingVoice\reference`, `IRacingCoach\corners.json`) | Built from your own driving, or downloaded from Garage 61 with your token |
| Session dumps (`session-info.yaml`), telemetry (`.ibt`) | Other drivers' names and IDs: never committed |

Installed separately: the [Claude CLI](https://claude.ai/download), signed in with your Claude subscription
(IRacingVoice understanding what you say, IRacingCoach's engineer, IRacingBroadcast's narration), and
[ffmpeg](https://ffmpeg.org) (IRacingBroadcast).
