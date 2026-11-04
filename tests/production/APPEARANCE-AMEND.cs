// APPEARANCE-AMEND: local checks only; full SEIT demonstrations stay pending.
// API contract (Blockparty.Simulation, Assets/Blockparty/Simulation/ResidentFactory.cs):
// AppearanceDimension = SkinTone, Face, EyeColor, HairType, HairStyle, HairColor,
// Clothing, Accessory; no BodyBuild/Height. AppearanceRecord and AppearanceEntry
// expose matching integer presets (Clothing uses the existing outfit accessors).
// ResidentFactory.OptionCount(dimension), DimensionCount, CreateNewGame(...),
// and new int[] ValidHairStyles(int hairType) declare the supported catalog.
// AppearanceStore.SetPreset(writer,id,dimension,value), TryGet, Export, Import:
// reject unsupported values/type-style pairs with ArgumentException + a reason,
// leaving appearance/world data unchanged; changing type may repair style only.
// Persistence contract (Blockparty.Persistence, Assets/Blockparty/Persistence/SaveV1.cs):
// existing GameStack, SaveV1.SaveGame/LoadGame persist all new presets and omit
// body build/height. Load refuses appearance rows containing build/height with
// a notice and no loaded game. Existing internal JSON helpers are used only to
// inject checksum-valid unsupported fields, following SAVE.cs's fault seam.
// Catalog assignments are not prescribed: offered lists must agree with setter,
// importer, seeded generator and loader. No mesh/UI claim is made here.
// Resource group isolated-csharp; no editor, display, fixed port or network.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;

