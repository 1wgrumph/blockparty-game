// RESIDENT-CREATE@1 + APPEARANCE-AMEND@1: new-game creation of exactly eight
// named editable seeded residents plus appearance/wardrobe state with
// identity preservation. Consumes STATE@1 (WorldState records, sole-writer
// audit) and RIGHTS@1 (appearance presets resolve to registered source
// Q-UBC; clothing ids are sim-level until the M2 cloth packages land a mesh
// catalog). Every resident shares one body proportion: no body build or
// height dimension exists.
// Pure C# 9, no UnityEngine dependency. Fast Enter Play Mode: no static
// mutable state (consts and pure statics only), explicit numeric conversions,
// no AppDomain/Assembly.Location/BinaryFormatter, no persisted string hashes.
using System;
using System.Collections.Generic;
using Blockparty.Contracts;

namespace Blockparty.Simulation
{
    // BP-CHR-02 creator dimensions. Clothing packs top/bottom/footwear into one
    // value (tops 8 x bottoms 6 x footwear 4 = 192) so the eight SDoc
    // dimensions stay independently editable; unpack with ResidentFactory
    // helpers. HairStyle values are only valid for their own HairType; ask
    // ResidentFactory.ValidHairStyles. No body build or height dimension.
    public enum AppearanceDimension
    {
        SkinTone = 0,
        Face = 1,
        EyeColor = 2,
        HairType = 3,
        HairStyle = 4,
        HairColor = 5,
        Clothing = 6,
        Accessory = 7,
    }

    // BODY-PRESET: per-resident base body, separate from the eight SDoc
    // dimensions. Male = 0 is the legacy value for saves without the field.
    public enum BaseBody
    {
        Male = 0,
        Female = 1,
    }

    // BP-CHR-05 outfit categories. The worn outfit follows the current activity
    // category unless the player overrides it. Mapping ActivityId catalog codes
    // to these categories arrives with the activity packages; callers pass the
    // resolved category until then.
    public enum OutfitCategory
    {
        Everyday = 0,
        Sleep = 1,
        Exercise = 2,
    }

    // One resident's appearance + wardrobe. Plain data; AppearanceStore mutates.
    public sealed class AppearanceRecord
    {
        public ResidentId Id { get; private set; }
        public int SkinTone { get; private set; }
        public int Face { get; private set; }
        public int EyeColor { get; private set; }
        public int HairType { get; private set; }
        public int HairStyle { get; private set; }
        public int HairColor { get; private set; }
        public int Accessory { get; private set; }
        public BaseBody BaseBody { get; private set; }

        private readonly int[] outfits = new int[ResidentFactory.OutfitCategoryCount];

        public AppearanceRecord(ResidentId id)
        {
            Id = id;
            OverrideCategory = -1;
        }

        // Player override: outfit-category index, or -1 for "follow mapping".
        public int OverrideCategory { get; private set; }

        public int GetOutfit(OutfitCategory category) { return outfits[(int)category]; }

        internal void SetPreset(int dimension, int value)
        {
            switch (dimension)
            {
                case 0: SkinTone = value; break;
                case 1: Face = value; break;
                case 2: EyeColor = value; break;
                case 3: HairType = value; break;
                case 4: HairStyle = value; break;
                case 5: HairColor = value; break;
                case 7: Accessory = value; break;
                default: outfits[(int)OutfitCategory.Everyday] = value; break;
            }
        }

        internal void SetOutfit(OutfitCategory category, int clothing) { outfits[(int)category] = clothing; }
        internal void SetOverride(int categoryOrMinusOne) { OverrideCategory = categoryOrMinusOne; }
        internal void SetBaseBody(BaseBody body) { BaseBody = body; }

        public AppearanceRecord Clone()
        {
            AppearanceRecord copy = new AppearanceRecord(Id);
            copy.SkinTone = SkinTone;
            copy.Face = Face;
            copy.EyeColor = EyeColor;
            copy.HairType = HairType;
            copy.HairStyle = HairStyle;
            copy.HairColor = HairColor;
            copy.Accessory = Accessory;
            copy.BaseBody = BaseBody;
            for (int i = 0; i < outfits.Length; i++)
            {
                copy.outfits[i] = outfits[i];
            }
            copy.OverrideCategory = OverrideCategory;
            return copy;
        }
    }

