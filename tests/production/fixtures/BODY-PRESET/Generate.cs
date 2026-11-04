// Compile with d43deab60dadee19e22e02496efbadb75d985b20 product sources only.
// Resource group isolated-csharp; no Unity editor, display, port or network.
using System;
using System.Collections.Generic;
using System.IO;
using Blockparty.Contracts;
using Blockparty.Activities;
using Blockparty.Persistence;
using Blockparty.Simulation;

public static class BodyPresetFixtureGenerator
{
    private sealed class EmptyScene : ISceneContent
    {
        public void RegisterObjects(ActivityExecutor executor) { }
    }
    public static int Main(string[] args)
    {
        string output = args[0];
        Directory.CreateDirectory(output);
        JsonObject corpus = new JsonObject();
        corpus.Add("producerCommit", "d43deab60dadee19e22e02496efbadb75d985b20");
        corpus.Add("dimensions", "SkinTone,Face,EyeColor,HairType,HairStyle,HairColor,Clothing,Accessory");
        JsonArray seeds = new JsonArray();
        foreach (long seed in new long[] { 1234L, 4242L, 999L })
        {
            AppearanceStore store;
            WorldState world = ResidentFactory.CreateNewGame(71L, seed, null, out store);
            JsonObject sample = new JsonObject();
            sample.Add("seed", seed);
            JsonArray residents = new JsonArray();
            foreach (ResidentRecord resident in world.ResidentsSnapshot())
            {
                AppearanceRecord r;
                if (!store.TryGet(resident.Id, out r)) throw new Exception("appearance absent");
                JsonObject row = new JsonObject();
                row.Add("resident", (long)resident.Id);
                JsonArray vector = new JsonArray();
                foreach (int value in new int[] { r.SkinTone, r.Face, r.EyeColor, r.HairType,
                    r.HairStyle, r.HairColor, r.GetOutfit(OutfitCategory.Everyday), r.Accessory })
                    vector.Add(new JsonNumber(value.ToString()));
                row.Add("values", vector);
                residents.Add(row);
            }
            sample.Add("residents", residents);
            seeds.Add(sample);
        }
        corpus.Add("seeds", seeds);
        File.WriteAllText(Path.Combine(output, "baseline-presets.json"), SaveV1.RenderJson(corpus));

        AppearanceStore appearances;
        WorldState legacy = ResidentFactory.CreateNewGame(71L, 4242L,
            new string[] { "Sam", "Sam", "", " ", "Five", "Six", "Seven", "Eight" }, out appearances);
        ResidentRecord[] rows = legacy.ResidentsSnapshot();
        StateWriter core = StateWriter.SimCore;
        legacy.SetMoney(core, rows[0].Id, 321L);
        legacy.SetTownFund(core, 765L);
        legacy.SetGameSeconds(core, 120L);
        legacy.SetNeed(core, rows[0].Id, Need.Hunger, 37.5f);
        legacy.SetTrait(core, rows[0].Id, TraitAxis.Sociable, 25);
        legacy.SetBond(core, rows[0].Id, rows[1].Id, 12.5f, -3.25f);
        legacy.AddFamilyLink(core, rows[0].Id, rows[1].Id, "Sibling");
        WorldEvent ev = legacy.AppendEvent(core, "Met", rows[0].Id, "legacy fixture");
        List<ResidentId> participants = new List<ResidentId>();
        participants.Add(rows[0].Id); participants.Add(rows[1].Id);
        legacy.AddMemory(core, rows[0].Id, new MemoryEntry(ev.Event, "Met", 120L,
            rows[0].Home, participants.AsReadOnly(), "OK", "participant"));
        Clock clock = new Clock(legacy);
        clock.SetSpeed(SimulationSpeed.Fast);
        clock.SetPreset(AgingPreset.Slow);
        clock.RestoreTickState(0.25, 20L);
        clock.Pause();
        CommandArbitrator commands = new CommandArbitrator(legacy, clock);
        ActivityExecutor executor = new ActivityExecutor(legacy, commands, clock);
        Accounts accounts = new Accounts(legacy);
        accounts.SetSharedBalance(core, rows[0].Household, 75L);
        Food food = new Food(legacy, commands, executor, accounts);
        food.SetHomeStock(core, rows[0].Home, 3, 2);
        ActionInstanceId paid = legacy.AllocateAction(core);
        if (!accounts.Transfer(core, paid, AccountRef.Shared(rows[0].Household),
            AccountRef.Expense("Food"), 5L).Accepted)
            throw new Exception("fixture payment refused");
        SaveWriteResult saved = SaveV1.SaveGame(new GameStack(legacy, clock, commands,
            executor, appearances, accounts, food), output);
        if (!saved.Ok) throw new Exception(saved.Detail);
        string path = Path.Combine(output, SaveFormat.FileName);
        byte[] original = File.ReadAllBytes(path);
        File.WriteAllBytes(Path.Combine(output, "legacy-v1-save.json"), original);
        SaveLoadResult loaded = SaveV1.LoadGame(output, new EmptyScene());
        if (!loaded.Ok) throw new Exception(loaded.Notice);
        saved = SaveV1.SaveGame(new GameStack(loaded.Game.World, loaded.Game.Clock,
            loaded.Game.Arbitrator, loaded.Game.Executor, loaded.Game.Appearances,
            loaded.Game.Accounts, loaded.Game.Food), output);
        if (!saved.Ok) throw new Exception(saved.Detail);
        JsonObject expected = SaveV1.ParseDocument(original).GetObject("payload", "fixture");
        JsonObject worldRow = expected.GetObject("world", "fixture");
        worldRow.Set("epoch", new JsonNumber((worldRow.GetUInt("epoch", "fixture") + 1u).ToString()));
        JsonObject actual = SaveV1.ParseDocument(File.ReadAllBytes(path)).GetObject("payload", "fixture");
        if (SaveV1.RenderJson(expected) != SaveV1.RenderJson(actual))
            throw new Exception("baseline round-trip changed payload beyond the documented epoch increment");
        File.Delete(path);
        File.Delete(Path.Combine(output, SaveFormat.BackupName));
        Console.WriteLine("PASS legacy payload round-trip: epoch alone advances, RNG and every other section unchanged");
        Console.WriteLine("BODY-PRESET FIXTURES GENERATED");
        return 0;
    }
}
