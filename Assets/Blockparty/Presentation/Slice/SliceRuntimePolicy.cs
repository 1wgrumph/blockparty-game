using System;
using System.IO;
using Blockparty.Contracts;
using Blockparty.Persistence;

namespace Blockparty.Presentation
{
    // Small Unity-free policy surface so the player and the standalone repair
    // witness exercise the same food, locomotion and catch-up decisions.
    public static class SliceRuntimePolicy
    {
        public const int MaxSimulationStepsPerFrame = 4;
        public const float ChairExitSeconds = 0.35f;
        public const float ChairClearanceMeters = 0.65f;
        public const float ResidentClearanceMeters = 0.5f;
        public const int GroceryUnitCost = 25;
        public const int FastFoodCost = 18;

        public static string FoodCode(bool wantsEat, int meals, int groceries, long money)
        {
            if (wantsEat && meals > 0) return "ACT-FOOD-02";
            if (groceries > 0) return "ACT-FOOD-01";
            if (meals > 0) return "ACT-FOOD-02";
            if (money >= GroceryUnitCost) return "ACT-FOOD-03";
            if (money >= FastFoodCost) return "ACT-FOOD-04";
            return string.Empty;
        }

        public static string LocomotionClip(bool eating, bool seated, bool travelling, bool performing)
        {
            if (eating) return "eat";
            if (seated) return "sit";
            if (travelling) return "walk";
            if (performing) return "reach";
            return "hold";
        }

        public static int StepsThisFrame(float elapsed, int maximum)
        {
            if (elapsed < 0.1f || maximum <= 0) return 0;
            int steps = (int)Math.Floor((double)elapsed / 0.1 + 0.00001);
            return Math.Min(maximum, steps);
        }

        public sealed class Startup
        {
            public readonly string Action;
            public readonly bool ContinueGame;
            public readonly bool SavesEnabled;
            public readonly string Notice;

            public Startup(string action, bool continueGame, bool savesEnabled, string notice)
            {
                Action = action;
                ContinueGame = continueGame;
                SavesEnabled = savesEnabled;
                Notice = notice ?? string.Empty;
            }
        }

        public static Startup ProbeStartup(string directory, ISceneContent scene)
        {
            if (directory == null || directory.Length == 0)
            {
                throw new ArgumentNullException("directory");
            }
            if (scene == null)
            {
                throw new ArgumentNullException("scene");
            }
            string primary = Path.Combine(directory, SaveFormat.FileName);
            string backup = Path.Combine(directory, SaveFormat.BackupName);
            if (!File.Exists(primary) && !File.Exists(backup))
            {
                return new Startup("new", false, true, string.Empty);
            }

            SaveLoadResult current = SaveV1.LoadGame(directory, scene);
            if (current.Ok)
            {
                return new Startup("existing", true, true, string.Empty);
            }
            if (!current.BackupAvailable)
            {
                return new Startup("refused", false, false,
                    "Saved game unavailable; progress will not be saved. " + current.Notice);
            }

            SaveLoadResult recovered = SaveV1.LoadBackupGame(directory, scene);
            if (!recovered.Ok)
            {
                return new Startup("refused", false, false,
                    "Saved game and backup unavailable; progress will not be saved. "
                    + recovered.Notice);
            }

            try
            {
                File.Copy(backup, primary, true);
                SaveLoadResult repaired = SaveV1.LoadGame(directory, scene);
                if (!repaired.Ok)
                {
                    return new Startup("refused", false, false,
                        "Saved game recovery failed validation; progress will not be saved. "
                        + repaired.Notice);
                }
                return new Startup("backup", true, true,
                    "Recovered the previous save from its backup.");
            }
            catch (Exception error)
            {
                return new Startup("refused", false, false,
                    "Saved game recovery failed; progress will not be saved. " + error.Message);
            }
        }
    }
}