    // Serializable appearance row for the save path. The WorldSnapshot merge
    // belongs to SAVE (a declared successor); until then this DTO round-trips
    // beside it with the same ResidentId keys.
    public sealed class AppearanceEntry
    {
        public long ResidentId;
        public int SkinTone;
        public int Face;
        public int EyeColor;
        public int HairType;
        public int HairStyle;
        public int HairColor;
        public int Accessory;
        public int OutfitEveryday;
        public int OutfitSleep;
        public int OutfitExercise;
        public int OverrideCategory;
        public BaseBody BaseBody;
    }

    // Appearance/wardrobe authority keyed by ResidentId. Every mutation takes a
    // StateWriter, rejects unknown writers, and appends to its own audit; reads
    // return clones or read-only views, never live records.
    public sealed class AppearanceStore
    {
        private readonly Dictionary<ResidentId, AppearanceRecord> records = new Dictionary<ResidentId, AppearanceRecord>();
        private readonly List<WriteAuditEntry> writeAudit = new List<WriteAuditEntry>();
        private long nextAuditSequence = 1;

        public int Count
        {
            get { return records.Count; }
        }

        public IReadOnlyList<WriteAuditEntry> WriteAudit
        {
            get { return writeAudit.AsReadOnly(); }
        }

        public bool TryGet(ResidentId id, out AppearanceRecord record)
        {
            AppearanceRecord found;
            if (records.TryGetValue(id, out found))
            {
                record = found.Clone();
                return true;
            }
            record = null;
            return false;
        }

        // skinTone >= 0 replaces the drawn tone; the draw still runs so the other presets keep their stream.
        internal void Register(ResidentId id, WorldRng rng, int skinTone = -1)
        {
            AppearanceRecord record = new AppearanceRecord(id);
            int drawn = rng.NextInt(0, ResidentFactory.OptionCount(AppearanceDimension.SkinTone));
            record.SetPreset(0, skinTone >= 0 ? skinTone : drawn);
            record.SetPreset(1, rng.NextInt(0, ResidentFactory.OptionCount(AppearanceDimension.Face)));
            record.SetPreset(2, rng.NextInt(0, ResidentFactory.OptionCount(AppearanceDimension.EyeColor)));
            int hairType = rng.NextInt(0, ResidentFactory.OptionCount(AppearanceDimension.HairType));
            record.SetPreset(3, hairType);
            int[] styles = ResidentFactory.ValidHairStyles(hairType);
            record.SetPreset(4, styles[rng.NextInt(0, styles.Length)]);
            record.SetPreset(5, rng.NextInt(0, ResidentFactory.OptionCount(AppearanceDimension.HairColor)));
            record.SetPreset(7, rng.NextInt(0, ResidentFactory.OptionCount(AppearanceDimension.Accessory)));
            for (int i = 0; i < ResidentFactory.OutfitCategoryCount; i++)
            {
                record.SetOutfit((OutfitCategory)i,
                    rng.NextInt(0, ResidentFactory.OptionCount(AppearanceDimension.Clothing)));
            }
            // Drawn after every existing preset so the eight-dimension stream
            // keeps its d43deab values for the same seed.
            record.SetBaseBody((BaseBody)rng.NextInt(0, 2));
            records.Add(id, record);
        }

        // BP-CHR-02: one dimension changes, the other seven stay equal, except
        // a hair type change repairs the hair style to the first valid style
        // for the new type when the current style no longer fits. A style
        // that is invalid for the resident hair type is refused with a reason.
        public void SetPreset(StateWriter writer, ResidentId id, AppearanceDimension dimension, int value)
        {
            AppearanceRecord record = Require(writer, "SetPreset " + ((long)id).ToString(), id);
            int count = ResidentFactory.OptionCount(dimension);
            if (value < 0 || value >= count)
            {
                throw new ArgumentOutOfRangeException("value", "Preset " + value.ToString()
                    + " outside 0.." + (count - 1).ToString() + " for " + dimension.ToString());
            }
            if (dimension == AppearanceDimension.HairStyle
                && !ResidentFactory.IsValidHairStyle(record.HairType, value))
            {
                throw new ArgumentException("Hair style " + value.ToString()
                    + " is not valid for hair type " + record.HairType.ToString(), "value");
            }
            if (dimension == AppearanceDimension.Clothing)
            {
                record.SetOutfit(OutfitCategory.Everyday, value);
            }
            else
            {
                record.SetPreset((int)dimension, value);
            }
            if (dimension == AppearanceDimension.HairType
                && !ResidentFactory.IsValidHairStyle(value, record.HairStyle))
            {
                record.SetPreset((int)AppearanceDimension.HairStyle,
                    ResidentFactory.ValidHairStyles(value)[0]);
            }
        }

