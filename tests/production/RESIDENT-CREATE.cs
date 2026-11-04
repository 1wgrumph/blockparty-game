// RESIDENT-CREATE production checks. Standalone console program: compiled and
// run with the Unity-bundled Roslyn + netcorerun until TOOLCHAIN lands the
// Unity test path. Positive checks prove eight seeded named editable residents
// with identity preserved through rename and wardrobe changes; negative checks
// inject failures the factory must refuse. Mirrors the STATE standalone pattern.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Blockparty.Contracts;
using Blockparty.Simulation;

public static class ResidentCreateChecks
{
    private static int failures;

    private static void Check(bool condition, string name)
    {
        if (condition)
        {
            Console.WriteLine("PASS " + name);
        }
        else
        {
            failures++;
            Console.WriteLine("FAIL " + name);
        }
    }

    public static int Main()
    {
        AppearanceStore app;
        WorldState world = ResidentFactory.CreateNewGame(1L, 1234L, null, out app);

        // Exactly eight residents, distinct ids, all named, all with appearance.
        ResidentRecord[] residents = Sorted(world);
        Check(residents.Length == 8, "create-eight");
        bool idsDistinct = true;
        bool allNamed = true;
        bool allStyled = true;
        for (int i = 0; i < residents.Length; i++)
        {
            for (int j = i + 1; j < residents.Length; j++)
            {
                if (residents[i].Id == residents[j].Id)
                {
                    idsDistinct = false;
                }
            }
            if (string.IsNullOrWhiteSpace(residents[i].DisplayName))
            {
                allNamed = false;
            }
            AppearanceRecord look;
            if (!app.TryGet(residents[i].Id, out look) || !PresetsInRange(look))
            {
                allStyled = false;
            }
        }
        Check(idsDistinct, "create-distinct-ids");
        Check(allNamed, "create-all-named");
        Check(allStyled && app.Count == 8, "create-all-styled");

        // Seeded: same seed reproduces names and presets; other seed diverges.
        AppearanceStore app2;
        WorldState same = ResidentFactory.CreateNewGame(1L, 1234L, null, out app2);
        Check(world.ComputeStateHash() == same.ComputeStateHash(), "seeded-state-identical");
        Check(ExportKey(app) == ExportKey(app2), "seeded-presets-identical");
        AppearanceStore app3;
        WorldState other = ResidentFactory.CreateNewGame(1L, 999L, null, out app3);
        Check(world.ComputeStateHash() != other.ComputeStateHash()
            || ExportKey(app) != ExportKey(app3), "seeds-diverge");

        // Blank and whitespace-only creation names store the seeded default for
        // that resident (SEIT-CHR-01-P4 shape): compare resident index 4 against
        // the no-edit run, then prove the name stays editable through reload.
        AppearanceStore nullApp;
        WorldState nullName = ResidentFactory.CreateNewGame(2L, 1234L,
            new string[] { "A", "B", "C", "D", null, "F", "G", "H" }, out nullApp);
        string default5 = Sorted(nullName)[4].DisplayName;
        AppearanceStore blankApp;
        WorldState blank = ResidentFactory.CreateNewGame(2L, 1234L,
            new string[] { "A", "B", "C", "D", "", "F", "G", "H" }, out blankApp);
        AppearanceStore wsApp;
        WorldState ws = ResidentFactory.CreateNewGame(2L, 1234L,
            new string[] { "A", "B", "C", "D", "   ", "F", "G", "H" }, out wsApp);
        ResidentRecord b5 = Sorted(blank)[4];
        ResidentRecord w5 = Sorted(ws)[4];
        Check(b5.DisplayName == default5 && b5.DisplayName.Length > 0, "blank-gets-seeded-default");
        Check(w5.DisplayName == default5, "whitespace-gets-seeded-default");
        blank.RenameResident(StateWriter.CommandArbitrator, b5.Id, "Jordan");
        ResidentRecord renamed;
        blank.TryGetResident(b5.Id, out renamed);
        WorldState reloaded = WorldState.LoadFromSnapshot(blank.ExportSnapshot());
        ResidentRecord jr;
        Check(renamed.DisplayName == "Jordan"
            && reloaded.TryGetResident(b5.Id, out jr) && jr.DisplayName == "Jordan",
            "seeded-default-editable-through-reload");

        // Duplicate display names stay two residents by ResidentId (RESOLVE-N4).
        AppearanceStore dupApp;
        WorldState dup = ResidentFactory.CreateNewGame(4L, 1234L,
            new string[] { "Sam", "x", "Sam", "y", "z", "w", "v", "u" }, out dupApp);
        IReadOnlyList<ResidentSummary> sams = dup.SearchByName("Sam");
        Check(sams.Count == 2 && sams[0].Id != sams[1].Id, "duplicate-names-distinct");

        // Seeded default colliding with a chosen name: no merge, both resolve.
        AppearanceStore colApp;
        WorldState col = ResidentFactory.CreateNewGame(5L, 1234L,
            new string[] { "A", "B", "C", "D", "", default5, "G", "H" }, out colApp);
        IReadOnlyList<ResidentSummary> cols = col.SearchByName(default5);
        Check(cols.Count == 2 && cols[0].Id != cols[1].Id, "default-collision-no-merge");

        // Eight dimensions stay independent except the type-dependent hair style.
        AppearanceStore indApp;
        WorldState ind = ResidentFactory.CreateNewGame(6L, 77L, null, out indApp);
        ResidentId solo = Sorted(ind)[0].Id;
        bool independent = true;
        for (int d = 0; d < ResidentFactory.DimensionCount; d++)
        {
            AppearanceDimension dim = (AppearanceDimension)d;
            for (int e = 0; e < ResidentFactory.DimensionCount; e++)
            {
                int count = ResidentFactory.OptionCount((AppearanceDimension)e);
                indApp.SetPreset(StateWriter.SimCore, solo, (AppearanceDimension)e, count - 1);
            }
            int[] before = PresetVector(indApp, solo);
            int value = dim == AppearanceDimension.HairStyle
                ? ResidentFactory.ValidHairStyles(before[(int)AppearanceDimension.HairType])[0] : 0;
            indApp.SetPreset(StateWriter.SimCore, solo, dim, value);
            int[] after = PresetVector(indApp, solo);
            independent &= before[d] != value && after[d] == value
                && ResidentFactory.IsValidHairStyle(after[(int)AppearanceDimension.HairType],
                    after[(int)AppearanceDimension.HairStyle]);
            for (int e = 0; e < ResidentFactory.DimensionCount; e++)
            {
                if (e != d && !(dim == AppearanceDimension.HairType
                    && e == (int)AppearanceDimension.HairStyle) && after[e] != before[e])
                {
                    independent = false;
                }
            }
        }
        Check(independent, "dimensions-independent");
        Check(ResidentFactory.DimensionCount == 8
            && Enum.GetValues(typeof(AppearanceDimension)).Length == 8
            && NoBodyChoices(typeof(AppearanceDimension))
            && NoBodyChoices(typeof(AppearanceRecord))
            && NoBodyChoices(typeof(AppearanceEntry))
            && NoBodyChoices(typeof(ResidentFactory)), "body-build-height-absent");

        // Clothing pack/unpack round-trips all 192 combos.
        bool combos = ResidentFactory.ClothingComboCount == 192;
        for (int t = 0; t < 8 && combos; t++)
        {
            for (int b = 0; b < 6 && combos; b++)
            {
                for (int f = 0; f < 4 && combos; f++)
                {
                    int packed = ResidentFactory.PackClothing(t, b, f);
                    int rt, rb, rf;
                    ResidentFactory.UnpackClothing(packed, out rt, out rb, out rf);
                    combos = rt == t && rb == b && rf == f;
                }
            }
        }
        Check(combos, "clothing-combos-roundtrip");

        // Rename preserves everything but the display name; no new id allocated.
        WorldState idw = ResidentFactory.CreateNewGame(7L, 4242L,
            new string[] { "R1", "R2", "R3", "R4", "R5", "R6", "R7", "R8" }, out app);
        ResidentId r2 = Sorted(idw)[1].Id;
        ResidentId r1 = Sorted(idw)[0].Id;
        idw.AddFamilyLink(StateWriter.SimCore, r2, r1, "Sibling");
        idw.SetTrait(StateWriter.SimCore, r2, TraitAxis.Sociable, 25);
        WorldEvent ev = idw.AppendEvent(StateWriter.SimCore, "Met", r2, "d");
        List<ResidentId> parts = new List<ResidentId>();
        parts.Add(r2);
        idw.AddMemory(StateWriter.SimCore, r2, new MemoryEntry(ev.Event, "Met", 0L,
            Sorted(idw)[1].Home, parts.AsReadOnly(), "OK", "participant"));
        long waterBefore = idw.NextResidentId;
        string beforeJson = ResidentKey(idw, r2);
        idw.RenameResident(StateWriter.CommandArbitrator, r2, "Renamed");
        ResidentRecord after2;
        idw.TryGetResident(r2, out after2);
        string afterJson = ResidentKey(idw, r2);
        Check(after2.DisplayName == "Renamed"
            && StripName(beforeJson) == StripName(afterJson)
            && idw.NextResidentId == waterBefore, "rename-preserves-identity");
        Check(idw.SearchByName("Renamed").Count == 1
            && idw.SearchByName("Renamed")[0].Id == r2
            && idw.SearchByName("R2").Count == 0, "rename-updates-search");

        // Wardrobe: every clothing item selectable in every category, no charge.
        WorldState ward = ResidentFactory.CreateNewGame(8L, 555L, null, out app);
        ResidentId w = Sorted(ward)[0].Id;
        ward.SetMoney(StateWriter.SimCore, w, 96L);
        ward.SetTownFund(StateWriter.SimCore, 1000L);
        bool selectable = true;
        try
        {
            for (int c = 0; c < ResidentFactory.ClothingComboCount; c++)
            {
                app.SetOutfit(StateWriter.CommandArbitrator, w, OutfitCategory.Everyday, c);
                app.SetOutfit(StateWriter.CommandArbitrator, w, OutfitCategory.Sleep, c);
                app.SetOutfit(StateWriter.CommandArbitrator, w, OutfitCategory.Exercise, c);
            }
        }
        catch (Exception)
        {
            selectable = false;
        }
        ResidentRecord wr;
        ward.TryGetResident(w, out wr);
        Check(selectable && wr.Money == 96L && ward.TownFund == 1000L, "wardrobe-no-charge");

        // Worn outfit follows the activity mapping unless overridden.
        int every = ResidentFactory.PackClothing(1, 1, 1);
        int sleep = ResidentFactory.PackClothing(2, 2, 2);
        int gym = ResidentFactory.PackClothing(3, 3, 3);
        app.SetOutfit(StateWriter.CommandArbitrator, w, OutfitCategory.Everyday, every);
        app.SetOutfit(StateWriter.CommandArbitrator, w, OutfitCategory.Sleep, sleep);
        app.SetOutfit(StateWriter.CommandArbitrator, w, OutfitCategory.Exercise, gym);
        bool mapping = app.WornClothing(w, OutfitCategory.Everyday) == every
            && app.WornClothing(w, OutfitCategory.Sleep) == sleep
            && app.WornClothing(w, OutfitCategory.Exercise) == gym;
        app.SetOverride(StateWriter.CommandArbitrator, w, OutfitCategory.Exercise);
        bool held = app.WornClothing(w, OutfitCategory.Everyday) == gym
            && app.WornClothing(w, OutfitCategory.Sleep) == gym;
        app.SetOverride(StateWriter.CommandArbitrator, w, null);
        bool resumed = app.WornClothing(w, OutfitCategory.Sleep) == sleep;
        Check(mapping && held && resumed, "wardrobe-mapping-override");

        // Appearance export/import round-trips every field.
        List<AppearanceEntry> rows = app.Export();
        AppearanceStore fresh = new AppearanceStore();
        fresh.Import(StateWriter.SimCore, rows);
        Check(ExportKey(app) == ExportKey(fresh) && fresh.Count == 8, "appearance-roundtrip");

        // Appearance edits leave authoritative sim state untouched.
        int hashBefore = ward.ComputeStateHash();
        app.SetPreset(StateWriter.SimCore, w, AppearanceDimension.EyeColor, 0);
        app.SetPreset(StateWriter.SimCore, w, AppearanceDimension.HairType, 0);
        app.SetPreset(StateWriter.SimCore, w, AppearanceDimension.HairStyle,
            ResidentFactory.ValidHairStyles(0)[0]);
        app.SetPreset(StateWriter.SimCore, w, AppearanceDimension.HairColor, 0);
        app.SetOutfit(StateWriter.SimCore, w, OutfitCategory.Everyday, 0);
        Check(ward.ComputeStateHash() == hashBefore, "appearance-edits-isolated");

        // Body/appearance presets consume the registered RIGHTS source.
        string register = File.ReadAllText("Assets/Blockparty/Content/asset-register.json");
        Check(register.Contains("\"" + ResidentFactory.AppearanceCatalogSource + "\""),
            "appearance-source-registered");

        // Sole writer: appearance mutations are audited with allowed writers.
        bool auditClean = app.WriteAudit.Count > 0;
        for (int i = 0; i < app.WriteAudit.Count; i++)
        {
            StateWriter writer = app.WriteAudit[i].Writer;
            if (writer != StateWriter.SimCore && writer != StateWriter.CommandArbitrator
                && writer != StateWriter.ActivityExecutor)
            {
                auditClean = false;
            }
        }
        Check(auditClean, "appearance-sole-writer-audit");

        // Fast Enter Play Mode: no static mutable state in the package.
        Check(NoStaticMutableState(typeof(WorldState).Assembly), "femp-no-static-mutable");

        // ---- negatives: injected failures must be refused ----
        string exportBefore = ExportKey(app);
        for (int d = 0; d < ResidentFactory.DimensionCount; d++)
        {
            AppearanceDimension dim = (AppearanceDimension)d;
            int count = ResidentFactory.OptionCount(dim);
            ExpectThrow(delegate { app.SetPreset(StateWriter.SimCore, w, dim, -1); },
                "preset-low-refused-" + d.ToString());
            ExpectThrow(delegate { app.SetPreset(StateWriter.SimCore, w, dim, count); },
                "preset-high-refused-" + d.ToString());
        }
        ExpectThrow(delegate { app.SetOutfit(StateWriter.SimCore, w, OutfitCategory.Sleep, 192); },
            "clothing-192-refused");
        ExpectThrow(delegate { app.SetPreset(StateWriter.SimCore, (ResidentId)424242L, AppearanceDimension.HairColor, 1); },
            "unknown-resident-refused");
        ExpectThrow(delegate { app.WornClothing((ResidentId)424242L, OutfitCategory.Everyday); },
            "worn-unknown-resident-refused");
        ExpectThrow(delegate { app.SetPreset((StateWriter)99, w, AppearanceDimension.HairColor, 1); },
            "invalid-writer-rejected");
        ExpectThrow(delegate { ResidentFactory.PackClothing(8, 0, 0); }, "pack-top-refused");
        ExpectThrow(delegate { ResidentFactory.PackClothing(0, 6, 0); }, "pack-bottom-refused");
        ExpectThrow(delegate { ResidentFactory.PackClothing(0, 0, 4); }, "pack-footwear-refused");
        List<AppearanceEntry> dupRows = app.Export();
        dupRows.Add(dupRows[0]);
        string freshBefore = ExportKey(fresh);
        ExpectThrow(delegate { fresh.Import(StateWriter.SimCore, dupRows); }, "import-duplicate-refused");
        for (int d = (int)AppearanceDimension.EyeColor; d <= (int)AppearanceDimension.HairColor; d++)
        {
            List<AppearanceEntry> badRows = app.Export();
            switch ((AppearanceDimension)d)
            {
                case AppearanceDimension.EyeColor: badRows[0].EyeColor = 999; break;
                case AppearanceDimension.HairType: badRows[0].HairType = 999; break;
                case AppearanceDimension.HairStyle: badRows[0].HairStyle = 999; break;
                case AppearanceDimension.HairColor: badRows[0].HairColor = 999; break;
            }
            ExpectThrow(delegate { fresh.Import(StateWriter.SimCore, badRows); },
                "import-range-refused-" + ((AppearanceDimension)d).ToString());
            Check(ExportKey(fresh) == freshBefore, "import-range-mutate-nothing-" + d.ToString());
        }
        ExpectThrow(delegate { fresh.Import(StateWriter.SimCore, null); }, "import-null-refused");
        Check(ExportKey(app) == exportBefore && ExportKey(fresh) == freshBefore,
            "negatives-mutate-nothing");

        Console.WriteLine(failures == 0 ? "RESIDENT-CREATE ALL PASS" : "RESIDENT-CREATE FAILURES: " + failures);
        return failures == 0 ? 0 : 1;
    }

