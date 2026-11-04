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
         ActorThreeQuarter,
         ActorCloseQuarter,
         ActorHeroLow,
         ActorGunSide,
         ActorRearQuarter,
         ActorLongQuarter,
         ActorDutchQuarter,
         ActorSweepQuarter,
         ActorLowSide,
         ActorRiseQuarter,
         CarQuarter,
         CarRearQuarter,
         MeleeBehind,
         MeleeTwoShot,
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
         Telephoto,
         BulletFollow,
         PullBack,
         WhipPan,
         CrashZoom,
         CarSide,
         CarLowFront,
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
      private int chancePercent = 40;
      private int cooldownMs = 45000;
      private bool lastEnemyOnly = true;
      private bool alwaysLastEnemy = false;
      private float scanRadius = 40.0f;
      private int durationMs = 7000;
      private int shotLengthMs = 3500;
      private int endOnPlayerPercent = 60;
      private float slowScale = 0.05f;
      private float orbitRadius = 1.9f;
      private float orbitHeight = 0.5f;
      private float orbitSpeedDeg = 4.0f; // degrees per real second
      private string[] timecycles = new string[] { "Steel", "Cinema", "Eclipse" };
      private bool debug = false;

      // a single health drop of at least this much counts as a hit; bleeding drops by 1-2 at a time
      private int hitDropThreshold = 6;
      // a death only counts as a kill if the last hit was this recent
      private int killWindowMs = 1500;
      private float camClearance = 1.3f;
      private bool skipArmed = false;

      // death cam: when the player is killed by an enemy, the roles are reversed
      private bool deathCam = true;
      private int deathChancePercent = 100;
      private bool deathMode = false;
      private bool bustedMode = false;
      private bool tiersOn = true;
      private int tier = 0;
      private bool lastEnemyKill = false;
      private int finalHoldMs = 1200;
      private bool finalShotStarted = false;
      private bool matchKill = true;
      private bool behindKill = false;
      private bool frontKnown = false;
      private float frontAngle = 0.0f;
      private int rampMs = 1500;
      private float rampFrom = 0.6f;
      private bool lastEnemyRamp = true;
      private float baseScale = 0.05f;
      private bool rampIn = false;
      private bool rampOutOn = true;
      private bool sequenceRules = true;
      private bool eyeline = true;
      private bool perShotLengths = true;
      private int openerMs = 900;
      private int baseShotMs = 3500;
      private int cutIndex = 0;
      private float sideSign = 1.0f;
      private bool adaptiveChance = true;
      private List<int> recentKills = new List<int>();
      private List<Shot> disabledShots = new List<Shot>();
      private int camCount = 0;
      private float dayLightScale = 0.6f;
      private bool isNight = false;
      private float lastSetScale = -1.0f;
      private bool madeInvincible = false;
      private bool invincible = true;
      private bool timeScaleSet = false;
      private int lastErrorTick = -100000;
      private int lastPedScanTick = 0;
      private bool situationShots = true;
      private float killDist = 0.0f;
      private bool explosive = false;
      private int longRangeChance = 50;
      private float longRangeDist = 25.0f;
      private int explosionChance = 80;
      private bool actorInCar = false;
      private bool victimInCar = false;
      private float radiusMul = 1.0f;
      private int guaranteeAfterMs = 300000;
      private int lastKillCamTick = Environment.TickCount;
      private bool deathAnyCause = true;
      private bool noActor = false;
      private int activeShotMs = 3500;
      private int bustedCuts = 2;
      private Vector3 deathStartPos;
      private bool deathHandled = false;
      private bool bustedCam = true;
      private bool bustedHandled = false;
      private int deathHoldMs = 30000;
      private float deathTimeScale = 0.02f;
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
      private static readonly float[] clearScales = new float[] { 1.0f, 0.75f, 0.55f };

      public KillCam() {
         LoadSettings();
         // clears any scripted camera left behind by a previous run of this script
         ReleaseView();
         this.Interval = 100;
         this.Tick += new EventHandler(this.KillCam_Tick);
         this.PerFrameDrawing += new GraphicsEventHandler(this.KillCam_Draw);
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
            if (!File.Exists(Settings.Filename)) {
               WriteDefaultIni();
               Settings.Load();
               lastIniWrite = File.GetLastWriteTime(Settings.Filename);
               ReadSettings();
               Game.Console.Print("KillCam: settings file recreated");
               return;
            }
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
            "ChancePercent=40",
            "// Minimum time between kill cams, in milliseconds",
            "CooldownMs=45000",
            "// true = only the last remaining enemy triggers it",
            "LastEnemyOnly=true",
            "// true = killing the last enemy always plays a kill cam (ignores the chance roll and cooldown)",
            "AlwaysLastEnemyCam=false",
            "// Higher chance (0-100) for kills from at least LongRangeDistance metres away",
            "LongRangeChancePercent=50",
            "LongRangeDistance=25",
            "// Higher chance (0-100) for kills with grenades, molotovs and rockets; these also get a big, wide shot",
            "ExplosionChancePercent=80",
            "// Radius (metres) around the player that is watched for kills",
            "ScanRadius=40",
            "// If no kill cam has played on a ped for this long (ms), the next qualifying kill skips the chance roll",
            "// and is guaranteed to trigger one. 0 = off",
            "GuaranteeAfterMs=300000",
            "// Chance adapts to the play: slightly higher in quiet moments and as time passes since the last cam,",
            "// lower during long firefights",
            "AdaptiveChance=true",
            "// Play a short cam when the player is thrown out of a fast-moving vehicle",
            "EjectCam=true",
            "EjectCamChancePercent=100",
            "// Minimum vehicle speed (m/s) at the moment of ejection",
            "EjectMinSpeed=8",
            "// Prints debug lines to the console (~)",
            "Debug=false",
            "// A single health drop at least this big counts as a hit (bleeding drops 1-2 at a time)",
            "HitDropThreshold=6",
            "// A death only counts as a kill if the last big hit was this recent (ms). Bleed-outs are ignored",
            "KillWindowMs=1500",
            "",
            "[Camera]",
            "// Length of the eject cam in real milliseconds (kill cams are one to three shots of ShotLengthMs)",
            "DurationMs=7000",
            "// Length of each shot in milliseconds",
            "ShotLengthMs=3500",
            "// Chance (0-100) that the last shot is on the player (face or wide shot)",
            "EndOnPlayerPercent=60",
            "// Game speed during the cam (0.02 - 1.0). Lower = slower",
            "SlowMotionScale=0.05",
            "// Orbit shot distance from the dead ped, metres",
            "OrbitRadius=1.9",
            "// Orbit shot height, metres",
            "OrbitHeight=0.5",
            "// Orbit rotation speed, degrees per real second",
            "OrbitSpeedDegPerSec=4",
            "// Cameras are pulled in when a wall is detected closer than this (metres)",
            "WallClearance=1.3",
            "// Shot sequencing: open on the victim, cut to the player, finish on the victim (or player); never repeat",
            "// the same kind of move twice in a row and alternate wide and tight shots",
            "SequenceRules=true",
            "// Keeps every cut on the same side of the line between player and victim, so left/right never flips",
            "EyelineMatching=true",
            "// Fast shots (whip pan, crash zoom, handheld) are shorter and slow ones (cranes, long lens) are longer than ShotLengthMs",
            "PerShotLengths=true",
            "// On a last-enemy kill the first shot is a short, tight shot on the victim lasting this many ms. 0 = off",
            "OpenerMs=900",
            "// Shots never used, comma separated. Example: DisabledShots=TopDown,WhipPan",
            "// Names: OrbitVictim, LowFront, HighCrane, OverShoulder, DollyIn, SideTrack, TopDown, LowRise, DutchOrbit, ZoomPull,",
            "// Telephoto, BulletFollow, PullBack, WhipPan, CrashZoom, GroundClose, HandheldClose, Flyby, TwoShot, ActorFace,",
            "// ActorWide, ActorOrbit, ActorLowAngle, ActorDolly, ActorProfile, ActorCrane, ActorThreeQuarter, ActorCloseQuarter,",
            "// ActorHeroLow, ActorGunSide, ActorRearQuarter, ActorLongQuarter, ActorDutchQuarter, ActorSweepQuarter, ActorLowSide, ActorRiseQuarter, CarQuarter, CarRearQuarter, MeleeBehind, MeleeTwoShot, CarSide, CarLowFront",
            "DisabledShots=",
            "// Choose shots by the situation: kill distance and whether anyone is in a vehicle",
            "SituationShots=true",
            "// Stronger cams for bigger moments: killing the last enemy, or a kill while at low health.",
            "// Each tier adds length and slower motion",
            "IntensityTiers=true",
            "// Extra time (ms) the final shot is held so the moment lands. Kill cams only",
            "FinalHoldMs=1200",
            "// Kills from behind start on the victim's face side so you see the reaction",
            "MatchKillDirection=true",
            "// What to do when the player is inside an interior:",
            "//   Tight    = use close, tight shots (default)",
            "//   Disabled = no camera indoors",
            "//   Normal   = same as outdoors (may clip walls)",
            "InteriorMode=Tight",
            "// Scale applied to distances in Tight mode (smaller = closer)",
            "InteriorScale=0.5",
            "",
            "[Player]",
            "// Player cannot be hurt during a kill cam",
            "Invincible=true",
            "",
            "[Death]",
            "// Reverse logic: when an enemy kills the player, play the cam on the player's body",
            "DeathCam=true",
            "DeathCamChancePercent=100",
            "// true = also play the death cam when the player dies with no killer (fall, explosion, drowning, car crash...)",
            "DeathCamAnyCause=true",
            "// Number of camera cuts during the busted cam (fewer = calmer). 1-4",
            "BustedCuts=2",
            "// Also play the cam when the player is busted (arrested). The nearest ped is used as the arresting officer",
            "BustedCam=true",
            "// How long (ms) the death/busted cam is held before the game is allowed to respawn you. Jump skips it",
            "HoldMs=30000",
            "// Game speed while holding (0.01 - 1.0). Very low speed stretches the game's own respawn timers",
            "HoldTimeScale=0.02",
            "",
            "[Light]",
            "// Soft light on the player's face during player shots. Full strength at night, scaled down by day",
            "Enabled=true",
            "DayIntensityScale=0.6",
            "Range=4.5",
            "Intensity=50",
            "",
            "[Effect]",
            "// The screen effect starts with the first camera and clears at the end of the last one.",
            "// It is never used on the death or busted cam",
            "// On a last-enemy kill the slow-motion eases in from near-normal speed",
            "LastEnemyRamp=true",
            "// Every kill cam eases the game speed back to normal over the last SlowRampMs",
            "SlowRampOut=true",
            "SlowRampMs=1500",
            "// Starting game speed of the ramp (0.1 - 1.0)",
            "SlowRampFrom=0.6",
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
         chancePercent = Settings.GetValueInteger("ChancePercent", "General", 40);
         cooldownMs = Settings.GetValueInteger("CooldownMs", "General", 45000);
         lastEnemyOnly = Settings.GetValueBool("LastEnemyOnly", "General", true);
         alwaysLastEnemy = Settings.GetValueBool("AlwaysLastEnemyCam", "General", false);
         longRangeChance = Settings.GetValueInteger("LongRangeChancePercent", "General", 50);
         longRangeDist = Settings.GetValueFloat("LongRangeDistance", "General", 25.0f);
         explosionChance = Settings.GetValueInteger("ExplosionChancePercent", "General", 80);
         scanRadius = Settings.GetValueFloat("ScanRadius", "General", 40.0f);
         debug = Settings.GetValueBool("Debug", "General", false);
         ejectCam = Settings.GetValueBool("EjectCam", "General", true);
         ejectChance = ClampPercent(Settings.GetValueInteger("EjectCamChancePercent", "General", 100));
         ejectMinSpeed = Settings.GetValueFloat("EjectMinSpeed", "General", 8.0f);
         adaptiveChance = Settings.GetValueBool("AdaptiveChance", "General", true);
         sequenceRules = Settings.GetValueBool("SequenceRules", "Camera", true);
         eyeline = Settings.GetValueBool("EyelineMatching", "Camera", true);
         perShotLengths = Settings.GetValueBool("PerShotLengths", "Camera", true);
         openerMs = Settings.GetValueInteger("OpenerMs", "Camera", 900);
         if (openerMs < 0) openerMs = 0;
         dayLightScale = Settings.GetValueFloat("DayIntensityScale", "Light", 0.6f);
         if (dayLightScale < 0.0f) dayLightScale = 0.0f;
         if (dayLightScale > 2.0f) dayLightScale = 2.0f;
         rampOutOn = Settings.GetValueBool("SlowRampOut", "Effect", true);
         ReadDisabledShots();
         durationMs = Settings.GetValueInteger("DurationMs", "Camera", 7000);
         shotLengthMs = Settings.GetValueInteger("ShotLengthMs", "Camera", 3500);
         endOnPlayerPercent = Settings.GetValueInteger("EndOnPlayerPercent", "Camera", 60);
         slowScale = Settings.GetValueFloat("SlowMotionScale", "Camera", 0.05f);
         orbitRadius = Settings.GetValueFloat("OrbitRadius", "Camera", 1.9f);
         orbitHeight = Settings.GetValueFloat("OrbitHeight", "Camera", 0.5f);
         orbitSpeedDeg = Settings.GetValueFloat("OrbitSpeedDegPerSec", "Camera", 4.0f);
         deathCam = Settings.GetValueBool("DeathCam", "Death", true);
         invincible = Settings.GetValueBool("Invincible", "Player", true);
         bustedCam = Settings.GetValueBool("BustedCam", "Death", true);
         guaranteeAfterMs = Settings.GetValueInteger("GuaranteeAfterMs", "General", 300000);
         situationShots = Settings.GetValueBool("SituationShots", "Camera", true);
         deathAnyCause = Settings.GetValueBool("DeathCamAnyCause", "Death", true);
         bustedCuts = Settings.GetValueInteger("BustedCuts", "Death", 2);
         if (bustedCuts < 1) bustedCuts = 1;
         deathHoldMs = Settings.GetValueInteger("HoldMs", "Death", 30000);
         deathTimeScale = Settings.GetValueFloat("HoldTimeScale", "Death", 0.02f);
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
         tiersOn = Settings.GetValueBool("IntensityTiers", "Camera", true);
         finalHoldMs = Settings.GetValueInteger("FinalHoldMs", "Camera", 1200);
         matchKill = Settings.GetValueBool("MatchKillDirection", "Camera", true);
         lastEnemyRamp = Settings.GetValueBool("LastEnemyRamp", "Effect", true);
         rampMs = Settings.GetValueInteger("SlowRampMs", "Effect", 1500);
         rampFrom = Settings.GetValueFloat("SlowRampFrom", "Effect", 0.6f);
         if (rampFrom < 0.1f) rampFrom = 0.1f;
         if (rampFrom > 1.0f) rampFrom = 1.0f;
         chancePercent = ClampPercent(chancePercent);
         longRangeChance = ClampPercent(longRangeChance);
         explosionChance = ClampPercent(explosionChance);
         endOnPlayerPercent = ClampPercent(endOnPlayerPercent);
         deathChancePercent = ClampPercent(deathChancePercent);
         if (cooldownMs < 0) cooldownMs = 0;
         if (durationMs < 300) durationMs = 300;
         if (finalHoldMs < 0) finalHoldMs = 0;
         if (guaranteeAfterMs < 0) guaranteeAfterMs = 0;
         if (rampMs < 0) rampMs = 0;
         if (longRangeDist < 0.0f) longRangeDist = 0.0f;
         if (scanRadius < 5.0f) scanRadius = 5.0f;

         // comma separated list; one is picked at random each time
         timecycles = ParseList(Settings.GetValueString("Timecycles", "Effect", "Steel,Cinema,Eclipse"));

         if (slowScale < 0.02f) slowScale = 0.02f;
         if (slowScale > 1.0f) slowScale = 1.0f;
         if (shotLengthMs < 300) shotLengthMs = 300;
      }

      private string[] ParseList(string list) {
         if (list == null || list.Trim().Length == 0) return new string[0];
         string[] parts = list.Split(',');
         List<string> result = new List<string>();
         for (int i = 0; i < parts.Length; i++) {
            string t = parts[i].Trim();
            if (t.Length > 0) result.Add(t);
         }
         return result.ToArray();
      }

      private void ReadDisabledShots() {
         disabledShots.Clear();
         string[] names = ParseList(Settings.GetValueString("DisabledShots", "Camera", ""));
         for (int i = 0; i < names.Length; i++) {
            try {
               disabledShots.Add((Shot)Enum.Parse(typeof(Shot), names[i], true));
            } catch (Exception) {
               Debug("unknown shot in DisabledShots: " + names[i]);
            }
         }
      }

      private int ClampPercent(int v) {
         if (v < 0) return 0;
         if (v > 100) return 100;
         return v;
      }

      private string errorText = "";
      private int errorTextTick = -100000;

      private void KillCam_Draw(object sender, GraphicsEventArgs e) {
         if (!debug) return;
         try {
            if (state == CamState.Playing) e.Graphics.DrawText("KillCam shot: " + shot.ToString(), 20.0f, 20.0f);
            if (errorText.Length > 0 && Environment.TickCount - errorTextTick < 15000) {
               e.Graphics.DrawText(errorText, 20.0f, 45.0f, System.Drawing.Color.Red);
            }
         } catch (Exception) { }
      }

      // errors always go to the log file, even with Debug off
      private void LogError(Exception ex) {
         try {
            File.AppendAllText(Path.ChangeExtension(Settings.Filename, ".log"),
               DateTime.Now.ToString("HH:mm:ss.fff") + "  ERROR " + ex.ToString() + "\r\n");
         } catch (Exception) { }
      }

      private void Debug(string text) {
         if (!debug) return;
         Game.Console.Print("KillCam: " + text);
         try {
            File.AppendAllText(Path.ChangeExtension(Settings.Filename, ".log"),
               DateTime.Now.ToString("HH:mm:ss.fff") + "  " + text + "\r\n");
         } catch (Exception) { }
      }

      private void KillCam_Tick(object sender, EventArgs e) {
         try {
            if (!enabled) {
               if (state == CamState.Playing) StopCamera();
               return;
            }
            if (state == CamState.Playing) {
               UpdateCamera();
            } else {
               if (releaseChecks > 0 && Environment.TickCount - releaseTick >= 700) {
                  releaseChecks--;
                  releaseTick = Environment.TickCount;
                  ReleaseView();
               }
               CheckIniReload();
               ScanForKills();
            }
         } catch (Exception ex) {
            if (Environment.TickCount - lastErrorTick > 5000) {
               lastErrorTick = Environment.TickCount;
               Game.Console.Print("KillCam error: " + ex.Message);
            }
            LogError(ex);
            if (debug) {
               string where = (ex.StackTrace == null) ? "" : ex.StackTrace;
               int li = where.IndexOf("at ");
               errorText = "KillCam ERROR: " + ex.Message + " " + (li >= 0 ? where.Substring(li, Math.Min(120, where.Length - li)).Replace("\r", " ").Replace("\n", " ") : "");
               errorTextTick = Environment.TickCount;
            }
            StopCamera();
            // hand the view back to the game even if the camera object was lost
            try { GTA.Native.Function.Call("ACTIVATE_SCRIPTED_CAMS", 0, 0); } catch (Exception) { }
         }
      }

      private bool IsTracked(Ped p) {
         for (int i = 0; i < tracked.Count; i++) {
            if (tracked[i].Ped.Equals(p)) return true;
         }
         return false;
      }

      // The last enemy check uses a fresh ped query (not the cached list, which refreshes twice a
      // second), so peds that just arrived are counted. Another ped is an enemy if it is alive,
      // within ScanRadius, and either in combat or already hurt by the player.
      private bool IsOtherEnemyAlive(Ped player, Ped killed) {
         Ped[] near = World.GetPeds(player.Position, scanRadius);
         for (int i = 0; i < near.Length; i++) {
            Ped p = near[i];
            if (p == null || !p.Exists() || p.isDead || p.Equals(player) || p.Equals(killed)) continue;
            if (p.isInCombat || p.HasBeenDamagedBy(player)) {
               Debug("other enemy alive: dist " + p.Position.DistanceTo(player.Position).ToString("0.0") + ", combat " + p.isInCombat.ToString() + ", health " + p.Health.ToString());
               return true;
            }
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

         if (ejectCam) CheckEject(player);

         Ped killed = null;
         int now = Environment.TickCount;
         for (int i = tracked.Count - 1; i >= 0; i--) {
            TrackedPed t = tracked[i];
            Ped p = t.Ped;
            if (p == null || !p.Exists()) {
               tracked.RemoveAt(i);
            } else if (p.isDead || p.Health <= 0) {
               // a ped in a euphoria ragdoll can have zero health before the game flags it dead
               tracked.RemoveAt(i);
               bool byPlayer = p.HasBeenDamagedBy(player) || (now - t.LastHitTick <= killWindowMs && player.isShooting);
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

         // the nearby-ped query allocates and the dedupe is O(n^2), so it runs at most four times a second
         if (now - lastPedScanTick < 250) return;
         lastPedScanTick = now;
         Ped[] nearby = World.GetPeds(player.Position, scanRadius);
         for (int i = 0; i < nearby.Length; i++) {
            Ped p = nearby[i];
            if (p == null || !p.Exists() || p.isDead || p.Health <= 0 || p.Equals(player)) continue;
            if (!IsTracked(p)) tracked.Add(new TrackedPed(p));
         }
      }

      private int peakSpeedTick = 0;
      private int ejectWatchUntil = 0;

      // a fast vehicle the player was just in, followed by the player being thrown clear, starts a short cam.
      // The peak speed of the last 1.5 s is used, because a crash has already stopped the car when the player leaves it
      private void CheckEject(Ped player) {
         int now = Environment.TickCount;
         bool inVeh = false;
         float spd = 0.0f;
         try {
            inVeh = player.isInVehicle();
            if (inVeh) {
               Vehicle v = player.CurrentVehicle;
               if (v != null && v.Exists()) spd = v.Speed;
            }
         } catch (Exception) { }
         if (inVeh) {
            wasInVehicle = true;
            if (spd >= lastVehSpeed || now - peakSpeedTick > 1500) {
               lastVehSpeed = spd;
               peakSpeedTick = now;
            }
            lastVehTick = now;
            return;
         }
         if (wasInVehicle) {
            wasInVehicle = false;
            // keep watching for a moment: the ragdoll can start a few frames after the player leaves the seat
            ejectWatchUntil = now + 1500;
         }
         if (now > ejectWatchUntil) return;
         bool thrown = false;
         try { thrown = player.isRagdoll || player.isInAir; } catch (Exception) { }
         if (!thrown) return;
         ejectWatchUntil = 0;
         if (now - peakSpeedTick > 2500 || lastVehSpeed < ejectMinSpeed) return;
         if (state != CamState.Idle || now - lastEndTick < cooldownMs) { Debug("eject cam skipped: busy or cooldown"); return; }
         if (rng.Next(100) >= ejectChance) return;
         Debug("ejected from vehicle, peak speed " + lastVehSpeed.ToString("0.0"));
         lastEnemyKill = false;
         ejectMode = true;
         BeginCamera(player, null, false, false);
         if (state != CamState.Playing) ejectMode = false;
      }

      // true when it has been too long since the last kill cam on a ped
      private bool IsOverdue() {
         return guaranteeAfterMs > 0 && Environment.TickCount - lastKillCamTick >= guaranteeAfterMs;
      }

      // quiet play and a long gap since the last cam raise the chance; a firefight lowers it
      private int AdaptChance(int chance) {
         if (!adaptiveChance) return chance;
         int now = Environment.TickCount;
         for (int i = recentKills.Count - 1; i >= 0; i--) {
            if (now - recentKills[i] > 30000) recentKills.RemoveAt(i);
         }
         float c = chance;
         if (recentKills.Count >= 4) c *= 0.6f;
         else if (recentKills.Count <= 1) c += 10.0f;
         if (guaranteeAfterMs > 0) {
            float gap = (now - lastKillCamTick) / (float)guaranteeAfterMs;
            if (gap > 1.0f) gap = 1.0f;
            if (gap > 0.0f) c += 25.0f * gap;
         }
         return ClampPercent((int)c);
      }

      private void TryStartCamera(Ped player, Ped killed) {
         recentKills.Add(Environment.TickCount);
         bool otherAlive = IsOtherEnemyAlive(player, killed);
         bool forced = alwaysLastEnemy && !otherAlive;
         // an overdue cam is guaranteed on the next kill, even if other enemies are still alive
         bool overdue = IsOverdue();
         if (!forced && !overdue && Environment.TickCount - lastEndTick < cooldownMs) { Debug("skipped: cooldown"); return; }
         if (lastEnemyOnly && otherAlive && !overdue) { Debug("skipped: other enemy alive"); return; }
         lastEnemyKill = !otherAlive;
         Debug("last enemy: " + lastEnemyKill.ToString());
         if (forced) {
            Debug("last enemy cam forced");
         } else if (overdue) {
            Debug("kill cam overdue, guaranteed");
         } else {
            // long-range and explosive kills get a higher chance (the highest applicable one wins)
            int chance = AdaptChance(chancePercent);
            bool isExplosive = false;
            float dist = 0.0f;
            try {
               dist = player.Position.DistanceTo(killed.Position);
               int w = (int)player.Weapons.CurrentType;
               isExplosive = (w == 4 || w == 5 || w == 6 || w == 18);
            } catch (Exception) { }
            if (dist >= longRangeDist && longRangeChance > chance) chance = longRangeChance;
            if (isExplosive && explosionChance > chance) chance = explosionChance;
            if (rng.Next(100) >= chance) { Debug("skipped: chance roll (" + chance.ToString() + "%)"); return; }
         }

         BeginCamera(killed, player, false, false);
         if (state == CamState.Playing) lastKillCamTick = Environment.TickCount;
      }

      // nearest living tracked ped to the player. mode 0 = one that damaged the player,
      // 1 = one still in combat, 2 = any
      private Ped NearestTracked(Ped player, int mode) {
         Ped best = null;
         float bestDist = 100000.0f;
         for (int i = 0; i < tracked.Count; i++) {
            Ped p = tracked[i].Ped;
            if (p == null || !p.Exists() || p.isDead || p.Equals(player)) continue;
            if (mode == 0 && !player.HasBeenDamagedBy(p)) continue;
            if (mode == 1 && !p.isInCombat) continue;
            float d = p.Position.DistanceTo(player.Position);
            if (d < bestDist) { bestDist = d; best = p; }
         }
         return best;
      }

      // Reverse logic: the player was killed, so the dead one is the player and the actor is the killer.
      private void TryStartDeathCamera(Ped player) {
         if (rng.Next(100) >= deathChancePercent) { Debug("death cam skipped: chance roll"); return; }
         Ped killer = NearestTracked(player, 0);
         if (killer == null) killer = NearestTracked(player, 1);
         if (killer == null) {
            if (!deathAnyCause) { Debug("death cam skipped: no killer found"); return; }
            Debug("death cam: no killer");
         }
         BeginCamera(player, killer, true, false);
      }

      private void TryStartBustedCamera(Ped player) {
         if (rng.Next(100) >= deathChancePercent) return;
         Ped cop = NearestTracked(player, 2);
         if (cop == null) { Debug("busted cam skipped: nobody nearby"); return; }
         BeginCamera(player, cop, true, true);
      }

      // Sets up and starts a cam. death = the player is the subject (death or busted), busted = arrest.
      private void BeginCamera(Ped focus, Ped actingPed, bool death, bool busted) {
         interior = false;
         try {
            interior = GTA.Native.Function.Call<bool>("IS_INTERIOR_SCENE");
         } catch (Exception) { }
         if (interior && string.Compare(interiorMode, "Disabled", true) == 0) { Debug("skipped: interior"); return; }
         if (interior && string.Compare(interiorMode, "Normal", true) == 0) interior = false;

         bustedMode = busted;
         deathMode = death;
         victim = focus;
         actor = actingPed;
         noActor = (actor == null);
         deathStartPos = focus.Position;
         lastVictimPos = victim.Position;
         lastActorPos = noActor ? victim.Position : actor.Position;
         ReadSituation();
         // the eject cam stays well back from the player
         if (ejectMode) radiusMul = 1.9f;

         float slowNow = PlanTiming(death, busted);
         if (!CreateCamera()) {
            Debug("camera could not be created, skipping");
            victim = null;
            actor = null;
            deathMode = false;
            bustedMode = false;
            return;
         }

         int now = Environment.TickCount;
         startTick = now;
         lastFrameTick = now;
         finalShotChosen = false;
         finalShotStarted = false;
         cutIndex = 0;
         sideSign = (rng.Next(2) == 0) ? 1.0f : -1.0f;
         ChooseShot(true);
         cam.Activate();
         // the screen effect starts with the first camera; it is never used on the death or busted cam
         if (!death && !ejectMode) StartEffect();

         baseScale = slowNow;
         rampIn = !death && lastEnemyRamp && lastEnemyKill && rampMs > 0;
         lastSetScale = -1.0f;
         ApplyTimeScale(now);
         timeScaleSet = true;
         // the player cannot be hurt during a kill cam (reset in StopCamera; the API cannot read the old state)
         if (!death && invincible && actor != null && actor.Exists()) {
            actor.Invincible = true;
            madeInvincible = true;
         }
         // the skip key must be released once before it counts, so a held sprint does not skip at once
         skipArmed = !SkipPressed();
         notArrestedSince = 0;
         state = CamState.Playing;
         this.Interval = 0;
         Debug(death ? "started (death cam)" : "started");
      }

      // Works out how long the cam runs, how long each shot lasts and how slow the game is.
      private float PlanTiming(bool death, bool busted) {
         tier = 0;
         baseShotMs = shotLengthMs;
         camCount = 0;
         activeDurationMs = death ? deathHoldMs : (ejectMode ? Math.Min(durationMs, 5000) : durationMs);
         float slow = death ? deathTimeScale : slowScale;
         if (ejectMode && slow < 0.15f) slow = 0.15f;
         if (!death) {
            if (tiersOn) {
               if (lastEnemyKill) tier++;
               if (explosive) tier++;
               if (actor != null && actor.Exists() && actor.Health <= 30) tier++;
               if (tier > 0) {
                  baseShotMs = (int)(baseShotMs * (1.0f + 0.15f * tier));
                  slow *= (1.0f - 0.12f * tier);
               }
            }
            // killing from a car: the world crawls so the player can still steer clear of obstacles afterwards
            if (actorInCar) slow *= 0.4f;
            if (slow < 0.02f) slow = 0.02f;
            if (!ejectMode) {
               // a kill cam is one to three shots; from a car it is one or two, and shorter
               camCount = actorInCar ? 1 + rng.Next(2) : 1 + rng.Next(3);
               if (actorInCar) baseShotMs = (int)(baseShotMs * 0.75f);
               // generous estimate; the real end is set when the last shot is chosen
               activeDurationMs = (int)(camCount * baseShotMs * 1.4f) + finalHoldMs;
               Debug("cameras " + camCount.ToString());
            }
            Debug("tier " + tier.ToString());
         }
         if (busted) baseShotMs = Math.Max(shotLengthMs, deathHoldMs / bustedCuts);
         activeShotMs = baseShotMs;
         return slow;
      }

      // a freshly created camera can fail to exist (e.g. while the game is mid death/respawn), so retry
      private bool CreateCamera() {
         cam = null;
         for (int attempt = 0; attempt < 3 && cam == null; attempt++) {
            Camera c = new Camera();
            if (c.Exists()) cam = c;
            else try { c.Delete(); } catch (Exception) { }
         }
         if (cam == null) return false;
         cam.FOV = 42.0f;
         hasFixedPos = false;
         return true;
      }

      private void SetScale(float v) {
         if (Math.Abs(v - lastSetScale) < 0.004f) return;
         lastSetScale = v;
         Game.TimeScale = v;
      }

      // Time scale over the cam's real-time clock. Last-enemy kills ease in from near-normal speed;
      // every kill cam eases back out over the final SlowRampMs.
      private void ApplyTimeScale(int now) {
         if (rampMs > 0) {
            float elapsed = now - startTick;
            if (rampIn && elapsed < rampMs) {
               float tin = elapsed / (float)rampMs;
               float e = tin * tin * (3.0f - 2.0f * tin);
               SetScale(rampFrom + (baseScale - rampFrom) * e);
               return;
            }
            float tout = 1.0f - (activeDurationMs - elapsed) / (float)rampMs;
            if (rampOutOn && tout > 0.0f) {
               if (tout > 1.0f) tout = 1.0f;
               float e2 = tout * tout * (3.0f - 2.0f * tout);
               SetScale(baseScale + (1.0f - baseScale) * e2);
               return;
            }
         }
         SetScale(baseScale);
      }

      // works out kill distance, kill direction and vehicle use for the shot weighting
      private void ReadSituation() {
         actorInCar = false;
         victimInCar = false;
         killDist = 0.0f;
         explosive = false;
         melee = false;
         radiusMul = 1.0f;
         behindKill = false;
         frontKnown = false;
         isNight = false;
         try {
            int hour = World.CurrentDayTime.Hours;
            isNight = hour >= 20 || hour < 6;
         } catch (Exception) { }
         if (!situationShots) return;
         try {
            if (actor != null && actor.Exists()) {
               killDist = actor.Position.DistanceTo(victim.Position);
               actorInCar = actor.isInVehicle();
               int wp = (int)actor.Weapons.CurrentType;
               explosive = (wp == 4 || wp == 5 || wp == 6 || wp == 18);
               melee = !deathMode && !actorInCar && killDist < 3.5f && wp >= 0 && wp <= 3;
               // GTA heading: 0 faces +Y and grows counter-clockwise, so forward = (-sin, cos)
               float hr = victim.Heading * (float)Math.PI / 180.0f;
               float fx = -(float)Math.Sin(hr), fy = (float)Math.Cos(hr);
               float tx = actor.Position.X - victim.Position.X, ty = actor.Position.Y - victim.Position.Y;
               float tl = (float)Math.Sqrt(tx * tx + ty * ty);
               if (tl > 0.5f) {
                  behindKill = (fx * tx + fy * ty) / tl < -0.3f;
                  frontAngle = (float)Math.Atan2(fy, fx);
                  frontKnown = true;
               }
            }
            victimInCar = victim.isInVehicle();
         } catch (Exception) { }
         if (actorInCar || victimInCar) radiusMul = 1.5f;
         Debug("situation: melee " + melee.ToString() + ", explosive " + explosive.ToString() + ", dist " + killDist.ToString("0.0") + ", actorCar " + actorInCar.ToString() + ", victimCar " + victimInCar.ToString());
      }

      private static readonly Shot[] quarterShots = new Shot[] {
         Shot.ActorThreeQuarter, Shot.ActorCloseQuarter, Shot.ActorHeroLow, Shot.ActorGunSide, Shot.ActorLongQuarter,
         Shot.ActorRearQuarter, Shot.ActorDutchQuarter, Shot.ActorSweepQuarter, Shot.ActorLowSide, Shot.ActorRiseQuarter
      };

      // in a death cam a quarter shot frames either the killer or, when victimQuarter is set, the dead player
      private bool melee = false;
      private bool ejectMode = false;
      private bool ejectCam = true;
      private int ejectChance = 100;
      private float ejectMinSpeed = 8.0f;
      private bool wasInVehicle = false;
      private float lastVehSpeed = 0.0f;
      private int lastVehTick = 0;
      private bool victimQuarter = false;
      private bool pendingVictimQuarter = false;

      private bool OnVictim(Shot sh) {
         return victimQuarter && deathMode && Array.IndexOf(quarterShots, sh) >= 0;
      }

      private Shot RandomDeathQuarter(Shot previous) {
         Shot pick = quarterShots[rng.Next(quarterShots.Length)];
         for (int tries = 0; tries < 8; tries++) {
            pick = quarterShots[rng.Next(quarterShots.Length)];
            if (!IsDisabled(pick) && !BreaksSequence(pick, previous, tries)) return pick;
         }
         return IsDisabled(pick) ? Shot.ActorThreeQuarter : pick;
      }

      private Vector3 ActorPos() {
         if (OnVictim(shot)) return lastVictimPos;
         if (actor != null && actor.Exists()) lastActorPos = actor.Position;
         return lastActorPos;
      }

      private Vector3 ActorHead() {
         // bone lookups hit game memory directly and can crash on a ped that is being streamed out, so the head is estimated from the position
         Vector3 ap = ActorPos();
         return new Vector3(ap.X, ap.Y, ap.Z + 0.65f);
      }

      // the actor's facing as a world angle (radians), matching the Offset() convention
      private float ActorFacing() {
         float h = (actor != null && actor.Exists()) ? actor.Heading : 0.0f;
         if (OnVictim(shot) && victim != null && victim.Exists()) h = victim.Heading;
         return h * (float)Math.PI / 180.0f + (float)Math.PI / 2.0f;
      }

      // Places the camera around the actor's head. yaw is measured from the direction they face
      // (0 = straight in front, +/-PI/2 = their side), height is relative to the head and lookDrop
      // lowers the aim point below the head.
      private void ActorQuarter(float yaw, float dist, float height, float lookDrop, float fov, out Vector3 pos, out Vector3 target) {
         Vector3 head = ActorHead();
         if (OnVictim(shot)) {
            // the dead player is framed higher and further back, looking down at the body
            dist *= 1.35f;
            height += 0.9f;
            lookDrop += 0.35f;
         }
         pos = ClearPosition(head, ActorFacing() + yaw, dist, height);
         target = new Vector3(head.X, head.Y, head.Z - lookDrop);
         cam.FOV = fov;
      }

      // Jump only: Space on keyboard, X on a controller. Sprint (Shift) must not skip.
      private bool SkipPressed() {
         return Game.isGameKeyPressed(GameKey.Jump);
      }

      // scales distances down for the tight interior mode
      private float S(float v) {
         return interior ? v * interiorScale : v;
      }

      // Heuristic wall check: if the first surface below a point high above it is much higher
      // than the subject, something solid (wall, roof, car) is in the way.
      private bool IsBlocked(Vector3 p, float subjectZ) {
         // indoors the ceiling always looks like a blocker, so the heuristic is skipped
         if (interior) return false;
         float ground = World.GetGroundZ(new Vector3(p.X, p.Y, subjectZ + 6.0f), GroundType.NextBelowCurrent);
         return ground > subjectZ + camClearance;
      }

      private float clearScale = 1.0f;
      private float clearTarget = 1.0f;
      private int clearEvalTick = 0;
      private bool clearInit = false;

      // Finds how far the wanted spot must be pulled in (1.0 = as wanted) to get clear of walls.
      // Re-checked at most every 250 ms and eased toward, so the camera does not pop and the
      // ground checks are not run every frame.
      private Vector3 ClearPosition(Vector3 center, float ang, float radius, float height) {
         if (interior) {
            float h = S(height);
            if (h > 1.4f) h = 1.4f;
            return Offset(center, ang, S(radius), h);
         }
         int now = Environment.TickCount;
         if (!clearInit || now - clearEvalTick >= 250) {
            float target = 0.4f;
            for (int i = 0; i < clearScales.Length; i++) {
               Vector3 p = Offset(center, ang, radius * radiusMul * clearScales[i], height + (1.0f - clearScales[i]) * 2.5f);
               if (!IsBlocked(p, center.Z)) { target = clearScales[i]; break; }
            }
            clearTarget = target;
            clearEvalTick = now;
            if (!clearInit) { clearScale = target; clearInit = true; }
         }
         float diff = clearTarget - clearScale;
         if (diff > 0.02f) diff = 0.02f;
         if (diff < -0.02f) diff = -0.02f;
         clearScale += diff;
         return Offset(center, ang, radius * radiusMul * clearScale, height + (1.0f - clearScale) * 2.5f);
      }

      private void StartEffect() {
         string[] list = timecycles;
         if (list.Length == 0) return;
         string name = list[rng.Next(list.Length)];
         if (name.Length == 0) return;
         effectName = name;
         try {
            GTA.Native.Function.Call("SET_TIMECYCLE_MODIFIER", name);
            effectOn = true;
         } catch (Exception) {
            effectOn = false;
         }
         Debug("effect " + name);
      }

      private string effectName = "";

      private void StopEffect() {
         if (!effectOn) return;
         try { GTA.Native.Function.Call("CLEAR_TIMECYCLE_MODIFIER"); } catch (Exception) { }
         effectOn = false;
      }

      // picks the next shot and sets up its starting camera state
      private void ChooseShot(bool first) {
         Shot next = Shot.OrbitVictim;
         pendingVictimQuarter = false;
         int elapsed = Environment.TickCount - startTick;
         bool countMode = camCount > 0 && !deathMode && !bustedMode;
         bool isFinal = countMode ? (cutIndex >= camCount - 1) : (elapsed + baseShotMs >= activeDurationMs);
         bool becameFinal = isFinal && !finalShotChosen && (!first || countMode);
         bool opener = first && lastEnemyKill && !deathMode && openerMs > 0 && (!countMode || camCount > 1);
         bool holdFinal = false;
         if (opener) {
            next = RandomOpenerShot();
         } else if (first) {
            next = PickVictimShot(Shot.OrbitVictim);
         } else if (becameFinal) {
            if (!deathMode && !noActor && rng.Next(100) < endOnPlayerPercent) {
               next = RandomActorShot(shot);
            } else if (deathMode && !bustedMode) {
               // the death cam always ends on the player
               next = RandomDeathQuarter(shot);
               pendingVictimQuarter = true;
            } else {
               next = RandomVictimShot(shot);
            }
         } else {
            next = RandomVictimShot(shot);
         }
         if (becameFinal) {
            finalShotChosen = true;
            holdFinal = !deathMode && !bustedMode && finalHoldMs > 0;
         }
         if (ejectMode) next = RandomEjectShot(shot);
         if (bustedMode) next = RandomBustedShot(first ? Shot.OrbitVictim : shot, isFinal);

         // each shot gets its own length: fast moves are short, slow moves are long
         activeShotMs = baseShotMs;
         if (opener) {
            activeShotMs = Math.Max(300, openerMs);
         } else if (perShotLengths && !bustedMode) {
            activeShotMs = Math.Max(300, (int)(baseShotMs * ShotLengthFactor(next)));
         }
         if (becameFinal && countMode) activeDurationMs = elapsed + activeShotMs;
         if (holdFinal) {
            // hold the last shot a little longer so the moment lands
            activeDurationMs += finalHoldMs;
            finalShotStarted = true;
            int remain = activeDurationMs - elapsed;
            if (activeShotMs < remain) activeShotMs = remain;
         }

         shot = next;
         victimQuarter = pendingVictimQuarter;
         shotStartTick = Environment.TickCount;
         cutIndex++;
         // the game can drop the modifier on its own, so it is re-applied on every cut
         if (effectOn && !first) {
            try { GTA.Native.Function.Call("SET_TIMECYCLE_MODIFIER", effectName); } catch (Exception) { }
         }
         shotDir = (rng.Next(2) == 0) ? 1.0f : -1.0f;
         hasFixedPos = false;
         clearInit = false;
         cam.FOV = 42.0f;
         cam.Roll = 0.0f;

         Vector3 pp = ActorPos();
         Vector3 target = IsActorShot() ? pp : lastVictimPos;
         // start the orbit on the side facing the other character
         Vector3 from = IsActorShot() ? lastVictimPos : pp;
         angle = (float)Math.Atan2(from.Y - target.Y, from.X - target.X);
         // random starting angle for variety
         angle += (float)(rng.NextDouble() * Math.PI - Math.PI / 2.0);
         // eyeline: every cut stays on the same side of the line between the two characters
         float lx = lastVictimPos.X - pp.X, ly = lastVictimPos.Y - pp.Y;
         if (eyeline && !noActor && lx * lx + ly * ly > 1.0f) {
            float lineAng = (float)Math.Atan2(ly, lx);
            angle = lineAng + sideSign * (float)(0.5 + rng.NextDouble() * 2.1);
         }
         // a kill from behind: start on the victim's face side so the reaction is visible
         if (matchKill && behindKill && frontKnown && !IsActorShot() && !deathMode) {
            angle = frontAngle + (float)(rng.NextDouble() * 1.2 - 0.6);
         }
         Debug("shot " + shot.ToString());
      }

      private Shot RandomOpenerShot() {
         Shot[] options = new Shot[] { Shot.HandheldClose, Shot.GroundClose, Shot.CrashZoom, Shot.DollyIn, Shot.LowFront };
         for (int i = 0; i < 6; i++) {
            Shot pick = options[rng.Next(options.Length)];
            if (!IsDisabled(pick)) return pick;
         }
         return Shot.OrbitVictim;
      }

      private bool IsDisabled(Shot sh) {
         return disabledShots.Count > 0 && disabledShots.Contains(sh);
      }

      // how long a shot runs compared with the normal shot length
      private float ShotLengthFactor(Shot sh) {
         switch (sh) {
            case Shot.WhipPan: case Shot.CrashZoom: case Shot.HandheldClose: case Shot.BulletFollow:
               return 0.5f;
            case Shot.ActorLongQuarter: case Shot.HighCrane: case Shot.ActorCrane: case Shot.Flyby: case Shot.SideTrack:
            case Shot.ZoomPull: case Shot.PullBack: case Shot.Telephoto: case Shot.TopDown:
               return 1.3f;
            default:
               return 1.0f;
         }
      }

      // camera-move family: the same family is never used twice in a row
      private int ShotFamily(Shot sh) {
         switch (sh) {
            case Shot.OrbitVictim: case Shot.DutchOrbit: case Shot.ActorOrbit: return 1;
            case Shot.HighCrane: case Shot.ActorCrane: case Shot.TopDown: case Shot.ActorRiseQuarter: return 2;
            case Shot.LowFront: case Shot.LowRise: case Shot.GroundClose: case Shot.ActorLowAngle: case Shot.ActorHeroLow: case Shot.CarLowFront: case Shot.ActorLowSide: return 3;
            case Shot.DollyIn: case Shot.ActorDolly: case Shot.ActorSweepQuarter: case Shot.PullBack: case Shot.ZoomPull: case Shot.CrashZoom: return 4;
            case Shot.Telephoto: case Shot.ActorLongQuarter: case Shot.BulletFollow: case Shot.Flyby: case Shot.SideTrack: return 5;
            case Shot.HandheldClose: case Shot.WhipPan: return 6;
            case Shot.TwoShot: case Shot.OverShoulder: case Shot.MeleeTwoShot: return 7;
            case Shot.MeleeBehind: return 9;
            default: return 8;
         }
      }

      // 0 = tight, 1 = medium, 2 = wide
      private int ShotSize(Shot sh) {
         switch (sh) {
            case Shot.HandheldClose: case Shot.GroundClose: case Shot.CrashZoom: case Shot.DollyIn: case Shot.ActorFace:
            case Shot.ActorCloseQuarter: case Shot.ActorProfile: case Shot.OverShoulder: case Shot.LowRise: case Shot.ActorGunSide:
               return 0;
            case Shot.HighCrane: case Shot.TopDown: case Shot.Flyby: case Shot.Telephoto: case Shot.ZoomPull: case Shot.PullBack:
            case Shot.ActorWide: case Shot.ActorCrane: case Shot.ActorLongQuarter: case Shot.SideTrack: case Shot.CarSide:
               return 2;
            default:
               return 1;
         }
      }

      // true when a candidate breaks the sequencing rules after the previous shot
      private bool BreaksSequence(Shot candidate, Shot previous, int attempt) {
         if (candidate == previous) return true;
         if (!sequenceRules) return false;
         if (ShotFamily(candidate) == ShotFamily(previous)) return true;
         // alternate wide and tight while there are tries left
         int ps = ShotSize(previous), cs = ShotSize(candidate);
         if (attempt < 5 && ps != 1 && cs == ps) return true;
         return false;
      }

      // eject cam: steady, non-orbiting shots only
      private Shot RandomEjectShot(Shot previous) {
         Shot[] options = new Shot[] { Shot.LowFront, Shot.DollyIn, Shot.PullBack, Shot.SideTrack, Shot.Telephoto, Shot.ZoomPull, Shot.LowRise };
         Shot pick = options[rng.Next(options.Length)];
         for (int tries = 0; tries < 8; tries++) {
            pick = options[rng.Next(options.Length)];
            if (pick != previous && !IsDisabled(pick)) return pick;
         }
         return IsDisabled(pick) ? Shot.PullBack : pick;
      }

      // busted: wide and two-shots, quarter angles on the arresting ped, and a raised quarter on the player to finish
      private Shot RandomBustedShot(Shot previous, bool isFinal) {
         if (isFinal) {
            pendingVictimQuarter = true;
            return RandomDeathQuarter(previous);
         }
         Shot[] options = new Shot[] { Shot.ActorWide, Shot.TwoShot, Shot.TwoShot, Shot.ActorThreeQuarter, Shot.ActorSweepQuarter,
            Shot.ActorHeroLow, Shot.ActorDutchQuarter, Shot.ActorGunSide, Shot.ActorRearQuarter };
         Shot pick = options[rng.Next(options.Length)];
         for (int tries = 0; tries < 8; tries++) {
            pick = options[rng.Next(options.Length)];
            if (!IsDisabled(pick) && pick != previous) break;
         }
         if (IsDisabled(pick)) pick = Shot.TwoShot;
         return pick;
      }

      // player-centric shots (only used when the actor is the player, never the killer)
      private Shot RandomActorShot(Shot previous) {
         Shot[] options;
         if (actorInCar) {
            // the windscreen and car body make face and profile shots useless
            options = new Shot[] { Shot.ActorWide, Shot.ActorWide, Shot.ActorOrbit, Shot.ActorCrane, Shot.CarSide, Shot.CarSide, Shot.CarSide, Shot.CarLowFront, Shot.CarLowFront,
               Shot.CarQuarter, Shot.CarQuarter, Shot.CarQuarter, Shot.CarRearQuarter, Shot.CarRearQuarter };
         } else if (interior) {
            options = new Shot[] { Shot.ActorFace, Shot.ActorProfile, Shot.ActorLowAngle, Shot.ActorDolly, Shot.ActorOrbit, Shot.ActorThreeQuarter, Shot.ActorThreeQuarter,
               Shot.ActorCloseQuarter, Shot.ActorGunSide, Shot.ActorRearQuarter, Shot.ActorDutchQuarter, Shot.ActorLowSide };
         } else {
            options = new Shot[] { Shot.ActorFace, Shot.ActorWide, Shot.ActorProfile, Shot.ActorLowAngle,
               Shot.ActorDolly, Shot.ActorOrbit, Shot.ActorCrane, Shot.ActorThreeQuarter, Shot.ActorThreeQuarter, Shot.ActorThreeQuarter,
               Shot.ActorCloseQuarter, Shot.ActorCloseQuarter, Shot.ActorHeroLow, Shot.ActorHeroLow, Shot.ActorGunSide,
               Shot.ActorGunSide, Shot.ActorRearQuarter, Shot.ActorLongQuarter, Shot.ActorLongQuarter,
               Shot.ActorDutchQuarter, Shot.ActorDutchQuarter, Shot.ActorSweepQuarter, Shot.ActorSweepQuarter, Shot.ActorLowSide, Shot.ActorLowSide,
               Shot.ActorRiseQuarter, Shot.ActorRiseQuarter };
         }
         Shot pick = options[rng.Next(options.Length)];
         for (int tries = 0; tries < 8; tries++) {
            pick = options[rng.Next(options.Length)];
            if (!IsDisabled(pick) && !BreaksSequence(pick, previous, tries)) return pick;
         }
         return IsDisabled(pick) ? Shot.ActorWide : pick;
      }

      private Shot RandomVictimShot(Shot previous) {
         // the second shot is usually the shooter, later cuts rarely are (not in a death cam, where the actor is the killer)
         if (!deathMode && !noActor && !IsActorShotType(previous)) {
            int actorChance = sequenceRules ? (cutIndex == 1 ? 75 : 20) : 35;
            if (actorInCar && actorChance < 60) actorChance = 60;
            if (rng.Next(100) < actorChance) {
               Shot a = RandomActorShot(previous);
               if (a != Shot.ActorFace) return a;
            }
         }
         if (deathMode && !bustedMode && Array.IndexOf(quarterShots, previous) < 0) {
            int roll = rng.Next(100);
            if (roll < 35 && !noActor) {
               // the killer gets the same hero angles the player gets in a kill cam
               return RandomDeathQuarter(previous);
            }
            if (roll < 60) {
               pendingVictimQuarter = true;
               return RandomDeathQuarter(previous);
            }
         }
         return PickVictimShot(previous);
      }

      private List<Shot> pool = new List<Shot>();

      private void W(Shot sh, int w) {
         for (int i = 0; i < w; i++) pool.Add(sh);
      }

      // weighted pick of a shot focused on the victim, shaped by distance and vehicles
      private Shot PickVictimShot(Shot previous) {
         pool.Clear();
         W(Shot.OrbitVictim, 2); W(Shot.LowFront, 2); W(Shot.OverShoulder, 2); W(Shot.DollyIn, 2);
         W(Shot.LowRise, 2); W(Shot.DutchOrbit, 1); W(Shot.GroundClose, 2); W(Shot.HandheldClose, 2);
         W(Shot.TwoShot, 2); W(Shot.PullBack, 2); W(Shot.WhipPan, 2); W(Shot.CrashZoom, 2); W(Shot.BulletFollow, 2);
         if (!interior) {
            W(Shot.HighCrane, 2); W(Shot.SideTrack, 2); W(Shot.TopDown, 1); W(Shot.ZoomPull, 1); W(Shot.Flyby, 1);
         }
         if (situationShots) {
            bool far = killDist > 14.0f;
            bool near = killDist > 0.0f && killDist < 4.0f;
            if (far && !interior) { W(Shot.Telephoto, 4); W(Shot.BulletFollow, 3); W(Shot.Flyby, 2); W(Shot.ZoomPull, 2); W(Shot.CrashZoom, 2); }
            if (near) { W(Shot.GroundClose, 3); W(Shot.HandheldClose, 2); W(Shot.DollyIn, 2); W(Shot.LowFront, 2); }
            if (matchKill && behindKill) { W(Shot.LowFront, 3); W(Shot.DollyIn, 3); W(Shot.GroundClose, 2); }
            // explosions get big, wide, high shots
            if (explosive && !interior) { W(Shot.HighCrane, 6); W(Shot.Flyby, 6); W(Shot.TopDown, 4); W(Shot.ZoomPull, 4); W(Shot.PullBack, 4); W(Shot.Telephoto, 3); }
            if (explosive && interior) { W(Shot.PullBack, 6); W(Shot.TwoShot, 4); }
            if (melee) { W(Shot.MeleeBehind, 10); W(Shot.MeleeTwoShot, 10); W(Shot.HandheldClose, 2); }
            if (actorInCar) { W(Shot.CarSide, 6); W(Shot.CarLowFront, 5); W(Shot.TwoShot, 6); }
            if (victimInCar && !interior) { W(Shot.Flyby, 2); W(Shot.HighCrane, 2); W(Shot.SideTrack, 2); }
         }
         Shot pick = previous;
         for (int tries = 0; tries < 8; tries++) {
            pick = pool[rng.Next(pool.Count)];
            if (BreaksSequence(pick, previous, tries) || IsDisabled(pick)) continue;
            // shots that need a second character or a vehicle fall back to solo shots
            bool needsActor = pick == Shot.TwoShot || pick == Shot.OverShoulder || pick == Shot.BulletFollow
               || pick == Shot.WhipPan || pick == Shot.CarSide || pick == Shot.CarLowFront || pick == Shot.MeleeBehind || pick == Shot.MeleeTwoShot;
            if (noActor && needsActor) continue;
            if (!actorInCar && (pick == Shot.CarSide || pick == Shot.CarLowFront)) continue;
            if (pick == Shot.Telephoto && interior) continue;
            break;
         }
         if (noActor && (pick == Shot.MeleeBehind || pick == Shot.MeleeTwoShot || pick == Shot.TwoShot || pick == Shot.OverShoulder || pick == Shot.BulletFollow || pick == Shot.WhipPan)) pick = Shot.OrbitVictim;
         if (!actorInCar && (pick == Shot.CarSide || pick == Shot.CarLowFront)) pick = Shot.OrbitVictim;
         if (IsDisabled(pick)) pick = IsDisabled(Shot.OrbitVictim) ? Shot.DutchOrbit : Shot.OrbitVictim;
         return pick;
      }

      private bool IsActorShot() {
         return IsActorShotType(shot);
      }

      private bool IsActorShotType(Shot s) {
         return s == Shot.ActorFace || s == Shot.ActorWide || s == Shot.ActorOrbit || s == Shot.ActorLowAngle
            || s == Shot.ActorDolly || s == Shot.ActorProfile || s == Shot.ActorCrane || s == Shot.ActorThreeQuarter
            || s == Shot.ActorCloseQuarter || s == Shot.ActorHeroLow || s == Shot.ActorGunSide
            || s == Shot.ActorRearQuarter || s == Shot.ActorLongQuarter || s == Shot.ActorDutchQuarter
            || s == Shot.ActorSweepQuarter || s == Shot.ActorLowSide || s == Shot.ActorRiseQuarter
            || s == Shot.CarSide || s == Shot.CarLowFront || s == Shot.CarQuarter || s == Shot.CarRearQuarter;
      }

      private Vector3 Offset(Vector3 center, float ang, float radius, float height) {
         return new Vector3(
            center.X + (float)Math.Cos(ang) * radius,
            center.Y + (float)Math.Sin(ang) * radius,
            center.Z + height);
      }

      private float ShotT() {
         float t = (Environment.TickCount - shotStartTick) / (float)activeShotMs;
         if (t < 0.0f) t = 0.0f;
         if (t > 1.0f) t = 1.0f;
         return t;
      }

      private void PositionCamera() {
         int now0 = Environment.TickCount;
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
               // slow push-in with a gentle sideways drift so it never sits frozen
               float tos = ShotT();
               pos = ClearPosition(pp, back + shotDir * (0.12f + 0.3f * tos), 1.9f - 0.5f * tos, 1.35f - 0.2f * tos);
               target = victimHead;
               break;
            case Shot.ActorThreeQuarter: {
               // front three-quarter hero shot, near chest/face height, drifting slowly round and in
               float t3 = ShotT();
               ActorQuarter(shotDir * (0.85f - 0.25f * t3), 3.0f - 0.6f * t3, -0.3f, 0.2f, 36.0f - 4.0f * t3, out pos, out target);
               break;
            }
            case Shot.ActorCloseQuarter: {
               // tight three-quarter on the face, slow push in on a longer lens
               float c3 = ShotT();
               ActorQuarter(shotDir * (0.7f + 0.15f * c3), 2.4f - 0.5f * c3, 0.0f, 0.05f, 28.0f - 4.0f * c3, out pos, out target);
               break;
            }
            case Shot.ActorHeroLow: {
               // heroic low angle from the front quarter, looking up at the shooter, slow rise
               float h3 = ShotT();
               ActorQuarter(shotDir * 0.6f, 3.4f, -1.1f + 0.35f * h3, 0.35f, 38.0f, out pos, out target);
               break;
            }
            case Shot.ActorGunSide: {
               // side-on so the gun arm runs across the frame, easing slowly forward
               float g3 = ShotT();
               ActorQuarter(shotDir * (1.35f - 0.2f * g3), 3.6f - 0.5f * g3, -0.35f, 0.3f, 34.0f, out pos, out target);
               break;
            }
            case Shot.ActorLongQuarter: {
               // distant long-lens three-quarter, compressed background, very slow push in
               float l3 = ShotT();
               ActorQuarter(shotDir * 0.75f, 7.0f - 1.2f * l3, -0.1f, 0.3f, 17.0f, out pos, out target);
               break;
            }
            case Shot.ActorDutchQuarter: {
               // tilted front three-quarter with a slow push in
               float d3 = ShotT();
               ActorQuarter(shotDir * 0.8f, 2.9f - 0.6f * d3, -0.15f, 0.15f, 34.0f, out pos, out target);
               cam.Roll = shotDir * 9.0f;
               break;
            }
            case Shot.ActorSweepQuarter: {
               // sweeps from one front quarter across to the other at chest height
               float w3 = ShotT();
               ActorQuarter(shotDir * (1.0f - 1.7f * w3), 3.2f, -0.2f, 0.2f, 36.0f, out pos, out target);
               break;
            }
            case Shot.ActorLowSide: {
               // low, close profile with the camera looking slightly up, creeping forward
               float s3 = ShotT();
               ActorQuarter(shotDir * (1.5f - 0.3f * s3), 2.6f - 0.4f * s3, -0.9f, 0.35f, 32.0f, out pos, out target);
               break;
            }
            case Shot.ActorRiseQuarter: {
               // front three-quarter that rises gently from chest to just above head height
               float u3 = ShotT();
               ActorQuarter(shotDir * 0.65f, 3.3f - 0.5f * u3, -0.4f + 1.2f * u3, 0.1f + 0.3f * u3, 36.0f, out pos, out target);
               break;
            }
            case Shot.ActorRearQuarter: {
               // behind and beside the shooter at shoulder height, looking past them along their aim
               float r3 = ShotT();
               ActorQuarter((float)Math.PI + shotDir * (0.55f - 0.15f * r3), 2.7f - 0.4f * r3, 0.15f, 0.0f, 38.0f, out pos, out target);
               target = Offset(ActorHead(), ActorFacing(), 5.0f, -0.45f);
               break;
            }
            case Shot.ActorFace: {
               // dynamic close-up: starts wide on the actor's right, stays back, swings toward the front and tightens a long lens
               int nowTick = Environment.TickCount;
               float t = (nowTick - shotStartTick) / (float)activeShotMs;
               if (t > 1.0f) t = 1.0f;
               Vector3 hd = ActorHead();
               float hdg = ((actor != null && actor.Exists()) ? actor.Heading : 0.0f) * (float)Math.PI / 180.0f;
               if (!hasFixedPos) {
                  faceScale = 1.0f;
                  if (IsBlocked(Offset(hd, hdg + 0.5f, 4.6f, 0.2f), pp.Z)) faceScale = 0.6f;
                  hasFixedPos = true;
               }
               float faceAngle = hdg + 0.45f + t * 0.5f;
               float faceDist = S((4.6f - 0.8f * t) * faceScale);
               // roughly head height, easing slightly lower as it pushes in
               pos = Offset(hd, faceAngle, faceDist, 0.05f - 0.15f * t);
               target = hd;
               cam.FOV = 24.0f - 6.0f * t;
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
               pos = ClearPosition(pp, angle, 3.0f, 2.2f - 0.8f * tc);
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
            case Shot.Telephoto: {
               // distant long-lens shot that slowly closes in
               if (!hasFixedPos) { fixedPos = ClearPosition(lastVictimPos, angle, 16.0f, 1.8f); hasFixedPos = true; }
               pos = fixedPos;
               cam.FOV = 16.0f - 5.0f * ShotT();
               target = victimHead;
               break;
            }
            case Shot.BulletFollow: {
               // travels from the shooter to the victim, as if riding the bullet
               float tb = ShotT();
               float eb = tb * tb * (3.0f - 2.0f * tb);
               Vector3 sh = ActorHead();
               float vx = lastVictimPos.X - sh.X, vy = lastVictimPos.Y - sh.Y;
               float vl = (float)Math.Sqrt(vx * vx + vy * vy);
               if (vl < 0.5f) vl = 0.5f;
               Vector3 startP = new Vector3(sh.X + vx / vl * 0.8f, sh.Y + vy / vl * 0.8f, sh.Z + 0.15f);
               Vector3 endP = new Vector3(lastVictimPos.X - vx / vl * 1.4f, lastVictimPos.Y - vy / vl * 1.4f, lastVictimPos.Z + 0.6f);
               pos = new Vector3(startP.X + (endP.X - startP.X) * eb, startP.Y + (endP.Y - startP.Y) * eb, startP.Z + (endP.Z - startP.Z) * eb);
               cam.FOV = 55.0f - 20.0f * eb;
               target = victimHead;
               break;
            }
            case Shot.PullBack: {
               // reverse dolly that pulls away from the victim
               float tpb = ShotT();
               pos = ClearPosition(lastVictimPos, angle, 1.4f + 2.8f * tpb, 0.8f + 0.6f * tpb);
               cam.FOV = 36.0f;
               target = victimHead;
               break;
            }
            case Shot.WhipPan: {
               // static camera that whips from the shooter to the victim
               if (!hasFixedPos) { fixedPos = ClearPosition(lastVictimPos, angle, 3.8f, 1.2f); hasFixedPos = true; }
               pos = fixedPos;
               float tw2 = ShotT() / 0.3f;
               if (tw2 > 1.0f) tw2 = 1.0f;
               float ew = tw2 * tw2 * (3.0f - 2.0f * tw2);
               Vector3 from2 = ActorHead();
               target = new Vector3(from2.X + (victimHead.X - from2.X) * ew, from2.Y + (victimHead.Y - from2.Y) * ew, from2.Z + (victimHead.Z - from2.Z) * ew);
               break;
            }
            case Shot.CrashZoom: {
               // lens snaps in hard on the victim, then drifts
               float tz = ShotT();
               if (!hasFixedPos) { fixedPos = ClearPosition(lastVictimPos, angle, 5.0f, 1.3f); hasFixedPos = true; }
               pos = fixedPos;
               cam.FOV = (tz < 0.18f) ? 58.0f - (tz / 0.18f) * 34.0f : 24.0f - (tz - 0.18f) * 7.0f;
               target = victimHead;
               break;
            }
            case Shot.MeleeBehind: {
               // raised, partly behind the attacker, looking past them at the victim
               float tm = ShotT();
               float ba = (float)Math.Atan2(pp.Y - lastVictimPos.Y, pp.X - lastVictimPos.X) + shotDir * (0.45f - 0.15f * tm);
               pos = ClearPosition(pp, ba, 2.8f - 0.5f * tm, 1.9f);
               target = new Vector3(lastVictimPos.X, lastVictimPos.Y, lastVictimPos.Z + 0.3f);
               cam.FOV = 38.0f;
               break;
            }
            case Shot.MeleeTwoShot: {
               // high two-shot from the side, angled down at both fighters
               float tz = ShotT();
               Vector3 mm = new Vector3((pp.X + lastVictimPos.X) * 0.5f, (pp.Y + lastVictimPos.Y) * 0.5f, (pp.Z + lastVictimPos.Z) * 0.5f);
               float gz = pp.DistanceTo(lastVictimPos);
               float sz = (float)Math.Atan2(lastVictimPos.Y - pp.Y, lastVictimPos.X - pp.X) + (float)Math.PI / 2.0f * shotDir;
               pos = ClearPosition(mm, sz, gz * 0.7f + 3.4f - 0.6f * tz, 2.3f - 0.3f * tz);
               target = new Vector3(mm.X, mm.Y, mm.Z + 0.3f);
               break;
            }
            case Shot.CarQuarter: {
               // front three-quarter of the driver, pulled back to take in the car
               float cq = ShotT();
               ActorQuarter(shotDir * 0.9f, 5.4f - 0.8f * cq, 0.5f, 0.2f, 34.0f, out pos, out target);
               break;
            }
            case Shot.CarRearQuarter: {
               // behind and above the car, looking over the driver along the road
               float cr = ShotT();
               ActorQuarter((float)Math.PI + shotDir * (0.45f - 0.1f * cr), 6.5f - 0.8f * cr, 1.4f, 0.1f, 36.0f, out pos, out target);
               break;
            }
            case Shot.CarSide: {
               // slow arc around the vehicle with the shooter in frame
               float tcs = ShotT();
               pos = ClearPosition(pp, angle + shotDir * 0.9f * tcs, 5.5f, 1.0f);
               target = ActorHead();
               break;
            }
            case Shot.CarLowFront: {
               if (!hasFixedPos) { fixedPos = ClearPosition(pp, angle, 4.5f, 0.35f); hasFixedPos = true; }
               pos = fixedPos;
               target = ActorHead();
               cam.FOV = 38.0f;
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
         // every shot on the player gets the soft face light (not on the killer/officer in death or busted cams)
         if (faceLight && IsActorShot() && !deathMode) {
            Vector3 lh = ActorHead();
            Vector3 lp = new Vector3(lh.X + (pos.X - lh.X) * 0.6f, lh.Y + (pos.Y - lh.Y) * 0.6f, lh.Z + 0.35f);
            if (faceLightObj == null) {
               faceLightObj = new Light(System.Drawing.Color.FromArgb(255, 235, 200), lightRange, lightIntensity);
            }
            faceLightObj.Position = lp;
            faceLightObj.Intensity = isNight ? lightIntensity : lightIntensity * dayLightScale;
            faceLightObj.Enabled = true;
         } else if (faceLightObj != null && faceLightObj.Enabled) {
            faceLightObj.Enabled = false;
         }
         // if the camera ended up inside geometry, cut to another shot instead of sitting in it
         if (!interior && now0 - obstructTick >= 250) {
            obstructTick = now0;
            Vector3 mid = new Vector3((pos.X + target.X) * 0.5f, (pos.Y + target.Y) * 0.5f, (pos.Z + target.Z) * 0.5f);
            if (IsBlocked(pos, target.Z - 1.0f) || IsBlocked(mid, target.Z - 1.0f)) obstructCount++;
            else obstructCount = 0;
            if (obstructCount >= 2 && now0 - shotStartTick > 700) {
               obstructCount = 0;
               Debug("camera obstructed, changing shot");
               ChooseShot(false);
               return;
            }
         }
         cam.Position = pos;
         cam.LookAt(target);
      }

      private int obstructTick = 0;
      private int obstructCount = 0;

      private void UpdateCamera() {
         int now = Environment.TickCount;
         Ped player = Player.Character;

         // end when time is up, when the player skips, or when the situation changes:
         // kill cam ends if the player dies; death cam ends when the dead player ped is gone (respawn)
         bool over = now - startTick >= activeDurationMs;
         if (deathMode) {
            if (victim == null || !victim.Exists()) over = true;
            else if (!bustedMode && !victim.isDead) over = true;
            else if (bustedMode && victim.Position.DistanceTo(deathStartPos) > 25.0f) over = true;
            else if (bustedMode && BrokeFree(now)) over = true;
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

         if (!deathMode) {
            ApplyTimeScale(now);
         } else if (timeScaleSet) {
            // the game's own wasted/busted handling can reset the time scale, so keep forcing it
            Game.TimeScale = deathTimeScale;
         }

         if (!finalShotStarted && now - shotStartTick >= activeShotMs) {
            ChooseShot(false);
         }

         // orbit speed uses real time, so slow motion does not stall the camera
         float dt = (now - lastFrameTick) / 1000.0f;
         lastFrameTick = now;
         if (shot == Shot.OrbitVictim || shot == Shot.HighCrane || shot == Shot.DutchOrbit || shot == Shot.ActorOrbit) {
            angle += shotDir * orbitSpeedDeg * dt * (float)Math.PI / 180.0f;
         }
         PositionCamera();
      }

      private int notArrestedSince = 0;

      // true when the arrest has been over for a moment (the player broke free); a short grace period covers flicker
      private bool BrokeFree(int now) {
         bool arrested = true;
         try { arrested = GTA.Native.Function.Call<bool>("IS_PLAYER_BEING_ARRESTED"); } catch (Exception) { }
         if (arrested) { notArrestedSince = 0; return false; }
         if (notArrestedSince == 0) notArrestedSince = now;
         return now - notArrestedSince >= 400;
      }

      private int releaseChecks = 0;
      private int releaseTick = 0;

      // hands the view back to the normal gameplay camera, whatever state the script camera is in
      private void ReleaseView() {
         try { GTA.Native.Function.Call("ACTIVATE_SCRIPTED_CAMS", 0, 0); } catch (Exception) { }
      }

      private void StopCamera() {
         // the camera goes first so a failure further down can never leave the view stuck
         if (cam != null) {
            try { cam.Deactivate(); } catch (Exception) { }
            try { cam.Delete(); } catch (Exception) { }
            cam = null;
         }
         ReleaseView();
         // the release is repeated briefly after the cam ends, in case the game re-applied it
         releaseChecks = 2;
         releaseTick = Environment.TickCount;
         if (faceLightObj != null) {
            try { faceLightObj.Enabled = false; } catch (Exception) { }
         }
         if (timeScaleSet) {
            Game.TimeScale = 1.0f;
            timeScaleSet = false;
         }
         StopEffect();
         if (madeInvincible) {
            madeInvincible = false;
            Ped player = Player.Character;
            if (player != null && player.Exists()) player.Invincible = false;
         }
         if (state == CamState.Playing) {
            lastEndTick = Environment.TickCount;
            Debug("stopped");
         }
         victim = null;
         actor = null;
         deathMode = false;
         bustedMode = false;
         ejectMode = false;
         state = CamState.Idle;
         this.Interval = 100;
      }
   }
}
