using System;
using System.Collections.Generic;
using System.IO;
using GTA;

namespace KillCam {

   // Kill-cam in the style of RDR2 / Max Payne 3: when you kill a ped, time slows down
   // and the camera swings around the victim, then returns control to the player.
   // Written in C# 2.0 style for ScriptHookDotNet.
   public class KillCam : Script {

      private enum CamState {
         Idle,
         Playing
      }

      // settings (see KillCam.ini)
      private bool enabled = true;
      private int chancePercent = 35;
      private int cooldownMs = 8000;
      private int durationMs = 2200;
      private float slowScale = 0.2f;
      private float scanRadius = 40.0f;
      private float orbitRadius = 3.2f;
      private float orbitHeight = 0.8f;
      private float orbitSpeedDeg = 70.0f; // degrees per real second
      private int minPlayerHealth = 1;
      private bool debug = false;

      private List<Ped> tracked = new List<Ped>();
      private CamState state = CamState.Idle;
      private Camera cam = null;
      private Ped victim = null;
      private Vector3 lastVictimPos;
      private int startTick = 0;
      private int lastEndTick = -100000;
      private float angle = 0.0f;
      private int lastFrameTick = 0;

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
               Settings.SetValue("ChancePercent", "General", 35);
               Settings.SetValue("CooldownMs", "General", 8000);
               Settings.SetValue("Debug", "General", false);
               Settings.SetValue("DurationMs", "Camera", 2200);
               Settings.SetValue("SlowMotionScale", "Camera", 0.2f);
               Settings.SetValue("OrbitRadius", "Camera", 3.2f);
               Settings.SetValue("OrbitHeight", "Camera", 0.8f);
               Settings.SetValue("OrbitSpeedDegPerSec", "Camera", 70.0f);
               Settings.SetValue("ScanRadius", "General", 40.0f);
               Settings.Save();
            }
         } catch (Exception ex) {
            Game.Console.Print("KillCam could not create ini: " + ex.Message);
         }
         enabled = Settings.GetValueBool("Enabled", "General", true);
         chancePercent = Settings.GetValueInteger("ChancePercent", "General", 35);
         cooldownMs = Settings.GetValueInteger("CooldownMs", "General", 8000);
         debug = Settings.GetValueBool("Debug", "General", false);
         scanRadius = Settings.GetValueFloat("ScanRadius", "General", 40.0f);
         durationMs = Settings.GetValueInteger("DurationMs", "Camera", 2200);
         slowScale = Settings.GetValueFloat("SlowMotionScale", "Camera", 0.2f);
         orbitRadius = Settings.GetValueFloat("OrbitRadius", "Camera", 3.2f);
         orbitHeight = Settings.GetValueFloat("OrbitHeight", "Camera", 0.8f);
         orbitSpeedDeg = Settings.GetValueFloat("OrbitSpeedDegPerSec", "Camera", 70.0f);
         if (slowScale < 0.05f) slowScale = 0.05f;
         if (slowScale > 1.0f) slowScale = 1.0f;
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
            TryStartCamera(killed);
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

      private void TryStartCamera(Ped killed) {
         if (Environment.TickCount - lastEndTick < cooldownMs) return;
         if (Player.Character.Health < minPlayerHealth) return;
         if (Game.Random.Next(100) >= chancePercent) return;

         victim = killed;
         lastVictimPos = victim.Position;
         // start behind the shooter's line of sight: begin on the player's side of the victim
         Vector3 toPlayer = Player.Character.Position - lastVictimPos;
         angle = (float)Math.Atan2(toPlayer.Y, toPlayer.X);
         cam = new Camera();
         cam.FOV = 55.0f;
         PositionCamera();
         cam.Activate();
         Game.TimeScale = slowScale;

         startTick = Environment.TickCount;
         lastFrameTick = startTick;
         state = CamState.Playing;
         this.Interval = 0;
         Debug("started");
      }

      private void PositionCamera() {
         if (victim != null && victim.Exists()) lastVictimPos = victim.Position;
         Vector3 target = new Vector3(lastVictimPos.X, lastVictimPos.Y, lastVictimPos.Z + 0.4f);
         Vector3 pos = new Vector3(
            target.X + (float)Math.Cos(angle) * orbitRadius,
            target.Y + (float)Math.Sin(angle) * orbitRadius,
            target.Z + orbitHeight);
         cam.Position = pos;
         cam.LookAt(target);
      }

      private void UpdateCamera() {
         int now = Environment.TickCount;
         Ped player = Player.Character;

         // end early if the player dies or time is up
         if (player == null || !player.Exists() || player.isDead || now - startTick >= durationMs) {
            StopCamera();
            return;
         }

         // orbit speed uses real time, so slow motion does not stall the camera
         float dt = (now - lastFrameTick) / 1000.0f;
         lastFrameTick = now;
         angle += orbitSpeedDeg * dt * (float)Math.PI / 180.0f;
         PositionCamera();
      }

      private void StopCamera() {
         Game.TimeScale = 1.0f;
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