        // BP-CHR-05: wardrobe selection never touches money; WorldState money
        // has no path through this store at all.
        public void SetOutfit(StateWriter writer, ResidentId id, OutfitCategory category, int clothing)
        {
            AppearanceRecord record = Require(writer, "SetOutfit " + ((long)id).ToString(), id);
            int count = ResidentFactory.OptionCount(AppearanceDimension.Clothing);
            if (clothing < 0 || clothing >= count)
            {
                throw new ArgumentOutOfRangeException("clothing", "Clothing preset " + clothing.ToString()
                    + " outside 0.." + (count - 1).ToString());
            }
            record.SetOutfit(category, clothing);
        }

        public void SetOverride(StateWriter writer, ResidentId id, OutfitCategory? category)
        {
            AppearanceRecord record = Require(writer, "SetOverride " + ((long)id).ToString(), id);
            record.SetOverride(category.HasValue ? (int)category.Value : -1);
        }

        // BODY-PRESET: creator base-body choice; touches only this field.
        public void SetBaseBody(StateWriter writer, ResidentId id, BaseBody body)
        {
            AppearanceRecord record = Require(writer, "SetBaseBody " + ((long)id).ToString(), id);
            ResidentFactory.CheckBaseBodyRange((int)body);
            record.SetBaseBody(body);
        }

        // Worn clothing: player override wins, else the activity mapping.
        public int WornClothing(ResidentId id, OutfitCategory currentActivity)
        {
            AppearanceRecord record;
            if (!records.TryGetValue(id, out record))
            {
                throw new ArgumentException("Unknown resident " + ((long)id).ToString(), "id");
            }
            if (record.OverrideCategory >= 0)
            {
                return record.GetOutfit((OutfitCategory)record.OverrideCategory);
            }
            return record.GetOutfit(currentActivity);
        }

        public List<AppearanceEntry> Export()
        {
            List<AppearanceEntry> rows = new List<AppearanceEntry>(records.Count);
            foreach (AppearanceRecord record in records.Values)
            {
                AppearanceEntry row = new AppearanceEntry();
                row.ResidentId = (long)record.Id;
                row.SkinTone = record.SkinTone;
                row.Face = record.Face;
                row.EyeColor = record.EyeColor;
                row.HairType = record.HairType;
                row.HairStyle = record.HairStyle;
                row.HairColor = record.HairColor;
                row.Accessory = record.Accessory;
                row.OutfitEveryday = record.GetOutfit(OutfitCategory.Everyday);
                row.OutfitSleep = record.GetOutfit(OutfitCategory.Sleep);
                row.OutfitExercise = record.GetOutfit(OutfitCategory.Exercise);
                row.OverrideCategory = record.OverrideCategory;
                row.BaseBody = record.BaseBody;
                rows.Add(row);
            }
            return rows;
        }

