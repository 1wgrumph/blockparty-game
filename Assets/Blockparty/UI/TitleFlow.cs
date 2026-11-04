// UI-TITLE@1: title/new/load/settings controller over RESIDENT-CREATE@1
// (ResidentFactory) and SAVE@1 (SaveV1). Owns screen flow plus the active
// GameStack; Unity scene/view wiring belongs to GREYBOX.
// Plain C# 9, no UnityEngine dependency. Fast Enter Play Mode: all state on
// the instance, no static mutable state, no lambdas, explicit conversions.
using System;
using Blockparty.Contracts;
using Blockparty.Activities;
using Blockparty.Persistence;
using Blockparty.Simulation;

namespace Blockparty.UI
{
    public enum TitleScreen
    {
        Title = 0,
        Creator = 1,
        Playing = 2,
        Pause = 3,
        Load = 4,
        Settings = 5,
        Credits = 6,
        Quit = 7,
    }

    public enum TitleAction
    {
        NewGame = 0,
        Continue = 1,
        Load = 2,
        Settings = 3,
        Credits = 4,
        Quit = 5,
        Pause = 6,
        Resume = 7,
        Save = 8,
        ReturnToTitle = 9,
        Back = 10,
    }

    public sealed class TitleFlowResult
    {
        public bool Ok { get; private set; }
        public string Reason { get; private set; }

        public TitleFlowResult(bool ok, string reason)
        {
            Ok = ok;
            Reason = reason ?? string.Empty;
        }
    }

    public sealed class TitleFlow
    {
        private readonly string directory;
        private readonly ISceneContent scene;
        private readonly string creditsText;
        private TitleScreen origin;
        private bool prepared;

        public TitleFlow(string directory, ISceneContent scene)
        {
            if (directory == null || directory.Length == 0)
            {
                throw new ArgumentNullException("directory");
            }
            if (scene == null)
            {
                throw new ArgumentNullException("scene");
            }
            this.directory = directory;
            this.scene = scene;
            Screen = TitleScreen.Title;
            origin = TitleScreen.Title;
            Settings = DefaultSettings();
            creditsText = "Block Party\n"
                + "A cozy neighborhood life simulator about neighbours on one street.\n"
                + "\n"
                + "Design, engineering, art and audio: the Block Party team.\n"
                + "Built with Unity 6.3 LTS.\n"
                + "\n"
                + "Special thanks to our playtesters and the neighborhood that inspired it.";
            SettingsLoadResult loaded = SaveV1.LoadSettings(directory);
            if (loaded.Ok)
            {
                Settings = loaded.Settings;
            }
        }

        public TitleScreen Screen { get; private set; }
        public GameStack Game { get; private set; }
        public SettingsRecord Settings { get; private set; }
        public string CreditsText { get { return creditsText; } }

        // Continue reflects a real validated save, not file existence: the
        // probe loads and verifies the document, leaving disk untouched.
        public bool ContinueEnabled { get { return ProbeSave().Ok; } }

        public string ContinueReason
        {
            get
            {
                SaveLoadResult probe = ProbeSave();
                if (probe.Ok)
                {
                    return string.Empty;
                }
                return NonBlank(probe.Notice, "The saved game is not available.");
            }
        }

        public SaveHeader? SavedHeader
        {
            get
            {
                SaveLoadResult probe = ProbeSave();
                if (!probe.Ok)
                {
                    return null;
                }
                return probe.Game.Header;
            }
        }

        public TitleFlowResult Activate(TitleAction action)
        {
            switch (action)
            {
                case TitleAction.NewGame:
                    if (Screen != TitleScreen.Title)
                    {
                        return Refused("New game starts from the title screen.");
                    }
                    prepared = false;
                    Screen = TitleScreen.Creator;
                    return Accepted();
                case TitleAction.Continue:
                    if (Screen != TitleScreen.Title)
                    {
                        return Refused("Continue starts from the title screen.");
                    }
                    return EnterLoadedGame();
                case TitleAction.Load:
                    if (Screen != TitleScreen.Title && Screen != TitleScreen.Pause)
                    {
                        return Refused("Load opens from the title or pause screen.");
                    }
                    origin = Screen;
                    Screen = TitleScreen.Load;
                    return Accepted();
                case TitleAction.Settings:
                    if (Screen != TitleScreen.Title && Screen != TitleScreen.Pause)
                    {
                        return Refused("Settings open from the title or pause screen.");
                    }
                    origin = Screen;
                    Screen = TitleScreen.Settings;
                    return Accepted();
                case TitleAction.Credits:
                    if (Screen != TitleScreen.Title)
                    {
                        return Refused("Credits open from the title screen.");
                    }
                    Screen = TitleScreen.Credits;
                    return Accepted();
                case TitleAction.Quit:
                    if (Screen != TitleScreen.Title)
                    {
                        return Refused("Quit leaves from the title screen.");
                    }
                    Screen = TitleScreen.Quit;
                    return Accepted();
                case TitleAction.Pause:
                    if (Screen != TitleScreen.Playing)
                    {
                        return Refused("Pause is available while playing.");
                    }
                    if (Game == null)
                    {
                        return Refused("There is no active world to pause.");
                    }
                    Game.Clock.Pause();
                    Screen = TitleScreen.Pause;
                    return Accepted();
                case TitleAction.Resume:
                    if (Screen != TitleScreen.Pause)
                    {
                        return Refused("Resume is available from the pause screen.");
                    }
                    if (Game == null)
                    {
                        return Refused("There is no paused world to resume.");
                    }
                    Game.Clock.Resume();
                    Screen = TitleScreen.Playing;
                    return Accepted();
                case TitleAction.Save:
                    if (Screen != TitleScreen.Pause)
                    {
                        return Refused("Saving is available from the pause screen.");
                    }
                    if (Game == null)
                    {
                        return Refused("There is no active world to save.");
                    }
                    SaveWriteResult written = SaveV1.SaveGame(Game, directory);
                    if (!written.Ok)
                    {
                        return Refused("The save failed: "
                            + NonBlank(written.Detail, "the world was left untouched."));
                    }
                    return Accepted();
                case TitleAction.ReturnToTitle:
                    if (Screen != TitleScreen.Pause)
                    {
                        return Refused("The title screen returns from the pause screen.");
                    }
                    Screen = TitleScreen.Title;
                    return Accepted();
                case TitleAction.Back:
                    if (Screen != TitleScreen.Load && Screen != TitleScreen.Settings
                        && Screen != TitleScreen.Credits)
                    {
                        return Refused("There is no previous screen to return to.");
                    }
                    Screen = Screen == TitleScreen.Credits ? TitleScreen.Title : origin;
                    return Accepted();
                default:
                    return Refused("Unknown title action.");
            }
        }