    private static ResidentRecord[] Sorted(WorldState world)
    {
        ResidentRecord[] residents = world.ResidentsSnapshot();
        Array.Sort(residents, delegate(ResidentRecord a, ResidentRecord b)
        {
            return ((long)a.Id).CompareTo((long)b.Id);
        });
        return residents;
    }

    private static bool PresetsInRange(AppearanceRecord look)
    {
        return look.SkinTone >= 0 && look.SkinTone < ResidentFactory.SkinToneCount
            && look.Face >= 0 && look.Face < ResidentFactory.FaceCount
            && look.EyeColor >= 0 && look.EyeColor < ResidentFactory.EyeColorCount
            && look.HairType >= 0 && look.HairType < ResidentFactory.HairTypeCount
            && look.HairStyle >= 0 && look.HairStyle < ResidentFactory.HairStyleCount
            && ResidentFactory.IsValidHairStyle(look.HairType, look.HairStyle)
            && look.HairColor >= 0 && look.HairColor < ResidentFactory.HairColorCount
            && look.Accessory >= 0 && look.Accessory < ResidentFactory.AccessoryCount
            && look.GetOutfit(OutfitCategory.Everyday) >= 0
            && look.GetOutfit(OutfitCategory.Everyday) < ResidentFactory.ClothingComboCount
            && look.GetOutfit(OutfitCategory.Sleep) >= 0
            && look.GetOutfit(OutfitCategory.Sleep) < ResidentFactory.ClothingComboCount
            && look.GetOutfit(OutfitCategory.Exercise) >= 0
            && look.GetOutfit(OutfitCategory.Exercise) < ResidentFactory.ClothingComboCount;
    }