public sealed class AppearanceAmendChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private int failures;

    private void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }

    private static AppearanceDimension[] Dimensions()
    {
        return new AppearanceDimension[] { AppearanceDimension.SkinTone,
            AppearanceDimension.Face, AppearanceDimension.EyeColor,
            AppearanceDimension.HairType, AppearanceDimension.HairStyle,
            AppearanceDimension.HairColor, AppearanceDimension.Clothing,
            AppearanceDimension.Accessory };
    }

    private static int[] Vector(AppearanceStore store, ResidentId id)
    {
        AppearanceRecord r;
        if (!store.TryGet(id, out r)) throw new Exception("Fixture appearance absent");
        return new int[] { r.SkinTone, r.Face, r.EyeColor, r.HairType, r.HairStyle,
            r.HairColor, r.GetOutfit(OutfitCategory.Everyday), r.Accessory };
    }

    private static bool Contains(int[] values, int value)
    {
        for (int i = 0; i < values.Length; i++) if (values[i] == value) return true;
        return false;
    }

    private static GameStack Fixture()
    {
        AppearanceStore appearances;
        WorldState world = ResidentFactory.CreateNewGame(71L, 424242L,
            new string[] { "Sam", "Sam", "", " ", "Five", "Six", "Seven", "Eight" },
            out appearances);
        Clock clock = new Clock(world);
        CommandArbitrator commands = new CommandArbitrator(world, clock);
        ResidentRecord[] residents = world.ResidentsSnapshot();
        world.SetTrait(Core, residents[0].Id, TraitAxis.Sociable, 25);
        world.SetBond(Core, residents[0].Id, residents[1].Id, 12.5f, -3.25f);
        world.AddFamilyLink(Core, residents[0].Id, residents[1].Id, "Sibling");
        WorldEvent ev = world.AppendEvent(Core, "Met", residents[0].Id, "appearance fixture");
        List<ResidentId> participants = new List<ResidentId>();
        participants.Add(residents[0].Id);
        participants.Add(residents[1].Id);
        world.AddMemory(Core, residents[0].Id, new MemoryEntry(ev.Event, "Met", 0L,
            residents[0].Home, participants.AsReadOnly(), "OK", "participant"));
        return new GameStack(world, clock, commands,
            new ActivityExecutor(world, commands, clock), appearances);
    }

    private static void NonDefault(AppearanceStore store, ResidentId id)
    {
        AppearanceDimension[] dimensions = Dimensions();
        for (int i = 0; i < dimensions.Length; i++)
        {
            if (dimensions[i] == AppearanceDimension.HairStyle) continue;
            store.SetPreset(Core, id, dimensions[i], ResidentFactory.OptionCount(dimensions[i]) - 1);
        }
        int type = Vector(store, id)[3];
        int[] styles = ResidentFactory.ValidHairStyles(type);
        for (int i = 0; i < styles.Length; i++)
        {
            if (styles[i] != 0)
            {
                store.SetPreset(Core, id, AppearanceDimension.HairStyle, styles[i]);
                return;
            }
        }
        throw new Exception("Hair type has no non-default style");
    }

    private static string Key(AppearanceStore store)
    {
        List<AppearanceEntry> rows = store.Export();
        // Eight rows, explicit ordering independent of Dictionary enumeration.
        for (int i = 1; i < rows.Count; i++)
        {
            AppearanceEntry row = rows[i];
            int j = i - 1;
            while (j >= 0 && rows[j].ResidentId > row.ResidentId)
            {
                rows[j + 1] = rows[j]; j--;
            }
            rows[j + 1] = row;
        }
        StringBuilder key = new StringBuilder();
        for (int i = 0; i < rows.Count; i++)
        {
            AppearanceEntry r = rows[i];
            key.Append(r.ResidentId).Append(':').Append(r.SkinTone).Append(',')
                .Append(r.Face).Append(',').Append(r.EyeColor).Append(',')
                .Append(r.HairType).Append(',').Append(r.HairStyle).Append(',')
                .Append(r.HairColor).Append(',').Append(r.Accessory).Append(',')
                .Append(r.OutfitEveryday).Append(',').Append(r.OutfitSleep).Append(',')
                .Append(r.OutfitExercise).Append(',').Append(r.OverrideCategory).Append(';');
        }
        return key.ToString();
    }

    private static bool Refused(Action action)
    {
        try { action(); }
        catch (ArgumentException e) { return !string.IsNullOrWhiteSpace(e.Message); }
        return false;
    }

    private void IndependentEdits()
    {
        AppearanceDimension[] dimensions = Dimensions();
        int[] positions = new int[] { 0, 7 };
        for (int p = 0; p < positions.Length; p++)
        {
            for (int d = 0; d < dimensions.Length; d++)
            {
                GameStack stack = Fixture();
                ResidentId id = stack.World.ResidentsSnapshot()[positions[p]].Id;
                NonDefault(stack.Appearances, id);
                int hash = stack.World.ComputeStateHash();
                int[] before = Vector(stack.Appearances, id);
                int value = 0;
                if (dimensions[d] == AppearanceDimension.HairStyle)
                {
                    int[] styles = ResidentFactory.ValidHairStyles(before[3]);
                    value = styles[0] == before[d] ? styles[1] : styles[0];
                }
                List<AppearanceEntry> otherRows = stack.Appearances.Export();
                for (int i = otherRows.Count - 1; i >= 0; i--)
                    if (otherRows[i].ResidentId == (long)id) otherRows.RemoveAt(i);
                AppearanceStore others = new AppearanceStore();
                others.Import(Core, otherRows);
                string otherBefore = Key(others);
                stack.Appearances.SetPreset(StateWriter.CommandArbitrator, id, dimensions[d], value);
                int[] after = Vector(stack.Appearances, id);
                bool ok = before[d] != value && after[d] == value;
                for (int e = 0; e < dimensions.Length; e++)
                    if (e != d && !(d == 3 && e == 4) && before[e] != after[e]) ok = false;
                ok &= Contains(ResidentFactory.ValidHairStyles(after[3]), after[4]);
                Check(ok, "SEIT-CHR-02-N1-independent-R" + (positions[p] + 1) + "-" + dimensions[d]);
                Check(hash == stack.World.ComputeStateHash(),
                    "SEIT-CHR-02-P1-world-unchanged-R" + (positions[p] + 1) + "-" + dimensions[d]);
                otherRows = stack.Appearances.Export();
                for (int i = otherRows.Count - 1; i >= 0; i--)
                    if (otherRows[i].ResidentId == (long)id) otherRows.RemoveAt(i);
                others.Import(Core, otherRows);
                Check(otherBefore == Key(others),
                    "SEIT-CHR-02-N1-other-residents-R" + (positions[p] + 1) + "-" + dimensions[d]);
            }
        }
    }

    private void HairCatalog()
    {
        int incompatibleCases = 0;
        for (int type = 0; type < ResidentFactory.OptionCount(AppearanceDimension.HairType); type++)
        {
            GameStack stack = Fixture();
            ResidentId id = stack.World.ResidentsSnapshot()[0].Id;
            NonDefault(stack.Appearances, id);
            stack.Appearances.SetPreset(Core, id, AppearanceDimension.HairType, type);
            int[] styles = ResidentFactory.ValidHairStyles(type);
            bool catalog = styles.Length >= 2;
            for (int i = 0; i < styles.Length; i++)
            {
                catalog &= styles[i] >= 0 && styles[i] < ResidentFactory.OptionCount(AppearanceDimension.HairStyle);
                for (int j = 0; j < i; j++) catalog &= styles[i] != styles[j];
            }
            Check(catalog, "SEIT-CHR-02-P1-offered-styles-type-" + type);
            for (int style = 0; style < ResidentFactory.OptionCount(AppearanceDimension.HairStyle); style++)
            {
                string before = Key(stack.Appearances);
                int hash = stack.World.ComputeStateHash();
                if (Contains(styles, style))
                {
                    stack.Appearances.SetPreset(Core, id, AppearanceDimension.HairStyle, style);
                    Check(Vector(stack.Appearances, id)[4] == style,
                        "SEIT-CHR-02-P1-valid-style-" + type + "-" + style);
                }
                else
                {
                    incompatibleCases++;
                    int invalid = style;
                    bool refused = Refused(delegate {
                        stack.Appearances.SetPreset(Core, id, AppearanceDimension.HairStyle, invalid);
                    });
                    Check(refused && before == Key(stack.Appearances) && hash == stack.World.ComputeStateHash(),
                        "SEIT-CHR-02-N1-incompatible-style-" + type + "-" + style);
                    List<AppearanceEntry> rows = stack.Appearances.Export();
                    for (int i = 0; i < rows.Count; i++)
                        if (rows[i].ResidentId == (long)id) rows[i].HairStyle = invalid;
                    bool importRefused = Refused(delegate { stack.Appearances.Import(Core, rows); });
                    Check(importRefused && before == Key(stack.Appearances) && hash == stack.World.ComputeStateHash(),
                        "SEIT-CHR-02-N1-incompatible-import-" + type + "-" + style);
                }
            }
        }
        Check(incompatibleCases > 0, "SEIT-CHR-02-N1-incompatible-pair-exercised");
    }

    private void InvalidPresets()
    {
        AppearanceDimension[] dimensions = Dimensions();
        for (int d = 0; d < dimensions.Length; d++)
        {
            int[] invalids = new int[] { -1, ResidentFactory.OptionCount(dimensions[d]) };
            for (int v = 0; v < invalids.Length; v++)
            {
                GameStack stack = Fixture();
                ResidentId id = stack.World.ResidentsSnapshot()[0].Id;
                NonDefault(stack.Appearances, id);
                string before = Key(stack.Appearances);
                int hash = stack.World.ComputeStateHash();
                AppearanceDimension dimension = dimensions[d];
                int invalid = invalids[v];
                bool refused = Refused(delegate { stack.Appearances.SetPreset(Core, id, dimension, invalid); });
                Check(refused && before == Key(stack.Appearances) && hash == stack.World.ComputeStateHash(),
                    "SEIT-CHR-02-N1-range-" + dimension + "-" + invalid);
            }
        }
    }

    private static bool NoBodyChoices(Type type)
    {
        MemberInfo[] members = type.GetMembers(BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.Public | BindingFlags.NonPublic);
        for (int i = 0; i < members.Length; i++)
        {
            string name = members[i].Name.ToLowerInvariant();
            if (name.Contains("bodybuild") || name.Contains("height") || name.Contains("bodyproportion")) return false;
        }
        return true;
    }

    private static bool NoMutableStatics(Type type)
    {
        FieldInfo[] fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        for (int i = 0; i < fields.Length; i++)
            if (!fields[i].IsInitOnly && !fields[i].IsLiteral) return false;
        Type[] nested = type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
        for (int i = 0; i < nested.Length; i++) if (!NoMutableStatics(nested[i])) return false;
        return true;
    }

    private sealed class EmptyScene : ISceneContent
    {
        public void RegisterObjects(ActivityExecutor executor) { }
    }

    private void Persistence()
    {
        string directory = Path.Combine(Path.GetTempPath(), "appearance-amend-"
            + System.Diagnostics.Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            GameStack stack = Fixture();
            ResidentRecord[] residents = stack.World.ResidentsSnapshot();
            for (int i = 0; i < residents.Length; i++)
            {
                NonDefault(stack.Appearances, residents[i].Id);
                stack.Appearances.SetPreset(Core, residents[i].Id, AppearanceDimension.EyeColor,
                    i % ResidentFactory.OptionCount(AppearanceDimension.EyeColor));
                stack.Appearances.SetPreset(Core, residents[i].Id, AppearanceDimension.HairType,
                    i % ResidentFactory.OptionCount(AppearanceDimension.HairType));
                stack.Appearances.SetPreset(Core, residents[i].Id, AppearanceDimension.HairColor,
                    i % ResidentFactory.OptionCount(AppearanceDimension.HairColor));
            }
            string before = Key(stack.Appearances);
            int worldHash = stack.World.ComputeStateHash();
            SaveWriteResult saved = SaveV1.SaveGame(stack, directory);
            Check(saved.Ok, "SEIT-AC-01-save-eight-presets " + saved.Detail);
            if (!saved.Ok) return;
            string path = Path.Combine(directory, SaveFormat.FileName);
            byte[] good = File.ReadAllBytes(path);
            SaveLoadResult loaded = SaveV1.LoadGame(directory, new EmptyScene());
            Check(loaded.Ok && loaded.Game != null && before == Key(loaded.Game.Appearances),
                "SEIT-AC-01-eight-appearance-roundtrip " + loaded.Notice);
            bool identities = loaded.Ok && loaded.Game.World.ResidentsSnapshot().Length == 8;
            for (int i = 0; i < residents.Length && identities; i++)
            {
                ResidentRecord found;
                identities &= loaded.Game.World.TryGetResident(residents[i].Id, out found)
                    && found.DisplayName == residents[i].DisplayName
                    && found.GetTrait(TraitAxis.Sociable) == residents[i].GetTrait(TraitAxis.Sociable);
            }
            Check(identities, "SEIT-AC-01-identities-names-traits-preserved");
            JsonObject document = SaveV1.ParseDocument(good);
            JsonArray rows = document.GetObject("payload", "test").GetArray("appearances", "test");
            bool noBody = true;
            for (int i = 0; i < rows.Count; i++)
            {
                string[] keys = rows.GetObject(i, "test").Keys();
                for (int k = 0; k < keys.Length; k++)
                {
                    string key = keys[k].ToLowerInvariant();
                    if (key == "build" || key.Contains("bodybuild") || key.Contains("height")
                        || key.Contains("bodyproportion")) noBody = false;
                }
            }
            Check(noBody, "SEIT-CHR-02-N1-save-has-no-body-choice");
            // Rebuild each fault from the real saved producer output. Valid
            // checksums ensure schema refusal, rather than corrupt-byte refusal.
            string[] unsupported = new string[] { "build", "height" };
            for (int i = 0; i < unsupported.Length; i++)
            {
                document = SaveV1.ParseDocument(good);
                JsonObject payload = document.GetObject("payload", "test");
                JsonObject row = payload.GetArray("appearances", "test").GetObject(0, "test");
                if (row.Has(unsupported[i])) row.Set(unsupported[i], new JsonNumber("1"));
                else row.Add(unsupported[i], 1);
                byte[] invalid = SaveV1.AssembleDocument(payload, SaveFormat.SchemaVersion,
                    stack.World.WorldId, (uint)stack.World.Epoch, stack.World.GameSeconds);
                File.WriteAllBytes(path, invalid);
                SaveLoadResult refused = SaveV1.LoadGame(directory, new EmptyScene());
                Check(!refused.Ok && refused.Game == null && !string.IsNullOrWhiteSpace(refused.Notice)
                    && before == Key(stack.Appearances) && worldHash == stack.World.ComputeStateHash()
                    && Convert.ToBase64String(File.ReadAllBytes(path)) == Convert.ToBase64String(invalid),
                    "SEIT-CHR-02-N1-refuse-removed-" + unsupported[i] + " " + refused.Notice);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private void Run()
    {
        Check(ResidentFactory.DimensionCount == 8 && Enum.GetNames(typeof(AppearanceDimension)).Length == 8,
            "SEIT-CHR-02-P1-eight-dimensions");
        Check(ResidentFactory.OptionCount(AppearanceDimension.EyeColor) >= 5
            && ResidentFactory.OptionCount(AppearanceDimension.HairType) == 4
            && ResidentFactory.OptionCount(AppearanceDimension.HairColor) >= 6,
            "SEIT-CHR-02-P1-owner-catalog-minimums");
        Check(NoBodyChoices(typeof(AppearanceDimension)) && NoBodyChoices(typeof(AppearanceRecord))
            && NoBodyChoices(typeof(AppearanceEntry)), "SEIT-CHR-02-N1-no-body-choice-schema");
        IndependentEdits();
        HairCatalog();
        InvalidPresets();
        GameStack first = Fixture();
        string initial = Key(first.Appearances);
        int hash = first.World.ComputeStateHash();
        ResidentId id = first.World.ResidentsSnapshot()[0].Id;
        NonDefault(first.Appearances, id);
        bool deterministic = true;
        for (int run = 0; run < 3; run++)
        {
            GameStack fresh = Fixture();
            deterministic &= initial == Key(fresh.Appearances) && hash == fresh.World.ComputeStateHash();
            ResidentRecord[] residents = fresh.World.ResidentsSnapshot();
            for (int i = 0; i < residents.Length; i++)
            {
                int[] values = Vector(fresh.Appearances, residents[i].Id);
                AppearanceDimension[] dimensions = Dimensions();
                for (int d = 0; d < dimensions.Length; d++)
                    deterministic &= values[d] >= 0 && values[d] < ResidentFactory.OptionCount(dimensions[d]);
                deterministic &= Contains(ResidentFactory.ValidHairStyles(values[3]), values[4]);
            }
        }
        Check(deterministic, "SEIT-CHR-02-P1-repeated-seeded-entry-valid-and-deterministic");
        Check(NoMutableStatics(typeof(ResidentFactory)) && NoMutableStatics(typeof(AppearanceStore))
            && NoMutableStatics(typeof(AppearanceRecord)) && NoMutableStatics(typeof(AppearanceEntry))
            && NoMutableStatics(typeof(SaveV1)), "SEIT-CHR-02-N1-femp-no-static-mutable-state");
        Persistence();
    }

    public static int Main()
    {
        AppearanceAmendChecks checks = new AppearanceAmendChecks();
        try { checks.Run(); }
        catch (Exception e) { checks.Check(false, "SEIT-CHR-02-N1-unexpected-exception " + e); }
        if (checks.failures == 0) Console.WriteLine("APPEARANCE-AMEND ALL PASS");
        else Console.WriteLine("APPEARANCE-AMEND FAILURES: " + checks.failures);
        return checks.failures == 0 ? 0 : 1;
    }
}
