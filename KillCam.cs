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
         PlayerOrbit,
         PlayerFront
      }

      // settings (see KillCam.ini)
      private bool enabled = true;
      private int chancePercent = 20;
      private int cooldownMs = 45000;
      private bool lastEnemyOnly = true;
      private float scanRadius = 40.0f;
      private int durationMs = 3200;
      private int shotLengthMs = 1000;
      private int endOnPlayerPercent = 60;
      private float slowScale = 0.2f;
      private float orbitRadius = 3.2f;
      private float orbitHeight = 0.8f;
      private float orbitSpeedDeg = 60.0f; // degrees per real second
      private bool invincible = true;
      private string[] timecycles = new string[] { "bleachbypass" };
      private int effectMs = 1800;
      private bool debug = false;

      private List<Ped> tracked = new List<Ped>();
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
      private bool wasInvincible = false;
      private bool effectOn = false;

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
               Settings.SetValue("DurationMs", "Camera", 3200);
               Settings.SetValue("ShotLengthMs", "Camera", 1000);
               Settings.SetValue("EndOnPlayerPercent", "Camera", 60);
               Settings.SetValue("SlowMotionScale", "Camera", 0.2f);
               Settings.SetValue("OrbitRadius", "Camera", 3.2f);
               Settings.SetValue("OrbitHeight", "Camera", 0.8f);
               Settings.SetValue("OrbitSpeedDegPerSec", "Camera", 60.0f);
               Settings.SetValue("Invincible", "Player", true);
               Settings.SetValue("Timecycles", "Effect", "bleachbypass");
               Settings.SetValue("EffectMs", "Effect", 1800);
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
         durationMs = Settings.GetValueInteger("DurationMs", "Camera", 3200);
         shotLengthMs = Settings.GetValueInteger("ShotLengthMs", "Camera", 1000);
         endOnPlayerPercent = Settings.GetValueInteger("EndOnPlayerPercent", "Camera", 60);
         slowScale = Settings.GetValueFloat("SlowMotionScale", "Camera", 0.2f);
         orbitRadius = Settings.GetValueFloat("OrbitRadius", "Camera", 3.2f);
         orbitHeight = Settings.GetValueFloat("OrbitHeight", "Camera", 0.8f);
         orbitSpeedDeg = Settings.GetValueFloat("OrbitSpeedDegPerSec", "Camera", 60.0f);
         invincible = Settings.GetValueBool("Invincible", "Player", true);
         effectMs = Settings.GetValueInteger("EffectMs", "Effect", 1800);

         // comma separated list; one is picked at random each time
         string list = Settings.GetValueString("Timecycles", "Effect", "bleachbypass");
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

      private bool IsOtherEnemyAlive(Ped player, Ped killed) {
         Ped[] nearby = World.GetPeds(player.Position, scanRadius);
         for (int i = 0; i < nearby.Length; i++) {
            Ped p = nearby[i];
            if (p == null || !p.Exists() || p.isDead || p.Equals(player) || p.Equals(killed)) continue;
            // an enemy is anyone still fighting, or anyone we already hurt
            if (p.isInCombat || tracked.Contains(p)) return true;
         }
         return false;
      }

      // Watches living peds the player has hurt, and fires when one of them dies.
      // Peds that were already dead when first seen never trigger, so old corpses are ignored.
      private void ScanForKills() {
         Ped player = Player.Character;
         if (player == null || !player.Exists() || player.isDead) {
            tracked.Clear();
            return;
         }

         Ped killed = null;
         for (int i = tracked.Count - 1; i >= 0; i--) {
            Ped p = tracked[i];
            if (p == null || !p.Exists()) {
               tracked.RemoveAt(i);
            } else if (p.isDead) {
               tracked.RemoveAt(i);
               if (killed == null) killed = p;
            } else if (p.Position.DistanceTo(player.Position) > scanRadius * 1.5f) {
               tracked.RemoveAt(i);
            }
         }

         if (killed != null) {
            TryStartCamera(player, killed);
            return;
         }

         Ped[] nearby = World.GetPeds(player.Position, scanRadius);
         for (int i = 0; i < nearby.Length; i++) {
            Ped p = nearby[i];
            if (p == null || !p.Exists() || p.isDead || p.Equals(player)) continue;
            if (!tracked.Contains(p) && p.HasBeenDamagedBy(player)) {
               tracked.Add(p);
            }
         }
      }

      private void TryStartCamera(Ped player, Ped killed) {
         if (Environment.TickCount - lastEndTick < cooldownMs) return;
         if (lastEnemyOnly && IsOtherEnemyAlive(player, killed)) return;
         if (Game.Random.Next(100) >= chancePercent) return;

         victim = killed;
         lastVictimPos = victim.Position;
         cam = new Camera();
         cam.FOV = 55.0f;
         finalShotChosen = false;
         hasFixedPos = false;
         int now = Environment.TickCount;
         startTick = now;
         lastFrameTick = now;
         ChooseShot(true);
         cam.Activate();

         Game.TimeScale = slowScale;
         if (invincible) {
            wasInvincible = player.Invincible;
            player.Invincible = true;
         }
         StartEffect();

         state = CamState.Playing;
         this.Interval = 0;
         Debug("started");
      }

      private void StartEffect() {
         if (timecycles.Length == 0) return;
         string name = timecycles[Game.Random.Next(timecycles.Length)];
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
            if (Game.Random.Next(100) < endOnPlayerPercent) {
               next = (Game.Random.Next(2) == 0) ? Shot.PlayerOrbit : Shot.PlayerFront;
            } else {
               next = RandomVictimShot(shot);
            }
         } else if (!first) {
            next = RandomVictimShot(shot);
         }

         shot = next;
         shotStartTick = Environment.TickCount;
         shotDir = (Game.Random.Next(2) == 0) ? 1.0f : -1.0f;
         hasFixedPos = false;

         Vector3 pp = Player.Character.Position;
         Vector3 target = IsPlayerShot() ? pp : lastVictimPos;
         // start the orbit on the side facing the other character
         Vector3 from = IsPlayerShot() ? lastVictimPos : pp;
         angle = (float)Math.Atan2(from.Y - target.Y, from.X - target.X);
         // random starting angle for variety
         angle += (float)(Game.Random.NextDouble() * Math.PI - Math.PI / 2.0);
         Debug("shot " + shot.ToString());
      }

      private Shot RandomVictimShot(Shot previous) {
         Shot[] options = new Shot[] { Shot.OrbitVictim, Shot.LowFront, Shot.HighCrane, Shot.OverShoulder };
         Shot pick = options[Game.Random.Next(options.Length)];
         if (pick == previous) pick = options[(Array.IndexOf(options, pick) + 1) % options.Length];
         return pick;
      }

      private bool IsPlayerShot() {
         return shot == Shot.PlayerOrbit || shot == Shot.PlayerFront;
      }

      private Vector3 Offset(Vector3 center, float ang, float radius, float height) {
         return new Vector3(
            center.X + (float)Math.Cos(ang) * radius,
            center.Y + (float)Math.Sin(ang) * radius,
            center.Z + height);
      }

      private void PositionCamera() {
         if (victim != null && victim.Exists()) lastVictimPos = victim.Position;
         Vector3 pp = Player.Character.Position;
         Vector3 victimHead = new Vector3(lastVictimPos.X, lastVictimPos.Y, lastVictimPos.Z + 0.4f);
         Vector3 playerHead = new Vector3(pp.X, pp.Y, pp.Z + 0.5f);
         Vector3 pos;
         Vector3 target;

         switch (shot) {
            case Shot.LowFront:
               // static, close to the ground, looking up at the victim
               if (!hasFixedPos) { fixedPos = Offset(lastVictimPos, angle, 2.6f, -0.4f); hasFixedPos = true; }
               pos = fixedPos;
               target = victimHead;
               break;
            case Shot.HighCrane:
               // slowly rising shot from above
               float rise = (Environment.TickCount - shotStartTick) / 1000.0f;
               pos = Offset(lastVictimPos, angle, 3.5f, 3.5f + rise * 1.2f);
               target = victimHead;
               break;
            case Shot.OverShoulder:
               // behind the player, looking at the victim
               float dx = lastVictimPos.X - pp.X;
               float dy = lastVictimPos.Y - pp.Y;
               float back = (float)Math.Atan2(dy, dx) + (float)Math.PI;
               pos = Offset(pp, back, 2.2f, 1.2f);
               target = victimHead;
               break;
            case Shot.PlayerOrbit:
               pos = Offset(pp, angle, orbitRadius, orbitHeight);
               target = playerHead;
               break;
            case Shot.PlayerFront:
               // static close-up of the player
               if (!hasFixedPos) { fixedPos = Offset(pp, angle, 2.0f, 0.5f); hasFixedPos = true; }
               pos = fixedPos;
               target = playerHead;
               break;
            default:
               pos = Offset(lastVictimPos, angle, orbitRadius, orbitHeight);
               target = victimHead;
               break;
         }
         cam.Position = pos;
         cam.LookAt(target);
      }

      private void UpdateCamera() {
         int now = Environment.TickCount;
         Ped player = Player.Character;

         // end early only if the player somehow dies, otherwise when time is up
         if (player == null || !player.Exists() || player.isDead || now - startTick >= durationMs) {
            StopCamera();
            return;
         }

         if (effectOn && now - startTick >= effectMs) StopEffect();

         if (now - shotStartTick >= shotLengthMs) ChooseShot(false);

         // orbit speed uses real time, so slow motion does not stall the camera
         float dt = (now - lastFrameTick) / 1000.0f;
         lastFrameTick = now;
         if (shot == Shot.OrbitVictim || shot == Shot.PlayerOrbit || shot == Shot.HighCrane) {
            angle += shotDir * orbitSpeedDeg * dt * (float)Math.PI / 180.0f;
         }
         PositionCamera();
      }

      private void StopCamera() {
         Game.TimeScale = 1.0f;
         StopEffect();
         if (state == CamState.Playing && invincible) {
            Ped player = Player.Character;
            if (player != null && player.Exists()) player.Invincible = wasInvincible;
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
