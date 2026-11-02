using System;
using System.Collections.Generic;
using System.IO;
using GTA;

namespace KillCam {

   // Kill-cam in the style of RDR2 / Max Payne 3. Rare, and by default only on the last enemy:
   // time slows, the player is invincible, a timecycle effect plays, and the camera cuts
   // between several random angles, often finishing on the player.
   // Written in C# 2.0 style for ScriptHookDotNet.
   public class KillCam : Script {

      private enum CamState {
         Idle,
         Playing
      }

      private enum Shot {
         OrbitVictim,
         LowFront,
         HighCrane,
         OverShoulder,
         ActorFace,
         ActorWide,
         DollyIn,
         SideTrack,
         TopDown,
         LowRise,
         DutchOrbit,
         ZoomPull,
         ActorOrbit,
         ActorLowAngle,
         ActorDolly,
         ActorProfile,
         ActorCrane,
         GroundClose,
         HandheldClose,
         Flyby,
         TwoShot
      }

      // a nearby ped plus what we need to tell a direct kill from a bleed-out
      private class TrackedPed {
         public Ped Ped;
         public int Health;
         public int LastHitTick;
         public TrackedPed(Ped p) {
            Ped = p;
            Health = p.Health;
            LastHitTick = -100000;
         }
      }

      // settings (see KillCam.ini)
      private bool enabled = true;
      private int chancePercent = 20;
      private int cooldownMs = 45000;
      private bool lastEnemyOnly = true;
      private float scanRadius = 40.0f;
      private int durationMs = 7000;
      private int shotLengthMs = 3500;
      private int endOnPlayerPercent = 60;
      private float slowScale = 0.1f;
      private float orbitRadius = 1.9f;
      private float orbitHeight = 0.5f;
      private float orbitSpeedDeg = 12.0f; // degrees per real second
      private bool invincible = true;
      private string[] timecycles = new string[] { "Steel", "Cinema", "Eclipse" };
      private int effectMs = 7000;
      private bool debug = false;

      // a single health drop of at least this much counts as a hit; bleeding drops by 1-2 at a time
      private int hitDropThreshold = 6;
      // a death only counts as a kill if the last hit was this recent
      private int killWindowMs = 1500;
      private float camClearance = 1.3f;
      private bool lockPlayer = false;
      private int lastAimTick = 0;
      private bool skipArmed = false;
      private bool controlLocked = false;

      // death cam: when the player is killed by an enemy, the roles are reversed
      private bool deathCam = true;
      private int deathChancePercent = 100;
      private bool deathMode = false;
      private bool lastStand = true;
      private int lastStandHealth = 15;
      private float lastStandTimeScale = 0.25f;
      private bool lastStandArmed = true;
      private bool fakeDeath = false;
      private int lastRagdollTick = 0;
      private bool bustedMode = false;
      private Vector3 deathStartPos;
      private bool deathHandled = false;
      private bool bustedCam = true;
      private bool bustedHandled = false;
      private int deathHoldMs = 30000;
      private float deathTimeScale = 0.05f;
      private bool holdFade = true;
      private bool forceAim = false;
      private int activeDurationMs = 7000;

      // light on the actor's face during the face shot
      private bool faceLight = true;
      private float lightRange = 4.5f;
      private float lightIntensity = 50.0f;

      // the actor is the one who did the killing: the player normally, the enemy in a death cam
      private Ped actor = null;
      private Vector3 lastActorPos;
      private float faceScale = 1.0f;
      private Light faceLightObj = null;

      private List<TrackedPed> tracked = new List<TrackedPed>();
      private CamState state = CamState.Idle;
      private Camera cam = null;
      private Ped victim = null;
      private Vector3 lastVictimPos;
      private int startTick = 0;
      private int lastEndTick = -100000;
      private int lastFrameTick = 0;
      private int shotStartTick = 0;
      private Shot shot = Shot.OrbitVictim;
      private float angle = 0.0f;
      private float shotDir = 1.0f;
      private Vector3 fixedPos;
      private bool hasFixedPos = false;
      private bool finalShotChosen = false;
      private bool effectOn = false;
      private Random rng = new Random();

      // interior handling: Normal, Tight (much closer cameras) or Disabled (no camera indoors)
      private string interiorMode = "Tight";
      private bool interior = false;
      private float interiorScale = 0.5f;
      private int lastIniCheckTick = 0;
      private DateTime lastIniWrite = DateTime.MinValue;

      public KillCam() {
         LoadSettings();
         this.Interval = 100;
         this.Tick += new EventHandler(this.KillCam_Tick);
      }

      // Creates KillCam.ini next to the script on first launch.
      private void LoadSettings() {
         try {
            if (!File.Exists(Settings.Filename)) {
               WriteDefaultIni();
               Settings.Load();
            }
            lastIniWrite = File.GetLastWriteTime(Settings.Filename);
         } catch (Exception ex) {
            Game.Console.Print("KillCam could not create ini: " + ex.Message);
         }
         ReadSettings();
      }

      // Reloads the ini when it is saved while the game is running (checked about once a second).
      private void CheckIniReload() {
         int now = Environment.TickCount;
         if (now - lastIniCheckTick < 1000) return;
         lastIniCheckTick = now;
         try {
            DateTime w = File.GetLastWriteTime(Settings.Filename);
            if (w != lastIniWrite) {
               lastIniWrite = w;
               Settings.Load();
               ReadSettings();
               Game.Console.Print("KillCam: settings reloaded");
            }
         } catch (Exception) { }
      }

