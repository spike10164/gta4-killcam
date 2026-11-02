# GTA IV Kill Cam

ScriptHookDotNet script. When you kill a ped, time slows and the camera orbits the victim for ~2 seconds, then returns to normal (RDR2 / Max Payne 3 style).

Install: install ScriptHookDotNet, copy `KillCam.cs` to `<GTA IV>/scripts/`. `KillCam.ini` is created on first launch:

```
[General]  Enabled, ChancePercent (35), CooldownMs (8000), ScanRadius (40), Debug
[Camera]   DurationMs (2200), SlowMotionScale (0.2), OrbitRadius, OrbitHeight, OrbitSpeedDegPerSec
```
Set `ChancePercent=100` to see it on every kill. Errors/debug text go to the in-game console (`~`).

Only kills of peds the player damaged are detected. Not compiled or tested in-game.
