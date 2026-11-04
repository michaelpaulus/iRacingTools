# IRacingVoice

Hold push-to-talk, say what you want, let go. It does it and confirms out loud ("No tyres. Fuel, 20 litres.").
Speech is recognised on this PC by Whisper; Claude (through the Claude CLI, on your subscription) works out what you
meant and picks the actions and answers; the Kokoro voice speaks the replies. Fuel per lap is measured as you drive, so "add three laps of fuel" and "how
many laps until empty?" work after one clean lap.

## Things you can say

| Say | Does |
|---|---|
| "no tyres", "lefts only", "all four", "rears" | Picks which tyres are changed at the next stop |
| "fuel ten", "twenty litres", "five gallons", "no fuel", "fill it up" | Sets the fuel to add. Uses iRacing's units setting: a number without a unit is gallons or litres to match, and it answers in those units |
| "auto fuel on", "auto fuel with a two lap margin", "auto fuel off" | iRacing's auto fuel (`#autofuel` chat command) |
| "tear-off on/off", "fast repair on/off", "clear the pit stop" | Pit service options |
| "left front plus two", "all pressures 26", "rears down one psi" | Cold pressures for the next stop (and selects those tyres) |
| "put wets on", "switch to softs" | Tyre compound for the next stop |
| "mute chat", "unmute chat", "mute" | Toggles iRacing's voice chat (*Mute Voice Chat* control, see setup) |
| "mute the race channel", "unmute team channel" | iRacing radio channels (`@mute` / `@unmute`) |
| "talk on the team channel", "join / leave the spotter channel", "scan on/off" | `@transmit`, `@add` / `@remove`, `@scan` / `@noscan` |
| "start / stop logging" | iRacing's telemetry recording (.ibt) |
| "take a screenshot", "start / stop recording" | iRacing's own screenshot and video capture |
| "show me that again", "pause", "slow motion", "back to live" | Replay |
| "watch car 12", "cockpit cam", "TV cam on me" | Camera (spectating and replays) |
| "mute that driver" | Presses iRacing's *Mute a Driver* control (see setup) |
| "tell car 12 sorry about that" | Text chat, public or private |
| "force feedback 45" | Wheel strength (max force, Nm) |

