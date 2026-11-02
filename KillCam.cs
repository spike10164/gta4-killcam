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
         PlayerFace,
         PlayerWide
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
      private bool lockPlayer = true;
      private bool skipArmed = false;
      private bool controlLocked = false;

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

      public KillCam() {
         LoadSettings();
         this.Interval = 100;
         this.Tick += new EventHandler(this.KillCam_Tick);
      }

      // Creates KillCam.ini next to the script on first launch.
      private void LoadSettings() {
         try {
            if (!File.Exists(Settings.Filename)) {
               Settings.SetValue("Enabled", "General", true);
               Settings.SetValue("ChancePercent", "General", 20);
               Settings.SetValue("CooldownMs", "General", 45000);
               Settings.SetValue("LastEnemyOnly", "General", true);
               Settings.SetValue("ScanRadius", "General", 40.0f);
               Settings.SetValue("Debug", "General", false);
               Settings.SetValue("DurationMs", "Camera", 7000);
               Settings.SetValue("ShotLengthMs", "Camera", 3500);
               Settings.SetValue("EndOnPlayerPercent", "Camera", 60);
               Settings.SetValue("SlowMotionScale", "Camera", 0.1f);
               Settings.SetValue("OrbitRadius", "Camera", 1.9f);
               Settings.SetValue("OrbitHeight", "Camera", 0.5f);
               Settings.SetValue("OrbitSpeedDegPerSec", "Camera", 12.0f);
               Settings.SetValue("Invincible", "Player", true);
               Settings.SetValue("LockPlayerControl", "Player", true);
               Settings.SetValue("HitDropThreshold", "General", 6);
               Settings.SetValue("KillWindowMs", "General", 1500);
               Settings.SetValue("WallClearance", "Camera", 1.3f);
               Settings.SetValue("Timecycles", "Effect", "Steel,Cinema,Eclipse");
               Settings.SetValue("EffectMs", "Effect", 7000);
               Settings.Save();
            }
         } catch (Exception ex) {
            Game.Console.Print("KillCam could not create ini: " + ex.Message);
         }
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
         lockPlayer = Settings.GetValueBool("LockPlayerControl", "Player", true);
         hitDropThreshold = Settings.GetValueInteger("HitDropThreshold", "General", 6);
         killWindowMs = Settings.GetValueInteger("KillWindowMs", "General", 1500);
         camClearance = Settings.GetValueFloat("WallClearance", "Camera", 1.3f);
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
         if (player == null || !player.Exists() || player.isDead) {
            tracked.Clear();
            return;
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

         victim = killed;
         lastVictimPos = victim.Position;
         cam = new Camera();
         cam.FOV = 42.0f;
         finalShotChosen = false;
         hasFixedPos = false;
         int now = Environment.TickCount;
         startTick = now;
         lastFrameTick = now;
         ChooseShot(true);
         cam.Activate();

         Game.TimeScale = slowScale;
         if (invincible) {
            player.Invincible = true;
         }
         if (lockPlayer) {
            Player.CanControlCharacter = false;
            controlLocked = true;
         }
         AimAtVictim(player);
         // the skip key must be released once before it counts, so a held sprint does not skip at once
         skipArmed = !SkipPressed();
         StartEffect();

         state = CamState.Playing;
         this.Interval = 0;
         Debug("started");
      }

      // Space on keyboard and A on a controller are both bound to Sprint in GTA IV
      private bool SkipPressed() {
         return isKeyPressed(System.Windows.Forms.Keys.Space) || Game.isGameKeyPressed(GameKey.Sprint);
      }

      private void AimAtVictim(Ped player) {
         try {
            player.Task.ClearAll();
            player.Task.AimAt(new Vector3(lastVictimPos.X, lastVictimPos.Y, lastVictimPos.Z + 0.3f), durationMs);
         } catch (Exception) { }
      }

      // Heuristic wall check: if the first surface below a point high above it is much higher
      // than the subject, something solid (wall, roof, car) is in the way.
      private bool IsBlocked(Vector3 p, float subjectZ) {
         float ground = World.GetGroundZ(new Vector3(p.X, p.Y, subjectZ + 6.0f), GroundType.NextBelowCurrent);
         return ground > subjectZ + camClearance;
      }

      // Tries the wanted spot, then closer and higher ones, and returns the first clear one.
      private Vector3 ClearPosition(Vector3 center, float ang, float radius, float height) {
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
         bool isFinal = (Environment.TickCount - startTick) + shotLengthMs >= durationMs;
         if (isFinal && !finalShotChosen) {
            finalShotChosen = true;
            if (rng.Next(100) < endOnPlayerPercent) {
               next = (rng.Next(2) == 0) ? Shot.PlayerFace : Shot.PlayerWide;
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

         Vector3 pp = Player.Character.Position;
         Vector3 target = IsPlayerShot() ? pp : lastVictimPos;
         // start the orbit on the side facing the other character
         Vector3 from = IsPlayerShot() ? lastVictimPos : pp;
         angle = (float)Math.Atan2(from.Y - target.Y, from.X - target.X);
         // random starting angle for variety
         angle += (float)(rng.NextDouble() * Math.PI - Math.PI / 2.0);
         Debug("shot " + shot.ToString());
      }

      private Shot RandomVictimShot(Shot previous) {
         Shot[] options = new Shot[] { Shot.OrbitVictim, Shot.LowFront, Shot.HighCrane, Shot.OverShoulder };
         Shot pick = options[rng.Next(options.Length)];
         if (pick == previous) pick = options[(Array.IndexOf(options, pick) + 1) % options.Length];
         return pick;
      }

      private bool IsPlayerShot() {
         return shot == Shot.PlayerFace || shot == Shot.PlayerWide;
      }

      private Vector3 Offset(Vector3 center, float ang, float radius, float height) {
         return new Vector3(
            center.X + (float)Math.Cos(ang) * radius,
            center.Y + (float)Math.Sin(ang) * radius,
            center.Z + height);
      }

      private void PositionCamera() {
         if (victim != null && victim.Exists()) lastVictimPos = victim.Position;
         Ped player = Player.Character;
         Vector3 pp = player.Position;
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
            case Shot.PlayerFace:
               // close on the face from the player's right, a bit in front, clear of the gun arm
               if (!hasFixedPos) {
                  float h = player.Heading * (float)Math.PI / 180.0f;
                  float fx = -(float)Math.Sin(h);
                  float fy = (float)Math.Cos(h);
                  float rx = (float)Math.Cos(h);
                  float ry = (float)Math.Sin(h);
                  Vector3 hd = player.GetBonePosition(Bone.Head);
                  Vector3 want = new Vector3(hd.X + rx * 0.9f + fx * 0.9f, hd.Y + ry * 0.9f + fy * 0.9f, hd.Z + 0.15f);
                  if (IsBlocked(want, pp.Z)) {
                     want = new Vector3(hd.X + rx * 0.6f + fx * 0.5f, hd.Y + ry * 0.6f + fy * 0.5f, hd.Z + 0.9f);
                  }
                  fixedPos = want;
                  hasFixedPos = true;
               }
               pos = fixedPos;
               target = player.GetBonePosition(Bone.Head);
               break;
            case Shot.PlayerWide:
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
            default:
               pos = ClearPosition(lastVictimPos, angle, orbitRadius, orbitHeight + 0.6f);
               target = victimHead;
               break;
         }
         cam.Position = pos;
         cam.LookAt(target);
      }

      private void UpdateCamera() {
         int now = Environment.TickCount;
         Ped player = Player.Character;

         // end early if the player dies, the time is up, or the player skips
         if (player == null || !player.Exists() || player.isDead || now - startTick >= durationMs) {
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
            AimAtVictim(player);
         }

         // orbit speed uses real time, so slow motion does not stall the camera
         float dt = (now - lastFrameTick) / 1000.0f;
         lastFrameTick = now;
         if (shot == Shot.OrbitVictim || shot == Shot.HighCrane) {
            angle += shotDir * orbitSpeedDeg * dt * (float)Math.PI / 180.0f;
         }
         PositionCamera();
      }

      private void StopCamera() {
         Game.TimeScale = 1.0f;
         StopEffect();
         if (state == CamState.Playing && invincible) {
            Ped player = Player.Character;
            if (player != null && player.Exists()) player.Invincible = false;
         }
         if (controlLocked) {
            Player.CanControlCharacter = true;
            controlLocked = false;
         }
         if (state == CamState.Playing) {
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
         state = CamState.Idle;
         this.Interval = 100;
      }
   }
}