        public void Import(StateWriter writer, List<AppearanceEntry> rows)
        {
            CheckWriter(writer, "Import");
            if (rows == null)
            {
                throw new ArgumentNullException("rows");
            }
            Dictionary<ResidentId, AppearanceRecord> next = new Dictionary<ResidentId, AppearanceRecord>();
            for (int i = 0; i < rows.Count; i++)
            {
                AppearanceEntry row = rows[i];
                ResidentFactory.CheckPresetRange(AppearanceDimension.SkinTone, row.SkinTone);
                ResidentFactory.CheckPresetRange(AppearanceDimension.Face, row.Face);
                ResidentFactory.CheckPresetRange(AppearanceDimension.EyeColor, row.EyeColor);
                ResidentFactory.CheckPresetRange(AppearanceDimension.HairType, row.HairType);
                ResidentFactory.CheckPresetRange(AppearanceDimension.HairStyle, row.HairStyle);
                ResidentFactory.CheckPresetRange(AppearanceDimension.HairColor, row.HairColor);
                ResidentFactory.CheckPresetRange(AppearanceDimension.Accessory, row.Accessory);
                if (!ResidentFactory.IsValidHairStyle(row.HairType, row.HairStyle))
                {
                    throw new ArgumentException("Hair style " + row.HairStyle.ToString()
                        + " is not valid for hair type " + row.HairType.ToString(), "rows");
                }
                ResidentFactory.CheckPresetRange(AppearanceDimension.Clothing, row.OutfitEveryday);
                ResidentFactory.CheckPresetRange(AppearanceDimension.Clothing, row.OutfitSleep);
                ResidentFactory.CheckPresetRange(AppearanceDimension.Clothing, row.OutfitExercise);
                if (row.OverrideCategory < -1 || row.OverrideCategory >= ResidentFactory.OutfitCategoryCount)
                {
                    throw new ArgumentException("Override category out of range", "rows");
                }
                ResidentFactory.CheckBaseBodyRange((int)row.BaseBody);
                ResidentId id = (ResidentId)row.ResidentId;
                if (next.ContainsKey(id))
                {
                    throw new ArgumentException("Duplicate appearance row for resident "
                        + row.ResidentId.ToString(), "rows");
                }
                AppearanceRecord record = new AppearanceRecord(id);
                record.SetPreset(0, row.SkinTone);
                record.SetPreset(1, row.Face);
                record.SetPreset(2, row.EyeColor);
                record.SetPreset(3, row.HairType);
                record.SetPreset(4, row.HairStyle);
                record.SetPreset(5, row.HairColor);
                record.SetPreset(7, row.Accessory);
                record.SetOutfit(OutfitCategory.Everyday, row.OutfitEveryday);
                record.SetOutfit(OutfitCategory.Sleep, row.OutfitSleep);
                record.SetOutfit(OutfitCategory.Exercise, row.OutfitExercise);
                record.SetOverride(row.OverrideCategory);
                record.SetBaseBody(row.BaseBody);
                next.Add(id, record);
            }
            records.Clear();
            foreach (KeyValuePair<ResidentId, AppearanceRecord> pair in next)
            {
                records.Add(pair.Key, pair.Value);
            }
        }

        private AppearanceRecord Require(StateWriter writer, string detail, ResidentId id)
        {
            CheckWriter(writer, detail);
            AppearanceRecord record;
            if (!records.TryGetValue(id, out record))
            {
                throw new ArgumentException("Unknown resident " + ((long)id).ToString(), "id");
            }
            return record;
        }

        private void CheckWriter(StateWriter writer, string detail)
        {
            if (writer != StateWriter.SimCore && writer != StateWriter.CommandArbitrator && writer != StateWriter.ActivityExecutor)
            {
                throw new ArgumentException("Unknown state writer", "writer");
            }
            writeAudit.Add(new WriteAuditEntry(nextAuditSequence, writer, detail));
            nextAuditSequence++;
        }
    }

    // New-game entry point (BP-CHR-01/02/05).
    public static class ResidentFactory
    {
        public const int InitialResidentCount = 8;

        // Versioned default-name list: v1 is WorldState.DefaultNames; the world
        // records this version alongside the seed for SEIT-CHR-01-P2/N4.
        public const int NameListVersion = 1;

        // RIGHTS@1 registered source consumed for body/appearance presets.
        public const string AppearanceCatalogSource = "Q-UBC";

        public const int SkinToneCount = 7;
        public const int FaceCount = 6;
        public const int EyeColorCount = 5;
        public const int HairTypeCount = 4;
        public const int HairStyleCount = 8;
        public const int HairColorCount = 6;
        public const int TopCount = 8;
        public const int BottomCount = 6;
        public const int FootwearCount = 4;
        public const int AccessoryCount = 4;
        public const int ClothingComboCount = 8 * 6 * 4;
        public const int OutfitCategoryCount = 3;
        public const int DimensionCount = 8;

        public static int OptionCount(AppearanceDimension dimension)
        {
            switch (dimension)
            {
                case AppearanceDimension.SkinTone: return SkinToneCount;
                case AppearanceDimension.Face: return FaceCount;
                case AppearanceDimension.EyeColor: return EyeColorCount;
                case AppearanceDimension.HairType: return HairTypeCount;
                case AppearanceDimension.HairStyle: return HairStyleCount;
                case AppearanceDimension.HairColor: return HairColorCount;
                case AppearanceDimension.Clothing: return ClothingComboCount;
                case AppearanceDimension.Accessory: return AccessoryCount;
                default: throw new ArgumentException("Unknown dimension", "dimension");
            }
        }

