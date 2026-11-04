# GTA IV Kill Cam

ScriptHookDotNet script. When you kill a ped, time slows and the camera orbits the victim for ~2 seconds, then returns to normal (RDR2 / Max Payne 3 style).

Install: install ScriptHookDotNet, copy `KillCam.cs` to `<GTA IV>/scripts/`. `KillCam.ini` is created on first launch.

- Rare: 40% chance, 45 s cooldown, and only on the **last enemy** (`LastEnemyOnly=true`; an enemy is any nearby ped still in combat or that you hurt).
- Player is **invincible** during a kill cam (`[Player] Invincible=true`, can be turned off) and turned off afterwards (the API can't read the previous state).
- Camera cuts every `ShotLengthMs` between random shots: orbit victim, low front, high crane, over-the-shoulder. The final shot is the player (orbit or close-up) with `EndOnPlayerPercent` chance.
- A **timecycle** effect starts with the first camera and clears at the end of the last one (never on the death or busted cam). If the camera hits geometry it cuts to another shot. The death cam uses the normal shot set, reversed (the dead player is the subject, the killer the actor). `Timecycles` is a comma-separated list of modifier names (one picked at random); an unknown name may do nothing, so try others from the game's timecycle modifier files.

- **Skip** with the Jump button only (Space on keyboard, the Jump button on a controller). Shift does not skip. Hold-release first: a key held when the cam starts doesn't skip.
- Bleed-outs are ignored: only a sudden health drop (`HitDropThreshold`) within `KillWindowMs` of death counts as a kill.
- Victim shots are kept higher and moved off walls using a ground-height heuristic (`WallClearance`).
- Final shot on the player is either a face close-up from his right side or a wide shot with both him and the victim.

- **Face shot** is dynamic: it starts on the actor's right, pushes in, swings toward the front and tightens the lens. A soft light is drawn near the face so he's visible (`[Light]` Enabled/Range/Intensity).
- **Death cam** (`[Death] DeathCam=true`): when an enemy kills the player, the roles reverse. The camera orbits the dead player and the final shot is the killer's face or a wide shot of both. It needs a killer (an enemy that damaged you or was still fighting). `DeathCamChancePercent` sets how often.

- `AlwaysLastEnemyCam=true` (`[General]`): killing the last enemy always plays a kill cam, skipping the chance roll and cooldown.

Set `ChancePercent=100`, `CooldownMs=0`, `LastEnemyOnly=false` and `Debug=true` to test; debug text goes to the in-game console (`~`).
Not compiled or tested in-game.

## Newer options
- More shots: dolly-in, side track, top-down roll, low rise, Dutch-angle orbit, zoom pull-out. In the death cam the camera never zooms on the killer's face.
- `InteriorMode` (`Tight` / `Disabled` / `Normal`) and `InteriorScale` control behaviour indoors.
- The ini is reloaded automatically when saved during play. It ships with comments describing each timecycle.
- Delete the old `KillCam.ini` to get the commented defaults.

## Death / busted hold
- On death or a busted arrest the cam plays on the player and is held up to `HoldMs` (30 s) or until Jump is pressed. The game is slowed to `HoldTimeScale` to stretch the game's own respawn timer. The script does not touch the screen fade; the cam ends when you respawn. Tune `HoldTimeScale`.

## Busted
The busted cam alternates wide and two-shot framings of the arresting ped and the player.

## Ini
If `KillCam.ini` is deleted during play it is recreated automatically.

## More shots
Player-centric: orbit, low angle, dolly-in, right-side profile, crane (plus face and wide). They also appear mid-sequence in normal kill cams. Victim/scene: ground close-up, handheld close, flyby, two-shot.

## Death/busted update
- `DeathCamAnyCause=true` plays the death cam on any death (falls, explosions...), using solo shots when there is no killer.
- `BustedCuts` (default 2) sets how many cuts the busted cam makes.
- The screen is forced to stay faded in during the hold to stop the black screen.

## Situation-aware cams
- `LongRangeChancePercent` / `LongRangeDistance` and `ExplosionChancePercent` raise the cam chance for far kills and for grenade/molotov/rocket kills. Explosive kills also get a bigger tier (longer, slower) and big, high, wide shots.
- `SituationShots=true`: shots are weighted by kill distance (far = telephoto/bullet-follow/flyby, close = ground/handheld/dolly), and vehicles (car-side arc and low-front shots when you're driving; wider radii when anyone is in a vehicle).
- New styles: Telephoto, BulletFollow, PullBack, WhipPan, CrashZoom, CarSide, CarLowFront.

## Slow motion
- `LastEnemyRamp`: on last-enemy kills the slow-motion eases in from near-normal speed. `SlowRampOut` eases it back to normal at the end of every kill cam.
- `SequenceRules`: open on the victim, cut to the player, never repeat the same kind of move, alternate wide and tight. `EyelineMatching` keeps cuts on one side of the player-victim line. `PerShotLengths` makes fast shots short and slow ones long. `OpenerMs` starts last-enemy kills with a short tight shot. `DisabledShots` lists shots never to use.
- `AdaptiveChance`: the chance rises in quiet play and as time passes since the last cam, and drops during long firefights.
- The face light is full strength at night and scaled by `DayIntensityScale` by day.
- Busted cams never apply a screen effect.

## Pacing and intensity
- `IntensityTiers`: last enemy or a kill at low health each add a tier (longer, slower).
- `FinalHoldMs`: holds the last shot of kill cams.
- `MatchKillDirection`: kills from behind start on the victim's face side.

## More situations
- **Busted cam:** uses wide shots, two-shots and hero quarter angles on the arresting ped, and always ends on a raised, pulled-back quarter angle on the player.
- **Melee kills** (fists, bat, pool cue, knife within 3.5 m) favour `MeleeBehind` (raised, partly behind the attacker looking at the victim) and `MeleeTwoShot` (high two-shot).
- **Vehicle kills** also use `CarQuarter` and `CarRearQuarter`.
- **Ejection:** `EjectCam=true` plays a short cam (max 5 s, game speed at least 0.15) when the player is thrown out of a vehicle moving faster than `EjectMinSpeed` m/s. `EjectCamChancePercent` sets how often. It uses the normal cooldown.
- **Camera count:** each kill cam plays one to three shots at random (one or two from a car, with shorter shots). `DurationMs` now only applies to the eject cam.
