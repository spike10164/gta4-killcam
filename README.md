# GTA IV Kill Cam

ScriptHookDotNet script. When you kill a ped, time slows and the camera orbits the victim for ~2 seconds, then returns to normal (RDR2 / Max Payne 3 style).

Install: install ScriptHookDotNet, copy `KillCam.cs` to `<GTA IV>/scripts/`. `KillCam.ini` is created on first launch.

- Rare: 20% chance, 45 s cooldown, and only on the **last enemy** (`LastEnemyOnly=true`; an enemy is any nearby ped still in combat or that you hurt).
- Player is **invincible** during the cam (`Invincible=true`) and turned off afterwards (the API can't read the previous state).
- Camera cuts every `ShotLengthMs` between random shots: orbit victim, low front, high crane, over-the-shoulder. The final shot is the player (orbit or close-up) with `EndOnPlayerPercent` chance.
- A **timecycle** effect plays for `EffectMs`. `Timecycles` is a comma-separated list of modifier names (one picked at random); an unknown name may do nothing, so try others from the game's timecycle modifier files.

- **Skip** with the Jump button only (Space on keyboard, the Jump button on a controller). Shift does not skip. Hold-release first: a key held when the cam starts doesn't skip.
- The player aims at the victim during the cam. By default input is NOT locked: the aim task is re-issued every 300 ms, so moving may briefly fight it. `LockPlayerControl=true` freezes input instead.
- Bleed-outs are ignored: only a sudden health drop (`HitDropThreshold`) within `KillWindowMs` of death counts as a kill.
- Victim shots are kept higher and moved off walls using a ground-height heuristic (`WallClearance`).
- Final shot on the player is either a face close-up from his right side or a wide shot with both him and the victim.

- **Face shot** is dynamic: it starts on the actor's right, pushes in, swings toward the front and tightens the lens. A soft light is drawn near the face so he's visible (`[Light]` Enabled/Range/Intensity).
- **Death cam** (`[Death] DeathCam=true`): when an enemy kills the player, the roles reverse. The camera orbits the dead player and the final shot is the killer's face or a wide shot of both. It needs a killer (an enemy that damaged you or was still fighting). `DeathCamChancePercent` sets how often.

Set `ChancePercent=100`, `CooldownMs=0`, `LastEnemyOnly=false` and `Debug=true` to test; debug text goes to the in-game console (`~`).
Not compiled or tested in-game.

## Newer options
- More shots: dolly-in, side track, top-down roll, low rise, Dutch-angle orbit, zoom pull-out. In the death cam the camera never zooms on the killer's face.
- `InteriorMode` (`Tight` / `Disabled` / `Normal`) and `InteriorScale` control behaviour indoors.
- The ini is reloaded automatically when saved during play. It ships with comments describing each timecycle.
- Delete the old `KillCam.ini` to get the commented defaults.