**Coaching.** With Crew Chief installed, the corners come from its track data (`trackLandmarksData.json`, about 470
iRacing tracks): coaching works from your first lap, and corners have their real turn numbers and names ("Turn 24,
Karussell", "how was Eau Rouge?"). After your first clean lap the braking points and apexes are taken from your own
driving. Without it (or on a track it doesn't know) the agent learns the corners from your first clean lap. Either
way they're remembered per car and track in `coach\`. Every corner is judged on technique, largely following Suellio Almeida's coaching (Almeida Racing
Academy): firm braking just short of the ABS; no gap in grip between braking and turning (turn as the brake comes off,
or you braked too early); the brake used as a range, not a switch; no turning deep in the ABS; maximum rotation at the
slowest point; no cornering while accelerating; quick to 50% throttle, then committed to 100 (a lift means rotation
was missing); plus coasting, revs, and your own best pass through the corner as evidence. Every tip says why.

| Say | Gets |
|---|---|
| "how was that corner?", "what can I do better?" | The corner you just drove: time against your best, the main thing to fix and how |
| "how was turn 12?" | The same for that corner |
| "how was that lap?" | Lap time against your best and where most time went |
| "where am I losing time?" | Your three costliest corners and what to fix in the worst |
| "what should I work on?" | Your most common habit over the last lap (the thing to practise everywhere), with a reminder where it cost most |
| "that's enough" | Drops the reminder |

**Reference laps.** Add a faster lap to compare against in the command center (*Reference laps...*): a Garage 61
telemetry export (.csv; you pick the car and track) or an iRacing recording (.ibt; it knows its own car and track).
Corner answers then add what the reference did differently ("The reference lap is 3 tenths quicker here: it's back on
the throttle 45 metres earlier and brakes harder, 87 percent against your 65"), "how was that lap?" gives the gap to
it, "where am I losing time?" lists the corners furthest off it, and "what's the reference lap?" says which one it is.
The fastest lap in `reference\<car_track>\` is used. Tick *Download a comparison lap automatically* to have it fetched
from Garage 61's API when you start a car and track (needs Garage 61 Pro and a personal access token in
`GARAGE61_TOKEN`; without Garage 61's approval the API only shows your own and your teammates' laps; refreshed weekly).
`run.cmd reference-check <lap.csv> <any .ibt of that track>` checks that a file reads correctly.

After a corner answer you get one reminder about 5 seconds before that corner's braking point next lap, and one
"better / same / not this time" right after it. Ask again for another round. `run.cmd coach-replay <file.ibt>`
prints what it would have said after every corner of a recorded session (add `--ask <lap> <turn>` to try the reminder).

**Questions never change anything.** "How much fuel do I have?", "how many laps left?", "what's the pit stop set
to?", "how many incidents?", "what position am I?" (with the gaps either side in your class), "who's behind me?" (the
cars around you on track, any class, lapped or not), "what was my last lap?" (and the live delta to your best),
"what's the track temp?" (wetness, rain, wind) and "how are my tyres?" / "water temp?" (temperatures, warnings, and
tyre wear from your last stop, which is when iRacing measures it) are answered straight from iRacing's live data
(in your iRacing units). `run.cmd answers <file.ibt> 0.5` prints them from a recording (recordings don't keep the
other cars, so the gaps and nearby cars only work live).

**Ask it anything.** Any other question ("what's the minimum weight in GT3?", "how do I get better launches?",
"who won Le Mans in 1970?") is answered by Claude itself, out loud, in a sentence or two. Too long? Tap push-to-talk
(a press under half a second): it stops talking, and an answer still being worked out is dropped. A longer press stops
it too and asks something new.

Several things in one sentence work: "no tyres, twenty litres and a tear-off". Everything stays inside iRacing:
it never touches Windows volume, so "mute" can't mute the game. Only what you mention changes ("no tyres" leaves
the fuel alone), and if the model misses a plain pit command, the agent reads tyres, fuel, tear-off and fast repair
from the words itself.

## Setup

1. The Claude CLI, signed in with your Claude subscription: install it (`irm https://claude.ai/install.ps1 | iex`),
   run `claude`, then `/login` with your Claude account. The agent runs `claude -p` for each command, so there's no
   API key and nothing billed per command; it uses your subscription's limits and takes a few seconds longer than the
   API. The model is `Models` in voice.json (`claude-haiku-5-5` by default). For the API instead, use Kind
   "anthropic" with KeyVariable "ANTHROPIC_API_KEY"; OpenAI-compatible services such as Groq also work (Kind "openai").
2. In iRacing, Settings > Controls > Voice Chat Controls: bind *Mute Voice Chat* to Shift+Ctrl+Alt+M and *Mute a
   Driver* to Shift+Ctrl+Alt+D (or bind your own and set `MuteVoiceChatKeys` / `MuteDriverKeys` in voice.json to match).
3. Push-to-talk button: the Push-to-talk button on the Voice agent row in the command center.
4. Voice: `KokoroVoice` in voice.json (am_michael by default; others listed in the file's comments). The Kokoro model
   (~330 MB) and the Whisper speech model (~150 MB) download on first run. To hear voices side by side:
   `run.cmd voice-samples samples am_michael bm_george bm_lewis` writes a WAV of each.

## Running

The command center starts it with iRacing. To run it by hand:

```
run.cmd                                   live
run.cmd test "no tyres and 20 litres"     run a command as if spoken (sends to iRacing if it's running)
run.cmd say clip.wav "fuel ten litres"    make a test clip
run.cmd hear clip.wav                     check speech recognition on a clip
```

## Resource use

Idle, it just watches the push-to-talk button. After each command: about half a second of CPU for speech
recognition (2 threads, below-normal priority; `WhisperThreads`), and a short-lived `claude` process. The model runs
on Anthropic's servers, so it takes nothing else from your PC.

Chat, radio, auto fuel and the mute keys type into iRacing, so iRacing needs to be the focused window (it is while
you drive); otherwise the agent says so and types nothing, so keystrokes never land in another app. Pit service, fuel and force feedback go through the iRacing SDK and work regardless.
