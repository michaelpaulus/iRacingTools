# iRacing Coach

Live corner coaching against a reference lap, plus a push-to-talk race engineer powered by Claude.

## One-time setup

1. **Claude CLI** for the engineer, on your Claude subscription (no API key): install it with
   `irm https://claude.ai/install.ps1 | iex`, run `claude`, then `/login` with your Claude account. Without it,
   corner callouts still work.
2. **Push-to-talk:** run `run.cmd bind` and press the wheel button or key you want to hold while talking.
   Windows only exposes the first 32 buttons of a device; for a higher button, map it to a keyboard key in your wheel software.
3. Optional: run Windows Speech Recognition training (Control Panel > Speech Recognition > Train your computer) to improve accuracy.

## Use

- `run.cmd` - start it before or after iRacing; it waits for the sim.
- Hold push-to-talk and ask, e.g. "where am I losing the most time?", "how much fuel per lap?", "what should I change for the understeer?"
- You can also type a question in the console window and press Enter.
- `run.cmd replay "<file.ibt>" 0 --quiet` - coach a recorded session as a test (speed 0 = as fast as possible, 1 = real time with voice).
- `run.cmd ask "<question>" "<file.ibt>"` - ask the engineer about a recorded session.

## Overlay

A small always-on-top window shows the live gap to the reference within the current sector (it restarts at each of
iRacing's sector splits; green = gaining right now, red = losing) with the last sector's result top right,
a bar for time gained/lost since entering the current corner (full scale 0.5 s), and a track map with you (blue)
and the reference car (orange). It needs iRacing in windowed or borderless mode, not exclusive fullscreen.

- Drag it to where you want it; the position is saved. Scroll Lock hides/shows it (`OverlayToggleKey` in coach.json).
- The gap starts when you cross the start/finish line, so it is blank on the lap you join mid-way or after a reset.
- `run.cmd replay "<file.ibt>" 1 --quiet` plays a recorded session in real time with the overlay, to try it out.

### Pedal window

Brake and throttle bars (you left, reference right) and traces from 300 m behind to 200 m ahead, in their own window
(starts below the main overlay). Turn it off with `ShowPedals`.

All overlay windows except the windscreen marker: drag the middle to move, drag an edge or corner to resize.
Position and size are saved in coach.json.

### Relative strip and windscreen marker

- **Relative strip** (thin window, starts to the right of the overlay; drag to move): your car fixed in the middle,
  the reference car above (ahead) or below (behind) by track distance, ±150 m, with the time gap and metres on top.
- **Windscreen marker**: an orange box drawn over your centre screen roughly where the reference car would be,
  up to `MarkerMaxDistanceM` ahead. Clicks pass through it to iRacing. It's approximate: both cars are assumed to be
  on the reference line on flat ground, so it drifts on crests and dips. If it sits consistently above or below the
  road, adjust `MarkerHorizonOffsetPx` (negative = up). The field of view is taken from iRacing's triple-screen setup;
  override with `MarkerFovDeg`.
- Turn either off with `ShowStrip` / `ShowWindscreenMarker`. Scroll Lock hides/shows all overlay windows.

## Files

- `coach.json` - settings: reference lap CSV, voice, callout threshold (`MinSecondsLostToCall`), model.
- `corners.json` - corner names spoken in callouts; created on first run; rename any `kilometre x.x` entry.
- `coach_notes.md` - optional background the engineer uses (what earlier analysis found about your driving).
  Create it and edit freely.

All three are yours, not the repo's: they're kept out of git.

Corner coaching only runs on the track the reference lap is from (`ReferenceTrack`). To use another car/track,
export a fast lap from Garage 61 as CSV and point `ReferenceCsv` / `ReferenceTrack` / `TrackLengthKm` at it.