        // Creator step: ResidentFactory builds the editable eight-resident
        // stack; the flow stays in Creator until BeginPlay.
        public TitleFlowResult PrepareNewGame(long worldId, long seed, string[] names)
        {
            if (Screen != TitleScreen.Creator)
            {
                return Refused("Residents are created from the creator screen.");
            }
            AppearanceStore appearances;
            WorldState world = ResidentFactory.CreateNewGame(worldId, seed, names, out appearances);
            Clock clock = new Clock(world);
            CommandArbitrator arbitrator = new CommandArbitrator(world, clock);
            ActivityExecutor executor = new ActivityExecutor(world, arbitrator, clock);
            // M1 scene reserves the resident-numbered markers, then eight interaction stations.
            for (int i = 0; i < world.ResidentsSnapshot().Length * 2; i++) world.AllocateObject(StateWriter.SimCore);
            scene.RegisterObjects(executor);
            Accounts accounts = new Accounts(world);
            Food food = new Food(world, arbitrator, executor, accounts);
            Game = new GameStack(world, clock, arbitrator, executor, appearances, accounts, food);
            prepared = true;
            return Accepted();
        }

        // BODY-PRESET: creator base-body choice per resident. Appearance-only:
        // the world hash and screen are untouched.
        public TitleFlowResult SetBaseBody(ResidentId id, BaseBody body)
        {
            if (Screen != TitleScreen.Creator)
            {
                return Refused("Base bodies change from the creator screen.");
            }
            if (Game == null || !prepared)
            {
                return Refused("Prepare the household before changing base bodies.");
            }
            if (body != BaseBody.Male && body != BaseBody.Female)
            {
                return Refused("Unknown base body.");
            }
            try
            {
                Game.Appearances.SetBaseBody(StateWriter.SimCore, id, body);
            }
            catch (ArgumentException e)
            {
                return Refused(e.Message);
            }
            return Accepted();
        }

        public TitleFlowResult BeginPlay()
        {
            if (Screen != TitleScreen.Creator)
            {
                return Refused("Play begins from the creator screen.");
            }
            if (Game == null || !prepared)
            {
                return Refused("Prepare the household before entering play.");
            }
            Screen = TitleScreen.Playing;
            return Accepted();
        }

        public TitleFlowResult LoadSavedGame()
        {
            if (Screen != TitleScreen.Load)
            {
                return Refused("Saved games load from the load screen.");
            }
            return EnterLoadedGame();
        }

        // Settings persist separately from the world and never touch it.
        public TitleFlowResult ApplySettings(SettingsRecord value)
        {
            if (Screen != TitleScreen.Settings)
            {
                return Refused("Settings change from the settings screen.");
            }
            SaveWriteResult written = SaveV1.SaveSettings(value, directory);
            if (!written.Ok)
            {
                return Refused("The settings write failed: "
                    + NonBlank(written.Detail, "the previous settings were kept."));
            }
            Settings = value;
            return Accepted();
        }

        private TitleFlowResult EnterLoadedGame()
        {
            SaveLoadResult loaded = SaveV1.LoadGame(directory, scene);
            if (!loaded.Ok)
            {
                return Refused(NonBlank(loaded.Notice, "The saved game could not be loaded."));
            }
            LoadedGame restored = loaded.Game;
            Game = new GameStack(restored.World, restored.Clock, restored.Arbitrator,
                restored.Executor, restored.Appearances, restored.Accounts, restored.Food);
            Game.Clock.Resume();
            Screen = TitleScreen.Playing;
            return Accepted();
        }

        private SaveLoadResult ProbeSave()
        {
            return SaveV1.LoadGame(directory, scene);
        }

        private static SettingsRecord DefaultSettings()
        {
            return new SettingsRecord(SaveV1.SettingsSchemaVersion, 1.0f, 1.0f, false);
        }

        private static TitleFlowResult Accepted()
        {
            return new TitleFlowResult(true, string.Empty);
        }

        private static TitleFlowResult Refused(string reason)
        {
            return new TitleFlowResult(false, reason);
        }

        private static string NonBlank(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }
            return value;
        }
    }
}