      private void WriteDefaultIni() {
         string[] lines = new string[] {
            "// KillCam settings. Saved changes are picked up while the game is running.",
            "// Lines starting with // are comments.",
            "",
            "[General]",
            "// Master switch",
            "Enabled=true",
            "// Chance (0-100) that a qualifying kill triggers the kill cam",
            "ChancePercent=20",
            "// Minimum time between kill cams, in milliseconds",
            "CooldownMs=45000",
            "// true = only the last remaining enemy triggers it",
            "LastEnemyOnly=true",
            "// Radius (metres) around the player that is watched for kills",
            "ScanRadius=40",
            "// Prints debug lines to the console (~)",
            "Debug=false",
            "// A single health drop at least this big counts as a hit (bleeding drops 1-2 at a time)",
            "HitDropThreshold=6",
            "// A death only counts as a kill if the last big hit was this recent (ms). Bleed-outs are ignored",
            "KillWindowMs=1500",
            "",
            "[Camera]",
            "// Total length of the cam in real milliseconds",
            "DurationMs=7000",
            "// Length of each shot in milliseconds",
            "ShotLengthMs=3500",
            "// Chance (0-100) that the last shot is on the player (face or wide shot)",
            "EndOnPlayerPercent=60",
            "// Game speed during the cam (0.05 - 1.0). Lower = slower",
            "SlowMotionScale=0.1",
            "// Orbit shot distance from the dead ped, metres",
            "OrbitRadius=1.9",
            "// Orbit shot height, metres",
            "OrbitHeight=0.5",
            "// Orbit rotation speed, degrees per real second",
            "OrbitSpeedDegPerSec=12",
            "// Cameras are pulled in when a wall is detected closer than this (metres)",
            "WallClearance=1.3",
            "// What to do when the player is inside an interior:",
            "//   Tight    = use close, tight shots (default)",
            "//   Disabled = no camera indoors",
            "//   Normal   = same as outdoors (may clip walls)",
            "InteriorMode=Tight",
            "// Scale applied to distances in Tight mode (smaller = closer)",
            "InteriorScale=0.5",
            "",
            "[Player]",
            "// Player cannot be hurt during the cam",
            "Invincible=true",
            "// true = freeze player input during the cam. false = player keeps control while being forced to aim at the dead ped",
            "LockPlayerControl=false",
            "// Experimental. Makes the player aim at the dead ped. This takes over the player's movement and shooting",
            "// and can cause camera jitter, so it is off by default",
            "ForceAim=false",
            "",
            "[Death]",
            "// Reverse logic: when an enemy kills the player, play the cam on the player's body",
            "DeathCam=true",
            "DeathCamChancePercent=100",
            "// Last stand (recommended): when a hit leaves the player at or below LastStandHealth, the player is made",
            "// invincible and ragdolled so the death cam can play with no respawn problems. After HoldMs, or when",
            "// Jump is pressed, the player then really dies. A hit that kills outright still uses the old behaviour",
            "LastStand=true",
            "LastStandHealth=15",
            "// Game speed during the last stand cam (0.05 - 1.0)",
            "LastStandTimeScale=0.25",
            "// Also play the cam when the player is busted (arrested). The nearest ped is used as the arresting officer",
            "BustedCam=true",
            "// How long (ms) the death/busted cam is held before the game is allowed to respawn you. Jump skips it",
            "HoldMs=30000",
            "// Game speed while holding (0.01 - 1.0). Very low speed stretches the game's own respawn timers",
            "HoldTimeScale=0.05",
            "// Keep forcing the screen to stay faded in while holding, so the fade to black does not hide the cam",
            "KeepScreenVisible=true",
            "",
            "[Light]",
            "// Soft light on the player's face during the face close-up",
            "Enabled=true",
            "Range=4.5",
            "Intensity=50",
            "",
            "[Effect]",
            "// How long the screen effect stays on (ms)",
            "EffectMs=7000",
            "// Comma separated list; one is picked at random each time. Available names and what they look like:",
            "//   Steel      cool, desaturated blue-steel grade with strong contrast (moody, thriller look)",
            "//   Cinema     high-contrast filmic grade, slightly teal shadows and warm highlights",
            "//   Eclipse    dark and dramatic, deep red-purple shadows with a dim overall tone",
            "//   Hot        very warm orange-red wash, like a heat haze",
            "//   Fire       similar to Hot but stronger glow and punchier contrast",
            "//   Sepia      old-photo brown tint, mostly monochrome",
            "//   Noire      dark, near black-and-white with heavy contrast and thick fog",
            "//   Rouge      strong red tint over everything",
            "//   Verte      green tint, murky jungle/night-vision feel",
            "//   Bronze     golden-bronze warm tint, soft and low-contrast",
            "//   Vintage    faded retro look with olive-yellow tones",
            "//   Romantic   soft pink-magenta glow with a dreamy blur",
            "//   Neon       saturated pink/purple club look with bloom",
            "//   Ulraviolet deep blue-violet tint, like black light",
            "//   Psyche     very strong psychedelic colours, warped and blurry (extreme)",
            "//   Muddy      dull brown-grey, washed out and heavy",
            "//   Classic    clean neutral contrast boost, closest to normal",
            "//   Tweaked    heavy pink/green colour split with high contrast (stylised)",
            "//   Sketch     flat, high-contrast, comic-like grade",
            "//   colors     slightly boosted colour saturation",
            "//   death      the game's own wasted/death look: desaturated, dark edges",
            "//   injured    heavy desaturation and blur, like low health",
            "Timecycles=Steel,Cinema,Eclipse"
         };
         File.WriteAllLines(Settings.Filename, lines);
      }

