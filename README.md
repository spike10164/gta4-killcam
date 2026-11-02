# GTA IV Kill Cam

ScriptHookDotNet script. When you kill a ped, time slows and the camera orbits the victim for ~2 seconds, then returns to normal (RDR2 / Max Payne 3 style).

Install: install ScriptHookDotNet, copy `KillCam.cs` to `<GTA IV>/scripts/`. `KillCam.ini` is created on first launch.

- Rare: 20% chance, 45 s cooldown, and only on the **last enemy** (`LastEnemyOnly=true`; an enemy is any nearby ped still in combat or that you hurt).
- Player is **invincible** during the cam (`Invincible=true`) and turned off afterwards (the API can't read the previous state).
- Camera cuts every `ShotLengthMs` between random shots: orbit victim, low front, high crane, over-the-shoulder. The final shot is the player (orbit or close-up) with `EndOnPlayerPercent` chance.
- A **timecycle** effect plays for `EffectMs`. `Timecycles` is a comma-separated list of modifier names (one picked at random); an unknown name may do nothing, so try others from the game's timecycle modifier files.

Set `ChancePercent=100`, `CooldownMs=0`, `LastEnemyOnly=false` and `Debug=true` to test; debug text goes to the in-game console (`~`).
Not compiled or tested in-game.
