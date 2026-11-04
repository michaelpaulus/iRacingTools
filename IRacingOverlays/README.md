# IRacingOverlays

Overlays for iRacing. iRacing must run in **windowed or borderless** mode, not exclusive fullscreen.

## Standings

Styled after iRacing's own standings, with the extras it leaves out:

| Column | What it shows |
|---|---|
| Pos, # | Class position, live while racing (not only at the line); car number in the class colour. After the flag: iRacing's finishing order, kept (with the iRating and SR estimates) when you go back to the pits |
| Country | Flag of the driver's iRacing flair (`flags\`, 40 px PNGs from flagcdn.com); a two-letter code badge if there's no image |
| Car | The make's badge (`logos\`). The badges aren't in git: they're downloaded from Wikipedia in the background the first time the overlay starts (or `run.cmd logos-download`); until then a short text tag shows the make |
| Tyre | Compound the car is on: D (dry) / W (wet) for cars with one dry compound, else S / M / H / I / W. Blank when iRacing doesn't say |
| Lic, iR | Licence and safety rating, iRating |
| ±iR | Projected iRating change if the race finished now (race sessions only) |
| Laps | Laps completed |
| Last | Last lap time: purple = fastest lap in the class, green = the driver's own best |
| Gap | Seconds to you (negative = ahead of you), or `±nL` when a lap or more apart. Measured by interval timing (how long ago the car in front was where the car behind is now), so it matches the spotter. Grey, except the cars directly ahead and behind you, in bold white. Practice/qualifying: seconds apart on track, the short way round. Live (10 updates a second), never from lap times. Blank for cars not on track |

The title bar shows the strength of field and lap / time remaining. Multiclass races get one block per class with
its own SOF; your class comes first and other classes show their top 2 with gaps to their own leader.
Your class shows 10 rows: the top 3, then the cars around you (`MaxRows` / `LeaderRows` in `overlays.json`).

On the grid and the formation lap the order and gaps are live, like the race. If you crash or spin in a race (off
the track, or far slower than the cars around you), the list switches to the cars around you on track, with the time
until each one reaches you, and back to the standings a few seconds after you're up to speed
(`RelativeWhenInTrouble`).

### How the numbers are worked out

- **SOF and iRating**: the community reverse-engineered iRacing formula used by most calculators. It is per class,
  and drivers who never took the start are counted as non-starters. Close to iRacing's figure, not guaranteed exact.
- **Safety rating**: iRacing does not publish the formula and hides other drivers' incidents, so this is only for
  you. Each corner driven (iRacing's turn count for the track) earns `SrGainPerCorner`; each incident point costs
  as many corners as your licence's `SrTargetCpi` (the corners per incident that holds SR steady: higher at A
  than at D). Practice, warmup and qualifying count at reduced weight; hosted sessions don't count. Crossing a whole
  number adds `SrWholeNumberBump` (0.40, per iRacing), or takes it away going down. Not modelled: iRacing weighs each
  session against your recent history, which the SDK doesn't expose. Tune the numbers in `overlays.json` against
  your real results.

## Running

```
run.cmd                     live, alongside iRacing
run.cmd demo                simulated 20-car race, no iRacing needed
run.cmd demo multiclass 4   GT3 + GT4, at 4x speed
run.cmd png out.png         render the demo standings to an image
run.cmd dump                with iRacing running: print what the standings see, save session-info.yaml
run.cmd logos-download      fetch the car badges now (otherwise done in the background on start)
run.cmd logos badges.png    every car badge on one image, to check them
```

`session-info.yaml` holds other drivers' names and IDs, so it's kept out of git.

- Drag the overlay to move it, drag its left or right edge to resize (everything scales). It keeps two positions:
  one while you're in the car and one while you're out of it (garage, spectating, replays). Each is saved when you
  move the overlay in that state, and it jumps between them as you get in and out.
- **Pause** locks it (clicks go through to iRacing) / unlocks it. **Scroll Lock** hides / shows it.
- Settings live in `overlays.json` (rows shown, opacity, gap to you or to the leader, SR rates, hotkeys).

## Tests

```
dotnet test ..\IRacingOverlays.Tests
```

Set `IRACING_SESSION_YAML` to a dumped session-info file to also check the parser against a real session.