        // BP-CHR-02 hair catalog: each hair type owns two styles, so every
        // type offers at least two styles and every other style is refused.
        // A fresh array per call keeps FEMP's no-static-mutable-state rule.
        public static int[] ValidHairStyles(int hairType)
        {
            CheckPresetRange(AppearanceDimension.HairType, hairType);
            return new int[] { hairType * 2, hairType * 2 + 1 };
        }

        public static bool IsValidHairStyle(int hairType, int style)
        {
            int[] styles = ValidHairStyles(hairType);
            for (int i = 0; i < styles.Length; i++)
            {
                if (styles[i] == style)
                {
                    return true;
                }
            }
            return false;
        }

        public static int PackClothing(int top, int bottom, int footwear)
        {
            if (top < 0 || top >= TopCount)
            {
                throw new ArgumentOutOfRangeException("top");
            }
            if (bottom < 0 || bottom >= BottomCount)
            {
                throw new ArgumentOutOfRangeException("bottom");
            }
            if (footwear < 0 || footwear >= FootwearCount)
            {
                throw new ArgumentOutOfRangeException("footwear");
            }
            return (top * BottomCount + bottom) * FootwearCount + footwear;
        }

        public static void UnpackClothing(int clothing, out int top, out int bottom, out int footwear)
        {
            CheckPresetRange(AppearanceDimension.Clothing, clothing);
            footwear = clothing % FootwearCount;
            bottom = (clothing / FootwearCount) % BottomCount;
            top = clothing / (BottomCount * FootwearCount);
        }

        internal static void CheckPresetRange(AppearanceDimension dimension, int value)
        {
            int count = OptionCount(dimension);
            if (value < 0 || value >= count)
            {
                throw new ArgumentException("Preset " + value.ToString()
                    + " outside 0.." + (count - 1).ToString() + " for " + dimension.ToString());
            }
        }

        internal static void CheckBaseBodyRange(int value)
        {
            if (value < 0 || value > 1)
            {
                throw new ArgumentException("Preset " + value.ToString()
                    + " outside 0..1 for BaseBody");
            }
        }

        // Creates a new game: exactly eight seeded residents. names[i] may be
        // null, empty or whitespace-only, which stores the seeded default for
        // that resident (BP-CHR-01); anything else is stored verbatim, so
        // duplicates stay distinct by ResidentId. Same seed reproduces identical
        // names and presets before edits (SEIT-CHR-01-P2).
        public static WorldState CreateNewGame(long worldId, long seed, string[] names, out AppearanceStore appearances)
        {
            string[] cleaned = new string[InitialResidentCount];
            for (int i = 0; i < InitialResidentCount; i++)
            {
                string want = names != null && i < names.Length ? names[i] : null;
                cleaned[i] = string.IsNullOrWhiteSpace(want) ? null : want;
            }
            WorldState world = WorldState.CreateSeeded(worldId, seed, InitialResidentCount, cleaned);
            appearances = new AppearanceStore();
            ResidentRecord[] residents = world.ResidentsSnapshot();
            SortByResidentId(residents);
            // SKIN-TONES (M5-OD-45): the starting residents wear every skin tone, in an order
            // shuffled from the world seed on its own stream; arrivals keep their drawn tone
            // and newborns blend their parents' (FamilyCreation).
            int[] tones = new int[SkinToneCount];
            WorldRng order = new WorldRng(seed ^ 0x5D1E7035L);
            for (int i = 0; i < tones.Length; i++) tones[i] = i;
            for (int i = tones.Length - 1; i > 0; i--)
            {
                int j = order.NextInt(0, i + 1);
                int swap = tones[i]; tones[i] = tones[j]; tones[j] = swap;
            }
            for (int i = 0; i < residents.Length; i++)
            {
                // Per-resident stream derived from the world seed: same seed and
                // creation order reproduce the same presets (no string hashing).
                WorldRng rng = new WorldRng(seed + 7919L * (long)(i + 1));
                appearances.Register(residents[i].Id, rng, tones[i % tones.Length]);
            }
            return world;
        }

        // Insertion sort: eight residents, and no delegate (a lambda would make
        // the compiler cache it in a static field, breaking FEMP).
        private static void SortByResidentId(ResidentRecord[] residents)
        {
            for (int i = 1; i < residents.Length; i++)
            {
                ResidentRecord key = residents[i];
                int j = i - 1;
                while (j >= 0 && (long)residents[j].Id > (long)key.Id)
                {
                    residents[j + 1] = residents[j];
                    j--;
                }
                residents[j + 1] = key;
            }
        }
    }
}