      private void ReadSettings() {
         enabled = Settings.GetValueBool("Enabled", "General", true);
         chancePercent = Settings.GetValueInteger("ChancePercent", "General", 20);
         cooldownMs = Settings.GetValueInteger("CooldownMs", "General", 45000);
         lastEnemyOnly = Settings.GetValueBool("LastEnemyOnly", "General", true);
         scanRadius = Settings.GetValueFloat("ScanRadius", "General", 40.0f);
         debug = Settings.GetValueBool("Debug", "General", false);
         durationMs = Settings.GetValueInteger("DurationMs", "Camera", 7000);
         shotLengthMs = Settings.GetValueInteger("ShotLengthMs", "Camera", 3500);
         endOnPlayerPercent = Settings.GetValueInteger("EndOnPlayerPercent", "Camera", 60);
         slowScale = Settings.GetValueFloat("SlowMotionScale", "Camera", 0.1f);
         orbitRadius = Settings.GetValueFloat("OrbitRadius", "Camera", 1.9f);
         orbitHeight = Settings.GetValueFloat("OrbitHeight", "Camera", 0.5f);
         orbitSpeedDeg = Settings.GetValueFloat("OrbitSpeedDegPerSec", "Camera", 12.0f);
         invincible = Settings.GetValueBool("Invincible", "Player", true);
         lockPlayer = Settings.GetValueBool("LockPlayerControl", "Player", false);
         deathCam = Settings.GetValueBool("DeathCam", "Death", true);
         bustedCam = Settings.GetValueBool("BustedCam", "Death", true);
         lastStand = Settings.GetValueBool("LastStand", "Death", true);
         lastStandHealth = Settings.GetValueInteger("LastStandHealth", "Death", 15);
         lastStandTimeScale = Settings.GetValueFloat("LastStandTimeScale", "Death", 0.25f);
         if (lastStandTimeScale < 0.05f) lastStandTimeScale = 0.05f;
         if (lastStandTimeScale > 1.0f) lastStandTimeScale = 1.0f;
         deathHoldMs = Settings.GetValueInteger("HoldMs", "Death", 30000);
         deathTimeScale = Settings.GetValueFloat("HoldTimeScale", "Death", 0.05f);
         holdFade = Settings.GetValueBool("KeepScreenVisible", "Death", true);
         forceAim = Settings.GetValueBool("ForceAim", "Player", false);
         if (deathTimeScale < 0.01f) deathTimeScale = 0.01f;
         if (deathTimeScale > 1.0f) deathTimeScale = 1.0f;
         deathChancePercent = Settings.GetValueInteger("DeathCamChancePercent", "Death", 100);
         faceLight = Settings.GetValueBool("Enabled", "Light", true);
         lightRange = Settings.GetValueFloat("Range", "Light", 4.5f);
         lightIntensity = Settings.GetValueFloat("Intensity", "Light", 50.0f);
         hitDropThreshold = Settings.GetValueInteger("HitDropThreshold", "General", 6);
         killWindowMs = Settings.GetValueInteger("KillWindowMs", "General", 1500);
         camClearance = Settings.GetValueFloat("WallClearance", "Camera", 1.3f);
         interiorMode = Settings.GetValueString("InteriorMode", "Camera", "Tight").Trim();
         interiorScale = Settings.GetValueFloat("InteriorScale", "Camera", 0.5f);
         if (interiorScale < 0.2f) interiorScale = 0.2f;
         if (interiorScale > 1.0f) interiorScale = 1.0f;
         effectMs = Settings.GetValueInteger("EffectMs", "Effect", 7000);

         // comma separated list; one is picked at random each time
         string list = Settings.GetValueString("Timecycles", "Effect", "Steel,Cinema,Eclipse");
         if (list != null && list.Trim().Length > 0) {
            string[] parts = list.Split(',');
            for (int i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim();
            timecycles = parts;
         } else {
            timecycles = new string[0];
         }

         if (slowScale < 0.05f) slowScale = 0.05f;
         if (slowScale > 1.0f) slowScale = 1.0f;
         if (shotLengthMs < 300) shotLengthMs = 300;
      }

      private void Debug(string text) {
         if (debug) Game.Console.Print("KillCam: " + text);
      }

      private void KillCam_Tick(object sender, EventArgs e) {
         try {
            if (!enabled) return;
            if (state == CamState.Playing) {
               UpdateCamera();
            } else {
               CheckIniReload();
               ScanForKills();
            }
         } catch (Exception ex) {
            Game.Console.Print("KillCam error: " + ex.Message);
            StopCamera();
         }
      }

      private bool IsTracked(Ped p) {
         for (int i = 0; i < tracked.Count; i++) {
            if (tracked[i].Ped.Equals(p)) return true;
         }
         return false;
      }

      private bool IsOtherEnemyAlive(Ped player, Ped killed) {
         for (int i = 0; i < tracked.Count; i++) {
            Ped p = tracked[i].Ped;
            if (p == null || !p.Exists() || p.isDead || p.Equals(player) || p.Equals(killed)) continue;
            // an enemy is anyone still fighting, or anyone we already hurt
            if (p.isInCombat || p.HasBeenDamagedBy(player)) return true;
         }
         return false;
      }

      // Tracks every living ped nearby, so a one-shot kill between ticks is still seen.
      // A death only counts as a kill if the player damaged that ped.
      // Peds that were already dead when first seen never trigger, so old corpses are ignored.
      private void ScanForKills() {
         Ped player = Player.Character;
         if (player != null && player.Exists() && player.isDead) {
            if (deathCam && !deathHandled) {
               deathHandled = true;
               TryStartDeathCamera(player);
            }
            tracked.Clear();
            return;
         }
         if (player == null || !player.Exists()) {
            tracked.Clear();
            return;
         }
         deathHandled = false;

         if (deathCam && lastStand) {
            int hp = player.Health;
            if (hp > lastStandHealth + 15) lastStandArmed = true;
            if (lastStandArmed && hp > 0 && hp <= lastStandHealth) {
               lastStandArmed = false;
               if (TryStartLastStand(player)) return;
            }
         }

         if (bustedCam) {
            bool arrested = false;
            try { arrested = GTA.Native.Function.Call<bool>("IS_PLAYER_BEING_ARRESTED"); } catch (Exception) { }
            if (!arrested) {
               bustedHandled = false;
            } else if (!bustedHandled) {
               bustedHandled = true;
               TryStartBustedCamera(player);
               return;
            }
         }

         Ped killed = null;
         int now = Environment.TickCount;
         for (int i = tracked.Count - 1; i >= 0; i--) {
            TrackedPed t = tracked[i];
            Ped p = t.Ped;
            if (p == null || !p.Exists()) {
               tracked.RemoveAt(i);
            } else if (p.isDead) {
               tracked.RemoveAt(i);
               bool byPlayer = p.HasBeenDamagedBy(player);
               // a big final drop (or a recent big hit) is a kill; slow bleeding is not
               bool direct = (t.Health >= hitDropThreshold) || (now - t.LastHitTick <= killWindowMs);
               Debug("ped died, by player: " + byPlayer.ToString() + ", direct: " + direct.ToString());
               if (byPlayer && direct && killed == null) killed = p;
            } else if (p.Position.DistanceTo(player.Position) > scanRadius * 1.5f) {
               tracked.RemoveAt(i);
            } else {
               int h = p.Health;
               if (t.Health - h >= hitDropThreshold) t.LastHitTick = now;
               t.Health = h;
            }
         }

         if (killed != null) {
            TryStartCamera(player, killed);
         }

         Ped[] nearby = World.GetPeds(player.Position, scanRadius);
         for (int i = 0; i < nearby.Length; i++) {
            Ped p = nearby[i];
            if (p == null || !p.Exists() || p.isDead || p.Equals(player)) continue;
            if (!IsTracked(p)) tracked.Add(new TrackedPed(p));
         }
      }

      private void TryStartCamera(Ped player, Ped killed) {
         if (Environment.TickCount - lastEndTick < cooldownMs) { Debug("skipped: cooldown"); return; }
         if (lastEnemyOnly && IsOtherEnemyAlive(player, killed)) { Debug("skipped: other enemy alive"); return; }
         if (rng.Next(100) >= chancePercent) { Debug("skipped: chance roll"); return; }

         BeginCamera(killed, player, false);
      }

      // Reverse logic: the player was killed, so the dead one is the player and the actor is the killer.
      private void TryStartDeathCamera(Ped player) {
         if (rng.Next(100) >= deathChancePercent) { Debug("death cam skipped: chance roll"); return; }

         // the killer is the nearest living ped that damaged the player, else the nearest one still fighting
         Ped killer = null;
         float best = 100000.0f;
         for (int pass = 0; pass < 2 && killer == null; pass++) {
            for (int i = 0; i < tracked.Count; i++) {
               Ped p = tracked[i].Ped;
               if (p == null || !p.Exists() || p.isDead || p.Equals(player)) continue;
               if (pass == 0 ? !player.HasBeenDamagedBy(p) : !p.isInCombat) continue;
               float d = p.Position.DistanceTo(player.Position);
               if (d < best) { best = d; killer = p; }
            }
         }
         if (killer == null) { Debug("death cam skipped: no killer found"); return; }
         BeginCamera(player, killer, true);
      }

      private bool TryStartLastStand(Ped player) {
         if (rng.Next(100) >= deathChancePercent) return false;
         Ped killer = null;
         float best = 100000.0f;
         for (int pass = 0; pass < 2 && killer == null; pass++) {
            for (int i = 0; i < tracked.Count; i++) {
               Ped p = tracked[i].Ped;
               if (p == null || !p.Exists() || p.isDead || p.Equals(player)) continue;
               if (pass == 0 ? !player.HasBeenDamagedBy(p) : !p.isInCombat) continue;
               float d = p.Position.DistanceTo(player.Position);
               if (d < best) { best = d; killer = p; }
            }
         }
         if (killer == null) return false;
         BeginCamera(player, killer, true, false);
         if (state != CamState.Playing) return false;
         fakeDeath = true;
         player.Invincible = true;
         player.ForceRagdoll(deathHoldMs, false);
         lastRagdollTick = Environment.TickCount;
         Game.TimeScale = lastStandTimeScale;
         return true;
      }

      private void TryStartBustedCamera(Ped player) {
         if (rng.Next(100) >= deathChancePercent) return;
         Ped cop = null;
         float best = 100000.0f;
         for (int i = 0; i < tracked.Count; i++) {
            Ped p = tracked[i].Ped;
            if (p == null || !p.Exists() || p.isDead || p.Equals(player)) continue;
            float d = p.Position.DistanceTo(player.Position);
            if (d < best) { best = d; cop = p; }
         }
         if (cop == null) { Debug("busted cam skipped: nobody nearby"); return; }
         BeginCamera(player, cop, true, true);
      }

      // Fades the screen back in if the game's wasted/busted fade-out starts
      private void KeepVisible() {
         try {
            if (!GTA.Native.Function.Call<bool>("IS_SCREEN_FADED_IN")) {
               GTA.Native.Function.Call("DO_SCREEN_FADE_IN", 0);
            }
         } catch (Exception) { }
      }

      private void BeginCamera(Ped focus, Ped actingPed, bool death) {
         BeginCamera(focus, actingPed, death, false);
      }

      private void BeginCamera(Ped focus, Ped actingPed, bool death, bool busted) {
         bustedMode = busted;
         fakeDeath = false;
         deathStartPos = focus.Position;
         interior = false;
         try {
            interior = GTA.Native.Function.Call<bool>("IS_INTERIOR_SCENE");
         } catch (Exception) { }
         if (interior && string.Compare(interiorMode, "Disabled", true) == 0) { Debug("skipped: interior"); return; }
         if (interior && string.Compare(interiorMode, "Normal", true) == 0) interior = false;
         victim = focus;
         actor = actingPed;
         deathMode = death;
         lastVictimPos = victim.Position;
         lastActorPos = actor.Position;
         cam = new Camera();
         cam.FOV = 42.0f;
         finalShotChosen = false;
         hasFixedPos = false;
         int now = Environment.TickCount;
         startTick = now;
         lastFrameTick = now;
         ChooseShot(true);
         cam.Activate();

         activeDurationMs = death ? deathHoldMs : durationMs;
         Game.TimeScale = death ? deathTimeScale : slowScale;
         if (!death) {
            Ped player = actor;
            if (invincible) {
               player.Invincible = true;
            }
            if (lockPlayer) {
               Player.CanControlCharacter = false;
               controlLocked = true;
            }
            if (forceAim) AimAtVictim(player, true);
         }
         // the skip key must be released once before it counts, so a held sprint does not skip at once
         skipArmed = !SkipPressed();
         StartEffect();

         state = CamState.Playing;
         this.Interval = 0;
         Debug(death ? "started (death cam)" : "started");
      }

      private Vector3 ActorPos() {
         if (actor != null && actor.Exists()) lastActorPos = actor.Position;
         return lastActorPos;
      }

      private Vector3 ActorHead() {
         if (actor != null && actor.Exists()) return actor.GetBonePosition(Bone.Head);
         return new Vector3(lastActorPos.X, lastActorPos.Y, lastActorPos.Z + 1.6f);
      }

      // Jump only: Space on keyboard, X on a controller. Sprint (Shift) must not skip.
      private bool SkipPressed() {
         return Game.isGameKeyPressed(GameKey.Jump);
      }

      // scales distances down for the tight interior mode
      private float S(float v) {
         return interior ? v * interiorScale : v;
      }

      // Forces the aim without locking input. Without the lock, player input can cancel the task,
      // so UpdateCamera re-issues it every few hundred ms (clear = false avoids resetting the pose).
      private void AimAtVictim(Ped player, bool clear) {
         try {
            if (clear) player.Task.ClearAll();
            player.Task.AimAt(new Vector3(lastVictimPos.X, lastVictimPos.Y, lastVictimPos.Z + 0.3f), durationMs);
            lastAimTick = Environment.TickCount;
         } catch (Exception) { }
      }

      // Heuristic wall check: if the first surface below a point high above it is much higher
      // than the subject, something solid (wall, roof, car) is in the way.
      private bool IsBlocked(Vector3 p, float subjectZ) {
         // indoors the ceiling always looks like a blocker, so the heuristic is skipped
         if (interior) return false;
         float ground = World.GetGroundZ(new Vector3(p.X, p.Y, subjectZ + 6.0f), GroundType.NextBelowCurrent);
         return ground > subjectZ + camClearance;
      }

      // Tries the wanted spot, then closer and higher ones, and returns the first clear one.
      private Vector3 ClearPosition(Vector3 center, float ang, float radius, float height) {
         if (interior) {
            float h = S(height);
            if (h > 1.4f) h = 1.4f;
            return Offset(center, ang, S(radius), h);
         }
         float[] scales = new float[] { 1.0f, 0.75f, 0.55f };
         for (int i = 0; i < scales.Length; i++) {
            Vector3 p = Offset(center, ang, radius * scales[i], height + (1.0f - scales[i]) * 2.5f);
            if (!IsBlocked(p, center.Z)) return p;
         }
         // nothing clear: look straight down from above
         return new Vector3(center.X, center.Y, center.Z + height + 3.0f);
      }

      private void StartEffect() {
         if (timecycles.Length == 0) return;
         string name = timecycles[rng.Next(timecycles.Length)];
         if (name.Length == 0) return;
         GTA.Native.Function.Call("SET_TIMECYCLE_MODIFIER", name);
         effectOn = true;
         Debug("effect " + name);
      }

      private void StopEffect() {
         if (!effectOn) return;
         GTA.Native.Function.Call("CLEAR_TIMECYCLE_MODIFIER");
         effectOn = false;
      }

      // picks the next shot and sets up its starting camera state
      private void ChooseShot(bool first) {
         Shot next = Shot.OrbitVictim;
         bool isFinal = (Environment.TickCount - startTick) + shotLengthMs >= activeDurationMs;
         if (isFinal && !finalShotChosen) {
            finalShotChosen = true;
            if (rng.Next(100) < endOnPlayerPercent) {
               // in a death cam the actor is the killer, so never push in on their face
               if (deathMode) next = Shot.ActorWide;
               else next = RandomActorShot();
            } else {
               next = RandomVictimShot(shot);
            }
         } else if (!first) {
            next = RandomVictimShot(shot);
         }

         shot = next;
         shotStartTick = Environment.TickCount;
         shotDir = (rng.Next(2) == 0) ? 1.0f : -1.0f;
         hasFixedPos = false;
         cam.FOV = 42.0f;
         cam.Roll = 0.0f;

         Vector3 pp = ActorPos();
         Vector3 target = IsActorShot() ? pp : lastVictimPos;
         // start the orbit on the side facing the other character
         Vector3 from = IsActorShot() ? lastVictimPos : pp;
         angle = (float)Math.Atan2(from.Y - target.Y, from.X - target.X);
         // random starting angle for variety
         angle += (float)(rng.NextDouble() * Math.PI - Math.PI / 2.0);
         Debug("shot " + shot.ToString());
      }

      // player-centric shots (only used when the actor is the player, never the killer)
      private Shot RandomActorShot() {
         Shot[] options;
         if (interior) {
            options = new Shot[] { Shot.ActorFace, Shot.ActorProfile, Shot.ActorLowAngle, Shot.ActorDolly, Shot.ActorOrbit };
         } else {
            options = new Shot[] { Shot.ActorFace, Shot.ActorWide, Shot.ActorProfile, Shot.ActorLowAngle,
               Shot.ActorDolly, Shot.ActorOrbit, Shot.ActorCrane };
         }
         return options[rng.Next(options.Length)];
      }

      private Shot RandomVictimShot(Shot previous) {
         // sometimes cut to the player mid-sequence (not in a death cam, where the actor is the killer)
         if (!deathMode && rng.Next(100) < 35) {
            Shot a = RandomActorShot();
            if (a != previous && a != Shot.ActorFace) return a;
         }
         Shot[] options;
         if (interior) {
            options = new Shot[] { Shot.OrbitVictim, Shot.LowFront, Shot.OverShoulder, Shot.DollyIn, Shot.LowRise,
               Shot.DutchOrbit, Shot.GroundClose, Shot.HandheldClose, Shot.TwoShot };
         } else {
            options = new Shot[] { Shot.OrbitVictim, Shot.LowFront, Shot.HighCrane, Shot.OverShoulder,
               Shot.DollyIn, Shot.SideTrack, Shot.TopDown, Shot.LowRise, Shot.DutchOrbit, Shot.ZoomPull,
               Shot.GroundClose, Shot.HandheldClose, Shot.Flyby, Shot.TwoShot };
         }
         Shot pick = options[rng.Next(options.Length)];
         if (pick == previous) pick = options[(Array.IndexOf(options, pick) + 1) % options.Length];
         return pick;
      }

      private bool IsActorShot() {
         return shot == Shot.ActorFace || shot == Shot.ActorWide || shot == Shot.ActorOrbit || shot == Shot.ActorLowAngle
            || shot == Shot.ActorDolly || shot == Shot.ActorProfile || shot == Shot.ActorCrane;
      }

      private Vector3 Offset(Vector3 center, float ang, float radius, float height) {
         return new Vector3(
            center.X + (float)Math.Cos(ang) * radius,
            center.Y + (float)Math.Sin(ang) * radius,
            center.Z + height);
      }

      private float ShotT() {
         float t = (Environment.TickCount - shotStartTick) / (float)shotLengthMs;
         if (t < 0.0f) t = 0.0f;
         if (t > 1.0f) t = 1.0f;
         return t;
      }

      private void PositionCamera() {
         if (victim != null && victim.Exists()) lastVictimPos = victim.Position;
         Vector3 pp = ActorPos();
         Vector3 victimHead = new Vector3(lastVictimPos.X, lastVictimPos.Y, lastVictimPos.Z + 0.4f);
         Vector3 pos;
         Vector3 target;

         switch (shot) {
            case Shot.LowFront:
               // static, low, looking up at the victim (kept off the floor and out of walls)
               if (!hasFixedPos) { fixedPos = ClearPosition(lastVictimPos, angle, 1.8f, 0.5f); hasFixedPos = true; }
               pos = fixedPos;
               target = victimHead;
               break;
            case Shot.HighCrane:
               // slowly rising shot from above
               float rise = (Environment.TickCount - shotStartTick) / 1000.0f;
               pos = ClearPosition(lastVictimPos, angle, 2.4f, 2.4f + rise * 0.4f);
               target = victimHead;
               break;
            case Shot.OverShoulder:
               // behind the player, looking at the victim
               float dx = lastVictimPos.X - pp.X;
               float dy = lastVictimPos.Y - pp.Y;
               float back = (float)Math.Atan2(dy, dx) + (float)Math.PI;
               if (!hasFixedPos) { fixedPos = ClearPosition(pp, back, 1.5f, 1.3f); hasFixedPos = true; }
               pos = fixedPos;
               target = victimHead;
               break;
            case Shot.ActorFace: {
               // dynamic close-up: starts wide on the actor's right, pushes in, swings toward the front and tightens the lens
               int nowTick = Environment.TickCount;
               float t = (nowTick - shotStartTick) / (float)shotLengthMs;
               if (t > 1.0f) t = 1.0f;
               Vector3 hd = ActorHead();
               float hdg = ((actor != null && actor.Exists()) ? actor.Heading : 0.0f) * (float)Math.PI / 180.0f;
               if (!hasFixedPos) {
                  faceScale = 1.0f;
                  if (IsBlocked(Offset(hd, hdg + 0.5f, 2.3f, 0.2f), pp.Z)) faceScale = 0.65f;
                  hasFixedPos = true;
               }
               float faceAngle = hdg + 0.45f + t * 0.5f;
               float faceDist = S((2.3f - 0.5f * t) * faceScale);
               // roughly head height, easing slightly lower as it pushes in
               pos = Offset(hd, faceAngle, faceDist, 0.05f - 0.15f * t);
               target = hd;
               cam.FOV = 44.0f - 8.0f * t;
               if (faceLight) {
                  // a soft light between the camera and the face, a little above it
                  Vector3 lp = new Vector3(hd.X + (pos.X - hd.X) * 0.6f, hd.Y + (pos.Y - hd.Y) * 0.6f, hd.Z + 0.35f);
                  if (faceLightObj == null) {
                     faceLightObj = new Light(System.Drawing.Color.FromArgb(255, 235, 200), lightRange, lightIntensity);
                  }
                  faceLightObj.Position = lp;
                  faceLightObj.Enabled = true;
               }
               break;
            }
            case Shot.ActorWide:
               // pulled back to frame both the player and the dead ped
               if (!hasFixedPos) {
                  Vector3 mid = new Vector3((pp.X + lastVictimPos.X) * 0.5f, (pp.Y + lastVictimPos.Y) * 0.5f, (pp.Z + lastVictimPos.Z) * 0.5f);
                  float gap = pp.DistanceTo(lastVictimPos);
                  float side = (float)Math.Atan2(lastVictimPos.Y - pp.Y, lastVictimPos.X - pp.X) + (float)Math.PI / 2.0f * shotDir;
                  fixedPos = ClearPosition(mid, side, gap * 0.9f + 3.0f, 1.8f);
                  hasFixedPos = true;
               }
               pos = fixedPos;
               target = new Vector3((pp.X + lastVictimPos.X) * 0.5f, (pp.Y + lastVictimPos.Y) * 0.5f, (pp.Z + lastVictimPos.Z) * 0.5f + 0.7f);
               break;
            case Shot.ActorOrbit:
               pos = ClearPosition(pp, angle, 3.3f, 1.2f);
               target = ActorHead();
               break;
            case Shot.ActorLowAngle: {
               // heroic low angle looking up at the player
               if (!hasFixedPos) { fixedPos = ClearPosition(pp, angle, 3.0f, 0.2f); hasFixedPos = true; }
               pos = fixedPos;
               Vector3 ah = ActorHead();
               target = new Vector3(ah.X, ah.Y, ah.Z - 0.1f);
               cam.FOV = 38.0f;
               break;
            }
            case Shot.ActorDolly: {
               float tad = ShotT();
               pos = ClearPosition(pp, angle, 5.0f - 2.4f * tad, 1.5f - 0.4f * tad);
               target = ActorHead();
               break;
            }
            case Shot.ActorProfile: {
               // side-on view from the player's right, slowly drifting forward
               float tp = ShotT();
               float ph = ((actor != null && actor.Exists()) ? actor.Heading : 0.0f) * (float)Math.PI / 180.0f;
               Vector3 phd = ActorHead();
               pos = Offset(phd, ph + 0.15f + tp * 0.35f, S(3.2f), 0.2f);
               if (IsBlocked(pos, pp.Z)) pos = ClearPosition(phd, ph + 0.15f, 3.2f, 0.4f);
               target = phd;
               cam.FOV = 36.0f;
               break;
            }
            case Shot.ActorCrane: {
               float tc = ShotT();
               pos = ClearPosition(pp, angle, 3.0f, 3.5f - 1.2f * tc);
               target = ActorHead();
               break;
            }
            case Shot.GroundClose: {
               // very low and close on the victim, drifting around slowly
               float tg = ShotT();
               pos = ClearPosition(lastVictimPos, angle + shotDir * 0.5f * tg, 1.5f, 0.2f);
               target = victimHead;
               cam.FOV = 38.0f;
               break;
            }
            case Shot.HandheldClose: {
               // handheld feel: small random-ish drift and roll
               float ms = Environment.TickCount / 1000.0f;
               Vector3 hp0 = ClearPosition(lastVictimPos, angle, 2.0f, 0.9f);
               pos = new Vector3(hp0.X + (float)Math.Sin(ms * 2.3f) * 0.05f, hp0.Y + (float)Math.Cos(ms * 1.9f) * 0.05f, hp0.Z + (float)Math.Sin(ms * 3.1f) * 0.04f);
               cam.Roll = (float)Math.Sin(ms * 1.7f) * 1.8f;
               target = victimHead;
               break;
            }
            case Shot.Flyby: {
               // fast pass from far to near across the scene
               float tf = ShotT();
               float ftang = angle + (float)Math.PI / 2.0f;
               Vector3 fb = Offset(lastVictimPos, angle, 5.0f - 2.5f * tf, 1.0f + 0.4f * tf);
               float fs = (tf - 0.5f) * 6.0f * shotDir;
               pos = new Vector3(fb.X + (float)Math.Cos(ftang) * fs, fb.Y + (float)Math.Sin(ftang) * fs, fb.Z);
               if (IsBlocked(pos, lastVictimPos.Z)) pos = ClearPosition(lastVictimPos, angle, 3.0f, 1.6f);
               target = victimHead;
               break;
            }
            case Shot.TwoShot: {
               // side-on framing of both characters with a slow push
               float tw = ShotT();
               Vector3 midp = new Vector3((pp.X + lastVictimPos.X) * 0.5f, (pp.Y + lastVictimPos.Y) * 0.5f, (pp.Z + lastVictimPos.Z) * 0.5f);
               float g2 = pp.DistanceTo(lastVictimPos);
               float sd = (float)Math.Atan2(lastVictimPos.Y - pp.Y, lastVictimPos.X - pp.X) + (float)Math.PI / 2.0f * shotDir;
               pos = ClearPosition(midp, sd, g2 * 0.8f + 3.0f - 0.8f * tw, 1.1f);
               target = new Vector3(midp.X, midp.Y, midp.Z + 0.6f);
               break;
            }
            case Shot.DollyIn: {
               // slow push toward the victim
               float td = ShotT();
               pos = ClearPosition(lastVictimPos, angle, 3.4f - 1.7f * td, 1.0f - 0.3f * td);
               target = victimHead;
               break;
            }
            case Shot.SideTrack: {
               // camera glides sideways past the victim
               float ts = ShotT();
               float tang = angle + (float)Math.PI / 2.0f;
               Vector3 basePos = ClearPosition(lastVictimPos, angle, 2.8f, 0.9f);
               float slide = (ts - 0.5f) * 3.5f * shotDir;
               pos = new Vector3(basePos.X + (float)Math.Cos(tang) * slide, basePos.Y + (float)Math.Sin(tang) * slide, basePos.Z);
               target = victimHead;
               break;
            }
            case Shot.TopDown: {
               // overhead shot with a slow roll
               float tt = ShotT();
               pos = Offset(lastVictimPos, angle, 0.4f, 5.0f - 1.0f * tt);
               if (IsBlocked(pos, lastVictimPos.Z)) pos = ClearPosition(lastVictimPos, angle, 2.2f, 2.2f);
               cam.Roll = shotDir * 35.0f * tt;
               cam.FOV = 55.0f;
               target = victimHead;
               break;
            }
            case Shot.LowRise: {
               // starts almost on the floor and rises with a slight tilt
               float tr = ShotT();
               pos = ClearPosition(lastVictimPos, angle, 2.3f, 0.15f + 1.3f * tr);
               cam.Roll = shotDir * 6.0f * (1.0f - tr);
               target = victimHead;
               break;
            }
            case Shot.DutchOrbit:
               // orbit with a tilted horizon
               pos = ClearPosition(lastVictimPos, angle, orbitRadius + 0.6f, orbitHeight + 0.4f);
               cam.Roll = shotDir * 14.0f;
               target = victimHead;
               break;
            case Shot.ZoomPull:
               // fixed far camera with a lens zoom that pulls out
               if (!hasFixedPos) { fixedPos = ClearPosition(lastVictimPos, angle, 6.0f, 1.6f); hasFixedPos = true; }
               pos = fixedPos;
               cam.FOV = 20.0f + 28.0f * ShotT();
               target = victimHead;
               break;
            default:
               pos = ClearPosition(lastVictimPos, angle, orbitRadius, orbitHeight + 0.6f);
               target = victimHead;
               break;
         }
         if (shot != Shot.ActorFace && faceLightObj != null && faceLightObj.Enabled) faceLightObj.Enabled = false;
         cam.Position = pos;
         cam.LookAt(target);
      }

      private void UpdateCamera() {
         int now = Environment.TickCount;
         Ped player = Player.Character;

         // end when time is up, when the player skips, or when the situation changes:
         // kill cam ends if the player dies; death cam ends when the dead player ped is gone (respawn)
         bool over = now - startTick >= activeDurationMs;
         if (deathMode) {
            if (victim == null || !victim.Exists()) over = true;
            else if (fakeDeath) {
               if (victim.isDead) {
                  fakeDeath = false;
               } else if (now - lastRagdollTick > 2000) {
                  lastRagdollTick = now;
                  if (!victim.isRagdoll) victim.ForceRagdoll(deathHoldMs, false);
               }
            }
            else if (!bustedMode && !victim.isDead) over = true;
            else if (bustedMode && victim.Position.DistanceTo(deathStartPos) > 25.0f) over = true;
            if (!over && holdFade) KeepVisible();
         } else if (player == null || !player.Exists() || player.isDead) {
            over = true;
         }
         if (over) {
            StopCamera();
            return;
         }
         bool skip = SkipPressed();
         if (!skip) skipArmed = true;
         if (skip && skipArmed) {
            Debug("skipped");
            StopCamera();
            return;
         }

         if (effectOn && now - startTick >= effectMs) StopEffect();

         if (now - shotStartTick >= shotLengthMs) {
            ChooseShot(false);
            if (!deathMode && forceAim) AimAtVictim(player, false);
         }

         if (!deathMode && forceAim && now - lastAimTick >= 300) AimAtVictim(player, false);

         // orbit speed uses real time, so slow motion does not stall the camera
         float dt = (now - lastFrameTick) / 1000.0f;
         lastFrameTick = now;
         if (shot == Shot.OrbitVictim || shot == Shot.HighCrane || shot == Shot.DutchOrbit || shot == Shot.ActorOrbit) {
            angle += shotDir * orbitSpeedDeg * dt * (float)Math.PI / 180.0f;
         }
         PositionCamera();
      }

      private void StopCamera() {
         if (fakeDeath) {
            fakeDeath = false;
            Game.TimeScale = 1.0f;
            try {
               if (victim != null && victim.Exists()) {
                  victim.Invincible = false;
                  victim.Die();
               }
            } catch (Exception) { }
         }
         if (faceLightObj != null) {
            try { faceLightObj.Enabled = false; } catch (Exception) { }
         }
         Game.TimeScale = 1.0f;
         StopEffect();
         if (state == CamState.Playing && invincible && !deathMode) {
            Ped player = Player.Character;
            if (player != null && player.Exists()) player.Invincible = false;
         }
         if (controlLocked) {
            Player.CanControlCharacter = true;
            controlLocked = false;
         }
         if (state == CamState.Playing && !deathMode && forceAim) {
            Ped p2 = Player.Character;
            if (p2 != null && p2.Exists()) p2.Task.ClearAll();
         }
         if (cam != null) {
            try {
               cam.Deactivate();
               cam.Delete();
            } catch (Exception) { }
            cam = null;
         }
         if (state == CamState.Playing) {
            lastEndTick = Environment.TickCount;
            Debug("stopped");
         }
         victim = null;
         actor = null;
         deathMode = false;
         bustedMode = false;
         state = CamState.Idle;
         this.Interval = 100;
      }
   }
}
