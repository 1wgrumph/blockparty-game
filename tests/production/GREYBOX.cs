/* GREYBOX@1, Test-first red step. Local integration evidence, not whole
 * SEIT acceptance or an alpha/full-game claim. Other activity classes stay pending.
 * API: ResidentFactory.CreateNewGame, WorldState, Clock, CommandArbitrator,
 * ActivityExecutor, RulePlayer.Decide/Apply/Tick, Food.Submit/Tick/Complete,
 * Accounts and SaveV1.SaveGame/LoadGame with real files and ISceneContent.
 * Four fresh eight-resident runs cover cook/fast-food x manual/BOT. Fixture initialization and the real Clock
 * use SimCore; all play goes through commands and Food.Tick (never an
 * extra Executor.Tick). BOT.Apply is intentionally NOT replaced with Food.Submit.
 * Oracles: BP-ACT-03 and BP-SEC-05 starting catalog (cook 2700s, +45 hunger,
 * +2 health, +10 Cooking, -1 grocery/+2 meals; fast food 900s, -18 money,
 * +35 hunger/-3 health/+5 fun); BP-AUTO-01/02 and BP-SAVE-01/04.
 * Save integration contract: GameStack must capture the same Accounts and Food
 * instances used by the running world; LoadedGame must return restored instances.
 * The seven-argument GameStack overload (..., appearances, accounts, food) and
 * public Accounts/Food fields or properties are discovered to keep baseline C#
 * compilation meaningful. On the old five-argument API we still run real SaveV1,
 * rebuild the available services without reseeding, and expose the lost state.
 * Reload comparison uses canonical action roles, not SAVE-remapped numeric ids,
 * NextActionId or rebased resident sequence/generations. It normalizes epoch+1
 * and superseded reservations; strict refusal/replay State checks stay unchanged.
 *
 * Unity staging: copy this file unchanged into an Editor test assembly referencing
 * the actual runtime assembly and TestAssemblies (do not copy product C# files).
 * Use Unity 6000.3.10f1 with Fast Enter Play Mode/domain reload disabled. Stage
 * Assets/Blockparty/Scenes/EightResident.unity plus all real content dependencies.
 * GreyboxUnityTests enters Play twice with that scene loaded; a live scene
 * component must own the real WorldState, and eight distinct animated rendered
 * resident roots must be present. No test-created world satisfies that scene check.
 * Run -runTests -testPlatform EditMode -testFilter Blockparty.GreyboxUnityTests.
 * Developer evidence under evidence/production/GREYBOX/: result.json (status
 * PASS only after qualification), unity-results.xml, unity.log, standalone.log,
 * command/action/effect traces, before-save and after-load hashes/save documents,
 * and continuous manual/BOT recordings of connected homes, title/inspect/map,
 * takeover, save/quit/Continue. Scene smoke is not proof of those UI flows.
 * Resource group: standalone isolated-csharp (no engine/display/network); unique
 * pid+GUID temp directory, finally cleanup. Unity: editor,display; one externally
 * locked job per scratch project, one warm editor; cold imports may exceed 30s.
 * Developer must qualify five serial + two concurrent isolated Unity copies.
 *
 * SEIT dispositions: AC-01, CHR-01-P1/P2/N1/N4 and IF-RESOLVE-P2/P3/N4/N5:
 * local factory/identity/default-name/reload slices only; editing, naming UI,
 * death/search/follow and full creation matrix remain predecessor/acceptance gates.
 * CHR-01-P3/P4/N2/N3: personality/appearance/player recognition remain pending.
 * AC-05/14/20/23, AUTO-01-P1/N1/N3, SAVE-01-P1/P2/N1, IF-SAVE-P1/P2,
 * TB-06-P1/N4: local food/command/continuity slices below, not complete cases.
 * AUTO-01-N2: observation-schema isolation remains BOT's component coverage.
 * SAVE-01-N2, IF-SAVE-P3/N5: settings/conception remain SAVE/later-alpha coverage.
 * SYS-01-P1/N1/N2: source mapping/submission inspection remains pending; this is
 * a food-only M1 integration slice, not ten-class completion. Corruption and
 * insufficient-funds checks additionally trace AC-14 and ACT-03-N2 respectively.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Blockparty.Activities;
using Blockparty.Automation;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;

#if UNITY_EDITOR
namespace Blockparty
{
    public sealed class GreyboxUnityTests
    {
        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator SceneHasEightLiveResidentsAcrossPlayEntries()
        {
            NUnit.Framework.Assert.AreEqual("6000.3.10f1", UnityEngine.Application.unityVersion);
            NUnit.Framework.Assert.IsTrue(UnityEditor.EditorSettings.enterPlayModeOptionsEnabled);
            NUnit.Framework.Assert.IsTrue((UnityEditor.EditorSettings.enterPlayModeOptions
                & UnityEditor.EnterPlayModeOptions.DisableDomainReload) != 0);
            for (int entry = 0; entry < 2; entry++)
            {
                UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Blockparty/Scenes/EightResident.unity");
                yield return new UnityEngine.TestTools.EnterPlayMode(false);
                NUnit.Framework.Assert.IsTrue(UnityEngine.Application.isPlaying);
                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                NUnit.Framework.Assert.AreEqual("EightResident", scene.name);
                WorldState live = null;
                var animatedRoots = new HashSet<UnityEngine.Transform>();
                foreach (UnityEngine.GameObject root in scene.GetRootGameObjects())
                {
                    foreach (UnityEngine.MonoBehaviour behaviour in root.GetComponentsInChildren<UnityEngine.MonoBehaviour>())
                    {
                        if (behaviour == null) continue;
                        foreach (FieldInfo field in behaviour.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                        {
                            object value = field.GetValue(behaviour);
                            if (value is WorldState) live = (WorldState)value;
                            if (value is GameStack) live = ((GameStack)value).World;
                        }
                    }
                    foreach (UnityEngine.Animation animation in root.GetComponentsInChildren<UnityEngine.Animation>())
                        if (animation.GetComponentInChildren<UnityEngine.Renderer>() != null) animatedRoots.Add(animation.transform);
                    foreach (UnityEngine.Animator animator in root.GetComponentsInChildren<UnityEngine.Animator>())
                        if (animator.GetComponentInChildren<UnityEngine.Renderer>() != null) animatedRoots.Add(animator.transform);
                }
                NUnit.Framework.Assert.IsNotNull(live, "SEIT-AC-20 scene must own the real live world");
                NUnit.Framework.Assert.AreEqual(8, live.ResidentsSnapshot().Length, "SEIT-AC-01 live residents");
                NUnit.Framework.Assert.AreEqual(8, animatedRoots.Count, "SEIT-AC-20 eight rendered animated resident roots");
                yield return new UnityEngine.TestTools.ExitPlayMode();
            }
        }

        [UnityEngine.TestTools.UnityTearDown]
        public System.Collections.IEnumerator ExitOwnedPlayMode()
        {
            if (UnityEngine.Application.isPlaying) yield return new UnityEngine.TestTools.ExitPlayMode();
        }
    }
}
#else
public sealed class GreyboxChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private const StateWriter Exec = StateWriter.ActivityExecutor;
    private int failures;
    private long commandId = 100000L;

    private sealed class Scene : ISceneContent
    {
        public readonly ObjectId[] Stations = new ObjectId[8];
        public void RegisterObjects(ActivityExecutor executor)
        {
            for (int i = 0; i < Stations.Length; i++)
                executor.RegisterObject(Stations[i], new[] { new ApproachPose(i * 4f, 0f, 2f) });
        }
    }

    private sealed class Fixture
    {
        public WorldState World;
        public AppearanceStore Appearances;
        public Clock Clock;
        public CommandArbitrator Commands;
        public ActivityExecutor Executor;
        public Accounts Accounts;
        public Food Food;
        public RulePlayer Bot;
        public Scene Scene;
        public ResidentId[] Residents;
        public ActionInstanceId[] Actions = new ActionInstanceId[8];
    }

    private void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }

    private static Fixture NewFixture(long money = 50L, int groceries = 8)
    {
        var f = new Fixture();
        f.World = ResidentFactory.CreateNewGame(27L, 42L,
            new[] { "Alex", "Alex", "", "  ", "Eli", "Fran", "Gio", "Harper" }, out f.Appearances);
        f.Clock = new Clock(f.World);
        f.Commands = new CommandArbitrator(f.World, f.Clock);
        f.Executor = new ActivityExecutor(f.World, f.Commands, f.Clock);
        f.Accounts = new Accounts(f.World);
        f.Food = new Food(f.World, f.Commands, f.Executor, f.Accounts);
        f.Bot = new RulePlayer(f.Commands);
        f.Scene = new Scene();
        ResidentRecord[] rows = f.World.ResidentsSnapshot();
        Array.Sort(rows, (a, b) => ((long)a.Id).CompareTo((long)b.Id));
        f.Residents = new ResidentId[rows.Length];
        HomeRecord second = f.World.AllocateHome(Core, 8);
        HouseholdRecord household = f.World.AllocateHousehold(Core, "Second household", second.Id);
        // Object and resident ids share the command TargetId namespace; avoid aliases.
        ObjectId station;
        do { station = f.World.AllocateObject(Core); } while ((long)station <= 8L);
        for (int i = 0; i < rows.Length; i++)
        {
            f.Residents[i] = rows[i].Id;
            if (i >= 4) f.World.SetHousehold(Core, rows[i].Id, household.Id, second.Id);
            f.World.SetMoney(Core, rows[i].Id, money);
            for (int n = 0; n < StateSchema.NeedCount; n++)
                f.World.SetNeed(Core, rows[i].Id, (Need)n, 40f);
            f.Executor.SetResidentPosition(rows[i].Id, new ApproachPose(i * 4f, 0f, -2f));
            f.Scene.Stations[i] = i == 0 ? station : f.World.AllocateObject(Core);
        }
        f.Scene.RegisterObjects(f.Executor);
        foreach (HomeRecord home in f.World.ExportSnapshot().Homes)
        {
            f.Food.SetHomeStock(Core, home.Id, groceries, 0);
        }
        foreach (HouseholdRecord h in f.World.ExportSnapshot().Households)
            f.Accounts.SetSharedBalance(Core, h.Id, 77L);
        return f;
    }

    private static ResidentRecord Resident(Fixture f, int i)
    {
        ResidentRecord r;
        if (!f.World.TryGetResident(f.Residents[i], out r)) throw new Exception("Resident lost");
        return r;
    }

    private CommandEnvelope Direct(Fixture f, int i, string code)
    {
        return new CommandEnvelope((CommandId)(commandId++), f.World.Epoch,
            f.World.GameSeconds, CommandSource.PlayerHuman,
            f.Commands.CurrentStamp(f.Residents[i]).Sequence + 1u,
            CommandKind.DirectActivity, f.Residents[i], new ActivityId(code),
            (long)f.Scene.Stations[i], new CommandParameters.Direct(Blockparty.Contracts.QueueMode.Immediate));
    }

    private static PlayerObservation Observe(Fixture f, int i, string code)
    {
        ResidentRecord r = Resident(f, i);
        float[] needs = new float[StateSchema.NeedCount];
        for (int n = 0; n < needs.Length; n++) needs[n] = r.GetNeed((Need)n);
        return new PlayerObservation(r.Id, f.Commands.CurrentStamp(r.Id), f.World.GameSeconds,
            Array.AsReadOnly(needs), Array.AsReadOnly(new[] {
                new AvailableAction(new ActivityId(code), (long)f.Scene.Stations[i]) }));
    }

    private void Begin(Fixture f, string code, bool bot)
    {
        if (bot) f.Commands.EnableAutomation();
        for (int i = 0; i < f.Residents.Length; i++)
        {
            CommandResult result = bot ? f.Bot.Apply(f.Bot.Decide(Observe(f, i, code)))
                : f.Food.Submit(Direct(f, i, code));
            if (!result.Accepted) throw new Exception("Fixture command refused: " + result.Reason);
            ActionEntry current = f.Commands.GetCurrent(f.Residents[i]);
            if (current == null) current = f.Commands.GetPending(f.Residents[i])[0];
            f.Actions[i] = current.Action;
            Check(current.Source == (bot ? CommandSource.PlayerAutomated : CommandSource.PlayerHuman),
                "SEIT-AUTO-01-P1 SEIT-AC-23 command-source " + code + " resident=" + (long)f.Residents[i]);
        }
        // Four metres of actual travel, bounded in game ticks, no wall-clock sleeps.
        for (int tick = 0; tick < 200 && !AllPerforming(f); tick++) Step(f, 1);
        if (!AllPerforming(f)) throw new Exception("Fixture did not reach Perform after travel");
    }

    private static bool AllPerforming(Fixture f)
    {
        foreach (ActionInstanceId id in f.Actions)
        {
            ExecutionRecord e = f.Executor.GetExecution(id);
            if (e == null || e.Stage != ExecutionStage.Perform) return false;
        }
        return true;
    }

    private static void Step(Fixture f, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            f.Clock.AdvanceRealSeconds(Core, 0.1);
            f.Food.Tick(Exec);
        }
    }

    private static int FoodEvents(Fixture f, ResidentId resident)
    {
        int count = 0;
        foreach (WorldEvent e in f.World.Events)
            if (e.Kind == "FoodCompleted" && e.Actor == resident) count++;
        return count;
    }

    private static int Cooking(ResidentRecord r)
    {
        foreach (SkillEntry skill in r.Skills) if (skill.SkillId == "Cooking") return skill.Points;
        return 0;
    }

    private static int Commits(Fixture f, ActionInstanceId action)
    {
        int count = 0;
        foreach (EffectLedgerEntry entry in f.World.EffectLedger)
            if (entry.Action == action && entry.State == LedgerState.Committed) count++;
        return count;
    }

    private void Outcomes(Fixture f, string code, string tag, int priorTransactions = 0, int initialGroceries = 8)
    {
        bool cook = code == "ACT-FOOD-01";
        for (int i = 0; i < f.Residents.Length; i++)
        {
            ResidentRecord r = Resident(f, i);
            bool needs = true;
            for (int n = 0; n < StateSchema.NeedCount; n++)
            {
                Need need = (Need)n;
                float expected = need == Need.Hunger ? (cook ? 85f : 75f)
                    : need == Need.Health ? (cook ? 42f : 37f)
                    : need == Need.Fun && !cook ? 45f : 40f;
                needs &= r.GetNeed(need) == expected;
            }
            ExecutionRecord e = f.Executor.GetExecution(f.Actions[i]);
            bool travel = false, perform = false;
            if (e != null) foreach (StageRecord stage in e.Trace)
            {
                if (stage.Stage == ExecutionStage.Travel && !stage.Skipped) travel = true;
                if (stage.Stage == ExecutionStage.Perform && !stage.Skipped) perform = true;
            }
            bool acquired = false, released = false;
            foreach (SlotEvent slot in f.Executor.ReservationLog)
                if (slot.Hold.Action == f.Actions[i])
                { acquired |= slot.Acquired; released |= !slot.Acquired; }
            Check(e != null && e.Outcome == TerminalOutcome.Completed && travel && perform
                && acquired && released && !f.Executor.GetHold(f.Scene.Stations[i], 0).HasValue
                && f.Commands.GetCurrent(r.Id) == null && Commits(f, f.Actions[i]) == 1,
                "SEIT-AUTO-01-N3 SEIT-AC-20 physical-completion " + tag + " resident=" + (long)r.Id);
            Check(needs && r.Money == (cook ? 50L : 32L) && Cooking(r) == (cook ? 10 : 0)
                && FoodEvents(f, r.Id) == 1,
                "SEIT-AC-05 SEIT-AC-23 SEIT-AUTO-01-N1 catalog-once " + tag + " resident=" + (long)r.Id);
        }
        foreach (HomeRecord home in f.World.ExportSnapshot().Homes)
            Check(f.Food.GroceryUnits(home.Id) == initialGroceries - (cook ? 4 : 0)
                && f.Food.PreparedMeals(home.Id) == (cook ? 8 : 0),
                "SEIT-AC-20 household-stock " + tag + " home=" + (long)home.Id);
        Check(f.Accounts.Ledger.Count == priorTransactions + (cook ? 0 : 8), "SEIT-AC-05 transaction-count " + tag);
        foreach (AccountTransaction tx in f.Accounts.Ledger)
            Check(tx.Amount == 18L && tx.Destination.Equals(AccountRef.Expense("FastFood")),
                "SEIT-AC-05 transaction-catalog " + tag);
        string before = State(f);
        foreach (ResidentId id in f.Residents)
        {
            int i = Array.IndexOf(f.Residents, id);
            Check(!f.Food.Complete(id, f.Actions[i]).Accepted,
                "SEIT-AC-14 duplicate-completion-refused " + tag + " resident=" + (long)id);
        }
        f.Food.Tick(Exec);
        Check(before == State(f), "SEIT-AC-14 replay-leaves-state-unchanged " + tag);
    }

    private static string State(Fixture f, bool normalizeEpoch = false)
    {
        WorldState world = f.World;
        if (normalizeEpoch)
        {
            WorldSnapshot snapshot = world.ExportSnapshot();
            snapshot.Epoch = 0u;
            world = WorldState.LoadFromSnapshot(snapshot);
        }
        var text = new StringBuilder(world.ComputeStateHash().ToString());
        foreach (HomeRecord h in world.ExportSnapshot().Homes)
            text.Append("|stock:").Append((long)h.Id).Append(':').Append(f.Food.GroceryUnits(h.Id))
                .Append(':').Append(f.Food.PreparedMeals(h.Id));
        foreach (HouseholdRecord h in world.ExportSnapshot().Households)
            text.Append("|shared:").Append((long)h.Id).Append(':').Append(f.Accounts.Balance(AccountRef.Shared(h.Id)));
        foreach (AccountTransaction tx in f.Accounts.Ledger)
            text.Append("|tx:").Append((long)tx.Action).Append(':').Append(tx.Amount).Append(':').Append(tx.GameSeconds);
        foreach (ResidentId id in f.Residents)
        {
            ActionEntry current = f.Commands.GetCurrent(id);
            text.Append("|current:").Append(current == null ? 0L : (long)current.Action);
            foreach (ActionEntry pending in f.Commands.GetPending(id)) text.Append("|queue:").Append((long)pending.Action);
        }
        using (var sha = System.Security.Cryptography.SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "");
    }

    private static long ActionRole(Dictionary<long, long> roles, long action)
    {
        long role;
        if (!roles.TryGetValue(action, out role))
        {
            role = roles.Count + 1L;
            roles.Add(action, role);
        }
        return role;
    }

    private static string ContinuityState(Fixture f)
    {
        // One routine per resident in this fixture; history follows ledger order.
        var roles = new Dictionary<long, long>();
        foreach (ActionInstanceId action in f.Actions) ActionRole(roles, (long)action);
        AccountsSnapshot accounts = f.Accounts.Export();
        foreach (AccountsSnapshot.LedgerRow row in accounts.Ledger)
            row.Action = ActionRole(roles, row.Action);
        for (int i = 0; i < accounts.Applied.Count; i++)
            accounts.Applied[i] = ActionRole(roles, accounts.Applied[i]);
        accounts.Applied.Sort();
        Food.FoodSnapshot food = f.Food.Export();
        foreach (Food.FoodSnapshot.TrackedRow row in food.Tracked)
            row.Action = ActionRole(roles, row.Action);
        food.Tracked.Sort((a, b) => a.Action.CompareTo(b.Action));

        WorldSnapshot world = f.World.ExportSnapshot();
        world.Epoch = 0u;
        world.NextActionId = 0L;
        foreach (ResidentRecord resident in world.Residents)
        {
            resident.SetResidentSequence(0u);
            resident.SetDecisionSlotGeneration(0u);
            resident.SetControlGeneration(0u);
        }
        for (int i = 0; i < world.Events.Count; i++)
        {
            WorldEvent e = world.Events[i];
            if (e.Kind != "FoodCompleted") continue;
            int end = e.Detail.IndexOf(';');
            long action = long.Parse(e.Detail.Substring("action=".Length, end - "action=".Length),
                System.Globalization.CultureInfo.InvariantCulture);
            world.Events[i] = new WorldEvent(e.Event, e.GameSeconds, e.Kind, e.Actor,
                "action=" + ActionRole(roles, action) + e.Detail.Substring(end));
        }
        var effects = new List<EffectLedgerEntry>();
        foreach (EffectLedgerEntry row in world.EffectLedger)
        {
            // SAVE retains old Reserved rows and re-reserves under fresh ids.
            // Ignore only an orphan reservation shadowed by this resident's routine.
            if (row.State == LedgerState.Reserved && !roles.ContainsKey((long)row.Action)
                && f.Executor.GetExecution(row.Action) == null && !row.Recipient.Second.HasValue)
            {
                int resident = Array.IndexOf(f.Residents, row.Recipient.First);
                ExecutionRecord replacement = resident < 0 ? null : f.Executor.GetExecution(f.Actions[resident]);
                if (replacement != null && replacement.Activity.Code == row.EffectKind) continue;
            }
            var canonical = new EffectLedgerEntry((ActionInstanceId)ActionRole(roles, (long)row.Action),
                row.EffectKind, row.Recipient);
            if (row.State == LedgerState.Committed) canonical.TryCommit();
            effects.Add(canonical);
        }
        world.EffectLedger = effects;
        var text = new StringBuilder(WorldState.LoadFromSnapshot(world).ComputeStateHash().ToString());
        var json = new System.Text.Json.JsonSerializerOptions { IncludeFields = true };
        text.Append("|accounts:").Append(System.Text.Json.JsonSerializer.Serialize(accounts, json));
        text.Append("|food:").Append(System.Text.Json.JsonSerializer.Serialize(food, json));
        text.Append("|clock:").Append(f.Clock.Speed).Append(':').Append(f.Clock.Paused)
            .Append(':').Append(f.Clock.Preset).Append(':').Append(f.Clock.TotalTicks)
            .Append(':').Append(f.Clock.TickRemainder.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
            .Append("|automation:").Append(f.Commands.AutomationEnabled);
        for (int i = 0; i < f.Residents.Length; i++)
        {
            ResidentId resident = f.Residents[i];
            text.Append("|resident:").Append((long)resident);
            AppendQueueEntry(text, f.Commands.GetCurrent(resident), roles);
            foreach (ActionEntry pending in f.Commands.GetPending(resident)) AppendQueueEntry(text, pending, roles);
            ExecutionRecord execution = f.Executor.GetExecution(f.Actions[i]);
            text.Append("|execution:");
            if (execution != null)
                text.Append(execution.Kind).Append(':').Append(execution.Activity.Code).Append(':')
                    .Append(execution.TargetId).Append(':').Append(execution.Stage).Append(':').Append(execution.Outcome);
            ApproachPose position = f.Executor.GetResidentPosition(resident);
            text.Append("|position:").Append(position.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                .Append(':').Append(position.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                .Append(':').Append(position.Z.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }
        using (var sha = System.Security.Cryptography.SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "");
    }

    private static void AppendQueueEntry(StringBuilder text, ActionEntry entry, Dictionary<long, long> roles)
    {
        text.Append("|entry:");
        if (entry == null) { text.Append("idle"); return; }
        text.Append(ActionRole(roles, (long)entry.Action)).Append(':').Append(entry.Kind).Append(':')
            .Append(entry.Activity.Code).Append(':').Append(entry.TargetId).Append(':')
            .Append(entry.Owner).Append(':').Append(entry.Source).Append(':').Append((long)entry.Command);
    }

    private static GameStack SaveStack(Fixture f)
    {
        var signature = new[] { typeof(WorldState), typeof(Clock), typeof(CommandArbitrator),
            typeof(ActivityExecutor), typeof(AppearanceStore), typeof(Accounts), typeof(Food) };
        ConstructorInfo extended = typeof(GameStack).GetConstructor(signature);
        return extended == null ? new GameStack(f.World, f.Clock, f.Commands, f.Executor, f.Appearances)
            : (GameStack)extended.Invoke(new object[] { f.World, f.Clock, f.Commands, f.Executor,
                f.Appearances, f.Accounts, f.Food });
    }

    private static T SavedService<T>(LoadedGame game, string name) where T : class
    {
        FieldInfo field = game.GetType().GetField(name);
        if (field != null) return field.GetValue(game) as T;
        PropertyInfo property = game.GetType().GetProperty(name);
        return property == null ? null : property.GetValue(game) as T;
    }

    private void SaveContinuity(string root, string code)
    {
        int groceries = code == "ACT-FOOD-01" ? 4 : 8;
        Fixture reference = NewFixture(50L, groceries);
        HouseholdId sharedHousehold = Resident(reference, 0).Household;
        ActionInstanceId paid = reference.World.AllocateAction(Core);
        if (!reference.Accounts.Transfer(Core, paid, AccountRef.Shared(sharedHousehold),
            AccountRef.Expense("FastFood"), 18L).Accepted) throw new Exception("Seed payment failed");
        Begin(reference, code, false);
        Step(reference, 50); // 300 game seconds into Perform, before either catalog completion.
        string directory = Path.Combine(root, code);
        SaveWriteResult written = SaveV1.SaveGame(SaveStack(reference), directory);
        Check(written.Ok, "SEIT-IF-SAVE-P1 mid-routine-write " + code + " " + written.Detail);
        if (!written.Ok) return;
        SaveLoadResult loaded = SaveV1.LoadGame(directory, reference.Scene);
        Check(loaded.Ok, "SEIT-SAVE-01-P1 mid-routine-load " + code + " " + loaded.Notice);
        if (!loaded.Ok) return;
        LoadedGame g = loaded.Game;
        Accounts accounts = SavedService<Accounts>(g, "Accounts");
        Food food = SavedService<Food>(g, "Food");
        Check(accounts != null && food != null,
            "SEIT-SAVE-01-P2 SEIT-IF-SAVE-P2 restored-Accounts-and-Food " + code);
        // Exercise today's actual reload even when its side-service persistence is absent.
        accounts = accounts ?? new Accounts(g.World);
        var resumed = new Fixture { World = g.World, Clock = g.Clock, Commands = g.Arbitrator,
            Executor = g.Executor, Appearances = g.Appearances, Accounts = accounts,
            Food = food ?? new Food(g.World, g.Arbitrator, g.Executor, accounts),
            Bot = new RulePlayer(g.Arbitrator), Scene = reference.Scene, Residents = reference.Residents };
        foreach (HouseholdRecord h in reference.World.ExportSnapshot().Households)
            Check(resumed.Accounts.Balance(AccountRef.Shared(h.Id)) == (h.Id == sharedHousehold ? 59L : 77L),
                "SEIT-SAVE-01-P1 shared-balance-restored " + code + " household=" + (long)h.Id);
        foreach (HomeRecord h in reference.World.ExportSnapshot().Homes)
            Check(resumed.Food.GroceryUnits(h.Id) == groceries && resumed.Food.PreparedMeals(h.Id) == 0,
                "SEIT-SAVE-01-P1 food-stock-restored " + code + " home=" + (long)h.Id);
        Check(resumed.Accounts.Ledger.Count == 1 && resumed.Accounts.Ledger[0].Action == paid
            && resumed.Accounts.Ledger[0].Source.Equals(AccountRef.Shared(sharedHousehold))
            && resumed.Accounts.Ledger[0].Destination.Equals(AccountRef.Expense("FastFood"))
            && resumed.Accounts.Ledger[0].Amount == 18L,
            "SEIT-SAVE-01-P2 pre-save-account-ledger-restored " + code);
        string beforeReplay = State(resumed);
        CommandResult replay = resumed.Accounts.Transfer(Core, paid, AccountRef.Shared(sharedHousehold),
            AccountRef.Expense("FastFood"), 18L);
        Check(replay.Accepted && State(resumed) == beforeReplay, "SEIT-AC-14 saved-payment-replay-is-idempotent " + code);
        if (code == "ACT-FOOD-01")
        {
            foreach (Fixture cooking in new[] { reference, resumed })
            {
                CommandEnvelope competing = Direct(cooking, 0, code);
                string before = State(cooking);
                CommandResult rejected = cooking.Food.Submit(competing);
                Check(!rejected.Accepted && rejected.Reason == RejectReason.ObjectUnavailable
                    && cooking.Food.GroceryUnits(Resident(cooking, 0).Home) == 4
                    && State(cooking) == before,
                    "SEIT-AC-14 mid-cook-groceries-remain-reserved " + (cooking == reference ? "reference" : "reload"));
            }
        }
        for (int i = 0; i < resumed.Residents.Length; i++)
        {
            ActionEntry current = resumed.Commands.GetCurrent(resumed.Residents[i]);
            if (current == null) throw new Exception("Saved food action lost before resume");
            resumed.Actions[i] = current.Action;
            Check(FoodEvents(resumed, resumed.Residents[i]) == 0,
                "SEIT-AC-14 no-load-time-reward " + code + " resident=" + (long)resumed.Residents[i]);
        }
        // Both clocks must complete at the same absolute tick; do not give reload a fresh duration.
        int remaining = (code == "ACT-FOOD-01" ? 450 : 150) - 50;
        Step(reference, remaining - 1);
        Step(resumed, remaining - 1);
        Check(FoodEvents(reference, reference.Residents[0]) == 0
            && FoodEvents(resumed, resumed.Residents[0]) == 0,
            "SEIT-AC-14 no-early-completion " + code);
        Step(reference, 1);
        Step(resumed, 1);
        Outcomes(resumed, code, "reload-" + code, 1, groceries);
        string expected = ContinuityState(reference), actual = ContinuityState(resumed);
        Check(expected == actual, "SEIT-SAVE-01-N1 SEIT-TB-06-P1 SEIT-AC-14 identical-state-hash "
            + code + " reference=" + expected + " reloaded=" + actual);
        float hunger = Resident(resumed, 0).GetNeed(Need.Hunger);
        try
        {
            resumed.World.SetNeed(Core, resumed.Residents[0], Need.Hunger, hunger - 1f);
            Check(ContinuityState(resumed) != actual,
                "SEIT-SAVE-01-N1 hash-oracle-rejects-one-reloaded-need-change " + code);
        }
        finally { resumed.World.SetNeed(Core, resumed.Residents[0], Need.Hunger, hunger); }
        AccountsSnapshot originalAccounts = resumed.Accounts.Export();
        try
        {
            AccountsSnapshot wrong = resumed.Accounts.Export();
            wrong.Ledger[0].Amount++;
            resumed.Accounts.Import(Core, wrong);
            Check(ContinuityState(resumed) != actual,
                "SEIT-SAVE-01-N1 hash-oracle-rejects-one-reloaded-ledger-amount-change " + code);
        }
        finally { resumed.Accounts.Import(Core, originalAccounts); }
        long residentMark = reference.World.NextResidentId, objectMark = reference.World.NextObjectId;
        ResidentRecord first = Resident(resumed, 0);
        ResidentRecord fresh = resumed.World.AllocateResident(Core, "After load", LifeStage.Adult,
            first.Household, first.Home);
        Check((long)fresh.Id >= residentMark && (long)resumed.World.AllocateObject(Core) >= objectMark,
            "SEIT-TB-06-N4 post-load-ids-above-high-water-marks " + code);
    }

    private void Takeover()
    {
        Fixture f = NewFixture();
        Begin(f, "ACT-FOOD-01", true);
        ActionInstanceId interrupted = f.Actions[0];
        RuleDecision stale = f.Bot.Decide(Observe(f, 0, "ACT-FOOD-01"));
        Check(f.Food.Submit(Direct(f, 0, "ACT-FOOD-04")).Accepted,
            "SEIT-AC-23 manual-takeover-command-accepted");
        f.Actions[0] = f.Commands.GetCurrent(f.Residents[0]).Action;
        Step(f, 1);
        int count = f.Commands.CommandLog.Count;
        string before = State(f);
        f.Bot.Tick(new[] { Observe(f, 0, "ACT-FOOD-01") });
        CommandResult refused = f.Bot.Apply(stale);
        Check(!f.Commands.AutomationEnabled && !refused.Accepted && State(f) == before
            && f.Commands.CommandLog.Count == count
            && f.Executor.GetExecution(interrupted).Outcome == TerminalOutcome.Interrupted,
            "SEIT-AC-23 takeover-yields-next-tick-and-discards-pending-BOT-decision");
        Step(f, 200);
        Check(Resident(f, 0).Money == 32L && Resident(f, 0).GetNeed(Need.Hunger) == 75f
            && Cooking(Resident(f, 0)) == 0 && FoodEvents(f, f.Residents[0]) == 1,
            "SEIT-AC-05 manual-fast-food-after-takeover-completes-once");
    }

    private void DiscardedFoodReservation(string root)
    {
        // F2: non-player work is discarded on load; its stock must become usable.
        Fixture f = NewFixture(50L, 1);
        f.Executor.SetResidentPosition(f.Residents[0], new ApproachPose(0f, 0f, 2f));
        if (!f.Commands.EnqueueAutonomous(f.Residents[0], new ActivityId("ACT-FOOD-01"),
            (long)f.Scene.Stations[0])) throw new Exception("Autonomy fixture refused");
        Step(f, 3);
        Check(f.Food.Export().Tracked[0].ReservedGroceries == 1,
            "F2 autonomous-save-fixture-reserved");
        string directory = Path.Combine(root, "discarded-food");
        if (!SaveV1.SaveGame(SaveStack(f), directory).Ok) throw new Exception("Autonomy save failed");
        SaveLoadResult loaded = SaveV1.LoadGame(directory, f.Scene);
        if (!loaded.Ok) throw new Exception(loaded.Notice);
        LoadedGame g = loaded.Game;
        for (int i = 0; i < 500; i++)
        {
            g.Clock.AdvanceRealSeconds(Core, 0.1);
            g.Food.Tick(Exec);
        }
        CommandResult retry = g.Food.Submit(new CommandEnvelope(g.World.AllocateCommand(Core),
            g.World.Epoch, g.World.GameSeconds, CommandSource.PlayerHuman,
            g.Arbitrator.CurrentStamp(f.Residents[0]).Sequence + 1u, CommandKind.DirectActivity,
            f.Residents[0], new ActivityId("ACT-FOOD-01"), (long)f.Scene.Stations[0],
            new CommandParameters.Direct(Blockparty.Contracts.QueueMode.Immediate)));
        Check(retry.Accepted && g.Food.GroceryUnits(Resident(f, 0).Home) == 1
            && g.Food.PreparedMeals(Resident(f, 0).Home) == 0
            && FoodEvents(new Fixture { World = g.World }, f.Residents[0]) == 0,
            "F2 discarded-autonomy-releases-grocery-for-retry");
        for (int i = 0; i < 500; i++)
        {
            g.Clock.AdvanceRealSeconds(Core, 0.1);
            g.Food.Tick(Exec);
        }
        Check(g.Food.GroceryUnits(Resident(f, 0).Home) == 0
            && g.Food.PreparedMeals(Resident(f, 0).Home) == 2
            && FoodEvents(new Fixture { World = g.World }, f.Residents[0]) == 1,
            "F2 discarded-autonomy-retry-completes-once");

        // Reconciliation must not sanitize malformed reservations before import validates them.
        string path = Path.Combine(directory, SaveFormat.FileName);
        JsonObject document = SaveV1.ParseDocument(File.ReadAllBytes(path));
        JsonObject payload = document.GetObject("payload", "F2");
        payload.GetObject("food", "F2").GetArray("tracked", "F2").GetObject(0, "F2")
            .Set("reservedGroceries", new JsonNumber("-1"));
        SaveHeader header = loaded.Game.Header;
        byte[] invalid = SaveV1.AssembleDocument(payload, header.SchemaVersion, header.WorldId,
            (uint)header.Epoch, header.SavedAtGameSeconds);
        File.WriteAllBytes(path, invalid);
        string liveBefore = g.World.ComputeStateHash().ToString();
        SaveLoadResult refused = SaveV1.LoadGame(directory, f.Scene);
        Check(!refused.Ok && refused.Notice.Contains("Food reservations are never negative")
            && liveBefore == g.World.ComputeStateHash().ToString()
            && Convert.ToBase64String(invalid) == Convert.ToBase64String(File.ReadAllBytes(path)),
            "F2 malformed-orphan-reservation-refused-with-state-and-bytes-unchanged");
    }

    private void Negatives(string root)
    {
        Fixture poor = NewFixture(10L);
        CommandEnvelope command = Direct(poor, 0, "ACT-FOOD-04");
        string before = State(poor);
        CommandResult refused = poor.Food.Submit(command);
        Check(!refused.Accepted && refused.Reason == RejectReason.InsufficientFunds && State(poor) == before,
            "SEIT-AC-05 SEIT-ACT-03-N2 insufficient-funds-refused-state-unchanged");
        Fixture empty = NewFixture(50L, 0);
        command = Direct(empty, 0, "ACT-FOOD-01");
        before = State(empty);
        refused = empty.Food.Submit(command);
        Check(!refused.Accepted && refused.Reason == RejectReason.ObjectUnavailable && State(empty) == before,
            "SEIT-AC-20 SEIT-ACT-03-N2 missing-grocery-refused-state-unchanged");
        foreach (bool truncate in new[] { false, true })
        {
            Fixture live = NewFixture();
            string dir = Path.Combine(root, truncate ? "truncated" : "corrupt");
            if (!SaveV1.SaveGame(SaveStack(live), dir).Ok) throw new Exception("Fault fixture save failed");
            string path = Path.Combine(dir, SaveFormat.FileName);
            byte[] bytes = File.ReadAllBytes(path);
            if (truncate) Array.Resize(ref bytes, bytes.Length / 2);
            else
            {
                // Keep valid JSON, corrupt the recorded checksum, not an invented payload.
                string json = Encoding.UTF8.GetString(bytes);
                int offset = json.IndexOf("\"sha256\":\"", StringComparison.Ordinal) + 10;
                if (offset < 10) throw new Exception("Saved header has no sha256");
                char old = json[offset];
                bytes = Encoding.UTF8.GetBytes(json.Substring(0, offset) + (old == '0' ? "1" : "0") + json.Substring(offset + 1));
            }
            File.WriteAllBytes(path, bytes);
            before = State(live);
            SaveLoadResult result = SaveV1.LoadGame(dir, live.Scene);
            Check(!result.Ok && result.Game == null && !string.IsNullOrWhiteSpace(result.Notice)
                && State(live) == before && Convert.ToBase64String(bytes) == Convert.ToBase64String(File.ReadAllBytes(path)),
                "SEIT-AC-14 corrupt-save-refused-with-live-state-and-bytes-unchanged " + (truncate ? "truncated" : "checksum"));
        }
    }

    public static int Main() { return new GreyboxChecks().Run(); }

    private int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "greybox-" + System.Diagnostics.Process.GetCurrentProcess().Id
            + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (bool bot in new[] { false, true })
                foreach (string code in new[] { "ACT-FOOD-01", "ACT-FOOD-04" })
                {
                    Fixture f = NewFixture();
                    Check(f.Residents.Length == 8 && f.Appearances.Count == 8
                        && Resident(f, 0).Id != Resident(f, 1).Id
                        && Resident(f, 0).DisplayName == Resident(f, 1).DisplayName
                        && !string.IsNullOrWhiteSpace(Resident(f, 2).DisplayName)
                        && !string.IsNullOrWhiteSpace(Resident(f, 3).DisplayName)
                        && Resident(f, 0).Household != Resident(f, 7).Household,
                        "SEIT-AC-01 SEIT-CHR-01-P1 SEIT-CHR-01-N1 SEIT-CHR-01-N4 SEIT-IF-RESOLVE-N4 eight-factory-residents-two-households");
                    Begin(f, code, bot);
                    Step(f, code == "ACT-FOOD-01" ? 450 : 150);
                    Outcomes(f, code, (bot ? "BOT-" : "manual-") + code);
                }
            Takeover();
            foreach (string code in new[] { "ACT-FOOD-01", "ACT-FOOD-04" }) SaveContinuity(root, code);
            Negatives(root);
            DiscardedFoodReservation(root);
            Check(File.Exists("Assets/Blockparty/Scenes/EightResident.unity"),
                "SEIT-AC-20 EightResident-scene-asset-present (Unity wiring remains a separate gate)");
        }
        catch (Exception e)
        {
            Check(false, "SEIT-AC-20 integration-fixture-exception " + e);
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine(failures == 0 ? "GREYBOX ALL PASS" : "GREYBOX FAILURES: " + failures);
        return failures == 0 ? 0 : 1;
    }
}
#endif