    private static int[] PresetVector(AppearanceStore store, ResidentId id)
    {
        AppearanceRecord look;
        store.TryGet(id, out look);
        return new int[]
        {
            look.SkinTone, look.Face, look.EyeColor, look.HairType, look.HairStyle, look.HairColor,
            look.GetOutfit(OutfitCategory.Everyday), look.Accessory,
        };
    }

    private static string ExportKey(AppearanceStore store)
    {
        List<AppearanceEntry> rows = store.Export();
        rows.Sort(delegate(AppearanceEntry a, AppearanceEntry b)
        {
            return a.ResidentId.CompareTo(b.ResidentId);
        });
        string key = string.Empty;
        for (int i = 0; i < rows.Count; i++)
        {
            AppearanceEntry r = rows[i];
            key = key + r.ResidentId.ToString() + ":" + r.SkinTone.ToString() + ","
                + r.Face.ToString() + "," + r.EyeColor.ToString() + "," + r.HairType.ToString()
                + "," + r.HairStyle.ToString() + "," + r.HairColor.ToString()
                + "," + r.Accessory.ToString() + "," + r.OutfitEveryday.ToString() + ","
                + r.OutfitSleep.ToString() + "," + r.OutfitExercise.ToString() + ","
                + r.OverrideCategory.ToString() + ";";
        }
        return key;
    }

    private static string ResidentKey(WorldState world, ResidentId id)
    {
        ResidentRecord r;
        world.TryGetResident(id, out r);
        return r.DisplayName + "|" + ((long)r.Id).ToString() + "|" + ((int)r.Stage).ToString()
            + "|" + r.GetTrait(TraitAxis.Sociable).ToString() + "|" + r.Family.Count.ToString()
            + "|" + r.Memories.Count.ToString() + "|" + r.Household.Value.ToString();
    }

    private static string StripName(string key)
    {
        return key.Substring(key.IndexOf('|'));
    }

    private static void ExpectThrow(Action action, string name)
    {
        try
        {
            action();
            failures++;
            Console.WriteLine("FAIL " + name);
        }
        catch (Exception)
        {
            Console.WriteLine("PASS " + name);
        }
    }

    private static bool NoBodyChoices(Type type)
    {
        MemberInfo[] members = type.GetMembers(BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.Public | BindingFlags.NonPublic);
        for (int i = 0; i < members.Length; i++)
        {
            string name = members[i].Name.ToLowerInvariant();
            if (name.Contains("bodybuild") || name.Contains("height")) return false;
        }
        return true;
    }

    // Fast Enter Play Mode forbids static mutable state: every static field in
    // this package must be const or readonly (domain reload off keeps statics).
    private static bool NoStaticMutableState(Assembly assembly)
    {
        foreach (Type type in assembly.GetTypes())
        {
            if (type.Namespace == null || !type.Namespace.StartsWith("Blockparty.", StringComparison.Ordinal))
            {
                continue;
            }
            FieldInfo[] fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fields.Length; i++)
            {
                if (fields[i].IsLiteral)
                {
                    continue;
                }
                if (!fields[i].IsInitOnly)
                {
                    Console.WriteLine("mutable static: " + type.FullName + "." + fields[i].Name);
                    return false;
                }
            }
        }
        return true;
    }
}
