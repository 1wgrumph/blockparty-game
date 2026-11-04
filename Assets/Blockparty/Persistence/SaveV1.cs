// SAVE@1: versioned atomic persistence (PERSIST, CONTRACT-BP-09-SAVE).
// Consumes IFC-LOCK@1 (SaveFormat, SaveHeader, SettingsRecord), STATE@1
// (WorldState snapshot/load, ledgers), CLOCK@1 (Clock controls), COMMAND@1
// (arbitrator queues, wills, caregivers) and NAV@1 (in-progress executions,
// holds, positions) plus RESIDENT-CREATE@1 (AppearanceStore export/import),
// ECONOMY@1 (Accounts shared balances, ledger, applied ids) and FOOD@1
// (Food home stock, tracked actions) through their Export/Import seams.
// Plain C# 9, no UnityEngine dependency.
// Atomic write (DES-BP-ATOMIC-SAVE): snapshot between ticks, write save.tmp,
// flush, reparse and validate payload/hash, retain the last validated save as
// save.bak, then atomically replace save.json. Load validates into fresh
// objects before returning; corrupt input is refused with a recovery notice,
// the invalid bytes are retained, and no live world is touched (BP-SAVE-02).
// Unknown schema versions are refused naming the version; v1 is the first
// version, so only the refusal branch exists until SAVE-V2 records a
// migration (BP-SAVE-03). Resume restores accepted player queues and
// in-progress actions with elapsed game seconds preserved; reserved ledger
// entries never commit twice because completion checks entry state first,
// and loading a paused world applies no rewards (BP-SAVE-04).
// JSON: the SDoc names System.Text.Json, but that assembly is absent from
// Unity's Assembly-CSharp reference set, so this file carries a minimal
// deterministic UTF-8 JSON writer/reader emitting the same schema v1
// document shape (header plus payload sections, sha256 of the canonical
// payload bytes). No new package for serialization.
// Restore replays saved player queues through CommandArbitrator.Submit and
// re-dispatches in-progress actions through ActivityExecutor.Dispatch,
// because neither class exposes an import API; SAVE cannot add one (other
// packages' files). Consequences, all recorded on the load report: restored
// entries keep their CommandId but take fresh ActionInstanceIds from the
// persisted high-water marks (never reused); sequences and generations
// re-baseline under the incremented epoch; non-player queue entries are
// discarded like unaccepted proposals; terminal-at-save currents are dropped
// instead of re-executed; executor stage traces restart at the load tick.
// Accounts and Food state follows the saved-to-fresh action map on restore,
// so reservations and exactly-once guards survive the remap; saves without
// the accounts and food sections still load, with empty service state.
// The host re-registers scene objects (geometry plus reachability) through
// ISceneContent before queued work resumes; holds are evidenced in the
// objects section and re-acquired by dispatch.
// Fast Enter Play Mode: no static mutable state (consts and pure static
// methods only), no lambdas, no LINQ, explicit numeric conversions, no
// AppDomain/Assembly.Location/BinaryFormatter, no wall-clock reads (time is
// game seconds only), and string hashing is never persisted.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Blockparty.Activities;
using Blockparty.AI;
using Blockparty.Contracts;
using Blockparty.Simulation;

namespace Blockparty.Persistence
{
    // Input bundle for SaveGame. The caller saves between fixed ticks after
    // the executor Tick, so no interruption is unprocessed and no tick is
    // half-applied. The five-argument stack omits the economy, food,
    // expenses and ownership sections; the seven-argument stack captures the
    // same live Accounts and Food instances the running world uses, never
    // copies; the eight-argument stacks add the live Expenses, Ownership or
    // Home instance the same way, the nine-argument stack carries both
    // Expenses and Ownership, and the ten-argument stack adds Housing. Every
    // stack carries live Club, Creative and Party facades bound to its world; saves
    // include those sections only when they hold state. The Creative-plus-image
    // stack also captures the live ImageAdapter's disclosed content.
    public sealed class GameStack
    {
        public readonly WorldState World;
        public readonly Clock Clock;
        public readonly CommandArbitrator Arbitrator;
        public readonly ActivityExecutor Executor;
        public readonly AppearanceStore Appearances;
        public readonly Accounts Accounts;
        public readonly Food Food;
        public readonly Expenses Expenses;
        public readonly Ownership Ownership;
        public readonly Home Home;
        public readonly Club Club;
        public readonly Housing Housing;
        public readonly Creative Creative;
        public readonly ImageAdapter Image;
        public readonly FamilyCreation Family;
        public readonly Party Party;

        public GameStack(WorldState world, Clock clock, CommandArbitrator arbitrator,
            ActivityExecutor executor, AppearanceStore appearances)
            : this(world, clock, arbitrator, executor, appearances, null, null, null, null, null, null, null, false, null)
        {
        }

        public GameStack(WorldState world, Clock clock, CommandArbitrator arbitrator,
            ActivityExecutor executor, AppearanceStore appearances, Accounts accounts, Food food)
            : this(world, clock, arbitrator, executor, appearances, accounts, food, null, null, null, null, null, true, null)
        {
        }

        public GameStack(WorldState world, Clock clock, CommandArbitrator arbitrator,
            ActivityExecutor executor, AppearanceStore appearances, Accounts accounts, Food food,
            Expenses expenses)
            : this(world, clock, arbitrator, executor, appearances, accounts, food, expenses, null, null, null, null, true, null)
        {
            if (expenses == null)
            {
                throw new ArgumentNullException("expenses");
            }
        }

        public GameStack(WorldState world, Clock clock, CommandArbitrator arbitrator,
            ActivityExecutor executor, AppearanceStore appearances, Accounts accounts, Food food,
            Ownership ownership)
            : this(world, clock, arbitrator, executor, appearances, accounts, food, null, ownership, null, null, null, true, null)
        {
            if (ownership == null)
            {
                throw new ArgumentNullException("ownership");
            }
        }

        public GameStack(WorldState world, Clock clock, CommandArbitrator arbitrator,
            ActivityExecutor executor, AppearanceStore appearances, Accounts accounts, Food food,
            Home home)
            : this(world, clock, arbitrator, executor, appearances, accounts, food, null, null, home, null, null, true, null)
        {
            if (home == null)
            {
                throw new ArgumentNullException("home");
            }
        }

        public GameStack(WorldState world, Clock clock, CommandArbitrator arbitrator,
            ActivityExecutor executor, AppearanceStore appearances, Accounts accounts, Food food,
            Expenses expenses, Ownership ownership)
            : this(world, clock, arbitrator, executor, appearances, accounts, food, expenses, ownership, null, null, null, true, null)
        {
            if (expenses == null)
            {
                throw new ArgumentNullException("expenses");
            }
            if (ownership == null)
            {
                throw new ArgumentNullException("ownership");
            }
        }

        public GameStack(WorldState world, Clock clock, CommandArbitrator arbitrator,
            ActivityExecutor executor, AppearanceStore appearances, Accounts accounts, Food food,
            Expenses expenses, Ownership ownership, Housing housing)
            : this(world, clock, arbitrator, executor, appearances, accounts, food, expenses, ownership, null, housing, null, true, null)
        {
            if (expenses == null)
            {
                throw new ArgumentNullException("expenses");
            }
            if (ownership == null)
            {
                throw new ArgumentNullException("ownership");
            }
            if (housing == null)
            {
                throw new ArgumentNullException("housing");
            }
        }

        // BIRTH@1: rebundling a LoadedGame keeps the restored FamilyCreation
        // instance so its pregnancy/birth state is saved by reference.
        public GameStack(WorldState world, Clock clock, CommandArbitrator arbitrator,
            ActivityExecutor executor, AppearanceStore appearances, Accounts accounts, Food food,
            Expenses expenses, Ownership ownership, Housing housing, FamilyCreation family)
            : this(world, clock, arbitrator, executor, appearances, accounts, food, expenses, ownership, null, housing, null, true, null, family)
        {
            if (expenses == null)
            {
                throw new ArgumentNullException("expenses");
            }
            if (ownership == null)
            {
                throw new ArgumentNullException("ownership");
            }
            if (housing == null)
            {
                throw new ArgumentNullException("housing");
            }
            if (family == null)
            {
                throw new ArgumentNullException("family");
            }
        }

        public GameStack(WorldState world, Clock clock, CommandArbitrator arbitrator,
            ActivityExecutor executor, AppearanceStore appearances, Accounts accounts, Food food,
            Expenses expenses, Creative creative)
            : this(world, clock, arbitrator, executor, appearances, accounts, food, expenses, null, null, null, creative, true, null)
        {
            if (expenses == null) throw new ArgumentNullException("expenses");
            if (creative == null) throw new ArgumentNullException("creative");
        }

        public GameStack(WorldState world, Clock clock, CommandArbitrator arbitrator,
            ActivityExecutor executor, AppearanceStore appearances, Accounts accounts, Food food,
            Expenses expenses, Creative creative, ImageAdapter image)
            : this(world, clock, arbitrator, executor, appearances, accounts, food, expenses, null, null, null, creative, true, image)
        {
            if (expenses == null) throw new ArgumentNullException("expenses");
            if (creative == null) throw new ArgumentNullException("creative");
            if (image == null) throw new ArgumentNullException("image");
        }

        public GameStack(WorldState world, Clock clock, CommandArbitrator arbitrator,
            ActivityExecutor executor, AppearanceStore appearances, Accounts accounts, Food food,
            Expenses expenses, Ownership ownership, Housing housing, Creative creative)
            : this(world, clock, arbitrator, executor, appearances, accounts, food, expenses, ownership, null, housing, creative, true, null)
        {
            if (expenses == null) throw new ArgumentNullException("expenses");
            if (ownership == null) throw new ArgumentNullException("ownership");
            if (housing == null) throw new ArgumentNullException("housing");
            if (creative == null) throw new ArgumentNullException("creative");
        }

        // Preserve the live household services when bundling or resaving an integrated game.
        public GameStack(WorldState world, Clock clock, CommandArbitrator arbitrator,
            ActivityExecutor executor, AppearanceStore appearances, Accounts accounts, Food food,
            Expenses expenses, Ownership ownership, Home home, Club club, Housing housing,
            Creative creative, FamilyCreation family)
            : this(world, clock, arbitrator, executor, appearances, accounts, food, expenses,
                ownership, home, housing, creative, true, null, family, club)
        {
            if (expenses == null) throw new ArgumentNullException("expenses");
            if (ownership == null) throw new ArgumentNullException("ownership");
            if (home == null) throw new ArgumentNullException("home");
            if (club == null) throw new ArgumentNullException("club");
            if (housing == null) throw new ArgumentNullException("housing");
            if (creative == null) throw new ArgumentNullException("creative");
            if (family == null) throw new ArgumentNullException("family");
        }

        private GameStack(WorldState world, Clock clock, CommandArbitrator arbitrator,
            ActivityExecutor executor, AppearanceStore appearances, Accounts accounts, Food food,
            Expenses expenses, Ownership ownership, Home home, Housing housing, Creative creative,
            bool requireServices, ImageAdapter image, FamilyCreation family = null, Club club = null)
        {
            if (world == null)
            {
                throw new ArgumentNullException("world");
            }
            if (clock == null)
            {
                throw new ArgumentNullException("clock");
            }
            if (arbitrator == null)
            {
                throw new ArgumentNullException("arbitrator");
            }
            if (executor == null)
            {
                throw new ArgumentNullException("executor");
            }
            if (appearances == null)
            {
                throw new ArgumentNullException("appearances");
            }
            if (requireServices && accounts == null)
            {
                throw new ArgumentNullException("accounts");
            }
            if (requireServices && food == null)
            {
                throw new ArgumentNullException("food");
            }
            World = world;
            Clock = clock;
            Arbitrator = arbitrator;
            Executor = executor;
            Appearances = appearances;
            Accounts = accounts;
            Food = food;
            Expenses = expenses;
            Ownership = ownership;
            Home = home;
            Club = club ?? new Club(world);
            Housing = housing;
            if (housing != null)
            {
                arbitrator.BindHousing(housing);
            }
            Creative = creative ?? new Creative(world);
            Image = image;
            Family = family ?? new FamilyCreation(world, arbitrator, executor, appearances, housing);
            Party = Blockparty.Activities.Party.FromExecutor(executor)
                ?? new Party(world, arbitrator, executor, accounts);
        }
    }

    // Scene content the save does not own: object geometry (slot poses) and
    // reachability. LoadGame calls this on the fresh executor before
    // re-dispatching in-progress actions; actions whose objects are missing
    // are reconciled to failed with a report note, never resumed.
    public interface ISceneContent
    {
        void RegisterObjects(ActivityExecutor executor);
    }

    // File seam for the atomic write and for fault injection. The default
    // SaveFileStore does real flushed I/O; checks substitute a store that
    // throws or stops at a chosen step to prove BP-SAVE-02 recovery.
    public interface ISaveFileStore
    {
        bool Exists(string path);
        byte[] ReadAllBytes(string path);
        void EnsureDirectory(string directory);
        void WriteTemp(string tempPath, byte[] bytes);
        void Replace(string tempPath, string destPath, string backupPath);
    }

    public sealed class SaveFileStore : ISaveFileStore
    {
        public bool Exists(string path)
        {
            return File.Exists(path);
        }

        public byte[] ReadAllBytes(string path)
        {
            return File.ReadAllBytes(path);
        }

        public void EnsureDirectory(string directory)
        {
            Directory.CreateDirectory(directory);
        }

        public void WriteTemp(string tempPath, byte[] bytes)
        {
            using (FileStream stream = new FileStream(tempPath, FileMode.Create,
                FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        // Previous dest is retained as backup when it exists; otherwise the
        // temp file simply becomes the dest (first save, no backup yet).
        public void Replace(string tempPath, string destPath, string backupPath)
        {
            if (File.Exists(destPath))
            {
                File.Replace(tempPath, destPath, backupPath);
            }
            else
            {
                File.Move(tempPath, destPath);
            }
        }
    }

    public sealed class SaveWriteResult
    {
        public readonly bool Ok;
        public readonly string Detail;

        public SaveWriteResult(bool ok, string detail)
        {
            Ok = ok;
            Detail = detail ?? string.Empty;
        }
    }

    public sealed class SaveLoadResult
    {
        public readonly bool Ok;
        public readonly LoadedGame Game;
        public readonly string Notice;
        public readonly bool BackupAvailable;

        public SaveLoadResult(bool ok, LoadedGame game, string notice, bool backupAvailable)
        {
            Ok = ok;
            Game = game;
            Notice = notice ?? string.Empty;
            BackupAvailable = backupAvailable;
        }
    }

    public sealed class SettingsLoadResult
    {
        public readonly bool Ok;
        public readonly SettingsRecord Settings;
        public readonly string[] UnsupportedFields;
        public readonly string Detail;

        public SettingsLoadResult(bool ok, SettingsRecord settings,
            string[] unsupportedFields, string detail)
        {
            Ok = ok;
            Settings = settings;
            UnsupportedFields = unsupportedFields ?? new string[0];
            Detail = detail ?? string.Empty;
        }
    }

    // A loaded game: fresh world, appearances, clock, arbitrator, executor,
    // accounts, food, expenses, ownership, home, club, housing and image content
    // plus the save header and reconciliation notes. Missing sections restore
    // empty state.
    public sealed class LoadedGame
    {
        public readonly WorldState World;
        public readonly AppearanceStore Appearances;
        public readonly Clock Clock;
        public readonly CommandArbitrator Arbitrator;
        public readonly ActivityExecutor Executor;
        public readonly Accounts Accounts;
        public readonly Food Food;
        public readonly Expenses Expenses;
        public readonly Ownership Ownership;
        public readonly Home Home;
        public readonly Club Club;
        public readonly Housing Housing;
        public readonly Creative Creative;
        public readonly ImageAdapter Image;
        public readonly FamilyCreation Family;
        public readonly Party Party;
        public readonly SaveHeader Header;
        public readonly string[] Notes;

        public LoadedGame(WorldState world, AppearanceStore appearances, Clock clock,
            CommandArbitrator arbitrator, ActivityExecutor executor, SaveHeader header,
            string[] notes, Accounts accounts, Food food, Expenses expenses, Ownership ownership,
            Home home, Housing housing)
            : this(world, appearances, clock, arbitrator, executor, header, notes,
                accounts, food, expenses, ownership, home, null, housing)
        {
        }

        public LoadedGame(WorldState world, AppearanceStore appearances, Clock clock,
            CommandArbitrator arbitrator, ActivityExecutor executor, SaveHeader header,
            string[] notes, Accounts accounts, Food food, Expenses expenses, Ownership ownership,
            Home home, Club club, Creative creative)
            : this(world, appearances, clock, arbitrator, executor, header, notes,
                accounts, food, expenses, ownership, home, club, null, creative)
        {
        }

        public LoadedGame(WorldState world, AppearanceStore appearances, Clock clock,
            CommandArbitrator arbitrator, ActivityExecutor executor, SaveHeader header,
            string[] notes, Accounts accounts, Food food, Expenses expenses, Ownership ownership,
            Home home, Club club, Housing housing, Creative creative, Party party)
            : this(world, appearances, clock, arbitrator, executor, header, notes,
                accounts, food, expenses, ownership, home, club, housing, creative, null, null, party)
        {
        }

        public LoadedGame(WorldState world, AppearanceStore appearances, Clock clock,
            CommandArbitrator arbitrator, ActivityExecutor executor, SaveHeader header,
            string[] notes, Accounts accounts, Food food, Expenses expenses, Ownership ownership,
            Home home, Club club, Housing housing, Creative creative, FamilyCreation family)
            : this(world, appearances, clock, arbitrator, executor, header, notes,
                accounts, food, expenses, ownership, home, club, housing, creative, null, family)
        {
        }

        public LoadedGame(WorldState world, AppearanceStore appearances, Clock clock,
            CommandArbitrator arbitrator, ActivityExecutor executor, SaveHeader header,
            string[] notes, Accounts accounts, Food food, Expenses expenses, Ownership ownership,
            Home home, Club club = null, Housing housing = null, Creative creative = null,
            ImageAdapter image = null, FamilyCreation family = null, Party party = null)
        {
            World = world;
            Appearances = appearances;
            Clock = clock;
            Arbitrator = arbitrator;
            Executor = executor;
            Accounts = accounts;
            Food = food;
            Expenses = expenses;
            Ownership = ownership;
            Home = home;
            Club = club ?? new Club(world);
            Housing = housing;
            Creative = creative ?? new Creative(world);
            Image = image;
            Family = family ?? new FamilyCreation(world, arbitrator, executor, appearances, housing);
            Party = party ?? Blockparty.Activities.Party.FromExecutor(executor)
                ?? new Party(world, arbitrator, executor, accounts);
            Header = header;
            Notes = notes ?? new string[0];
        }
    }

    // Minimal JSON document model shared by the writer and the reader.
    // Objects render keys in insertion order, so saves of identical state
    // are byte-identical and the payload hash is stable. Numbers keep their
    // raw text; typed getters parse with the invariant culture, so 64-bit
    // integers never cross a double and floats and doubles round-trip exactly.
    internal abstract class JsonValue
    {
        internal abstract void Render(StringBuilder sb);
    }

    internal sealed class JsonObject : JsonValue
    {
        private readonly List<string> keys = new List<string>();
        private readonly Dictionary<string, JsonValue> map = new Dictionary<string, JsonValue>();

        public void Add(string key, JsonValue value)
        {
            if (key == null)
            {
                throw new ArgumentNullException("key");
            }
            if (value == null)
            {
                throw new ArgumentNullException("value");
            }
            if (map.ContainsKey(key))
            {
                throw new ArgumentException("Duplicate key " + key, "key");
            }
            keys.Add(key);
            map.Add(key, value);
        }

        public void Add(string key, string value)
        {
            Add(key, new JsonString(value));
        }

        public void Add(string key, long value)
        {
            Add(key, new JsonNumber(value.ToString()));
        }

        public void Add(string key, ulong value)
        {
            Add(key, new JsonNumber(value.ToString()));
        }

        public void Add(string key, int value)
        {
            Add(key, new JsonNumber(value.ToString()));
        }

        public void Add(string key, uint value)
        {
            Add(key, new JsonNumber(value.ToString()));
        }

        public void Add(string key, float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new InvalidOperationException("non-finite float for \"" + key
                    + "\": refusing to write an unloadable document");
            }
            Add(key, new JsonNumber(value.ToString("R", CultureInfo.InvariantCulture)));
        }

        public void Add(string key, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new InvalidOperationException("non-finite double for \"" + key
                    + "\": refusing to write an unloadable document");
            }
            Add(key, new JsonNumber(value.ToString("R", CultureInfo.InvariantCulture)));
        }

        public void Add(string key, bool value)
        {
            Add(key, value ? JsonBool.True : JsonBool.False);
        }

        public void AddNull(string key)
        {
            Add(key, JsonNull.Instance);
        }

        // Test forgery helper: replace an existing key in place, keeping the
        // canonical key order so the reassembled document stays hashable.
        internal void Set(string key, JsonValue value)
        {
            if (!map.ContainsKey(key))
            {
                throw new ArgumentException("Missing key " + key, "key");
            }
            map[key] = value;
        }

        public bool Has(string key)
        {
            return map.ContainsKey(key);
        }

        public string[] Keys()
        {
            return keys.ToArray();
        }

        internal override void Render(StringBuilder sb)
        {
            sb.Append('{');
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                JsonString.RenderText(sb, keys[i]);
                sb.Append(':');
                map[keys[i]].Render(sb);
            }
            sb.Append('}');
        }

        private JsonValue Get(string key, string path)
        {
            JsonValue value;
            if (!map.TryGetValue(key, out value))
            {
                throw new JsonSaveException(path + ": missing \"" + key + "\"");
            }
            return value;
        }

        public JsonObject GetObject(string key, string path)
        {
            JsonValue value = Get(key, path);
            JsonObject found = value as JsonObject;
            if (found == null)
            {
                throw new JsonSaveException(path + ": \"" + key + "\" is not an object");
            }
            return found;
        }

        public JsonArray GetArray(string key, string path)
        {
            JsonValue value = Get(key, path);
            JsonArray found = value as JsonArray;
            if (found == null)
            {
                throw new JsonSaveException(path + ": \"" + key + "\" is not an array");
            }
            return found;
        }

        public string GetString(string key, string path)
        {
            JsonValue value = Get(key, path);
            JsonString found = value as JsonString;
            if (found == null)
            {
                throw new JsonSaveException(path + ": \"" + key + "\" is not a string");
            }
            return found.Value;
        }

        public bool IsNull(string key, string path)
        {
            return Get(key, path) is JsonNull;
        }

        public long GetLong(string key, string path)
        {
            return ParseInt(GetNumber(key, path), path, key);
        }

        public ulong GetULong(string key, string path)
        {
            string raw = GetNumber(key, path);
            ulong parsed;
            if (!ulong.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out parsed))
            {
                throw new JsonSaveException(path + ": \"" + key + "\" is not a uint64");
            }
            return parsed;
        }

        public int GetInt(string key, string path)
        {
            long parsed = ParseInt(GetNumber(key, path), path, key);
            if (parsed < (long)int.MinValue || parsed > (long)int.MaxValue)
            {
                throw new JsonSaveException(path + ": \"" + key + "\" overflows int32");
            }
            return (int)parsed;
        }

        public uint GetUInt(string key, string path)
        {
            long parsed = ParseInt(GetNumber(key, path), path, key);
            if (parsed < 0L || parsed > (long)uint.MaxValue)
            {
                throw new JsonSaveException(path + ": \"" + key + "\" overflows uint32");
            }
            return (uint)parsed;
        }

        public float GetFloat(string key, string path)
        {
            string raw = GetNumber(key, path);
            float parsed;
            if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                throw new JsonSaveException(path + ": \"" + key + "\" is not a float");
            }
            return parsed;
        }

        public double GetDouble(string key, string path)
        {
            string raw = GetNumber(key, path);
            double parsed;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                throw new JsonSaveException(path + ": \"" + key + "\" is not a double");
            }
            return parsed;
        }

        public bool GetBool(string key, string path)
        {
            JsonValue value = Get(key, path);
            JsonBool found = value as JsonBool;
            if (found == null)
            {
                throw new JsonSaveException(path + ": \"" + key + "\" is not a bool");
            }
            return found.Value;
        }

        private string GetNumber(string key, string path)
        {
            JsonValue value = Get(key, path);
            JsonNumber found = value as JsonNumber;
            if (found == null)
            {
                throw new JsonSaveException(path + ": \"" + key + "\" is not a number");
            }
            return found.Raw;
        }

        private static long ParseInt(string raw, string path, string key)
        {
            long parsed;
            if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                throw new JsonSaveException(path + ": \"" + key + "\" is not an int64");
            }
            return parsed;
        }
    }

    internal sealed class JsonArray : JsonValue
    {
        private readonly List<JsonValue> items = new List<JsonValue>();

        public int Count
        {
            get { return items.Count; }
        }

        public void Add(JsonValue value)
        {
            items.Add(value);
        }

        internal override void Render(StringBuilder sb)
        {
            sb.Append('[');
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                items[i].Render(sb);
            }
            sb.Append(']');
        }

        public JsonObject GetObject(int index, string path)
        {
            JsonObject found = items[index] as JsonObject;
            if (found == null)
            {
                throw new JsonSaveException(path + ": index " + index.ToString() + " is not an object");
            }
            return found;
        }

        public JsonArray GetArray(int index, string path)
        {
            JsonArray found = items[index] as JsonArray;
            if (found == null)
            {
                throw new JsonSaveException(path + ": index " + index.ToString() + " is not an array");
            }
            return found;
        }

        public string GetString(int index, string path)
        {
            JsonString found = items[index] as JsonString;
            if (found == null)
            {
                throw new JsonSaveException(path + ": index " + index.ToString() + " is not a string");
            }
            return found.Value;
        }

        public long GetLong(int index, string path)
        {
            JsonNumber found = items[index] as JsonNumber;
            if (found == null)
            {
                throw new JsonSaveException(path + ": index " + index.ToString() + " is not a number");
            }
            long parsed;
            if (!long.TryParse(found.Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                throw new JsonSaveException(path + ": index " + index.ToString() + " is not an int64");
            }
            return parsed;
        }

        public float GetFloat(int index, string path)
        {
            JsonNumber found = items[index] as JsonNumber;
            if (found == null)
            {
                throw new JsonSaveException(path + ": index " + index.ToString() + " is not a number");
            }
            float parsed;
            if (!float.TryParse(found.Raw, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                throw new JsonSaveException(path + ": index " + index.ToString() + " is not a float");
            }
            return parsed;
        }

        public int GetInt(int index, string path)
        {
            long parsed = GetLong(index, path);
            if (parsed < (long)int.MinValue || parsed > (long)int.MaxValue)
            {
                throw new JsonSaveException(path + ": index " + index.ToString() + " overflows int32");
            }
            return (int)parsed;
        }
    }

    internal sealed class JsonString : JsonValue
    {
        public readonly string Value;

        public JsonString(string value)
        {
            Value = value ?? string.Empty;
        }

        internal override void Render(StringBuilder sb)
        {
            RenderText(sb, Value);
        }

        internal static void RenderText(StringBuilder sb, string text)
        {
            sb.Append('"');
            string safe = text ?? string.Empty;
            for (int i = 0; i < safe.Length; i++)
            {
                char c = safe[i];
                if (c == '"')
                {
                    sb.Append("\\\"");
                }
                else if (c == '\\')
                {
                    sb.Append("\\\\");
                }
                else if (c == '\n')
                {
                    sb.Append("\\n");
                }
                else if (c == '\r')
                {
                    sb.Append("\\r");
                }
                else if (c == '\t')
                {
                    sb.Append("\\t");
                }
                else if (c < 0x20)
                {
                    sb.Append("\\u00");
                    sb.Append(HexChar((c >> 4) & 15));
                    sb.Append(HexChar(c & 15));
                }
                else
                {
                    sb.Append(c);
                }
            }
            sb.Append('"');
        }

        private static char HexChar(int nibble)
        {
            return (char)(nibble < 10 ? ('0' + nibble) : ('a' + nibble - 10));
        }
    }

    internal sealed class JsonNumber : JsonValue
    {
        public readonly string Raw;

        public JsonNumber(string raw)
        {
            Raw = raw ?? "0";
        }

        internal override void Render(StringBuilder sb)
        {
            sb.Append(Raw);
        }
    }

    internal sealed class JsonBool : JsonValue
    {
        public static readonly JsonBool True = new JsonBool(true);
        public static readonly JsonBool False = new JsonBool(false);

        public readonly bool Value;

        private JsonBool(bool value)
        {
            Value = value;
        }

        internal override void Render(StringBuilder sb)
        {
            sb.Append(Value ? "true" : "false");
        }
    }

    internal sealed class JsonNull : JsonValue
    {
        public static readonly JsonNull Instance = new JsonNull();

        private JsonNull()
        {
        }

        internal override void Render(StringBuilder sb)
        {
            sb.Append("null");
        }
    }

    internal sealed class JsonSaveException : Exception
    {
        public JsonSaveException(string message)
            : base(message)
        {
        }
    }

    // Strict small parser: duplicate keys, trailing bytes and over-deep
    // nesting are refused with an offset. Unknown keys are kept in the DOM
    // so settings can report them; the save reader ignores them.
    internal sealed class JsonParser
    {
        private const int MaxDepth = 64;

        private readonly string text;
        private bool samFrame;
        private int index;

        private JsonParser(string text)
        {
            this.text = text;
            index = 0;
        }

        // SAM retains JsonDocument syntax and last-property-wins semantics;
        // saves keep their existing reader defaults.
        public static JsonValue ParseSamFrame(string text)
        {
            return Parse(text, true);
        }

        public static JsonValue Parse(string text)
        {
            return Parse(text, false);
        }

        private static JsonValue Parse(string text, bool samFrame)
        {
            if (text == null)
            {
                throw new JsonSaveException("empty document");
            }
            JsonParser parser = new JsonParser(text);
            parser.samFrame = samFrame;
            JsonValue value = parser.ParseValue(0);
            parser.SkipSpace();
            if (parser.index != parser.text.Length)
            {
                throw new JsonSaveException("trailing bytes at offset "
                    + parser.index.ToString());
            }
            return value;
        }

        private JsonValue ParseValue(int depth)
        {
            if (depth > MaxDepth)
            {
                throw new JsonSaveException("nesting exceeds "
                    + MaxDepth.ToString() + " at offset " + index.ToString());
            }
            SkipSpace();
            if (index >= text.Length)
            {
                throw new JsonSaveException("unexpected end of document");
            }
            char c = text[index];
            if (c == '{')
            {
                return ParseObject(depth);
            }
            if (c == '[')
            {
                return ParseArray(depth);
            }
            if (c == '"')
            {
                return new JsonString(ParseString());
            }
            if (c == 't')
            {
                Expect("true");
                return JsonBool.True;
            }
            if (c == 'f')
            {
                Expect("false");
                return JsonBool.False;
            }
            if (c == 'n')
            {
                Expect("null");
                return JsonNull.Instance;
            }
            return new JsonNumber(ParseNumber());
        }

        private JsonObject ParseObject(int depth)
        {
            index++;
            JsonObject found = new JsonObject();
            SkipSpace();
            if (index < text.Length && text[index] == '}')
            {
                index++;
                return found;
            }
            while (true)
            {
                SkipSpace();
                if (index >= text.Length || text[index] != '"')
                {
                    throw new JsonSaveException("expected a key at offset "
                        + index.ToString());
                }
                string key = ParseString();
                if (!samFrame && found.Has(key))
                {
                    throw new JsonSaveException("duplicate key \"" + key
                        + "\" at offset " + index.ToString());
                }
                SkipSpace();
                if (index >= text.Length || text[index] != ':')
                {
                    throw new JsonSaveException("expected ':' at offset "
                        + index.ToString());
                }
                index++;
                if (samFrame && found.Has(key))
                    found.Set(key, ParseValue(depth + 1));
                else
                    found.Add(key, ParseValue(depth + 1));
                SkipSpace();
                if (index >= text.Length)
                {
                    throw new JsonSaveException("unexpected end of document");
                }
                if (text[index] == '}')
                {
                    index++;
                    return found;
                }
                if (text[index] != ',')
                {
                    throw new JsonSaveException("expected ',' at offset "
                        + index.ToString());
                }
                index++;
            }
        }

        private JsonArray ParseArray(int depth)
        {
            index++;
            JsonArray found = new JsonArray();
            SkipSpace();
            if (index < text.Length && text[index] == ']')
            {
                index++;
                return found;
            }
            while (true)
            {
                found.Add(ParseValue(depth + 1));
                SkipSpace();
                if (index >= text.Length)
                {
                    throw new JsonSaveException("unexpected end of document");
                }
                if (text[index] == ']')
                {
                    index++;
                    return found;
                }
                if (text[index] != ',')
                {
                    throw new JsonSaveException("expected ',' at offset "
                        + index.ToString());
                }
                index++;
            }
        }

        private string ParseString()
        {
            index++;
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                if (index >= text.Length)
                {
                    throw new JsonSaveException("unterminated string");
                }
                char c = text[index];
                index++;
                if (c == '"')
                {
                    return sb.ToString();
                }
                if (c != '\\')
                {
                    if (samFrame && c < 0x20)
                        throw new JsonSaveException("unescaped control at offset " + (index - 1).ToString());
                    sb.Append(c);
                    continue;
                }
                if (index >= text.Length)
                {
                    throw new JsonSaveException("unterminated escape");
                }
                char e = text[index];
                index++;
                if (e == '"')
                {
                    sb.Append('"');
                }
                else if (e == '\\')
                {
                    sb.Append('\\');
                }
                else if (e == '/')
                {
                    sb.Append('/');
                }
                else if (e == 'n')
                {
                    sb.Append('\n');
                }
                else if (e == 'r')
                {
                    sb.Append('\r');
                }
                else if (e == 't')
                {
                    sb.Append('\t');
                }
                else if (e == 'b')
                {
                    sb.Append('\b');
                }
                else if (e == 'f')
                {
                    sb.Append('\f');
                }
                else if (e == 'u')
                {
                    sb.Append(ParseUnicode());
                }
                else
                {
                    throw new JsonSaveException("bad escape \\" + e.ToString());
                }
            }
        }

        private char ParseUnicode()
        {
            if (index + 4 > text.Length)
            {
                throw new JsonSaveException("unterminated unicode escape");
            }
            int code = 0;
            for (int i = 0; i < 4; i++)
            {
                char c = text[index];
                index++;
                code *= 16;
                if (c >= '0' && c <= '9')
                {
                    code += (int)c - (int)'0';
                }
                else if (c >= 'a' && c <= 'f')
                {
                    code += (int)c - (int)'a' + 10;
                }
                else if (c >= 'A' && c <= 'F')
                {
                    code += (int)c - (int)'A' + 10;
                }
                else
                {
                    throw new JsonSaveException("bad unicode escape");
                }
            }
            return (char)code;
        }

        private string ParseNumber()
        {
            int start = index;
            if (index < text.Length && text[index] == '-')
            {
                index++;
            }
            int integerStart = index;
            bool digits = false;
            while (index < text.Length && IsDigit(text[index]))
            {
                index++;
                digits = true;
            }
            if (samFrame && (!digits || (index - integerStart > 1 && text[integerStart] == '0')))
                throw new JsonSaveException("bad number at offset " + start.ToString());
            if (index < text.Length && text[index] == '.')
            {
                index++;
                int fractionStart = index;
                while (index < text.Length && IsDigit(text[index]))
                {
                    index++;
                    digits = true;
                }
                if (samFrame && index == fractionStart)
                    throw new JsonSaveException("bad number at offset " + start.ToString());
            }
            if (index < text.Length && (text[index] == 'e' || text[index] == 'E'))
            {
                index++;
                if (index < text.Length && (text[index] == '+' || text[index] == '-'))
                {
                    index++;
                }
                bool expDigits = false;
                while (index < text.Length && IsDigit(text[index]))
                {
                    index++;
                    expDigits = true;
                }
                if (!expDigits)
                {
                    throw new JsonSaveException("bad number at offset " + start.ToString());
                }
                digits = true;
            }
            if (!digits || start == index)
            {
                throw new JsonSaveException("bad value at offset " + start.ToString());
            }
            return text.Substring(start, index - start);
        }

        private static bool IsDigit(char c)
        {
            return c >= '0' && c <= '9';
        }

        private void Expect(string word)
        {
            if (index + word.Length > text.Length)
            {
                throw new JsonSaveException("bad value at offset " + index.ToString());
            }
            for (int i = 0; i < word.Length; i++)
            {
                if (text[index + i] != word[i])
                {
                    throw new JsonSaveException("bad value at offset " + index.ToString());
                }
            }
            index += word.Length;
        }

        private void SkipSpace()
        {
            while (index < text.Length)
            {
                char c = text[index];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r')
                {
                    index++;
                }
                else
                {
                    return;
                }
            }
        }
    }

    public static class SaveV1
    {
        public const int SettingsSchemaVersion = 1;
        public const string SettingsFileName = "settings.json";
        public const string SettingsTempName = "settings.tmp";
        public const string SettingsBackupName = "settings.bak";

        private const StateWriter Writer = StateWriter.SimCore;

        // SaveGame captures the world snapshot plus clock controls, accepted
        // queues, in-progress executions, holds, appearances and automation,
        // then writes one atomic document. It never mutates the stack: every
        // read below is a snapshot, a clone or a read-only view.
        public static SaveWriteResult SaveGame(GameStack stack, string directory)
        {
            return SaveGame(stack, directory, new SaveFileStore());
        }

        public static SaveWriteResult SaveGame(GameStack stack, string directory,
            ISaveFileStore store)
        {
            if (stack == null)
            {
                throw new ArgumentNullException("stack");
            }
            if (directory == null || directory.Length == 0)
            {
                throw new ArgumentNullException("directory");
            }
            if (store == null)
            {
                throw new ArgumentNullException("store");
            }
            WorldSnapshot snapshot;
            JsonObject payload;
            try
            {
                snapshot = stack.World.ExportSnapshot();
                payload = SerializePayload(stack, snapshot);
            }
            catch (Exception e)
            {
                return new SaveWriteResult(false, "capture failed: " + e.Message);
            }
            byte[] bytes = AssembleDocument(payload, SaveFormat.SchemaVersion,
                snapshot.WorldId, snapshot.Epoch, snapshot.GameSeconds);
            return AtomicWrite(store, directory, SaveFormat.FileName,
                SaveFormat.TempName, SaveFormat.BackupName, bytes);
        }

        // LoadGame validates save.json (parse, hash, version, header/payload
        // cross-check) and rebuilds a fresh stack: world with epoch plus one,
        // appearances, clock controls, replayed player queues, re-dispatched
        // in-progress actions and automation. Any failure returns a notice
        // and whether save.bak exists; nothing half-built escapes.
        public static SaveLoadResult LoadGame(string directory, ISceneContent scene)
        {
            return LoadGame(directory, scene, new SaveFileStore());
        }

        public static SaveLoadResult LoadGame(string directory, ISceneContent scene,
            ISaveFileStore store)
        {
            if (directory == null || directory.Length == 0)
            {
                throw new ArgumentNullException("directory");
            }
            if (scene == null)
            {
                throw new ArgumentNullException("scene");
            }
            if (store == null)
            {
                throw new ArgumentNullException("store");
            }
            return LoadFrom(store, Path.Combine(directory, SaveFormat.FileName),
                Path.Combine(directory, SaveFormat.BackupName), scene, false);
        }

        // Recovery path: load the retained backup instead of the current
        // file. Used after LoadGame reports BackupAvailable.
        public static SaveLoadResult LoadBackupGame(string directory, ISceneContent scene)
        {
            return LoadBackupGame(directory, scene, new SaveFileStore());
        }

        public static SaveLoadResult LoadBackupGame(string directory, ISceneContent scene,
            ISaveFileStore store)
        {
            if (directory == null || directory.Length == 0)
            {
                throw new ArgumentNullException("directory");
            }
            if (scene == null)
            {
                throw new ArgumentNullException("scene");
            }
            if (store == null)
            {
                throw new ArgumentNullException("store");
            }
            return LoadFrom(store, Path.Combine(directory, SaveFormat.BackupName),
                Path.Combine(directory, SaveFormat.BackupName), scene, true);
        }

        // Settings live in a separately versioned local record: they survive
        // process restart and world load, and touching them never mutates a
        // paused world (a different file, no world access at all).
        public static SaveWriteResult SaveSettings(SettingsRecord settings, string directory)
        {
            return SaveSettings(settings, directory, new SaveFileStore());
        }

        public static SaveWriteResult SaveSettings(SettingsRecord settings, string directory,
            ISaveFileStore store)
        {
            if (directory == null || directory.Length == 0)
            {
                throw new ArgumentNullException("directory");
            }
            if (store == null)
            {
                throw new ArgumentNullException("store");
            }
            if (float.IsNaN(settings.MixerLevel) || float.IsInfinity(settings.MixerLevel)
                || float.IsNaN(settings.TextScale) || float.IsInfinity(settings.TextScale)
                || float.IsNaN(settings.CursorSize) || float.IsInfinity(settings.CursorSize)
                || float.IsNaN(settings.CameraPanSpeed) || float.IsInfinity(settings.CameraPanSpeed)
                || float.IsNaN(settings.CameraZoomSpeed) || float.IsInfinity(settings.CameraZoomSpeed)
                || float.IsNaN(settings.CameraRotateSpeed) || float.IsInfinity(settings.CameraRotateSpeed))
                return new SaveWriteResult(false, "Settings must be finite.");
            if (!IsMixerLevel(settings.MixerLevel) || !IsMixerLevel(settings.AmbienceLevel)
                || !IsMixerLevel(settings.WorldLevel) || !IsMixerLevel(settings.VoiceLevel)
                || !IsMixerLevel(settings.InterfaceLevel))
                return new SaveWriteResult(false, "Settings mixer levels must be within [0, 1].");
            if (settings.TextScale < 1f || settings.TextScale > 1.5f)
                return new SaveWriteResult(false, "Settings text scale must be within [1, 1.5].");
            if (!IsValidRemaps(settings.InputRemaps))
                return new SaveWriteResult(false, "Settings input remaps are unknown, blank or conflicting.");
            JsonObject payload = new JsonObject();
            payload.Add("mixerLevel", settings.MixerLevel);
            payload.Add("textScale", settings.TextScale);
            payload.Add("reducedMotion", settings.ReducedMotion);
            payload.Add("ambienceLevel", settings.AmbienceLevel);
            payload.Add("worldLevel", settings.WorldLevel);
            payload.Add("voiceLevel", settings.VoiceLevel);
            payload.Add("interfaceLevel", settings.InterfaceLevel);
            payload.Add("nonColorCues", settings.NonColorCues);
            payload.Add("captions", settings.Captions);
            payload.Add("cursorSize", settings.CursorSize);
            payload.Add("cameraPanSpeed", settings.CameraPanSpeed);
            payload.Add("cameraZoomSpeed", settings.CameraZoomSpeed);
            payload.Add("cameraRotateSpeed", settings.CameraRotateSpeed);
            payload.Add("highContrastCursor", settings.HighContrastCursor);
            payload.Add("edgeScroll", settings.EdgeScroll);
            payload.Add("invertX", settings.InvertX);
            payload.Add("invertY", settings.InvertY);
            payload.Add("snapSelection", settings.SnapSelection);
            payload.Add("narrationEnabled", settings.NarrationEnabled);
            payload.Add("toggleControls", settings.ToggleControls);
            JsonObject remapObject = new JsonObject();
            if (settings.InputRemaps != null)
            {
                // Sorted keys: Dictionary order follows per-run string hashes.
                List<string> actions = new List<string>(settings.InputRemaps.Keys);
                actions.Sort(StringComparer.Ordinal);
                for (int i = 0; i < actions.Count; i++)
                {
                    remapObject.Add(actions[i], settings.InputRemaps[actions[i]]);
                }
            }
            payload.Add("inputRemaps", remapObject);
            byte[] bytes = AssembleSettingsDocument(payload);
            return AtomicWrite(store, directory, SettingsFileName,
                SettingsTempName, SettingsBackupName, bytes);
        }

        // Unknown settings fields are preserved in the report, never silently
        // dropped: the caller sees exactly what this version did not apply.
        public static SettingsLoadResult LoadSettings(string directory)
        {
            return LoadSettings(directory, new SaveFileStore());
        }

        public static SettingsLoadResult LoadSettings(string directory, ISaveFileStore store)
        {
            if (directory == null || directory.Length == 0)
            {
                throw new ArgumentNullException("directory");
            }
            if (store == null)
            {
                throw new ArgumentNullException("store");
            }
            string path = Path.Combine(directory, SettingsFileName);
            if (!store.Exists(path))
            {
                return new SettingsLoadResult(false, default(SettingsRecord),
                    new string[0], "no settings file at " + path);
            }
            byte[] bytes;
            try
            {
                bytes = store.ReadAllBytes(path);
            }
            catch (Exception e)
            {
                return new SettingsLoadResult(false, default(SettingsRecord),
                    new string[0], "settings read failed: " + e.Message);
            }
            JsonObject document;
            try
            {
                document = ParseDocument(bytes);
            }
            catch (JsonSaveException e)
            {
                return new SettingsLoadResult(false, default(SettingsRecord),
                    new string[0], "settings parse failed: " + e.Message);
            }
            try
            {
            JsonObject header = document.GetObject("header", "settings");
            JsonObject payload = document.GetObject("payload", "settings");
            if (!HashMatches(payload, header.GetString("sha256", "settings.header")))
            {
                return new SettingsLoadResult(false, default(SettingsRecord),
                    new string[0], "settings hash mismatch");
            }
            int version = header.GetInt("schemaVersion", "settings.header");
            if (version != SettingsSchemaVersion)
            {
                return new SettingsLoadResult(false, default(SettingsRecord),
                    new string[0], "unsupported settings schema version "
                    + version.ToString());
            }
            List<string> unknown = new List<string>();
            string[] keys = payload.Keys();
            for (int i = 0; i < keys.Length; i++)
            {
                if (!keys[i].Equals("mixerLevel") && !keys[i].Equals("textScale")
                    && !keys[i].Equals("reducedMotion") && !keys[i].Equals("ambienceLevel")
                    && !keys[i].Equals("worldLevel") && !keys[i].Equals("voiceLevel")
                    && !keys[i].Equals("interfaceLevel") && !keys[i].Equals("nonColorCues")
                    && !keys[i].Equals("captions") && !keys[i].Equals("inputRemaps")
                    && !keys[i].Equals("cursorSize") && !keys[i].Equals("cameraPanSpeed")
                    && !keys[i].Equals("cameraZoomSpeed") && !keys[i].Equals("cameraRotateSpeed")
                    && !keys[i].Equals("highContrastCursor") && !keys[i].Equals("edgeScroll")
                    && !keys[i].Equals("invertX") && !keys[i].Equals("invertY")
                    && !keys[i].Equals("snapSelection") && !keys[i].Equals("narrationEnabled")
                    && !keys[i].Equals("toggleControls"))
                {
                    unknown.Add(keys[i]);
                }
            }
            SettingsRecord settings;
            try
            {
                float mixerLevel = payload.GetFloat("mixerLevel", "settings.payload");
                settings = new SettingsRecord(SettingsSchemaVersion,
                    mixerLevel,
                    payload.GetFloat("textScale", "settings.payload"),
                    payload.GetBool("reducedMotion", "settings.payload"),
                    LevelOr(payload, "ambienceLevel", mixerLevel),
                    LevelOr(payload, "worldLevel", mixerLevel),
                    LevelOr(payload, "voiceLevel", mixerLevel),
                    LevelOr(payload, "interfaceLevel", mixerLevel),
                    BoolOr(payload, "nonColorCues", true),
                    BoolOr(payload, "captions", true),
                    RemapsOr(payload),
                    FloatOr(payload, "cursorSize", 1f),
                    FloatOr(payload, "cameraPanSpeed", 1f),
                    FloatOr(payload, "cameraZoomSpeed", 1f),
                    FloatOr(payload, "cameraRotateSpeed", 1f),
                    BoolOr(payload, "highContrastCursor", false),
                    BoolOr(payload, "edgeScroll", false),
                    BoolOr(payload, "invertX", false),
                    BoolOr(payload, "invertY", false),
                    BoolOr(payload, "snapSelection", false),
                    BoolOr(payload, "narrationEnabled", false),
                    BoolOr(payload, "toggleControls", false));
                // A persisted level outside the domain SaveSettings writes
                // must not become successfully loaded settings.
                if (!IsMixerLevel(settings.MixerLevel) || !IsMixerLevel(settings.AmbienceLevel)
                    || !IsMixerLevel(settings.WorldLevel) || !IsMixerLevel(settings.VoiceLevel)
                    || !IsMixerLevel(settings.InterfaceLevel))
                {
                    return new SettingsLoadResult(false, default(SettingsRecord),
                        new string[0], "settings mixer levels must be within [0, 1].");
                }
            }
            catch (JsonSaveException e)
            {
                return new SettingsLoadResult(false, default(SettingsRecord),
                    new string[0], "settings fields invalid: " + e.Message);
            }
            return new SettingsLoadResult(true, settings, unknown.ToArray(),
                unknown.Count == 0 ? string.Empty : "unsupported fields reported");
            }
            catch (JsonSaveException e)
            {
                return new SettingsLoadResult(false, default(SettingsRecord), new string[0],
                    "settings document invalid: " + e.Message);
            }
        }

        // Linear mixer levels live in [0, 1]; NaN fails both comparisons.
        private static bool IsMixerLevel(float value)
        {
            return value >= 0f && value <= 1f;
        }

        // Legacy v1 payloads predate the group keys: each missing level
        // inherits the master mixer level. Present keys still parse strictly.
        private static float LevelOr(JsonObject payload, string key, float fallback)
        {
            if (!payload.Has(key))
            {
                return fallback;
            }
            return payload.GetFloat(key, "settings.payload");
        }

        // Missing INPUT-ACCESS speeds default to neutral, matching the constructors.
        private static float FloatOr(JsonObject payload, string key, float fallback)
        {
            if (!payload.Has(key))
            {
                return fallback;
            }
            return payload.GetFloat(key, "settings.payload");
        }

        // Missing ACCESS flags default to enabled, matching the constructors.
        private static bool BoolOr(JsonObject payload, string key, bool fallback)
        {
            if (!payload.Has(key))
            {
                return fallback;
            }
            return payload.GetBool(key, "settings.payload");
        }

        // Missing inputRemaps means no overrides; a present key parses strictly.
        private static IReadOnlyDictionary<string, string> RemapsOr(JsonObject payload)
        {
            Dictionary<string, string> remaps = new Dictionary<string, string>();
            if (!payload.Has("inputRemaps"))
            {
                return remaps;
            }
            JsonObject stored = payload.GetObject("inputRemaps", "settings.payload");
            string[] actions = stored.Keys();
            for (int i = 0; i < actions.Length; i++)
            {
                remaps[actions[i]] = stored.GetString(actions[i], "settings.payload.inputRemaps");
            }
            return remaps;
        }

        // Remaps override the InputActions defaults: every entry names a
        // known action with one non-chord key, and no two effective bindings
        // share a key. Mirrors Accessibility so disk never holds a conflict.
        private static bool IsValidRemaps(IReadOnlyDictionary<string, string> remaps)
        {
            if (remaps == null)
            {
                return true;
            }
            foreach (KeyValuePair<string, string> pair in remaps)
            {
                if (!InputActions.IsKnown(pair.Key)
                    || string.IsNullOrWhiteSpace(pair.Value)
                    || pair.Value.IndexOf('+') >= 0 || pair.Value.IndexOf(',') >= 0)
                {
                    return false;
                }
            }
            var used = new HashSet<string>();
            for (int i = 0; i < InputActions.Names.Length; i++)
            {
                string action = InputActions.Names[i];
                string key;
                if (!remaps.TryGetValue(action, out key)
                    || string.IsNullOrWhiteSpace(key))
                {
                    key = InputActions.DefaultBinding(action);
                }
                if (!used.Add(key))
                {
                    return false;
                }
            }
            return true;
        }

        // tmp, flush, reparse plus hash validation, then replace with backup
        // retention. Every failure leaves the previous dest intact: tmp is
        // the only file a failed save may leave behind, and loads never read
        // it. Returns Ok with the written byte count in Detail.
        private static SaveWriteResult AtomicWrite(ISaveFileStore store, string directory,
            string fileName, string tempName, string backupName, byte[] bytes)
        {
            string destPath = Path.Combine(directory, fileName);
            string tempPath = Path.Combine(directory, tempName);
            string backupPath = Path.Combine(directory, backupName);
            try
            {
                store.EnsureDirectory(directory);
            }
            catch (Exception e)
            {
                return new SaveWriteResult(false, "directory failed: " + e.Message);
            }
            try
            {
                store.WriteTemp(tempPath, bytes);
            }
            catch (Exception e)
            {
                return new SaveWriteResult(false, "temp write failed: " + e.Message);
            }
            try
            {
                byte[] written = store.ReadAllBytes(tempPath);
                ValidateDocumentBytes(written);
            }
            catch (Exception e)
            {
                return new SaveWriteResult(false, "temp validation failed: " + e.Message);
            }
            try
            {
                store.Replace(tempPath, destPath, backupPath);
            }
            catch (Exception e)
            {
                return new SaveWriteResult(false, "replace failed: " + e.Message);
            }
            return new SaveWriteResult(true, fileName + " " + bytes.Length.ToString() + " bytes");
        }

        internal static byte[] AssembleDocument(JsonObject payload, int schemaVersion,
            long worldId, uint epoch, long savedAtGameSeconds)
        {
            string payloadText = RenderJson(payload);
            JsonObject header = new JsonObject();
            header.Add("schemaVersion", schemaVersion);
            header.Add("worldId", worldId);
            header.Add("worldEpoch", epoch);
            header.Add("savedAtGameSeconds", savedAtGameSeconds);
            header.Add("sha256", Sha256Hex(Encoding.UTF8.GetBytes(payloadText)));
            JsonObject document = new JsonObject();
            document.Add("header", header);
            document.Add("payload", payload);
            return Encoding.UTF8.GetBytes(RenderJson(document));
        }

        internal static byte[] AssembleSettingsDocument(JsonObject payload)
        {
            string payloadText = RenderJson(payload);
            JsonObject header = new JsonObject();
            header.Add("schemaVersion", SettingsSchemaVersion);
            header.Add("sha256", Sha256Hex(Encoding.UTF8.GetBytes(payloadText)));
            JsonObject document = new JsonObject();
            document.Add("header", header);
            document.Add("payload", payload);
            return Encoding.UTF8.GetBytes(RenderJson(document));
        }

        internal static string RenderJson(JsonValue value)
        {
            StringBuilder sb = new StringBuilder();
            value.Render(sb);
            return sb.ToString();
        }

        internal static string Sha256Hex(byte[] bytes)
        {
            using (SHA256 hash = SHA256.Create())
            {
                byte[] digest = hash.ComputeHash(bytes);
                StringBuilder sb = new StringBuilder(digest.Length * 2);
                for (int i = 0; i < digest.Length; i++)
                {
                    sb.Append(HexChar((digest[i] >> 4) & 15));
                    sb.Append(HexChar(digest[i] & 15));
                }
                return sb.ToString();
            }
        }

        private static char HexChar(int nibble)
        {
            return (char)(nibble < 10 ? ('0' + nibble) : ('a' + nibble - 10));
        }

        private static void ValidateDocumentBytes(byte[] bytes)
        {
            ParseDocument(bytes);
        }

        internal static JsonObject ParseDocument(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                throw new JsonSaveException("empty file");
            }
            string text;
            try
            {
                text = Encoding.UTF8.GetString(bytes);
            }
            catch (Exception e)
            {
                throw new JsonSaveException("not UTF-8: " + e.Message);
            }
            JsonValue root = JsonParser.Parse(text);
            JsonObject document = root as JsonObject;
            if (document == null)
            {
                throw new JsonSaveException("document is not an object");
            }
            return document;
        }

        private static bool HashMatches(JsonObject payload, string expected)
        {
            string hex = Sha256Hex(Encoding.UTF8.GetBytes(RenderJson(payload)));
            return string.Equals(hex, expected, StringComparison.Ordinal);
        }

        // Every datum is written exactly once: per-resident groups nest under
        // their resident, holds and waits sit in the objects section keyed by
        // action, and id-keyed lists are sorted so identical state renders
        // identical bytes.
        internal static JsonObject SerializePayload(GameStack stack, WorldSnapshot snapshot)
        {
            JsonObject payload = new JsonObject();
            payload.Add("world", SerializeWorld(snapshot));
            JsonObject clock = new JsonObject();
            clock.Add("speed", (int)stack.Clock.Speed);
            clock.Add("paused", stack.Clock.Paused);
            clock.Add("preset", (int)stack.Clock.Preset);
            clock.Add("tickRemainder", stack.Clock.TickRemainder);
            clock.Add("totalTicks", stack.Clock.TotalTicks);
            payload.Add("clock", clock);
            payload.Add("residents", SerializeResidents(snapshot));
            payload.Add("households", SerializeHouseholds(snapshot));
            payload.Add("homes", SerializeHomes(snapshot));
            payload.Add("events", SerializeEvents(snapshot));
            payload.Add("effectLedger", SerializeEffectLedger(snapshot));
            payload.Add("estateLedger", SerializeEstateLedger(snapshot));
            payload.Add("care", SerializeCare(snapshot));
            payload.Add("objects", SerializeObjects(stack));
            payload.Add("actions", SerializeActions(stack));
            payload.Add("automation", stack.Arbitrator.AutomationEnabled);
            payload.Add("appearances", SerializeAppearances(stack));
            if (stack.Accounts != null)
            {
                payload.Add("accounts", SerializeAccounts(stack.Accounts));
            }
            if (stack.Food != null)
            {
                payload.Add("food", SerializeFood(stack.Food));
            }
            if (stack.Expenses != null)
            {
                payload.Add("expenses", SerializeExpenses(stack.Expenses));
            }
            if (stack.Ownership != null)
            {
                payload.Add("ownership", SerializeOwnership(stack.Ownership));
            }
            if (stack.Home != null)
            {
                payload.Add("home", SerializeHome(stack.Home));
            }
            if (stack.Club != null && stack.Club.HasSaveState())
            {
                payload.Add("club", SerializeClub(stack.Club));
            }
            if (stack.Housing != null)
            {
                payload.Add("housing", SerializeHousing(stack.Housing));
            }
            if (stack.Creative != null && stack.Creative.HasSaveState())
            {
                payload.Add("creative", SerializeCreative(stack.Creative));
            }
            if (stack.Executor.Bicycles != null) payload.Add("bicycles", BicycleSave.Write(stack.Executor.Bicycles));
            if (stack.Executor.MinigolfService != null)
            {
                payload.Add("minigolf", SerializeMinigolf(stack.Executor.MinigolfService));
            }
            if (stack.Image != null && stack.Image.HasSaveState())
            {
                payload.Add("images", SerializeImages(stack.Image));
            }
            if (stack.Family != null && stack.Family.HasSaveState())
            {
                payload.Add("family", SerializeFamily(stack.Family));
            }
            if (stack.Party != null && stack.Party.HasSaveState())
            {
                payload.Add("party", SerializeParty(stack.Party));
            }
            if (snapshot.Storm.HasState())
            {
                payload.Add("storm", SerializeStorm(snapshot.Storm));
            }
            JsonArray aging = new JsonArray();
            List<ResidentRecord> agingResidents = new List<ResidentRecord>(snapshot.Residents);
            SortResidents(agingResidents);
            foreach (ResidentRecord resident in agingResidents)
            {
                if (!resident.HasAgingProgress) continue;
                JsonObject row = new JsonObject();
                row.Add("resident", (long)resident.Id);
                row.Add("progress", resident.AgingProgressDays);
                row.Add("remainder", resident.ElderRemainderDays);
                row.Add("frail", resident.IsFrail);
                aging.Add(row);
            }
            if (aging.Count > 0) payload.Add("aging", aging);
            JsonObject estate = SerializeEstate(snapshot);
            if (estate.GetArray("careRequired", "payload.estate").Count > 0)
            {
                payload.Add("estate", estate);
            }
            return payload;
        }

        // ESTATE persists the CareRequired elder flag per resident. Town title
        // shares stay in the existing ownership agreement section, so this
        // section is omitted when empty and older saves keep their shape.
        private static JsonObject SerializeEstate(WorldSnapshot snapshot)
        {
            JsonObject estate = new JsonObject();
            estate.Add("version", 1);
            JsonArray careRequired = new JsonArray();
            List<ResidentRecord> rows = new List<ResidentRecord>(snapshot.Residents);
            SortResidents(rows);
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].CareRequired)
                {
                    careRequired.Add(new JsonNumber(((long)rows[i].Id).ToString()));
                }
            }
            estate.Add("careRequired", careRequired);
            return estate;
        }

        private static JsonObject SerializeWorld(WorldSnapshot snapshot)
        {
            JsonObject world = new JsonObject();
            world.Add("schemaVersion", snapshot.SchemaVersion);
            world.Add("worldId", snapshot.WorldId);
            world.Add("epoch", snapshot.Epoch);
            world.Add("seed", snapshot.Seed);
            world.Add("rngState", snapshot.RngState);
            world.Add("relationshipRngState", snapshot.RelationshipRngState);
            world.Add("nextRelationshipDraw", snapshot.NextRelationshipDraw);
            JsonArray idle = new JsonArray();
            List<ResidentId> ids = new List<ResidentId>(snapshot.TierIdleSince.Keys);
            for (int i = 1; i < ids.Count; i++)
            {
                ResidentId id = ids[i]; int j = i - 1;
                while (j >= 0 && (long)ids[j] > (long)id) { ids[j + 1] = ids[j]; j--; }
                ids[j + 1] = id;
            }
            foreach (ResidentId id in ids)
            {
                JsonObject row = new JsonObject();
                row.Add("resident", (long)id); row.Add("since", snapshot.TierIdleSince[id]); idle.Add(row);
            }
            world.Add("tierIdleSince", idle);
            world.Add("gameSeconds", snapshot.GameSeconds);
            world.Add("aging", (int)snapshot.Aging);
            world.Add("townFund", snapshot.TownFund);
            world.Add("nextDecisionSequence", snapshot.NextDecisionSequence);
            world.Add("nextResidentId", snapshot.NextResidentId);
            world.Add("nextHouseholdId", snapshot.NextHouseholdId);
            world.Add("nextHomeId", snapshot.NextHomeId);
            world.Add("nextObjectId", snapshot.NextObjectId);
            world.Add("nextActionId", snapshot.NextActionId);
            world.Add("nextEventId", snapshot.NextEventId);
            world.Add("nextCommandId", snapshot.NextCommandId);
            world.Add("nextGatewayId", snapshot.NextGatewayId);
            return world;
        }

        private static JsonArray SerializeResidents(WorldSnapshot snapshot)
        {
            List<ResidentRecord> rows = new List<ResidentRecord>(snapshot.Residents);
            SortResidents(rows);
            JsonArray array = new JsonArray();
            for (int i = 0; i < rows.Count; i++)
            {
                array.Add(SerializeResident(rows[i]));
            }
            return array;
        }

        private static void SortResidents(List<ResidentRecord> rows)
        {
            for (int i = 1; i < rows.Count; i++)
            {
                ResidentRecord key = rows[i];
                int j = i - 1;
                while (j >= 0 && (long)rows[j].Id > (long)key.Id)
                {
                    rows[j + 1] = rows[j];
                    j--;
                }
                rows[j + 1] = key;
            }
        }

        private static JsonObject SerializeResident(ResidentRecord record)
        {
            JsonObject resident = new JsonObject();
            resident.Add("id", (long)record.Id);
            resident.Add("name", record.DisplayName);
            resident.Add("stage", (int)record.Stage);
            resident.Add("stageProgress", record.StageProgressDays);
            resident.Add("household", (long)record.Household);
            resident.Add("home", (long)record.Home);
            resident.Add("money", record.Money);
            resident.Add("jobId", record.JobId);
            resident.Add("careerLevel", record.CareerLevel);
            resident.Add("careerPoints", record.CareerPoints);
            resident.Add("educationPoints", record.EducationPoints);
            resident.Add("alive", record.IsAlive);
            resident.Add("residentSequence", record.ResidentSequence);
            resident.Add("decisionSlot", record.DecisionSlotGeneration);
            resident.Add("controlGen", record.ControlGeneration);
            resident.Add("starvationHours", record.StarvationHours);
            resident.Add("ill", record.Ill);
            resident.Add("fatigued", record.Fatigued);
            resident.Add("injured", record.Injured);
            resident.Add("lastChanceDrawDay", record.LastChanceDrawDay);
            JsonArray needs = new JsonArray();
            for (int i = 0; i < StateSchema.NeedCount; i++)
            {
                needs.Add(new JsonNumber(record.GetNeed((Need)i).ToString("R",
                    CultureInfo.InvariantCulture)));
            }
            resident.Add("needs", needs);
            JsonArray traits = new JsonArray();
            for (int i = 0; i < StateSchema.TraitCount; i++)
            {
                traits.Add(new JsonNumber(record.GetTrait((TraitAxis)i).ToString()));
            }
            resident.Add("traits", traits);
            JsonArray skills = new JsonArray();
            for (int i = 0; i < record.Skills.Count; i++)
            {
                JsonObject skill = new JsonObject();
                skill.Add("id", record.Skills[i].SkillId);
                skill.Add("level", record.Skills[i].Level);
                skill.Add("points", record.Skills[i].Points);
                skills.Add(skill);
            }
            resident.Add("skills", skills);
            JsonArray bonds = new JsonArray();
            for (int i = 0; i < record.Bonds.Count; i++)
            {
                JsonObject bond = new JsonObject();
                bond.Add("other", (long)record.Bonds[i].Other);
                bond.Add("friendship", record.Bonds[i].Friendship);
                bond.Add("romance", record.Bonds[i].Romance);
                bonds.Add(bond);
            }
            resident.Add("bonds", bonds);
            JsonArray family = new JsonArray();
            for (int i = 0; i < record.Family.Count; i++)
            {
                JsonObject link = new JsonObject();
                link.Add("other", (long)record.Family[i].Other);
                link.Add("kind", record.Family[i].Kind);
                family.Add(link);
            }
            resident.Add("family", family);
            JsonArray feelings = new JsonArray();
            for (int i = 0; i < record.Feelings.Count; i++)
            {
                JsonObject feeling = new JsonObject();
                feeling.Add("kind", (int)record.Feelings[i].Kind);
                feeling.Add("event", (long)record.Feelings[i].SourceEvent);
                feeling.Add("other", (long)record.Feelings[i].Other);
                feeling.Add("intensity", record.Feelings[i].Intensity);
                feeling.Add("expiry", record.Feelings[i].ExpiryGameSeconds);
                feelings.Add(feeling);
            }
            resident.Add("feelings", feelings);
            JsonArray memories = new JsonArray();
            for (int i = 0; i < record.Memories.Count; i++)
            {
                MemoryEntry memory = record.Memories[i];
                JsonObject row = new JsonObject();
                row.Add("event", (long)memory.Event);
                row.Add("kind", memory.Kind);
                row.Add("gameSeconds", memory.GameSeconds);
                row.Add("place", (long)memory.Place);
                JsonArray participants = new JsonArray();
                for (int p = 0; p < memory.Participants.Count; p++)
                {
                    participants.Add(new JsonNumber(((long)memory.Participants[p]).ToString()));
                }
                row.Add("participants", participants);
                row.Add("outcome", memory.OutcomeCode);
                row.Add("learnedBy", memory.LearnedBy);
                memories.Add(row);
            }
            resident.Add("memories", memories);
            JsonArray courses = new JsonArray();
            for (int i = 0; i < record.CompletedCourses.Count; i++)
            {
                courses.Add(new JsonString(record.CompletedCourses[i]));
            }
            resident.Add("courses", courses);
            return resident;
        }

        private static JsonArray SerializeHouseholds(WorldSnapshot snapshot)
        {
            List<long> ids = new List<long>();
            for (int i = 0; i < snapshot.Households.Count; i++)
            {
                ids.Add((long)snapshot.Households[i].Id);
            }
            ids.Sort();
            JsonArray array = new JsonArray();
            for (int i = 0; i < ids.Count; i++)
            {
                HouseholdRecord found = null;
                for (int h = 0; h < snapshot.Households.Count; h++)
                {
                    if ((long)snapshot.Households[h].Id == ids[i])
                    {
                        found = snapshot.Households[h];
                    }
                }
                JsonObject row = new JsonObject();
                row.Add("id", (long)found.Id);
                row.Add("name", found.Name);
                row.Add("home", (long)found.Home);
                array.Add(row);
            }
            return array;
        }

        private static JsonArray SerializeHomes(WorldSnapshot snapshot)
        {
            List<long> ids = new List<long>();
            for (int i = 0; i < snapshot.Homes.Count; i++)
            {
                ids.Add((long)snapshot.Homes[i].Id);
            }
            ids.Sort();
            JsonArray array = new JsonArray();
            for (int i = 0; i < ids.Count; i++)
            {
                HomeRecord found = null;
                for (int h = 0; h < snapshot.Homes.Count; h++)
                {
                    if ((long)snapshot.Homes[h].Id == ids[i])
                    {
                        found = snapshot.Homes[h];
                    }
                }
                JsonObject row = new JsonObject();
                row.Add("id", (long)found.Id);
                row.Add("beds", found.BedCount);
                array.Add(row);
            }
            return array;
        }

        private static JsonArray SerializeEvents(WorldSnapshot snapshot)
        {
            JsonArray array = new JsonArray();
            for (int i = 0; i < snapshot.Events.Count; i++)
            {
                WorldEvent row = snapshot.Events[i];
                JsonObject entry = new JsonObject();
                entry.Add("event", (long)row.Event);
                entry.Add("gameSeconds", row.GameSeconds);
                entry.Add("kind", row.Kind);
                if (row.Actor.HasValue)
                {
                    entry.Add("actor", (long)row.Actor.Value);
                }
                else
                {
                    entry.AddNull("actor");
                }
                entry.Add("detail", row.Detail);
                array.Add(entry);
            }
            return array;
        }

        private static JsonArray SerializeEffectLedger(WorldSnapshot snapshot)
        {
            JsonArray array = new JsonArray();
            for (int i = 0; i < snapshot.EffectLedger.Count; i++)
            {
                EffectLedgerEntry row = snapshot.EffectLedger[i];
                JsonObject entry = new JsonObject();
                entry.Add("action", (long)row.Action);
                entry.Add("kind", row.EffectKind);
                entry.Add("first", (long)row.Recipient.First);
                if (row.Recipient.Second.HasValue)
                {
                    entry.Add("second", (long)row.Recipient.Second.Value);
                }
                else
                {
                    entry.AddNull("second");
                }
                entry.Add("state", (int)row.State);
                array.Add(entry);
            }
            return array;
        }

        private static JsonArray SerializeEstateLedger(WorldSnapshot snapshot)
        {
            JsonArray array = new JsonArray();
            for (int i = 0; i < snapshot.EstateLedger.Count; i++)
            {
                EstateLedgerEntry row = snapshot.EstateLedger[i];
                JsonObject entry = new JsonObject();
                entry.Add("death", (long)row.DeathEvent);
                entry.Add("asset", row.AssetRef);
                entry.Add("recipient", row.Recipient);
                entry.Add("settled", row.Settled);
                array.Add(entry);
            }
            return array;
        }

        private static JsonArray SerializeCare(WorldSnapshot snapshot)
        {
            JsonArray array = new JsonArray();
            for (int i = 0; i < snapshot.CareAssignments.Count; i++)
            {
                CareAssignment row = snapshot.CareAssignments[i];
                JsonObject entry = new JsonObject();
                entry.Add("dependent", (long)row.Dependent);
                entry.Add("guardian", (long)row.Guardian);
                if (row.Placement.Household.HasValue)
                {
                    entry.Add("household", (long)row.Placement.Household.Value);
                }
                else
                {
                    entry.AddNull("household");
                }
                entry.Add("facility", row.Placement.FacilityPlace);
                entry.Add("transfer", (long)row.TransferEvent);
                array.Add(entry);
            }
            return array;
        }

        // Holds and busy-wait places for every in-flight execution, keyed by
        // action. Dispatch re-acquires them on load; this section evidences
        // the reconciliation and orders it (holders before waiters).
        private static JsonObject SerializeObjects(GameStack stack)
        {
            List<ExecutionRecord> live = LiveExecutions(stack);
            JsonArray holds = new JsonArray();
            JsonArray waits = new JsonArray();
            for (int i = 0; i < live.Count; i++)
            {
                SlotHold[] held = live[i].Holds;
                for (int h = 0; h < held.Length; h++)
                {
                    JsonObject row = new JsonObject();
                    row.Add("object", (long)held[h].Object);
                    row.Add("slot", held[h].SlotIndex);
                    row.Add("x", held[h].Pose.X);
                    row.Add("y", held[h].Pose.Y);
                    row.Add("z", held[h].Pose.Z);
                    row.Add("holder", (long)held[h].Holder);
                    row.Add("action", (long)held[h].Action);
                    holds.Add(row);
                }
                if (live[i].Queued)
                {
                    JsonObject wait = new JsonObject();
                    wait.Add("action", (long)live[i].Action);
                    wait.Add("object", live[i].TargetId.Value);
                    wait.Add("position", live[i].QueuePosition);
                    wait.Add("enqueue", live[i].EnqueueGameSeconds);
                    waits.Add(wait);
                }
            }
            JsonObject objects = new JsonObject();
            objects.Add("holds", holds);
            objects.Add("waits", waits);
            return objects;
        }

        // One block per resident: position, current plus pending queue
        // entries with their executions, last-valid will and caregiver.
        private static JsonArray SerializeActions(GameStack stack)
        {
            ResidentRecord[] residents = stack.World.ResidentsSnapshot();
            SortResidentArray(residents);
            JsonArray array = new JsonArray();
            for (int i = 0; i < residents.Length; i++)
            {
                ResidentId id = residents[i].Id;
                JsonObject block = new JsonObject();
                block.Add("resident", (long)id);
                ApproachPose pose = stack.Executor.GetResidentPosition(id);
                JsonObject poseJson = new JsonObject();
                poseJson.Add("x", pose.X);
                poseJson.Add("y", pose.Y);
                poseJson.Add("z", pose.Z);
                block.Add("pose", poseJson);
                ActionEntry current = stack.Arbitrator.GetCurrent(id);
                if (current == null)
                {
                    block.AddNull("current");
                }
                else
                {
                    block.Add("current", SerializeQueued(stack, current));
                }
                ActionEntry[] pending = stack.Arbitrator.GetPending(id);
                JsonArray pendingJson = new JsonArray();
                for (int p = 0; p < pending.Length; p++)
                {
                    pendingJson.Add(SerializeQueued(stack, pending[p]));
                }
                block.Add("pending", pendingJson);
                WillRecord will;
                if (stack.Arbitrator.TryGetLastWill(id, out will))
                {
                    block.Add("will", SerializeWill(will));
                }
                else
                {
                    block.AddNull("will");
                }
                ResidentId guardian;
                if (stack.Arbitrator.TryGetCaregiver(id, out guardian))
                {
                    block.Add("caregiver", (long)guardian);
                }
                else
                {
                    block.AddNull("caregiver");
                }
                array.Add(block);
            }
            return array;
        }

        private static void SortResidentArray(ResidentRecord[] residents)
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

        private static JsonObject SerializeQueued(GameStack stack, ActionEntry entry)
        {
            JsonObject row = new JsonObject();
            row.Add("command", (long)entry.Command);
            row.Add("action", (long)entry.Action);
            row.Add("kind", (int)entry.Kind);
            if (entry.Kind == CommandKind.TravelTo) row.Add("target_kind", (int)entry.TargetKind);
            row.Add("activity", entry.Activity.Code);
            if (entry.TargetId.HasValue)
            {
                row.Add("target", entry.TargetId.Value);
            }
            else
            {
                row.AddNull("target");
            }
            row.Add("owner", (int)entry.Owner);
            if (entry.Source.HasValue)
            {
                row.Add("source", (int)entry.Source.Value);
            }
            else
            {
                row.AddNull("source");
            }
            ExecutionRecord record = stack.Executor.GetExecution(entry.Action);
            if (record == null)
            {
                row.AddNull("execution");
            }
            else
            {
                row.Add("execution", SerializeExecution(record));
            }
            return row;
        }

        private static JsonObject SerializeExecution(ExecutionRecord record)
        {
            JsonObject execution = new JsonObject();
            execution.Add("stage", (int)record.Stage);
            if (record.Outcome.HasValue)
            {
                execution.Add("outcome", (int)record.Outcome.Value);
            }
            else
            {
                execution.AddNull("outcome");
            }
            execution.Add("performStart", record.PerformStartGameSeconds);
            execution.Add("enqueue", record.EnqueueGameSeconds);
            execution.Add("queued", record.Queued);
            execution.Add("ledgerReserved", record.LedgerReserved);
            execution.Add("terminal", record.IsTerminal);
            if (record.BlockedReason.HasValue)
            {
                execution.Add("blocked", (int)record.BlockedReason.Value);
            }
            else
            {
                execution.AddNull("blocked");
            }
            execution.Add("queuePosition", record.QueuePosition);
            return execution;
        }

        private static JsonObject SerializeWill(WillRecord will)
        {
            JsonObject row = new JsonObject();
            JsonArray shares = new JsonArray();
            for (int i = 0; i < will.MoneyShares.Count; i++)
            {
                JsonObject share = new JsonObject();
                share.Add("recipient", will.MoneyShares[i].Recipient);
                share.Add("percent", will.MoneyShares[i].Percent);
                shares.Add(share);
            }
            row.Add("shares", shares);
            row.Add("homePercent", will.HomeSharePercent);
            JsonArray heirlooms = new JsonArray();
            for (int i = 0; i < will.Heirlooms.Count; i++)
            {
                heirlooms.Add(new JsonNumber(((long)will.Heirlooms[i]).ToString()));
            }
            row.Add("heirlooms", heirlooms);
            row.Add("note", will.Note);
            row.Add("funeral", will.FuneralPreference);
            JsonArray households = new JsonArray();
            for (int i = 0; i < will.CareHouseholds.Count; i++)
            {
                households.Add(new JsonNumber(((long)will.CareHouseholds[i]).ToString()));
            }
            row.Add("careHouseholds", households);
            return row;
        }

        // Economy capture: shared balances, the transaction ledger in
        // insertion order, and the applied set behind exactly-once transfers.
        // Rows arrive sorted from Export, so identical state renders
        // identical bytes.
        private static JsonObject SerializeAccounts(Accounts accounts)
        {
            AccountsSnapshot snapshot = accounts.Export();
            JsonObject root = new JsonObject();
            JsonArray shared = new JsonArray();
            for (int i = 0; i < snapshot.Shared.Count; i++)
            {
                JsonObject row = new JsonObject();
                row.Add("household", snapshot.Shared[i].Household);
                row.Add("balance", snapshot.Shared[i].Amount);
                shared.Add(row);
            }
            root.Add("shared", shared);
            JsonArray ledger = new JsonArray();
            for (int i = 0; i < snapshot.Ledger.Count; i++)
            {
                AccountsSnapshot.LedgerRow tx = snapshot.Ledger[i];
                JsonObject row = new JsonObject();
                row.Add("action", tx.Action);
                row.Add("source", SerializeAccountRef(tx.Source));
                row.Add("destination", SerializeAccountRef(tx.Destination));
                row.Add("amount", tx.Amount);
                row.Add("gameSeconds", tx.GameSeconds);
                ledger.Add(row);
            }
            root.Add("ledger", ledger);
            JsonArray applied = new JsonArray();
            for (int i = 0; i < snapshot.Applied.Count; i++)
            {
                applied.Add(new JsonNumber(snapshot.Applied[i].ToString()));
            }
            root.Add("applied", applied);
            return root;
        }

        private static JsonObject SerializeAccountRef(AccountsSnapshot.AccountRefData data)
        {
            JsonObject row = new JsonObject();
            row.Add("kind", data.Kind);
            row.Add("id", data.Id);
            row.Add("name", data.Name);
            return row;
        }

        // Food capture: home stock plus the tracked actions with their
        // reservations and completion flags, sorted by id.
        private static JsonObject SerializeFood(Food food)
        {
            Food.FoodSnapshot snapshot = food.Export();
            JsonObject root = new JsonObject();
            JsonArray stock = new JsonArray();
            for (int i = 0; i < snapshot.Stock.Count; i++)
            {
                JsonObject row = new JsonObject();
                row.Add("home", snapshot.Stock[i].Home);
                row.Add("groceries", snapshot.Stock[i].Groceries);
                row.Add("meals", snapshot.Stock[i].Meals);
                stock.Add(row);
            }
            root.Add("stock", stock);
            JsonArray tracked = new JsonArray();
            for (int i = 0; i < snapshot.Tracked.Count; i++)
            {
                Food.FoodSnapshot.TrackedRow entry = snapshot.Tracked[i];
                JsonObject row = new JsonObject();
                row.Add("action", entry.Action);
                row.Add("resident", entry.Resident);
                row.Add("code", entry.Code);
                row.Add("reservedGroceries", entry.ReservedGroceries);
                row.Add("reservedMeals", entry.ReservedMeals);
                row.Add("done", entry.Done);
                tracked.Add(row);
            }
            root.Add("tracked", tracked);
            return root;
        }

        // Expenses capture: home configs plus every retained due, in DueId
        // order. Funding refs reuse the accounts ref shape; payments need no
        // section because the accounts ledger already records them.
        private static JsonObject SerializeExpenses(Expenses expenses)
        {
            Expenses.ExpensesSnapshot snapshot = expenses.Export();
            JsonObject root = new JsonObject();
            root.Add("nextDueId", snapshot.NextDueId);
            JsonArray homes = new JsonArray();
            for (int i = 0; i < snapshot.Homes.Count; i++)
            {
                Expenses.ExpensesSnapshot.HomeRow home = snapshot.Homes[i];
                JsonObject row = new JsonObject();
                row.Add("home", home.Home);
                row.Add("rented", home.Rented);
                row.Add("nextWeek", home.NextWeek);
                JsonArray funding = new JsonArray();
                for (int j = 0; j < home.Funding.Count; j++)
                {
                    funding.Add(SerializeAccountRef(home.Funding[j]));
                }
                row.Add("funding", funding);
                homes.Add(row);
            }
            root.Add("homes", homes);
            JsonArray dues = new JsonArray();
            for (int i = 0; i < snapshot.Dues.Count; i++)
            {
                Expenses.ExpensesSnapshot.DueRow due = snapshot.Dues[i];
                JsonObject row = new JsonObject();
                row.Add("dueId", due.DueId);
                row.Add("home", due.Home);
                row.Add("kind", due.Kind);
                row.Add("amount", due.Amount);
                row.Add("dueGameSeconds", due.DueGameSeconds);
                row.Add("paid", due.Paid);
                row.Add("shortfall", due.Shortfall);
                row.Add("divisible", due.Divisible);
                dues.Add(row);
            }
            root.Add("dues", dues);
            return root;
        }

        // SHARES ownership capture: accepted-agreement history per home plus
        // pending listings. Shares keep stored order so agreements read back
        // unchanged; homes are ascending for stable bytes.
        private static JsonObject SerializeOwnership(Ownership ownership)
        {
            OwnershipSnapshot snapshot = ownership.Export();
            JsonObject root = new JsonObject();
            JsonArray history = new JsonArray();
            for (int h = 0; h < snapshot.History.Count; h++)
            {
                OwnershipSnapshot.HomeHistory saved = snapshot.History[h];
                JsonObject homeRow = new JsonObject();
                homeRow.Add("home", saved.Home);
                JsonArray agreements = new JsonArray();
                for (int a = 0; a < saved.Agreements.Count; a++)
                {
                    OwnershipSnapshot.AgreementRow agreement = saved.Agreements[a];
                    JsonObject agreementRow = new JsonObject();
                    agreementRow.Add("value", agreement.Value);
                    JsonArray shares = new JsonArray();
                    for (int s = 0; s < agreement.Shares.Count; s++)
                    {
                        JsonObject shareRow = new JsonObject();
                        shareRow.Add("household", agreement.Shares[s].Household);
                        shareRow.Add("percent", agreement.Shares[s].Percent);
                        shareRow.Add("childcareDays", agreement.Shares[s].ChildcareDays);
                        shares.Add(shareRow);
                    }
                    agreementRow.Add("shares", shares);
                    agreements.Add(agreementRow);
                }
                homeRow.Add("agreements", agreements);
                history.Add(homeRow);
            }
            root.Add("history", history);
            JsonArray listings = new JsonArray();
            for (int i = 0; i < snapshot.Listings.Count; i++)
            {
                OwnershipSnapshot.ListingRow saved = snapshot.Listings[i];
                JsonObject row = new JsonObject();
                row.Add("home", saved.Home);
                row.Add("seller", saved.Seller);
                row.Add("value", saved.Value);
                row.Add("amountDue", saved.AmountDue);
                row.Add("dueGameSeconds", saved.DueGameSeconds);
                listings.Add(row);
            }
            root.Add("listings", listings);
            return root;
        }

        // CLUB capture: memberships, standings, help targets, help-event
        // helpers and evaluated gratitude pairs. Rows arrive sorted from
        // Export, so identical state renders identical bytes.
        private static JsonObject SerializeClub(Club club)
        {
            ClubSnapshot snapshot = club.Export();
            JsonObject root = new JsonObject();
            JsonArray members = new JsonArray();
            for (int i = 0; i < snapshot.Members.Count; i++)
            {
                JsonObject row = new JsonObject();
                row.Add("club", snapshot.Members[i].Club);
                row.Add("resident", snapshot.Members[i].Resident);
                members.Add(row);
            }
            root.Add("members", members);
            JsonArray standings = new JsonArray();
            for (int i = 0; i < snapshot.Standings.Count; i++)
            {
                JsonObject row = new JsonObject();
                row.Add("club", snapshot.Standings[i].Club);
                row.Add("resident", snapshot.Standings[i].Resident);
                row.Add("count", snapshot.Standings[i].Count);
                standings.Add(row);
            }
            root.Add("standings", standings);
            JsonArray targets = new JsonArray();
            for (int i = 0; i < snapshot.Targets.Count; i++)
            {
                JsonObject row = new JsonObject();
                row.Add("target", snapshot.Targets[i].Target);
                row.Add("household", snapshot.Targets[i].Household);
                row.Add("state", snapshot.Targets[i].State);
                targets.Add(row);
            }
            root.Add("targets", targets);
            JsonArray helps = new JsonArray();
            for (int i = 0; i < snapshot.Helps.Count; i++)
            {
                JsonObject row = new JsonObject();
                row.Add("event", snapshot.Helps[i].Event);
                row.Add("helper", snapshot.Helps[i].Helper);
                row.Add("target", snapshot.Helps[i].Target);
                helps.Add(row);
            }
            root.Add("helps", helps);
            JsonArray evaluated = new JsonArray();
            for (int i = 0; i < snapshot.Evaluated.Count; i++)
            {
                JsonObject row = new JsonObject();
                row.Add("resident", snapshot.Evaluated[i].Resident);
                row.Add("event", snapshot.Evaluated[i].Event);
                evaluated.Add(row);
            }
            root.Add("evaluated", evaluated);
            return root;
        }

        private static JsonObject SerializeHousing(Housing housing)
        {
            Housing.HousingSnapshot snapshot = housing.Export();
            JsonObject root = new JsonObject();
            JsonArray homes = new JsonArray();
            for (int i = 0; i < snapshot.Homes.Count; i++)
            {
                JsonObject row = new JsonObject();
                row.Add("home", snapshot.Homes[i].Home);
                row.Add("price", snapshot.Homes[i].Price);
                homes.Add(row);
            }
            root.Add("homes", homes);
            JsonArray furniture = new JsonArray();
            for (int i = 0; i < snapshot.Furniture.Count; i++)
            {
                JsonObject row = new JsonObject();
                row.Add("code", snapshot.Furniture[i].Code);
                row.Add("price", snapshot.Furniture[i].Price);
                row.Add("slots", snapshot.Furniture[i].Slots);
                furniture.Add(row);
            }
            root.Add("furniture", furniture);
            JsonArray items = new JsonArray();
            for (int i = 0; i < snapshot.Items.Count; i++)
            {
                Housing.HousingSnapshot.ItemRow saved = snapshot.Items[i];
                JsonObject row = new JsonObject();
                row.Add("id", saved.Id);
                row.Add("code", saved.Code);
                row.Add("home", saved.Home);
                row.Add("owner", SerializeAccountRef(saved.Owner));
                row.Add("inInventory", saved.InInventory);
                row.Add("purchasePrice", saved.PurchasePrice);
                row.Add("x", saved.X);
                row.Add("y", saved.Y);
                row.Add("z", saved.Z);
                row.Add("slots", saved.Slots);
                items.Add(row);
            }
            root.Add("items", items);
            JsonArray caregivers = new JsonArray();
            for (int i = 0; i < snapshot.Caregivers.Count; i++) caregivers.Add(new JsonNumber(snapshot.Caregivers[i].ToString()));
            root.Add("caregivers", caregivers);
            JsonArray reservations = new JsonArray();
            for (int i = 0; i < snapshot.Reservations.Count; i++)
            {
                JsonObject row = new JsonObject();
                row.Add("household", snapshot.Reservations[i].Household);
                row.Add("count", snapshot.Reservations[i].Count);
                reservations.Add(row);
            }
            root.Add("reservations", reservations);
            JsonArray vacancies = new JsonArray();
            for (int i = 0; i < snapshot.Vacancies.Count; i++)
            {
                JsonObject row = new JsonObject();
                row.Add("home", snapshot.Vacancies[i].Home);
                row.Add("since", snapshot.Vacancies[i].Since);
                vacancies.Add(row);
            }
            root.Add("vacancies", vacancies);
            JsonArray applied = new JsonArray();
            for (int i = 0; i < snapshot.Applied.Count; i++) applied.Add(new JsonNumber(snapshot.Applied[i].ToString()));
            root.Add("applied", applied);
            return root;
        }

        // STORM capture: durable warning/active/cooldown state, authored object
        // classifications, resident shelter assignments and exposure rows.
        // Rows are sorted by id so identical state renders identical bytes.
        private static JsonObject SerializeStorm(StormSnapshot snapshot)
        {
            JsonObject root = new JsonObject();
            root.Add("version", 1);
            root.Add("scheduledStart", snapshot.ScheduledStartGameSeconds);
            root.Add("activeEvent", snapshot.ActiveEvent);
            root.Add("activeEnd", snapshot.ActiveEndGameSeconds);
            root.Add("cooldownUntil", snapshot.CooldownUntilGameSeconds);

            List<StormObjectRow> objects = new List<StormObjectRow>(snapshot.Objects);
            for (int i = 1; i < objects.Count; i++)
            {
                StormObjectRow key = objects[i];
                int j = i - 1;
                while (j >= 0 && objects[j].Object > key.Object)
                {
                    objects[j + 1] = objects[j];
                    j--;
                }
                objects[j + 1] = key;
            }
            JsonArray objectRows = new JsonArray();
            for (int i = 0; i < objects.Count; i++)
            {
                StormObjectRow entry = objects[i];
                JsonObject row = new JsonObject();
                row.Add("object", entry.Object);
                row.Add("outdoor", entry.Outdoor);
                row.Add("electric", entry.Electric);
                row.Add("common", entry.Common);
                row.Add("roofHome", entry.RoofHome);
                row.Add("damaged", entry.StormDamaged);
                objectRows.Add(row);
            }
            root.Add("objects", objectRows);

            List<StormResidentRow> residents = new List<StormResidentRow>(snapshot.Residents);
            for (int i = 1; i < residents.Count; i++)
            {
                StormResidentRow key = residents[i];
                int j = i - 1;
                while (j >= 0 && residents[j].Resident > key.Resident)
                {
                    residents[j + 1] = residents[j];
                    j--;
                }
                residents[j + 1] = key;
            }
            JsonArray residentRows = new JsonArray();
            for (int i = 0; i < residents.Count; i++)
            {
                StormResidentRow entry = residents[i];
                JsonObject row = new JsonObject();
                row.Add("resident", entry.Resident);
                row.Add("outdoors", entry.Outdoors);
                row.Add("shelter", entry.Shelter);
                row.Add("routedEvent", entry.RoutedEvent);
                residentRows.Add(row);
            }
            root.Add("residents", residentRows);

            List<StormExposureRow> exposures = new List<StormExposureRow>(snapshot.Exposures);
            for (int i = 1; i < exposures.Count; i++)
            {
                StormExposureRow key = exposures[i];
                int j = i - 1;
                while (j >= 0 && (exposures[j].Event > key.Event
                    || (exposures[j].Event == key.Event && exposures[j].Resident > key.Resident)))
                {
                    exposures[j + 1] = exposures[j];
                    j--;
                }
                exposures[j + 1] = key;
            }
            JsonArray exposureRows = new JsonArray();
            for (int i = 0; i < exposures.Count; i++)
            {
                JsonObject row = new JsonObject();
                row.Add("event", exposures[i].Event);
                row.Add("resident", exposures[i].Resident);
                exposureRows.Add(row);
            }
            root.Add("exposures", exposureRows);
            return root;
        }

        // CREATIVE capture: work items with recorded inputs and quality, the
        // per-viewer daily award ledger, applied completion ids and placed
        // items. Rows arrive sorted from Export, so identical state renders
        // identical bytes.
        private static JsonObject SerializeCreative(Creative creative)
        {
            CreativeSnapshot snapshot = creative.Export();
            JsonObject root = new JsonObject();
            JsonArray works = new JsonArray();
            for (int i = 0; i < snapshot.Works.Count; i++)
            {
                JsonObject row = new JsonObject();
                row.Add("item", snapshot.Works[i].Item);
                row.Add("author", snapshot.Works[i].Author);
                row.Add("personality", snapshot.Works[i].PersonalitySummary);
                row.Add("memory", snapshot.Works[i].MemoryEvent);
                row.Add("quality", snapshot.Works[i].QualityTier);
                works.Add(row);
            }
            root.Add("works", works);
            JsonArray awards = new JsonArray();
            for (int i = 0; i < snapshot.Awards.Count; i++)
            {
                JsonObject row = new JsonObject();
                row.Add("viewer", snapshot.Awards[i].Viewer);
                row.Add("item", snapshot.Awards[i].Item);
                row.Add("day", snapshot.Awards[i].GameDay);
                awards.Add(row);
            }
            root.Add("awards", awards);
            JsonArray applied = new JsonArray();
            for (int i = 0; i < snapshot.Applied.Count; i++)
            {
                JsonObject row = new JsonObject();
                row.Add("action", snapshot.Applied[i].Action);
                applied.Add(row);
            }
            root.Add("applied", applied);
            JsonArray displays = new JsonArray();
            for (int i = 0; i < snapshot.Displays.Count; i++)
            {
                JsonObject row = new JsonObject();
                row.Add("item", snapshot.Displays[i].Item);
                row.Add("wall", snapshot.Displays[i].Wall);
                displays.Add(row);
            }
            root.Add("displays", displays);
            return root;
        }

        // IMAGE capture: per-WorkItem content path, image bytes and the
        // player-visible disclosure naming that path. Rows arrive sorted by
        // item id from Export, so identical state renders identical bytes.
        private static JsonObject SerializeImages(ImageAdapter image)
        {
            ImageSnapshot snapshot = image.Export();
            JsonObject root = new JsonObject();
            JsonArray items = new JsonArray();
            for (int i = 0; i < snapshot.Rows.Count; i++)
            {
                JsonObject row = new JsonObject();
                row.Add("item", snapshot.Rows[i].Item);
                row.Add("path", snapshot.Rows[i].Path);
                row.Add("image", snapshot.Rows[i].ImageBase64);
                row.Add("disclosure", snapshot.Rows[i].Disclosure);
                items.Add(row);
            }
            root.Add("items", items);
            return root;
        }

        // BIRTH capture: capabilities, consent, beds, pregnancies and the
        // once-only birth receipts with parent/child appearance snapshots.
        private static JsonObject SerializeFamily(FamilyCreation family)
        {
            FamilyCreation.FamilySnapshot snapshot = family.Export();
            JsonObject root = new JsonObject();
            JsonArray capabilities = new JsonArray();
            for (int i = 0; i < snapshot.Capabilities.Count; i++)
            {
                JsonObject row = new JsonObject();
                row.Add("resident", snapshot.Capabilities[i].Resident);
                row.Add("canCarry", snapshot.Capabilities[i].CanCarry);
                row.Add("canImpregnate", snapshot.Capabilities[i].CanImpregnate);
                capabilities.Add(row);
            }
            root.Add("capabilities", capabilities);
            JsonArray partners = new JsonArray();
            for (int i = 0; i < snapshot.Partners.Count; i++)
            {
                JsonObject row = new JsonObject();
                row.Add("initiator", snapshot.Partners[i].Initiator);
                row.Add("partner", snapshot.Partners[i].Partner);
                row.Add("consent", snapshot.Partners[i].Consent);
                partners.Add(row);
            }
            root.Add("partners", partners);
            JsonArray beds = new JsonArray();
            for (int i = 0; i < snapshot.Beds.Count; i++)
            {
                JsonObject row = new JsonObject();
                row.Add("object", snapshot.Beds[i].Object);
                row.Add("home", snapshot.Beds[i].Home);
                beds.Add(row);
            }
            root.Add("beds", beds);
            JsonArray pregnancies = new JsonArray();
            for (int i = 0; i < snapshot.Pregnancies.Count; i++)
            {
                JsonObject row = new JsonObject();
                row.Add("carrier", snapshot.Pregnancies[i].Carrier);
                row.Add("otherParent", snapshot.Pregnancies[i].OtherParent);
                row.Add("conceptionEvent", snapshot.Pregnancies[i].ConceptionEvent);
                row.Add("dueGameTime", snapshot.Pregnancies[i].DueGameTime);
                row.Add("capacityHold", snapshot.Pregnancies[i].CapacityHold);
                row.Add("household", snapshot.Pregnancies[i].Household);
                pregnancies.Add(row);
            }
            root.Add("pregnancies", pregnancies);
            JsonArray births = new JsonArray();
            for (int i = 0; i < snapshot.Births.Count; i++)
            {
                JsonObject row = new JsonObject();
                row.Add("child", snapshot.Births[i].Child);
                row.Add("carrier", snapshot.Births[i].Carrier);
                row.Add("otherParent", snapshot.Births[i].OtherParent);
                row.Add("conceptionEvent", snapshot.Births[i].ConceptionEvent);
                row.Add("birthEvent", snapshot.Births[i].BirthEvent);
                row.Add("worldSeed", snapshot.Births[i].WorldSeed);
                row.Add("baseBodySeed", snapshot.Births[i].BaseBodySeed);
                row.Add("baseBody", snapshot.Births[i].BaseBody);
                row.Add("carrierAppearance", SerializeAppearanceEntry(snapshot.Births[i].CarrierAppearance));
                row.Add("otherParentAppearance", SerializeAppearanceEntry(snapshot.Births[i].OtherParentAppearance));
                row.Add("childAppearance", SerializeAppearanceEntry(snapshot.Births[i].ChildAppearance));
                births.Add(row);
            }
            root.Add("births", births);
            return root;
        }

        private static JsonObject SerializeAppearanceEntry(AppearanceEntry entry)
        {
            JsonObject row = new JsonObject();
            row.Add("resident", entry.ResidentId);
            row.Add("skin", entry.SkinTone);
            row.Add("face", entry.Face);
            row.Add("eyecolor", entry.EyeColor);
            row.Add("hairtype", entry.HairType);
            row.Add("hairstyle", entry.HairStyle);
            row.Add("haircolor", entry.HairColor);
            row.Add("accessory", entry.Accessory);
            row.Add("everyday", entry.OutfitEveryday);
            row.Add("sleep", entry.OutfitSleep);
            row.Add("exercise", entry.OutfitExercise);
            row.Add("override", entry.OverrideCategory);
            row.Add("baseBody", (int)entry.BaseBody);
            return row;
        }

        // Home capture: per-home cleanliness, supplies and decay time plus
        // the damage flags and tracked actions with completion flags, each
        // sorted by id.
        private static JsonObject SerializeHome(Home home)
        {
            Home.HomeSnapshot snapshot = home.Export();
            JsonObject root = new JsonObject();
            JsonArray states = new JsonArray();
            for (int i = 0; i < snapshot.States.Count; i++)
            {
                Home.HomeSnapshot.StateRow entry = snapshot.States[i];
                JsonObject row = new JsonObject();
                row.Add("home", entry.Home);
                row.Add("cleanliness", entry.Cleanliness);
                row.Add("supplies", entry.Supplies);
                row.Add("lastGameSeconds", entry.LastGameSeconds);
                states.Add(row);
            }
            root.Add("states", states);
            JsonArray damaged = new JsonArray();
            for (int i = 0; i < snapshot.Damaged.Count; i++)
            {
                Home.HomeSnapshot.DamageRow entry = snapshot.Damaged[i];
                JsonObject row = new JsonObject();
                row.Add("object", entry.Object);
                row.Add("damaged", entry.Damaged);
                damaged.Add(row);
            }
            root.Add("damaged", damaged);
            JsonArray tracked = new JsonArray();
            for (int i = 0; i < snapshot.Tracked.Count; i++)
            {
                Home.HomeSnapshot.TrackedRow entry = snapshot.Tracked[i];
                JsonObject row = new JsonObject();
                row.Add("action", entry.Action);
                row.Add("resident", entry.Resident);
                row.Add("code", entry.Code);
                row.Add("done", entry.Done);
                tracked.Add(row);
            }
            root.Add("tracked", tracked);
            return root;
        }

        // Minigolf capture: registered course geometry/physics and every match
        // checkpoint (shared match id, participants, balls, timers, pending
        // confirmation, draws and RNG state). Export sorts ids for stable bytes.
        private static JsonObject SerializeMinigolf(Minigolf minigolf)
        {
            Minigolf.MinigolfSnapshot snapshot = minigolf.Export();
            JsonObject root = new JsonObject();
            JsonArray courses = new JsonArray();
            for (int i = 0; i < snapshot.Courses.Count; i++)
            {
                Minigolf.MinigolfSnapshot.CourseRow entry = snapshot.Courses[i];
                JsonObject row = new JsonObject();
                row.Add("course", entry.Course);
                row.Add("friction", entry.Friction);
                row.Add("metresPerSecondPerPower", entry.MetresPerSecondPerPower);
                courses.Add(row);
            }
            root.Add("courses", courses);
            JsonArray holes = new JsonArray();
            for (int i = 0; i < snapshot.Holes.Count; i++)
            {
                Minigolf.MinigolfSnapshot.HoleRow entry = snapshot.Holes[i];
                JsonObject row = new JsonObject();
                row.Add("course", entry.Course);
                row.Add("index", entry.Index);
                row.Add("teeX", entry.TeeX);
                row.Add("teeY", entry.TeeY);
                row.Add("teeZ", entry.TeeZ);
                row.Add("cupX", entry.CupX);
                row.Add("cupY", entry.CupY);
                row.Add("cupZ", entry.CupZ);
                row.Add("minX", entry.MinX);
                row.Add("maxX", entry.MaxX);
                row.Add("minZ", entry.MinZ);
                row.Add("maxZ", entry.MaxZ);
                row.Add("cupRadius", entry.CupRadius);
                holes.Add(row);
            }
            root.Add("holes", holes);
            JsonArray matches = new JsonArray();
            for (int i = 0; i < snapshot.Matches.Count; i++)
            {
                Minigolf.MinigolfSnapshot.MatchRow entry = snapshot.Matches[i];
                JsonObject row = new JsonObject();
                row.Add("action", entry.Action);
                row.Add("course", entry.Course);
                row.Add("hole", entry.HoleIndex);
                row.Add("turn", entry.TurnIndex);
                row.Add("active", entry.ActiveIndex);
                row.Add("rolling", entry.Rolling);
                row.Add("completed", entry.Completed);
                row.Add("cancelled", entry.Cancelled);
                row.Add("cancelRequested", entry.CancelRequested);
                row.Add("hasPending", entry.HasPending);
                row.Add("pendingDegrees", entry.PendingDegrees);
                row.Add("pendingPower", entry.PendingPower);
                row.Add("aimDegrees", entry.AimDegrees);
                row.Add("aimPower", entry.AimPower);
                row.Add("startedAt", entry.StartedAt);
                row.Add("lastTick", entry.LastTick);
                row.Add("turnStart", entry.TurnStart);
                row.Add("puttStart", entry.PuttStart);
                row.Add("puttRestX", entry.PuttRestX);
                row.Add("puttRestY", entry.PuttRestY);
                row.Add("puttRestZ", entry.PuttRestZ);
                row.Add("randomState", entry.RandomState);
                row.Add("effectsApplied", entry.EffectsApplied);
                JsonArray participants = new JsonArray();
                for (int p = 0; p < entry.Participants.Count; p++)
                {
                    Minigolf.MinigolfSnapshot.ParticipantRow saved = entry.Participants[p];
                    JsonObject participant = new JsonObject();
                    participant.Add("resident", saved.Resident);
                    participant.Add("human", saved.Human);
                    participant.Add("winner", saved.Winner);
                    participant.Add("stroke0", saved.Stroke0);
                    participant.Add("stroke1", saved.Stroke1);
                    participant.Add("stroke2", saved.Stroke2);
                    participant.Add("complete0", saved.Complete0);
                    participant.Add("complete1", saved.Complete1);
                    participant.Add("complete2", saved.Complete2);
                    participant.Add("x", saved.X);
                    participant.Add("y", saved.Y);
                    participant.Add("z", saved.Z);
                    participant.Add("velocityX", saved.VelocityX);
                    participant.Add("velocityY", saved.VelocityY);
                    participant.Add("velocityZ", saved.VelocityZ);
                    participants.Add(participant);
                }
                row.Add("participants", participants);
                JsonArray putts = new JsonArray();
                for (int p = 0; p < entry.Putts.Count; p++)
                {
                    Minigolf.MinigolfSnapshot.PuttRow saved = entry.Putts[p];
                    JsonObject putt = new JsonObject();
                    putt.Add("resident", saved.Resident);
                    putt.Add("hole", saved.HoleIndex);
                    putt.Add("turn", saved.TurnIndex);
                    putt.Add("degrees", saved.Degrees);
                    putt.Add("power", saved.Power);
                    putt.Add("hasDraw", saved.HasDraw);
                    if (saved.HasDraw) putt.Add("draw", saved.Draw);
                    else putt.AddNull("draw");
                    putts.Add(putt);
                }
                row.Add("putts", putts);
                matches.Add(row);
            }
            root.Add("matches", matches);
            return root;
        }

        // PARTY capture: the caller's stable party id, the live NAV action it
        // resolves to, once-only markers, and every attendee interval plus the
        // stored sentiment draw. Rows arrive sorted from Export.
        private static JsonObject SerializeParty(Party party)
        {
            PartySnapshot snapshot = party.Export();
            JsonObject root = new JsonObject();
            JsonArray parties = new JsonArray();
            for (int i = 0; i < snapshot.Rows.Count; i++)
            {
                PartySnapshot.PartyRow entry = snapshot.Rows[i];
                JsonObject row = new JsonObject();
                row.Add("key", entry.Key);
                row.Add("action", entry.Action);
                row.Add("host", entry.Host);
                row.Add("start", entry.Start);
                row.Add("charged", entry.Charged);
                row.Add("done", entry.Done);
                JsonArray attendees = new JsonArray();
                for (int a = 0; a < entry.Attendees.Count; a++)
                {
                    PartySnapshot.AttendeeRow saved = entry.Attendees[a];
                    JsonObject line = new JsonObject();
                    line.Add("resident", saved.Resident);
                    line.Add("entry", saved.Entry);
                    line.Add("exit", saved.Exit);
                    line.Add("hasDraw", saved.HasDraw);
                    if (saved.HasDraw) line.Add("draw", saved.Draw);
                    else line.AddNull("draw");
                    attendees.Add(line);
                }
                row.Add("attendees", attendees);
                parties.Add(row);
            }
            root.Add("parties", parties);
            // PARTY-LIVE plans only when present, so plan-free saves keep their bytes.
            if (snapshot.Plans.Count > 0)
            {
                JsonArray plans = new JsonArray();
                for (int i = 0; i < snapshot.Plans.Count; i++)
                {
                    PartyPlan plan = snapshot.Plans[i];
                    JsonObject row = new JsonObject();
                    row.Add("id", plan.Id);
                    row.Add("host", plan.Host);
                    row.Add("created", plan.Created);
                    row.Add("start", plan.Start);
                    row.Add("venue", plan.Venue);
                    row.Add("travelStarted", plan.TravelStarted);
                    row.Add("hostAction", plan.HostAction);
                    row.Add("announced", plan.Announced);
                    row.Add("closed", plan.Closed);
                    JsonArray invites = new JsonArray();
                    for (int g = 0; g < plan.Invites.Count; g++)
                    {
                        PartyInvite invite = plan.Invites[g];
                        JsonObject line = new JsonObject();
                        line.Add("guest", invite.Guest);
                        line.Add("accepted", invite.Accepted);
                        line.Add("sent", invite.Sent);
                        line.Add("arrived", invite.Arrived);
                        line.Add("chatted", invite.Chatted);
                        line.Add("noShow", invite.NoShow);
                        invites.Add(line);
                    }
                    row.Add("invites", invites);
                    plans.Add(row);
                }
                root.Add("plans", plans);
            }
            return root;
        }

        private static JsonArray SerializeAppearances(GameStack stack)
        {
            List<AppearanceEntry> rows = stack.Appearances.Export();
            for (int i = 1; i < rows.Count; i++)
            {
                AppearanceEntry key = rows[i];
                int j = i - 1;
                while (j >= 0 && rows[j].ResidentId > key.ResidentId)
                {
                    rows[j + 1] = rows[j];
                    j--;
                }
                rows[j + 1] = key;
            }
            JsonArray array = new JsonArray();
            for (int i = 0; i < rows.Count; i++)
            {
                JsonObject row = new JsonObject();
                row.Add("resident", rows[i].ResidentId);
                row.Add("skin", rows[i].SkinTone);
                row.Add("face", rows[i].Face);
                row.Add("eyecolor", rows[i].EyeColor);
                row.Add("hairtype", rows[i].HairType);
                row.Add("hairstyle", rows[i].HairStyle);
                row.Add("haircolor", rows[i].HairColor);
                row.Add("accessory", rows[i].Accessory);
                row.Add("everyday", rows[i].OutfitEveryday);
                row.Add("sleep", rows[i].OutfitSleep);
                row.Add("exercise", rows[i].OutfitExercise);
                row.Add("override", rows[i].OverrideCategory);
                row.Add("baseBody", (int)rows[i].BaseBody);
                array.Add(row);
            }
            return array;
        }

        // Live executions reachable through the public API: the queued
        // entries' records plus any orphan live record the resident status
        // still shows (an interruption the executor has not processed yet).
        private static List<ExecutionRecord> LiveExecutions(GameStack stack)
        {
            List<ExecutionRecord> live = new List<ExecutionRecord>();
            ResidentRecord[] residents = stack.World.ResidentsSnapshot();
            for (int i = 0; i < residents.Length; i++)
            {
                ResidentId id = residents[i].Id;
                ActionEntry current = stack.Arbitrator.GetCurrent(id);
                if (current != null)
                {
                    AddLive(stack, live, current.Action);
                }
                ActionEntry[] pending = stack.Arbitrator.GetPending(id);
                for (int p = 0; p < pending.Length; p++)
                {
                    AddLive(stack, live, pending[p].Action);
                }
                ResidentStatus status = stack.Executor.GetResidentStatus(id);
                if (!status.IsIdle)
                {
                    AddLiveRecord(live, status.Execution);
                }
            }
            return live;
        }

        private static void AddLive(GameStack stack, List<ExecutionRecord> live,
            ActionInstanceId action)
        {
            ExecutionRecord record = stack.Executor.GetExecution(action);
            if (record != null && !record.IsTerminal)
            {
                AddLiveRecord(live, record);
            }
        }

        private static void AddLiveRecord(List<ExecutionRecord> live, ExecutionRecord record)
        {
            for (int i = 0; i < live.Count; i++)
            {
                if (live[i].Action == record.Action)
                {
                    return;
                }
            }
            live.Add(record);
        }

        private static SaveLoadResult LoadFrom(ISaveFileStore store, string path,
            string backupPath, ISceneContent scene, bool isBackup)
        {
            string label = isBackup ? "backup" : "save";
            bool backupAvailable = store.Exists(backupPath);
            if (!store.Exists(path))
            {
                return new SaveLoadResult(false, null,
                    "No " + label + " file at " + path
                    + (backupAvailable ? ". The previous backup is available." : "."),
                    backupAvailable);
            }
            byte[] bytes;
            try
            {
                bytes = store.ReadAllBytes(path);
            }
            catch (Exception e)
            {
                return new SaveLoadResult(false, null,
                    "The " + label + " could not be read: " + e.Message
                    + BackupOffer(backupAvailable, isBackup), backupAvailable);
            }
            JsonObject document;
            try
            {
                document = ParseDocument(bytes);
            }
            catch (JsonSaveException e)
            {
                return new SaveLoadResult(false, null,
                    "The " + label + " is not a readable save document: " + e.Message
                    + ". The invalid bytes were kept for diagnosis."
                    + BackupOffer(backupAvailable, isBackup), backupAvailable);
            }
            JsonObject headerJson;
            JsonObject payload;
            try
            {
                headerJson = document.GetObject("header", "document");
                payload = document.GetObject("payload", "document");
            }
            catch (JsonSaveException e)
            {
                return new SaveLoadResult(false, null,
                    "The " + label + " is missing its header or payload: " + e.Message
                    + ". The invalid bytes were kept for diagnosis."
                    + BackupOffer(backupAvailable, isBackup), backupAvailable);
            }
            if (!HashMatches(payload, RequiredHash(headerJson)))
            {
                return new SaveLoadResult(false, null,
                    "The " + label + " failed its integrity check (sha256 mismatch)."
                    + " The invalid bytes were kept for diagnosis."
                    + BackupOffer(backupAvailable, isBackup), backupAvailable);
            }
            int version = RequiredVersion(headerJson);
            if (!SaveV2Migration.IsSupportedVersion(version))
            {
                return new SaveLoadResult(false, null,
                    "Unsupported save schema version " + version.ToString()
                    + ": this build reads versions "
                    + SaveV2Migration.LegacyVersion.ToString() + " and "
                    + SaveFormat.SchemaVersion.ToString() + " only."
                    + " The file was left untouched."
                    + BackupOffer(backupAvailable, isBackup), backupAvailable);
            }
            SaveHeader header;
            try
            {
                header = ReadHeader(headerJson);
            }
            catch (JsonSaveException e)
            {
                return new SaveLoadResult(false, null,
                    "The " + label + " header is invalid: " + e.Message
                    + ". The invalid bytes were kept for diagnosis."
                    + BackupOffer(backupAvailable, isBackup), backupAvailable);
            }
            LoadedGame game;
            try
            {
                game = Restore(payload, header, scene);
            }
            catch (JsonSaveException e)
            {
                return new SaveLoadResult(false, null,
                    "The " + label + " payload is invalid: " + e.Message
                    + ". The invalid bytes were kept for diagnosis."
                    + BackupOffer(backupAvailable, isBackup), backupAvailable);
            }
            catch (Exception e)
            {
                return new SaveLoadResult(false, null,
                    "The " + label + " could not be restored: "
                    + e.GetType().Name + " " + e.Message
                    + BackupOffer(backupAvailable, isBackup), backupAvailable);
            }
            return new SaveLoadResult(true, game, string.Empty, backupAvailable);
        }

        private static string BackupOffer(bool backupAvailable, bool isBackup)
        {
            if (isBackup || !backupAvailable)
            {
                return string.Empty;
            }
            return " The previous backup is available.";
        }

        private static string RequiredHash(JsonObject headerJson)
        {
            try
            {
                return headerJson.GetString("sha256", "header");
            }
            catch (JsonSaveException)
            {
                return string.Empty;
            }
        }

        private static int RequiredVersion(JsonObject headerJson)
        {
            try
            {
                return headerJson.GetInt("schemaVersion", "header");
            }
            catch (JsonSaveException)
            {
                return -1;
            }
        }

        private static SaveHeader ReadHeader(JsonObject headerJson)
        {
            return new SaveHeader(
                headerJson.GetInt("schemaVersion", "header"),
                headerJson.GetLong("worldId", "header"),
                (WorldEpoch)headerJson.GetUInt("worldEpoch", "header"),
                headerJson.GetLong("savedAtGameSeconds", "header"),
                headerJson.GetString("sha256", "header"));
        }

        // Rebuilds the whole stack from a hash-verified v1 payload. Everything
        // is fresh: the running world (if any) is never touched, and any
        // throw below discards the partial stack.
        private static LoadedGame Restore(JsonObject payload, SaveHeader header,
            ISceneContent scene)
        {
            SaveV2Migration.MigrateToCurrent(payload, header.SchemaVersion);
            JsonObject worldJson = payload.GetObject("world", "payload");
            if (worldJson.GetLong("worldId", "payload.world") != header.WorldId
                || worldJson.GetUInt("epoch", "payload.world") != (uint)header.Epoch
                || worldJson.GetLong("gameSeconds", "payload.world") != header.SavedAtGameSeconds)
            {
                throw new JsonSaveException("header does not match payload identity");
            }
            WorldSnapshot snapshot = DeserializeWorld(payload);
            WorldState world = WorldState.LoadFromSnapshot(snapshot);
            AppearanceStore appearances = new AppearanceStore();
            appearances.Import(Writer, DeserializeAppearances(payload));
            Clock clock = new Clock(world);
            RestoreClock(clock, payload.GetObject("clock", "payload"));
            CommandArbitrator arbitrator = new CommandArbitrator(world, clock);
            ActivityExecutor executor = new ActivityExecutor(world, arbitrator, clock);
            scene.RegisterObjects(executor);
            List<string> notes = new List<string>();
            JsonArray blocks = payload.GetArray("actions", "payload");
            RestorePositions(executor, blocks);
            Dictionary<long, bool> heldActions = SavedHoldActions(payload);
            Dictionary<long, long> actionMap = new Dictionary<long, long>();
            List<ResumeItem> resume = ReplayQueues(world, arbitrator, blocks, heldActions, notes,
                actionMap);
            RestoreWills(world, arbitrator, blocks, notes);
            RestoreAutomation(world, arbitrator, payload.GetBool("automation", "payload"), notes);
            if (payload.Has("bicycles")) executor.Bicycles = BicycleSave.Read(payload.GetObject("bicycles", "payload"), actionMap, world, world.GameSeconds);
            ResumeExecutions(executor, clock, resume, blocks, notes);
            List<ActionInstanceId> liveIds = new List<ActionInstanceId>();
            for (int i = 0; i < resume.Count; i++)
            {
                if (resume[i].Dispatched)
                {
                    liveIds.Add(resume[i].Entry.Action);
                }
            }
            executor.ReconcileAfterLoad(liveIds.ToArray());
            Accounts accounts = new Accounts(world);
            if (payload.Has("accounts"))
            {
                accounts.Import(Writer, DeserializeAccounts(payload, actionMap));
            }
            Food food = new Food(world, arbitrator, executor, accounts);
            if (payload.Has("food"))
            {
                food.Import(Writer, DeserializeFood(payload, actionMap));
            }
            Expenses expenses = new Expenses(world, accounts);
            if (payload.Has("expenses"))
            {
                expenses.Import(Writer, DeserializeExpenses(payload));
            }
            Ownership ownership = new Ownership(world, accounts);
            if (payload.Has("ownership"))
            {
                ownership.Import(Writer, DeserializeOwnership(payload));
            }
            Home home = new Home(world, arbitrator, executor, accounts);
            if (payload.Has("home"))
            {
                home.Import(Writer, DeserializeHome(payload, actionMap));
            }
            Club club = new Club(world);
            if (payload.Has("club"))
            {
                club.Import(Writer, DeserializeClub(payload));
            }
            Housing housing = new Housing(world, accounts, ownership, executor, appearances);
            if (payload.Has("housing"))
            {
                housing.Import(Writer, DeserializeHousing(payload));
            }
            arbitrator.BindHousing(housing);
            Creative creative = new Creative(world);
            if (payload.Has("creative"))
            {
                creative.Import(Writer, DeserializeCreative(payload));
            }
            if (payload.Has("minigolf"))
            {
                Minigolf minigolf = new Minigolf(world, arbitrator, executor, clock);
                minigolf.Import(Writer, DeserializeMinigolf(payload));
            }
            // Pending gateway work is runtime state, never saved; the restored
            // adapter starts with a fresh boundary and restores only content.
            ImageAdapter image = new ImageAdapter(world,
                new ProposalBoundary(world, clock, arbitrator, new Autonomy(world, arbitrator)),
                creative);
            if (payload.Has("images"))
            {
                image.Import(Writer, DeserializeImages(payload));
            }
            FamilyCreation family = new FamilyCreation(world, arbitrator, executor, appearances, housing);
            if (payload.Has("family"))
            {
                family.Import(Writer, DeserializeFamily(payload));
            }
            Party party = new Party(world, arbitrator, executor, accounts);
            if (payload.Has("party"))
            {
                party.Import(Writer, DeserializeParty(payload, actionMap));
            }
            return new LoadedGame(world, appearances, clock, arbitrator, executor,
                header, notes.ToArray(), accounts, food, expenses, ownership, home, club, housing,
                creative, image, family, party);
        }

        private static void RestoreClock(Clock clock, JsonObject clockJson)
        {
            int speed = clockJson.GetInt("speed", "payload.clock");
            if (speed != (int)SimulationSpeed.Paused && speed != (int)SimulationSpeed.Normal
                && speed != (int)SimulationSpeed.Fast && speed != (int)SimulationSpeed.Ultra)
            {
                throw new JsonSaveException("payload.clock: unknown speed "
                    + speed.ToString());
            }
            int preset = clockJson.GetInt("preset", "payload.clock");
            if (!Enum.IsDefined(typeof(AgingPreset), (AgingPreset)preset))
            {
                throw new JsonSaveException("payload.clock: unknown aging preset "
                    + preset.ToString());
            }
            clock.SetSpeed((SimulationSpeed)speed);
            clock.SetPreset((AgingPreset)preset);
            double remainder = clockJson.GetDouble("tickRemainder", "payload.clock");
            if (double.IsNaN(remainder) || double.IsInfinity(remainder)
                || remainder < 0.0 || remainder >= 1.0)
            {
                throw new JsonSaveException("payload.clock: \"tickRemainder\" out of range");
            }
            long savedTicks = clockJson.GetLong("totalTicks", "payload.clock");
            if (savedTicks < 0L)
            {
                throw new JsonSaveException("payload.clock: \"totalTicks\" is negative");
            }
            clock.RestoreTickState(remainder, savedTicks);
            if (clockJson.GetBool("paused", "payload.clock"))
            {
                clock.Pause();
            }
            else
            {
                clock.Resume();
            }
        }

        private static WorldSnapshot DeserializeWorld(JsonObject payload)
        {
            JsonObject worldJson = payload.GetObject("world", "payload");
            WorldSnapshot snapshot = new WorldSnapshot();
            snapshot.SchemaVersion = worldJson.GetInt("schemaVersion", "payload.world");
            snapshot.WorldId = worldJson.GetLong("worldId", "payload.world");
            snapshot.Epoch = worldJson.GetUInt("epoch", "payload.world");
            snapshot.Seed = worldJson.GetLong("seed", "payload.world");
            snapshot.RngState = worldJson.GetULong("rngState", "payload.world");
            if (worldJson.Has("relationshipRngState"))
                snapshot.RelationshipRngState = worldJson.GetULong("relationshipRngState", "payload.world");
            if (worldJson.Has("nextRelationshipDraw"))
                snapshot.NextRelationshipDraw = worldJson.GetLong("nextRelationshipDraw", "payload.world");
            if (worldJson.Has("tierIdleSince"))
            {
                JsonArray idle = worldJson.GetArray("tierIdleSince", "payload.world");
                for (int i = 0; i < idle.Count; i++)
                {
                    JsonObject row = idle.GetObject(i, "payload.world.tierIdleSince");
                    snapshot.TierIdleSince.Add((ResidentId)row.GetLong("resident", "tierIdleSince"), row.GetLong("since", "tierIdleSince"));
                }
            }
            snapshot.GameSeconds = worldJson.GetLong("gameSeconds", "payload.world");
            int aging = worldJson.GetInt("aging", "payload.world");
            if (!Enum.IsDefined(typeof(AgingPreset), (AgingPreset)aging))
            {
                throw new JsonSaveException("payload.world: unknown aging preset "
                    + aging.ToString());
            }
            snapshot.Aging = (AgingPreset)aging;
            snapshot.TownFund = worldJson.GetLong("townFund", "payload.world");
            snapshot.NextDecisionSequence = worldJson.GetLong("nextDecisionSequence", "payload.world");
            snapshot.NextResidentId = worldJson.GetLong("nextResidentId", "payload.world");
            snapshot.NextHouseholdId = worldJson.GetLong("nextHouseholdId", "payload.world");
            snapshot.NextHomeId = worldJson.GetLong("nextHomeId", "payload.world");
            snapshot.NextObjectId = worldJson.GetLong("nextObjectId", "payload.world");
            snapshot.NextActionId = worldJson.GetLong("nextActionId", "payload.world");
            snapshot.NextEventId = worldJson.GetLong("nextEventId", "payload.world");
            snapshot.NextCommandId = worldJson.GetLong("nextCommandId", "payload.world");
            snapshot.NextGatewayId = worldJson.GetLong("nextGatewayId", "payload.world");
            JsonArray homes = payload.GetArray("homes", "payload");
            for (int i = 0; i < homes.Count; i++)
            {
                JsonObject row = homes.GetObject(i, "payload.homes");
                snapshot.Homes.Add(new HomeRecord(
                    (HomeId)row.GetLong("id", "payload.homes"),
                    row.GetInt("beds", "payload.homes")));
            }
            JsonArray households = payload.GetArray("households", "payload");
            for (int i = 0; i < households.Count; i++)
            {
                JsonObject row = households.GetObject(i, "payload.households");
                snapshot.Households.Add(new HouseholdRecord(
                    (HouseholdId)row.GetLong("id", "payload.households"),
                    row.GetString("name", "payload.households"),
                    (HomeId)row.GetLong("home", "payload.households")));
            }
            JsonArray residents = payload.GetArray("residents", "payload");
            for (int i = 0; i < residents.Count; i++)
            {
                snapshot.Residents.Add(DeserializeResident(
                    residents.GetObject(i, "payload.residents")));
            }
            JsonArray events = payload.GetArray("events", "payload");
            for (int i = 0; i < events.Count; i++)
            {
                JsonObject row = events.GetObject(i, "payload.events");
                ResidentId? actor = null;
                if (!row.IsNull("actor", "payload.events"))
                {
                    actor = (ResidentId)row.GetLong("actor", "payload.events");
                }
                snapshot.Events.Add(new WorldEvent(
                    (EventId)row.GetLong("event", "payload.events"),
                    row.GetLong("gameSeconds", "payload.events"),
                    row.GetString("kind", "payload.events"),
                    actor,
                    row.GetString("detail", "payload.events")));
            }
            JsonArray ledger = payload.GetArray("effectLedger", "payload");
            for (int i = 0; i < ledger.Count; i++)
            {
                JsonObject row = ledger.GetObject(i, "payload.effectLedger");
                LedgerRecipient recipient;
                if (row.IsNull("second", "payload.effectLedger"))
                {
                    recipient = new LedgerRecipient(
                        (ResidentId)row.GetLong("first", "payload.effectLedger"));
                }
                else
                {
                    recipient = new LedgerRecipient(
                        (ResidentId)row.GetLong("first", "payload.effectLedger"),
                        (ResidentId)row.GetLong("second", "payload.effectLedger"));
                }
                int state = row.GetInt("state", "payload.effectLedger");
                if (!Enum.IsDefined(typeof(LedgerState), (LedgerState)state))
                {
                    throw new JsonSaveException("payload.effectLedger: unknown state "
                        + state.ToString());
                }
                EffectLedgerEntry entry = new EffectLedgerEntry(
                    (ActionInstanceId)row.GetLong("action", "payload.effectLedger"),
                    row.GetString("kind", "payload.effectLedger"),
                    recipient);
                if ((LedgerState)state == LedgerState.Committed)
                {
                    entry.TryCommit();
                }
                snapshot.EffectLedger.Add(entry);
            }
            JsonArray estate = payload.GetArray("estateLedger", "payload");
            for (int i = 0; i < estate.Count; i++)
            {
                JsonObject row = estate.GetObject(i, "payload.estateLedger");
                snapshot.EstateLedger.Add(new EstateLedgerEntry(
                    (EventId)row.GetLong("death", "payload.estateLedger"),
                    row.GetString("asset", "payload.estateLedger"),
                    row.GetString("recipient", "payload.estateLedger"),
                    row.GetBool("settled", "payload.estateLedger")));
            }
            JsonArray care = payload.GetArray("care", "payload");
            for (int i = 0; i < care.Count; i++)
            {
                JsonObject row = care.GetObject(i, "payload.care");
                HouseholdId? household = null;
                if (!row.IsNull("household", "payload.care"))
                {
                    household = (HouseholdId)row.GetLong("household", "payload.care");
                }
                snapshot.CareAssignments.Add(new CareAssignment(
                    (ResidentId)row.GetLong("dependent", "payload.care"),
                    (ResidentId)row.GetLong("guardian", "payload.care"),
                    new CarePlacement(household, row.GetInt("facility", "payload.care")),
                    (EventId)row.GetLong("transfer", "payload.care")));
            }
            if (payload.Has("storm"))
            {
                snapshot.Storm = DeserializeStorm(payload);
            }
            if (payload.Has("aging"))
            {
                JsonArray agingRows = payload.GetArray("aging", "payload");
                for (int i = 0; i < agingRows.Count; i++)
                {
                    JsonObject row = agingRows.GetObject(i, "payload.aging");
                    long id = row.GetLong("resident", "payload.aging");
                    ResidentRecord resident = snapshot.Residents.Find(r => (long)r.Id == id);
                    if (resident == null) throw new JsonSaveException("payload.aging: unknown resident");
                    resident.SetAging(row.GetDouble("progress", "payload.aging"),
                        row.GetDouble("remainder", "payload.aging"), row.GetBool("frail", "payload.aging"));
                }
            }
            if (payload.Has("estate"))
            {
                JsonObject estateJson = payload.GetObject("estate", "payload");
                if (estateJson.GetInt("version", "payload.estate") != 1)
                {
                    throw new JsonSaveException("payload.estate: unsupported version");
                }
                JsonArray careRequired = estateJson.GetArray("careRequired", "payload.estate");
                for (int i = 0; i < careRequired.Count; i++)
                {
                    long id = careRequired.GetLong(i, "payload.estate.careRequired");
                    ResidentRecord resident = null;
                    for (int r = 0; r < snapshot.Residents.Count; r++)
                    {
                        if ((long)snapshot.Residents[r].Id == id)
                        {
                            resident = snapshot.Residents[r];
                            break;
                        }
                    }
                    if (resident == null)
                    {
                        throw new JsonSaveException("payload.estate: unknown resident "
                            + id.ToString());
                    }
                    resident.SetCareRequired(true);
                }
            }
            return snapshot;
        }

        private static StormSnapshot DeserializeStorm(JsonObject payload)
        {
            JsonObject section = payload.GetObject("storm", "payload");
            if (section.GetInt("version", "payload.storm") != 1)
            {
                throw new JsonSaveException("payload.storm: unsupported version");
            }
            StormSnapshot snapshot = new StormSnapshot();
            snapshot.ScheduledStartGameSeconds = section.GetLong("scheduledStart", "payload.storm");
            snapshot.ActiveEvent = section.GetLong("activeEvent", "payload.storm");
            snapshot.ActiveEndGameSeconds = section.GetLong("activeEnd", "payload.storm");
            snapshot.CooldownUntilGameSeconds = section.GetLong("cooldownUntil", "payload.storm");
            JsonArray objects = section.GetArray("objects", "payload.storm");
            for (int i = 0; i < objects.Count; i++)
            {
                JsonObject row = objects.GetObject(i, "payload.storm.objects");
                StormObjectRow entry = new StormObjectRow();
                entry.Object = row.GetLong("object", "payload.storm.objects");
                entry.Outdoor = row.GetBool("outdoor", "payload.storm.objects");
                entry.Electric = row.GetBool("electric", "payload.storm.objects");
                entry.Common = row.GetBool("common", "payload.storm.objects");
                entry.RoofHome = row.GetLong("roofHome", "payload.storm.objects");
                entry.StormDamaged = row.GetBool("damaged", "payload.storm.objects");
                snapshot.Objects.Add(entry);
            }
            JsonArray residents = section.GetArray("residents", "payload.storm");
            for (int i = 0; i < residents.Count; i++)
            {
                JsonObject row = residents.GetObject(i, "payload.storm.residents");
                StormResidentRow entry = new StormResidentRow();
                entry.Resident = row.GetLong("resident", "payload.storm.residents");
                entry.Outdoors = row.GetBool("outdoors", "payload.storm.residents");
                entry.Shelter = row.GetLong("shelter", "payload.storm.residents");
                entry.RoutedEvent = row.GetLong("routedEvent", "payload.storm.residents");
                snapshot.Residents.Add(entry);
            }
            JsonArray exposures = section.GetArray("exposures", "payload.storm");
            for (int i = 0; i < exposures.Count; i++)
            {
                JsonObject row = exposures.GetObject(i, "payload.storm.exposures");
                StormExposureRow entry = new StormExposureRow();
                entry.Event = row.GetLong("event", "payload.storm.exposures");
                entry.Resident = row.GetLong("resident", "payload.storm.exposures");
                snapshot.Exposures.Add(entry);
            }
            return snapshot;
        }

        private static ResidentRecord DeserializeResident(JsonObject row)
        {
            int stage = row.GetInt("stage", "payload.residents");
            if (!Enum.IsDefined(typeof(LifeStage), (LifeStage)stage))
            {
                throw new JsonSaveException("payload.residents: unknown stage "
                    + stage.ToString());
            }
            ResidentRecord record = new ResidentRecord(
                (ResidentId)row.GetLong("id", "payload.residents"),
                row.GetString("name", "payload.residents"),
                (LifeStage)stage,
                (HouseholdId)row.GetLong("household", "payload.residents"),
                (HomeId)row.GetLong("home", "payload.residents"));
            record.SetMoney(row.GetLong("money", "payload.residents"));
            record.SetCareer(
                row.GetString("jobId", "payload.residents"),
                row.GetInt("careerLevel", "payload.residents"),
                row.GetInt("careerPoints", "payload.residents"));
            record.SetEducation(row.GetInt("educationPoints", "payload.residents"));
            record.SetAlive(row.GetBool("alive", "payload.residents"));
            record.SetResidentSequence(row.GetUInt("residentSequence", "payload.residents"));
            record.SetDecisionSlotGeneration(row.GetUInt("decisionSlot", "payload.residents"));
            record.SetControlGeneration(row.GetUInt("controlGen", "payload.residents"));
            record.SetStage((LifeStage)stage, row.GetFloat("stageProgress", "payload.residents"));
            record.SetHealthFlags(
                row.GetFloat("starvationHours", "payload.residents"),
                row.GetBool("ill", "payload.residents"),
                row.GetBool("fatigued", "payload.residents"),
                row.GetBool("injured", "payload.residents"),
                row.GetInt("lastChanceDrawDay", "payload.residents"));
            JsonArray needs = row.GetArray("needs", "payload.residents");
            if (needs.Count != StateSchema.NeedCount)
            {
                throw new JsonSaveException("payload.residents: needs has "
                    + needs.Count.ToString() + " entries, want "
                    + StateSchema.NeedCount.ToString());
            }
            for (int i = 0; i < StateSchema.NeedCount; i++)
            {
                record.SetNeed(i, needs.GetFloat(i, "payload.residents.needs"));
            }
            JsonArray traits = row.GetArray("traits", "payload.residents");
            if (traits.Count != StateSchema.TraitCount)
            {
                throw new JsonSaveException("payload.residents: traits has "
                    + traits.Count.ToString() + " entries, want "
                    + StateSchema.TraitCount.ToString());
            }
            for (int i = 0; i < StateSchema.TraitCount; i++)
            {
                record.SetTrait(i, traits.GetInt(i, "payload.residents.traits"));
            }
            JsonArray skills = row.GetArray("skills", "payload.residents");
            for (int i = 0; i < skills.Count; i++)
            {
                JsonObject skill = skills.GetObject(i, "payload.residents.skills");
                SkillEntry entry = new SkillEntry(
                    skill.GetString("id", "payload.residents.skills"),
                    skill.GetInt("level", "payload.residents.skills"),
                    skill.GetInt("points", "payload.residents.skills"));
                record.SkillList.Add(entry);
            }
            JsonArray bonds = row.GetArray("bonds", "payload.residents");
            for (int i = 0; i < bonds.Count; i++)
            {
                JsonObject bond = bonds.GetObject(i, "payload.residents.bonds");
                PairBond entry = new PairBond(
                    (ResidentId)bond.GetLong("other", "payload.residents.bonds"), 0.0f, 0.0f);
                entry.Set(
                    bond.GetFloat("friendship", "payload.residents.bonds"),
                    bond.GetFloat("romance", "payload.residents.bonds"));
                record.BondList.Add(entry);
            }
            JsonArray family = row.GetArray("family", "payload.residents");
            for (int i = 0; i < family.Count; i++)
            {
                JsonObject link = family.GetObject(i, "payload.residents.family");
                record.FamilyList.Add(new FamilyLink(
                    (ResidentId)link.GetLong("other", "payload.residents.family"),
                    link.GetString("kind", "payload.residents.family")));
            }
            JsonArray feelings = row.GetArray("feelings", "payload.residents");
            for (int i = 0; i < feelings.Count; i++)
            {
                JsonObject feeling = feelings.GetObject(i, "payload.residents.feelings");
                int kind = feeling.GetInt("kind", "payload.residents.feelings");
                if (!Enum.IsDefined(typeof(FeelingKind), (FeelingKind)kind))
                {
                    throw new JsonSaveException("payload.residents: unknown feeling kind "
                        + kind.ToString());
                }
                record.FeelingList.Add(new Feeling(
                    (FeelingKind)kind,
                    (EventId)feeling.GetLong("event", "payload.residents.feelings"),
                    (ResidentId)feeling.GetLong("other", "payload.residents.feelings"),
                    feeling.GetInt("intensity", "payload.residents.feelings"),
                    feeling.GetLong("expiry", "payload.residents.feelings")));
            }
            JsonArray memories = row.GetArray("memories", "payload.residents");
            for (int i = 0; i < memories.Count; i++)
            {
                JsonObject memory = memories.GetObject(i, "payload.residents.memories");
                JsonArray parts = memory.GetArray("participants", "payload.residents.memories");
                List<ResidentId> participants = new List<ResidentId>();
                for (int p = 0; p < parts.Count; p++)
                {
                    participants.Add((ResidentId)parts.GetLong(p, "payload.residents.memories"));
                }
                record.MemoryList.Add(new MemoryEntry(
                    (EventId)memory.GetLong("event", "payload.residents.memories"),
                    memory.GetString("kind", "payload.residents.memories"),
                    memory.GetLong("gameSeconds", "payload.residents.memories"),
                    (HomeId)memory.GetLong("place", "payload.residents.memories"),
                    participants.AsReadOnly(),
                    memory.GetString("outcome", "payload.residents.memories"),
                    memory.GetString("learnedBy", "payload.residents.memories")));
            }
            JsonArray courses = row.GetArray("courses", "payload.residents");
            for (int i = 0; i < courses.Count; i++)
            {
                record.AddCompletedCourse(courses.GetString(i, "payload.residents.courses"));
            }
            return record;
        }

        private static List<AppearanceEntry> DeserializeAppearances(JsonObject payload)
        {
            JsonArray array = payload.GetArray("appearances", "payload");
            List<AppearanceEntry> rows = new List<AppearanceEntry>();
            for (int i = 0; i < array.Count; i++)
            {
                JsonObject row = array.GetObject(i, "payload.appearances");
                if (row.Has("build") || row.Has("height"))
                {
                    throw new JsonSaveException("payload.appearances: row " + i.ToString()
                        + " carries a removed body build or height field");
                }
                AppearanceEntry entry = new AppearanceEntry();
                entry.ResidentId = row.GetLong("resident", "payload.appearances");
                entry.SkinTone = row.GetInt("skin", "payload.appearances");
                entry.Face = row.GetInt("face", "payload.appearances");
                entry.EyeColor = row.GetInt("eyecolor", "payload.appearances");
                entry.HairType = row.GetInt("hairtype", "payload.appearances");
                entry.HairStyle = row.GetInt("hairstyle", "payload.appearances");
                entry.HairColor = row.GetInt("haircolor", "payload.appearances");
                entry.Accessory = row.GetInt("accessory", "payload.appearances");
                entry.OutfitEveryday = row.GetInt("everyday", "payload.appearances");
                entry.OutfitSleep = row.GetInt("sleep", "payload.appearances");
                entry.OutfitExercise = row.GetInt("exercise", "payload.appearances");
                entry.OverrideCategory = row.GetInt("override", "payload.appearances");
                // Absent key means a pre-BODY-PRESET save: male, no RNG draw.
                int baseBody = row.Has("baseBody") ? row.GetInt("baseBody", "payload.appearances") : 0;
                try
                {
                    ResidentFactory.CheckBaseBodyRange(baseBody);
                }
                catch (ArgumentException e)
                {
                    throw new JsonSaveException("payload.appearances: row " + i.ToString()
                        + " carries an out-of-range base body: " + e.Message);
                }
                entry.BaseBody = (BaseBody)baseBody;
                rows.Add(entry);
            }
            return rows;
        }

        // Queue actions take fresh ids on load, so service state follows the
        // saved-to-fresh map; ids outside the map (non-queue payments and
        // dropped history) keep their saved values, which can never collide
        // with future allocations from the persisted high-water marks.
        private static long RemapAction(Dictionary<long, long> actionMap, long saved)
        {
            long fresh;
            if (actionMap.TryGetValue(saved, out fresh))
            {
                return fresh;
            }
            return saved;
        }

        // Service rows reference actions allocated before the save, so every
        // saved id sits below the persisted high-water mark; anything at or
        // above it is forged and would collide with a fresh allocation.
        private static void RequireSavedActionBelowHighWater(long saved, long nextActionId,
            string path)
        {
            if (saved >= nextActionId)
            {
                throw new JsonSaveException(path + ": action " + saved.ToString()
                    + " is at or above the saved high-water mark " + nextActionId.ToString());
            }
        }

        private static AccountsSnapshot DeserializeAccounts(JsonObject payload,
            Dictionary<long, long> actionMap)
        {
            JsonObject section = payload.GetObject("accounts", "payload");
            long nextActionId = payload.GetObject("world", "payload")
                .GetLong("nextActionId", "payload.world");
            AccountsSnapshot snapshot = new AccountsSnapshot();
            JsonArray shared = section.GetArray("shared", "payload.accounts");
            for (int i = 0; i < shared.Count; i++)
            {
                JsonObject row = shared.GetObject(i, "payload.accounts.shared");
                AccountsSnapshot.BalanceRow balance = new AccountsSnapshot.BalanceRow();
                balance.Household = row.GetLong("household", "payload.accounts.shared");
                balance.Amount = row.GetLong("balance", "payload.accounts.shared");
                snapshot.Shared.Add(balance);
            }
            JsonArray ledger = section.GetArray("ledger", "payload.accounts");
            for (int i = 0; i < ledger.Count; i++)
            {
                JsonObject row = ledger.GetObject(i, "payload.accounts.ledger");
                AccountsSnapshot.LedgerRow tx = new AccountsSnapshot.LedgerRow();
                long savedAction = row.GetLong("action", "payload.accounts.ledger");
                RequireSavedActionBelowHighWater(savedAction, nextActionId,
                    "payload.accounts.ledger");
                tx.Action = RemapAction(actionMap, savedAction);
                tx.Source = DeserializeAccountRef(
                    row.GetObject("source", "payload.accounts.ledger"), "payload.accounts.ledger.source");
                tx.Destination = DeserializeAccountRef(
                    row.GetObject("destination", "payload.accounts.ledger"),
                    "payload.accounts.ledger.destination");
                tx.Amount = row.GetLong("amount", "payload.accounts.ledger");
                tx.GameSeconds = row.GetLong("gameSeconds", "payload.accounts.ledger");
                snapshot.Ledger.Add(tx);
            }
            JsonArray applied = section.GetArray("applied", "payload.accounts");
            for (int i = 0; i < applied.Count; i++)
            {
                long savedApplied = applied.GetLong(i, "payload.accounts.applied");
                RequireSavedActionBelowHighWater(savedApplied, nextActionId,
                    "payload.accounts.applied");
                snapshot.Applied.Add(RemapAction(actionMap, savedApplied));
            }
            return snapshot;
        }

        private static AccountsSnapshot.AccountRefData DeserializeAccountRef(JsonObject row, string path)
        {
            AccountsSnapshot.AccountRefData data = new AccountsSnapshot.AccountRefData();
            data.Kind = row.GetString("kind", path);
            data.Id = row.GetLong("id", path);
            data.Name = row.GetString("name", path);
            return data;
        }

        private static Food.FoodSnapshot DeserializeFood(JsonObject payload,
            Dictionary<long, long> actionMap)
        {
            JsonObject section = payload.GetObject("food", "payload");
            long nextActionId = payload.GetObject("world", "payload")
                .GetLong("nextActionId", "payload.world");
            Food.FoodSnapshot snapshot = new Food.FoodSnapshot();
            JsonArray stock = section.GetArray("stock", "payload.food");
            for (int i = 0; i < stock.Count; i++)
            {
                JsonObject row = stock.GetObject(i, "payload.food.stock");
                Food.FoodSnapshot.StockRow entry = new Food.FoodSnapshot.StockRow();
                entry.Home = row.GetLong("home", "payload.food.stock");
                entry.Groceries = row.GetInt("groceries", "payload.food.stock");
                entry.Meals = row.GetInt("meals", "payload.food.stock");
                snapshot.Stock.Add(entry);
            }
            JsonArray tracked = section.GetArray("tracked", "payload.food");
            for (int i = 0; i < tracked.Count; i++)
            {
                JsonObject row = tracked.GetObject(i, "payload.food.tracked");
                Food.FoodSnapshot.TrackedRow entry = new Food.FoodSnapshot.TrackedRow();
                long savedAction = row.GetLong("action", "payload.food.tracked");
                RequireSavedActionBelowHighWater(savedAction, nextActionId,
                    "payload.food.tracked");
                entry.Action = RemapAction(actionMap, savedAction);
                entry.Resident = row.GetLong("resident", "payload.food.tracked");
                entry.Code = row.GetString("code", "payload.food.tracked");
                entry.ReservedGroceries = row.GetInt("reservedGroceries", "payload.food.tracked");
                entry.ReservedMeals = row.GetInt("reservedMeals", "payload.food.tracked");
                entry.Done = row.GetBool("done", "payload.food.tracked");
                // Dropped work has no fresh queue id and cannot ever release its saved reservation.
                if (!entry.Done && !actionMap.ContainsKey(savedAction))
                {
                    entry.Done = true;
                }
                snapshot.Tracked.Add(entry);
            }
            return snapshot;
        }

        private static Home.HomeSnapshot DeserializeHome(JsonObject payload,
            Dictionary<long, long> actionMap)
        {
            JsonObject section = payload.GetObject("home", "payload");
            long nextActionId = payload.GetObject("world", "payload")
                .GetLong("nextActionId", "payload.world");
            Home.HomeSnapshot snapshot = new Home.HomeSnapshot();
            JsonArray states = section.GetArray("states", "payload.home");
            for (int i = 0; i < states.Count; i++)
            {
                JsonObject row = states.GetObject(i, "payload.home.states");
                Home.HomeSnapshot.StateRow entry = new Home.HomeSnapshot.StateRow();
                entry.Home = row.GetLong("home", "payload.home.states");
                entry.Cleanliness = row.GetDouble("cleanliness", "payload.home.states");
                entry.Supplies = row.GetInt("supplies", "payload.home.states");
                entry.LastGameSeconds = row.GetLong("lastGameSeconds", "payload.home.states");
                snapshot.States.Add(entry);
            }
            JsonArray damaged = section.GetArray("damaged", "payload.home");
            for (int i = 0; i < damaged.Count; i++)
            {
                JsonObject row = damaged.GetObject(i, "payload.home.damaged");
                Home.HomeSnapshot.DamageRow entry = new Home.HomeSnapshot.DamageRow();
                entry.Object = row.GetLong("object", "payload.home.damaged");
                entry.Damaged = row.GetBool("damaged", "payload.home.damaged");
                snapshot.Damaged.Add(entry);
            }
            JsonArray tracked = section.GetArray("tracked", "payload.home");
            for (int i = 0; i < tracked.Count; i++)
            {
                JsonObject row = tracked.GetObject(i, "payload.home.tracked");
                Home.HomeSnapshot.TrackedRow entry = new Home.HomeSnapshot.TrackedRow();
                long savedAction = row.GetLong("action", "payload.home.tracked");
                RequireSavedActionBelowHighWater(savedAction, nextActionId,
                    "payload.home.tracked");
                entry.Action = RemapAction(actionMap, savedAction);
                entry.Resident = row.GetLong("resident", "payload.home.tracked");
                entry.Code = row.GetString("code", "payload.home.tracked");
                entry.Done = row.GetBool("done", "payload.home.tracked");
                // Dropped work has no fresh queue id and can never complete.
                if (!entry.Done && !actionMap.ContainsKey(savedAction))
                {
                    entry.Done = true;
                }
                snapshot.Tracked.Add(entry);
            }
            return snapshot;
        }

        private static Expenses.ExpensesSnapshot DeserializeExpenses(JsonObject payload)
        {
            JsonObject section = payload.GetObject("expenses", "payload");
            Expenses.ExpensesSnapshot snapshot = new Expenses.ExpensesSnapshot();
            snapshot.NextDueId = section.GetLong("nextDueId", "payload.expenses");
            JsonArray homes = section.GetArray("homes", "payload.expenses");
            for (int i = 0; i < homes.Count; i++)
            {
                JsonObject row = homes.GetObject(i, "payload.expenses.homes");
                Expenses.ExpensesSnapshot.HomeRow home = new Expenses.ExpensesSnapshot.HomeRow();
                home.Home = row.GetLong("home", "payload.expenses.homes");
                home.Rented = row.GetBool("rented", "payload.expenses.homes");
                home.NextWeek = row.GetLong("nextWeek", "payload.expenses.homes");
                JsonArray funding = row.GetArray("funding", "payload.expenses.homes");
                for (int j = 0; j < funding.Count; j++)
                {
                    home.Funding.Add(DeserializeAccountRef(
                        funding.GetObject(j, "payload.expenses.homes.funding"),
                        "payload.expenses.homes.funding"));
                }
                snapshot.Homes.Add(home);
            }
            JsonArray dues = section.GetArray("dues", "payload.expenses");
            for (int i = 0; i < dues.Count; i++)
            {
                JsonObject row = dues.GetObject(i, "payload.expenses.dues");
                Expenses.ExpensesSnapshot.DueRow due = new Expenses.ExpensesSnapshot.DueRow();
                due.DueId = row.GetLong("dueId", "payload.expenses.dues");
                due.Home = row.GetLong("home", "payload.expenses.dues");
                due.Kind = row.GetString("kind", "payload.expenses.dues");
                due.Amount = row.GetLong("amount", "payload.expenses.dues");
                due.DueGameSeconds = row.GetLong("dueGameSeconds", "payload.expenses.dues");
                due.Paid = row.GetLong("paid", "payload.expenses.dues");
                due.Shortfall = row.GetLong("shortfall", "payload.expenses.dues");
                due.Divisible = row.GetBool("divisible", "payload.expenses.dues");
                snapshot.Dues.Add(due);
            }
            return snapshot;
        }

        // SHARES ownership restore: histories and listings carry no action
        // ids (settlement transfers live in the accounts ledger), so no
        // saved-to-fresh remap applies here.
        private static OwnershipSnapshot DeserializeOwnership(JsonObject payload)
        {
            JsonObject section = payload.GetObject("ownership", "payload");
            OwnershipSnapshot snapshot = new OwnershipSnapshot();
            JsonArray history = section.GetArray("history", "payload.ownership");
            for (int h = 0; h < history.Count; h++)
            {
                JsonObject homeRow = history.GetObject(h, "payload.ownership.history");
                OwnershipSnapshot.HomeHistory saved = new OwnershipSnapshot.HomeHistory();
                saved.Home = homeRow.GetLong("home", "payload.ownership.history");
                JsonArray agreements = homeRow.GetArray("agreements", "payload.ownership.history");
                for (int a = 0; a < agreements.Count; a++)
                {
                    JsonObject agreementRow = agreements.GetObject(a, "payload.ownership.history.agreements");
                    OwnershipSnapshot.AgreementRow agreement = new OwnershipSnapshot.AgreementRow();
                    agreement.Value = agreementRow.GetLong("value", "payload.ownership.history.agreements");
                    JsonArray shares = agreementRow.GetArray("shares", "payload.ownership.history.agreements");
                    for (int s = 0; s < shares.Count; s++)
                    {
                        JsonObject shareRow = shares.GetObject(s, "payload.ownership.shares");
                        OwnershipSnapshot.ShareRow share = new OwnershipSnapshot.ShareRow();
                        share.Household = shareRow.GetLong("household", "payload.ownership.shares");
                        share.Percent = shareRow.GetInt("percent", "payload.ownership.shares");
                        share.ChildcareDays = shareRow.GetInt("childcareDays", "payload.ownership.shares");
                        agreement.Shares.Add(share);
                    }
                    saved.Agreements.Add(agreement);
                }
                snapshot.History.Add(saved);
            }
            JsonArray listings = section.GetArray("listings", "payload.ownership");
            for (int i = 0; i < listings.Count; i++)
            {
                JsonObject row = listings.GetObject(i, "payload.ownership.listings");
                OwnershipSnapshot.ListingRow saved = new OwnershipSnapshot.ListingRow();
                saved.Home = row.GetLong("home", "payload.ownership.listings");
                saved.Seller = row.GetLong("seller", "payload.ownership.listings");
                saved.Value = row.GetLong("value", "payload.ownership.listings");
                saved.AmountDue = row.GetLong("amountDue", "payload.ownership.listings");
                saved.DueGameSeconds = row.GetLong("dueGameSeconds", "payload.ownership.listings");
                snapshot.Listings.Add(saved);
            }
            return snapshot;
        }

        // CLUB restore: row lists carry no action ids, so no saved-to-fresh
        // remap applies here.
        private static ClubSnapshot DeserializeClub(JsonObject payload)
        {
            JsonObject section = payload.GetObject("club", "payload");
            ClubSnapshot snapshot = new ClubSnapshot();
            JsonArray members = section.GetArray("members", "payload.club");
            for (int i = 0; i < members.Count; i++)
            {
                JsonObject row = members.GetObject(i, "payload.club.members");
                ClubSnapshot.MemberRow saved = new ClubSnapshot.MemberRow();
                saved.Club = row.GetString("club", "payload.club.members");
                saved.Resident = row.GetLong("resident", "payload.club.members");
                snapshot.Members.Add(saved);
            }
            JsonArray standings = section.GetArray("standings", "payload.club");
            for (int i = 0; i < standings.Count; i++)
            {
                JsonObject row = standings.GetObject(i, "payload.club.standings");
                ClubSnapshot.StandingRow saved = new ClubSnapshot.StandingRow();
                saved.Club = row.GetString("club", "payload.club.standings");
                saved.Resident = row.GetLong("resident", "payload.club.standings");
                saved.Count = row.GetInt("count", "payload.club.standings");
                snapshot.Standings.Add(saved);
            }
            JsonArray targets = section.GetArray("targets", "payload.club");
            for (int i = 0; i < targets.Count; i++)
            {
                JsonObject row = targets.GetObject(i, "payload.club.targets");
                ClubSnapshot.TargetRow saved = new ClubSnapshot.TargetRow();
                saved.Target = row.GetLong("target", "payload.club.targets");
                saved.Household = row.GetLong("household", "payload.club.targets");
                saved.State = row.GetString("state", "payload.club.targets");
                snapshot.Targets.Add(saved);
            }
            JsonArray helps = section.GetArray("helps", "payload.club");
            for (int i = 0; i < helps.Count; i++)
            {
                JsonObject row = helps.GetObject(i, "payload.club.helps");
                ClubSnapshot.HelpRow saved = new ClubSnapshot.HelpRow();
                saved.Event = row.GetLong("event", "payload.club.helps");
                saved.Helper = row.GetLong("helper", "payload.club.helps");
                saved.Target = row.GetLong("target", "payload.club.helps");
                snapshot.Helps.Add(saved);
            }
            JsonArray evaluated = section.GetArray("evaluated", "payload.club");
            for (int i = 0; i < evaluated.Count; i++)
            {
                JsonObject row = evaluated.GetObject(i, "payload.club.evaluated");
                ClubSnapshot.EvaluatedRow saved = new ClubSnapshot.EvaluatedRow();
                saved.Resident = row.GetLong("resident", "payload.club.evaluated");
                saved.Event = row.GetLong("event", "payload.club.evaluated");
                snapshot.Evaluated.Add(saved);
            }
            return snapshot;
        }

        private static Housing.HousingSnapshot DeserializeHousing(JsonObject payload)
        {
            JsonObject section = payload.GetObject("housing", "payload");
            Housing.HousingSnapshot snapshot = new Housing.HousingSnapshot();
            JsonArray homes = section.GetArray("homes", "payload.housing");
            for (int i = 0; i < homes.Count; i++)
            {
                JsonObject row = homes.GetObject(i, "payload.housing.homes");
                Housing.HousingSnapshot.HomeRow r = new Housing.HousingSnapshot.HomeRow();
                r.Home = row.GetLong("home", "payload.housing.homes");
                r.Price = row.GetLong("price", "payload.housing.homes");
                snapshot.Homes.Add(r);
            }
            JsonArray furniture = section.GetArray("furniture", "payload.housing");
            for (int i = 0; i < furniture.Count; i++)
            {
                JsonObject row = furniture.GetObject(i, "payload.housing.furniture");
                Housing.HousingSnapshot.FurnitureRow r = new Housing.HousingSnapshot.FurnitureRow();
                r.Code = row.GetString("code", "payload.housing.furniture");
                r.Price = row.GetLong("price", "payload.housing.furniture");
                r.Slots = row.GetInt("slots", "payload.housing.furniture");
                snapshot.Furniture.Add(r);
            }
            JsonArray items = section.GetArray("items", "payload.housing");
            for (int i = 0; i < items.Count; i++)
            {
                JsonObject row = items.GetObject(i, "payload.housing.items");
                Housing.HousingSnapshot.ItemRow r = new Housing.HousingSnapshot.ItemRow();
                r.Id = row.GetLong("id", "payload.housing.items");
                r.Code = row.GetString("code", "payload.housing.items");
                r.Home = row.GetLong("home", "payload.housing.items");
                r.Owner = DeserializeAccountRef(row.GetObject("owner", "payload.housing.items"), "payload.housing.items");
                r.InInventory = row.GetBool("inInventory", "payload.housing.items");
                r.PurchasePrice = row.GetLong("purchasePrice", "payload.housing.items");
                r.X = row.GetFloat("x", "payload.housing.items");
                r.Y = row.GetFloat("y", "payload.housing.items");
                r.Z = row.GetFloat("z", "payload.housing.items");
                r.Slots = row.GetInt("slots", "payload.housing.items");
                snapshot.Items.Add(r);
            }
            JsonArray caregivers = section.GetArray("caregivers", "payload.housing");
            for (int i = 0; i < caregivers.Count; i++) snapshot.Caregivers.Add(caregivers.GetLong(i, "payload.housing.caregivers"));
            JsonArray reservations = section.GetArray("reservations", "payload.housing");
            for (int i = 0; i < reservations.Count; i++)
            {
                JsonObject row = reservations.GetObject(i, "payload.housing.reservations");
                Housing.HousingSnapshot.ReservationRow r = new Housing.HousingSnapshot.ReservationRow();
                r.Household = row.GetLong("household", "payload.housing.reservations");
                r.Count = row.GetInt("count", "payload.housing.reservations");
                snapshot.Reservations.Add(r);
            }
            JsonArray vacancies = section.GetArray("vacancies", "payload.housing");
            for (int i = 0; i < vacancies.Count; i++)
            {
                JsonObject row = vacancies.GetObject(i, "payload.housing.vacancies");
                Housing.HousingSnapshot.VacancyRow r = new Housing.HousingSnapshot.VacancyRow();
                r.Home = row.GetLong("home", "payload.housing.vacancies");
                r.Since = row.GetLong("since", "payload.housing.vacancies");
                snapshot.Vacancies.Add(r);
            }
            JsonArray applied = section.GetArray("applied", "payload.housing");
            for (int i = 0; i < applied.Count; i++) snapshot.Applied.Add(applied.GetLong(i, "payload.housing.applied"));
            return snapshot;
        }

        // CREATIVE restore: item, author, memory and wall ids stay as saved;
        // Creative.Import binds them to the loaded world and refuses rows that
        // contradict it. Completion ids are terminal, so no action remap
        // applies.
        private static CreativeSnapshot DeserializeCreative(JsonObject payload)
        {
            JsonObject section = payload.GetObject("creative", "payload");
            CreativeSnapshot snapshot = new CreativeSnapshot();
            JsonArray works = section.GetArray("works", "payload.creative");
            for (int i = 0; i < works.Count; i++)
            {
                JsonObject row = works.GetObject(i, "payload.creative.works");
                CreativeSnapshot.WorkRow saved = new CreativeSnapshot.WorkRow();
                saved.Item = row.GetLong("item", "payload.creative.works");
                saved.Author = row.GetLong("author", "payload.creative.works");
                saved.PersonalitySummary = row.GetString("personality", "payload.creative.works");
                saved.MemoryEvent = row.GetLong("memory", "payload.creative.works");
                saved.QualityTier = row.GetString("quality", "payload.creative.works");
                snapshot.Works.Add(saved);
            }
            JsonArray awards = section.GetArray("awards", "payload.creative");
            for (int i = 0; i < awards.Count; i++)
            {
                JsonObject row = awards.GetObject(i, "payload.creative.awards");
                CreativeSnapshot.AwardRow saved = new CreativeSnapshot.AwardRow();
                saved.Viewer = row.GetLong("viewer", "payload.creative.awards");
                saved.Item = row.GetLong("item", "payload.creative.awards");
                saved.GameDay = row.GetLong("day", "payload.creative.awards");
                snapshot.Awards.Add(saved);
            }
            JsonArray applied = section.GetArray("applied", "payload.creative");
            for (int i = 0; i < applied.Count; i++)
            {
                JsonObject row = applied.GetObject(i, "payload.creative.applied");
                CreativeSnapshot.AppliedRow saved = new CreativeSnapshot.AppliedRow();
                saved.Action = row.GetLong("action", "payload.creative.applied");
                snapshot.Applied.Add(saved);
            }
            JsonArray displays = section.GetArray("displays", "payload.creative");
            for (int i = 0; i < displays.Count; i++)
            {
                JsonObject row = displays.GetObject(i, "payload.creative.displays");
                CreativeSnapshot.DisplayRow saved = new CreativeSnapshot.DisplayRow();
                saved.Item = row.GetLong("item", "payload.creative.displays");
                saved.Wall = row.GetLong("wall", "payload.creative.displays");
                snapshot.Displays.Add(saved);
            }
            return snapshot;
        }

        // BIRTH restore: rows are bound to the loaded world by
        // FamilyCreation.Import, which validates every resident, bed, household
        // and seeded body draw before accepting the receipt.
        private static FamilyCreation.FamilySnapshot DeserializeFamily(JsonObject payload)
        {
            JsonObject section = payload.GetObject("family", "payload");
            FamilyCreation.FamilySnapshot snapshot = new FamilyCreation.FamilySnapshot();
            JsonArray capabilities = section.GetArray("capabilities", "payload.family");
            for (int i = 0; i < capabilities.Count; i++)
            {
                JsonObject row = capabilities.GetObject(i, "payload.family.capabilities");
                FamilyCreation.FamilySnapshot.CapabilityRow saved = new FamilyCreation.FamilySnapshot.CapabilityRow();
                saved.Resident = row.GetLong("resident", "payload.family.capabilities");
                saved.CanCarry = row.GetBool("canCarry", "payload.family.capabilities");
                saved.CanImpregnate = row.GetBool("canImpregnate", "payload.family.capabilities");
                snapshot.Capabilities.Add(saved);
            }
            JsonArray partners = section.GetArray("partners", "payload.family");
            for (int i = 0; i < partners.Count; i++)
            {
                JsonObject row = partners.GetObject(i, "payload.family.partners");
                FamilyCreation.FamilySnapshot.PartnerRow saved = new FamilyCreation.FamilySnapshot.PartnerRow();
                saved.Initiator = row.GetLong("initiator", "payload.family.partners");
                saved.Partner = row.GetLong("partner", "payload.family.partners");
                saved.Consent = row.GetBool("consent", "payload.family.partners");
                snapshot.Partners.Add(saved);
            }
            JsonArray beds = section.GetArray("beds", "payload.family");
            for (int i = 0; i < beds.Count; i++)
            {
                JsonObject row = beds.GetObject(i, "payload.family.beds");
                FamilyCreation.FamilySnapshot.BedRow saved = new FamilyCreation.FamilySnapshot.BedRow();
                saved.Object = row.GetLong("object", "payload.family.beds");
                saved.Home = row.GetLong("home", "payload.family.beds");
                snapshot.Beds.Add(saved);
            }
            JsonArray pregnancies = section.GetArray("pregnancies", "payload.family");
            for (int i = 0; i < pregnancies.Count; i++)
            {
                JsonObject row = pregnancies.GetObject(i, "payload.family.pregnancies");
                FamilyCreation.FamilySnapshot.PregnancyRow saved = new FamilyCreation.FamilySnapshot.PregnancyRow();
                saved.Carrier = row.GetLong("carrier", "payload.family.pregnancies");
                saved.OtherParent = row.GetLong("otherParent", "payload.family.pregnancies");
                saved.ConceptionEvent = row.GetLong("conceptionEvent", "payload.family.pregnancies");
                saved.DueGameTime = row.GetLong("dueGameTime", "payload.family.pregnancies");
                saved.CapacityHold = row.GetInt("capacityHold", "payload.family.pregnancies");
                saved.Household = row.GetLong("household", "payload.family.pregnancies");
                snapshot.Pregnancies.Add(saved);
            }
            JsonArray births = section.GetArray("births", "payload.family");
            for (int i = 0; i < births.Count; i++)
            {
                JsonObject row = births.GetObject(i, "payload.family.births");
                FamilyCreation.FamilySnapshot.BirthRow saved = new FamilyCreation.FamilySnapshot.BirthRow();
                saved.Child = row.GetLong("child", "payload.family.births");
                saved.Carrier = row.GetLong("carrier", "payload.family.births");
                saved.OtherParent = row.GetLong("otherParent", "payload.family.births");
                saved.ConceptionEvent = row.GetLong("conceptionEvent", "payload.family.births");
                saved.BirthEvent = row.GetLong("birthEvent", "payload.family.births");
                saved.WorldSeed = row.GetLong("worldSeed", "payload.family.births");
                saved.BaseBodySeed = row.GetLong("baseBodySeed", "payload.family.births");
                saved.BaseBody = row.GetInt("baseBody", "payload.family.births");
                saved.CarrierAppearance = DeserializeAppearanceEntry(
                    row.GetObject("carrierAppearance", "payload.family.births"), "payload.family.births.carrierAppearance");
                saved.OtherParentAppearance = DeserializeAppearanceEntry(
                    row.GetObject("otherParentAppearance", "payload.family.births"), "payload.family.births.otherParentAppearance");
                saved.ChildAppearance = DeserializeAppearanceEntry(
                    row.GetObject("childAppearance", "payload.family.births"), "payload.family.births.childAppearance");
                snapshot.Births.Add(saved);
            }
            return snapshot;
        }

        private static AppearanceEntry DeserializeAppearanceEntry(JsonObject row, string path)
        {
            AppearanceEntry entry = new AppearanceEntry();
            entry.ResidentId = row.GetLong("resident", path);
            entry.SkinTone = row.GetInt("skin", path);
            entry.Face = row.GetInt("face", path);
            entry.EyeColor = row.GetInt("eyecolor", path);
            entry.HairType = row.GetInt("hairtype", path);
            entry.HairStyle = row.GetInt("hairstyle", path);
            entry.HairColor = row.GetInt("haircolor", path);
            entry.Accessory = row.GetInt("accessory", path);
            entry.OutfitEveryday = row.GetInt("everyday", path);
            entry.OutfitSleep = row.GetInt("sleep", path);
            entry.OutfitExercise = row.GetInt("exercise", path);
            entry.OverrideCategory = row.GetInt("override", path);
            entry.BaseBody = (BaseBody)row.GetInt("baseBody", path);
            return entry;
        }

        // MINIGOLF restore: course content plus match checkpoints. The shared
        // match id and putt draws are preserved; the executor reservation is
        // rebuilt by Minigolf.Import under that id.
        private static Minigolf.MinigolfSnapshot DeserializeMinigolf(JsonObject payload)
        {
            JsonObject section = payload.GetObject("minigolf", "payload");
            Minigolf.MinigolfSnapshot snapshot = new Minigolf.MinigolfSnapshot();
            JsonArray courses = section.GetArray("courses", "payload.minigolf");
            for (int i = 0; i < courses.Count; i++)
            {
                JsonObject row = courses.GetObject(i, "payload.minigolf.courses");
                Minigolf.MinigolfSnapshot.CourseRow saved = new Minigolf.MinigolfSnapshot.CourseRow();
                saved.Course = row.GetLong("course", "payload.minigolf.courses");
                saved.Friction = row.GetDouble("friction", "payload.minigolf.courses");
                saved.MetresPerSecondPerPower = row.GetDouble("metresPerSecondPerPower",
                    "payload.minigolf.courses");
                snapshot.Courses.Add(saved);
            }
            JsonArray holes = section.GetArray("holes", "payload.minigolf");
            for (int i = 0; i < holes.Count; i++)
            {
                JsonObject row = holes.GetObject(i, "payload.minigolf.holes");
                Minigolf.MinigolfSnapshot.HoleRow saved = new Minigolf.MinigolfSnapshot.HoleRow();
                saved.Course = row.GetLong("course", "payload.minigolf.holes");
                saved.Index = row.GetInt("index", "payload.minigolf.holes");
                saved.TeeX = row.GetFloat("teeX", "payload.minigolf.holes");
                saved.TeeY = row.GetFloat("teeY", "payload.minigolf.holes");
                saved.TeeZ = row.GetFloat("teeZ", "payload.minigolf.holes");
                saved.CupX = row.GetFloat("cupX", "payload.minigolf.holes");
                saved.CupY = row.GetFloat("cupY", "payload.minigolf.holes");
                saved.CupZ = row.GetFloat("cupZ", "payload.minigolf.holes");
                saved.MinX = row.GetDouble("minX", "payload.minigolf.holes");
                saved.MaxX = row.GetDouble("maxX", "payload.minigolf.holes");
                saved.MinZ = row.GetDouble("minZ", "payload.minigolf.holes");
                saved.MaxZ = row.GetDouble("maxZ", "payload.minigolf.holes");
                saved.CupRadius = row.GetDouble("cupRadius", "payload.minigolf.holes");
                snapshot.Holes.Add(saved);
            }
            JsonArray matches = section.GetArray("matches", "payload.minigolf");
            for (int i = 0; i < matches.Count; i++)
            {
                JsonObject row = matches.GetObject(i, "payload.minigolf.matches");
                Minigolf.MinigolfSnapshot.MatchRow saved = new Minigolf.MinigolfSnapshot.MatchRow();
                saved.Action = row.GetLong("action", "payload.minigolf.matches");
                saved.Course = row.GetLong("course", "payload.minigolf.matches");
                saved.HoleIndex = row.GetInt("hole", "payload.minigolf.matches");
                saved.TurnIndex = row.GetInt("turn", "payload.minigolf.matches");
                saved.ActiveIndex = row.GetInt("active", "payload.minigolf.matches");
                saved.Rolling = row.GetBool("rolling", "payload.minigolf.matches");
                saved.Completed = row.GetBool("completed", "payload.minigolf.matches");
                saved.Cancelled = row.GetBool("cancelled", "payload.minigolf.matches");
                saved.CancelRequested = row.GetBool("cancelRequested", "payload.minigolf.matches");
                saved.HasPending = row.GetBool("hasPending", "payload.minigolf.matches");
                saved.PendingDegrees = row.GetInt("pendingDegrees", "payload.minigolf.matches");
                saved.PendingPower = row.GetInt("pendingPower", "payload.minigolf.matches");
                saved.AimDegrees = row.GetInt("aimDegrees", "payload.minigolf.matches");
                saved.AimPower = row.GetInt("aimPower", "payload.minigolf.matches");
                saved.StartedAt = row.GetLong("startedAt", "payload.minigolf.matches");
                saved.LastTick = row.GetLong("lastTick", "payload.minigolf.matches");
                saved.TurnStart = row.GetLong("turnStart", "payload.minigolf.matches");
                saved.PuttStart = row.GetLong("puttStart", "payload.minigolf.matches");
                saved.PuttRestX = row.GetFloat("puttRestX", "payload.minigolf.matches");
                saved.PuttRestY = row.GetFloat("puttRestY", "payload.minigolf.matches");
                saved.PuttRestZ = row.GetFloat("puttRestZ", "payload.minigolf.matches");
                saved.RandomState = row.GetULong("randomState", "payload.minigolf.matches");
                saved.EffectsApplied = row.GetBool("effectsApplied", "payload.minigolf.matches");
                JsonArray participants = row.GetArray("participants", "payload.minigolf.matches");
                for (int p = 0; p < participants.Count; p++)
                {
                    JsonObject participantRow = participants.GetObject(p,
                        "payload.minigolf.matches.participants");
                    Minigolf.MinigolfSnapshot.ParticipantRow participant =
                        new Minigolf.MinigolfSnapshot.ParticipantRow();
                    participant.Resident = participantRow.GetLong("resident",
                        "payload.minigolf.matches.participants");
                    participant.Human = participantRow.GetBool("human",
                        "payload.minigolf.matches.participants");
                    participant.Winner = participantRow.GetBool("winner",
                        "payload.minigolf.matches.participants");
                    participant.Stroke0 = participantRow.GetInt("stroke0",
                        "payload.minigolf.matches.participants");
                    participant.Stroke1 = participantRow.GetInt("stroke1",
                        "payload.minigolf.matches.participants");
                    participant.Stroke2 = participantRow.GetInt("stroke2",
                        "payload.minigolf.matches.participants");
                    participant.Complete0 = participantRow.GetBool("complete0",
                        "payload.minigolf.matches.participants");
                    participant.Complete1 = participantRow.GetBool("complete1",
                        "payload.minigolf.matches.participants");
                    participant.Complete2 = participantRow.GetBool("complete2",
                        "payload.minigolf.matches.participants");
                    participant.X = participantRow.GetFloat("x",
                        "payload.minigolf.matches.participants");
                    participant.Y = participantRow.GetFloat("y",
                        "payload.minigolf.matches.participants");
                    participant.Z = participantRow.GetFloat("z",
                        "payload.minigolf.matches.participants");
                    participant.VelocityX = participantRow.GetFloat("velocityX",
                        "payload.minigolf.matches.participants");
                    participant.VelocityY = participantRow.GetFloat("velocityY",
                        "payload.minigolf.matches.participants");
                    participant.VelocityZ = participantRow.GetFloat("velocityZ",
                        "payload.minigolf.matches.participants");
                    saved.Participants.Add(participant);
                }
                JsonArray putts = row.GetArray("putts", "payload.minigolf.matches");
                for (int p = 0; p < putts.Count; p++)
                {
                    JsonObject puttRow = putts.GetObject(p, "payload.minigolf.matches.putts");
                    Minigolf.MinigolfSnapshot.PuttRow putt = new Minigolf.MinigolfSnapshot.PuttRow();
                    putt.Resident = puttRow.GetLong("resident", "payload.minigolf.matches.putts");
                    putt.HoleIndex = puttRow.GetInt("hole", "payload.minigolf.matches.putts");
                    putt.TurnIndex = puttRow.GetInt("turn", "payload.minigolf.matches.putts");
                    putt.Degrees = puttRow.GetInt("degrees", "payload.minigolf.matches.putts");
                    putt.Power = puttRow.GetInt("power", "payload.minigolf.matches.putts");
                    putt.HasDraw = puttRow.GetBool("hasDraw", "payload.minigolf.matches.putts");
                    putt.Draw = puttRow.IsNull("draw", "payload.minigolf.matches.putts")
                        ? 0.0 : puttRow.GetDouble("draw", "payload.minigolf.matches.putts");
                    saved.Putts.Add(putt);
                }
                snapshot.Matches.Add(saved);
            }
            return snapshot;
        }

        // IMAGE restore: rows carry raw work item ids. ImageAdapter.Import
        // binds each row to the creative section's rebuilt items and refuses
        // unknown, duplicate, non-PNG or self-contradicting content.
        private static ImageSnapshot DeserializeImages(JsonObject payload)
        {
            JsonObject section = payload.GetObject("images", "payload");
            ImageSnapshot snapshot = new ImageSnapshot();
            JsonArray items = section.GetArray("items", "payload.images");
            for (int i = 0; i < items.Count; i++)
            {
                JsonObject row = items.GetObject(i, "payload.images.items");
                ImageSnapshot.Row saved = new ImageSnapshot.Row();
                saved.Item = row.GetLong("item", "payload.images.items");
                saved.Path = row.GetString("path", "payload.images.items");
                saved.ImageBase64 = row.GetString("image", "payload.images.items");
                saved.Disclosure = row.GetString("disclosure", "payload.images.items");
                snapshot.Rows.Add(saved);
            }
            return snapshot;
        }

        // The caller's stable key is preserved; the live NAV action is remapped
        // to the replayed id so the party keeps observing the right execution.
        private static PartySnapshot DeserializeParty(JsonObject payload,
            Dictionary<long, long> actionMap)
        {
            JsonObject section = payload.GetObject("party", "payload");
            PartySnapshot snapshot = new PartySnapshot();
            JsonArray rows = section.GetArray("parties", "payload.party");
            for (int i = 0; i < rows.Count; i++)
            {
                JsonObject row = rows.GetObject(i, "payload.party.parties");
                PartySnapshot.PartyRow saved = new PartySnapshot.PartyRow();
                saved.Key = row.GetLong("key", "payload.party.parties");
                saved.Action = RemapAction(actionMap,
                    row.GetLong("action", "payload.party.parties"));
                saved.Host = row.GetLong("host", "payload.party.parties");
                saved.Start = row.GetLong("start", "payload.party.parties");
                saved.Charged = row.GetBool("charged", "payload.party.parties");
                saved.Done = row.GetBool("done", "payload.party.parties");
                JsonArray attendees = row.GetArray("attendees", "payload.party.parties");
                for (int a = 0; a < attendees.Count; a++)
                {
                    JsonObject line = attendees.GetObject(a, "payload.party.parties.attendees");
                    PartySnapshot.AttendeeRow att = new PartySnapshot.AttendeeRow();
                    att.Resident = line.GetLong("resident", "payload.party.parties.attendees");
                    att.Entry = line.GetLong("entry", "payload.party.parties.attendees");
                    att.Exit = line.GetLong("exit", "payload.party.parties.attendees");
                    att.HasDraw = line.GetBool("hasDraw", "payload.party.parties.attendees");
                    att.Draw = line.IsNull("draw", "payload.party.parties.attendees")
                        ? 0.0 : line.GetDouble("draw", "payload.party.parties.attendees");
                    saved.Attendees.Add(att);
                }
                snapshot.Rows.Add(saved);
            }
            if (section.Has("plans"))
            {
                const string where = "payload.party.plans";
                JsonArray plans = section.GetArray("plans", "payload.party");
                for (int i = 0; i < plans.Count; i++)
                {
                    JsonObject row = plans.GetObject(i, where);
                    PartyPlan plan = new PartyPlan();
                    plan.Id = row.GetLong("id", where);
                    plan.Host = row.GetLong("host", where);
                    plan.Created = row.GetLong("created", where);
                    plan.Start = row.GetLong("start", where);
                    plan.Venue = row.GetLong("venue", where);
                    plan.TravelStarted = row.GetBool("travelStarted", where);
                    long hostAction = row.GetLong("hostAction", where);
                    plan.HostAction = hostAction > 0L ? RemapAction(actionMap, hostAction) : 0L;
                    plan.Announced = row.GetBool("announced", where);
                    plan.Closed = row.GetBool("closed", where);
                    JsonArray invites = row.GetArray("invites", where);
                    for (int g = 0; g < invites.Count; g++)
                    {
                        JsonObject line = invites.GetObject(g, where + ".invites");
                        plan.Invites.Add(new PartyInvite
                        {
                            Guest = line.GetLong("guest", where + ".invites"),
                            Accepted = line.GetBool("accepted", where + ".invites"),
                            Sent = line.GetBool("sent", where + ".invites"),
                            Arrived = line.GetBool("arrived", where + ".invites"),
                            Chatted = line.GetBool("chatted", where + ".invites"),
                            NoShow = line.GetBool("noShow", where + ".invites"),
                        });
                    }
                    snapshot.Plans.Add(plan);
                }
            }
            return snapshot;
        }

        private sealed class ResumeItem
        {
            public ResidentId Resident;
            public ActionEntry Entry;
            public JsonObject Execution;
            public bool HadHolds;
            public bool Dispatched;
        }

        private static void RestorePositions(ActivityExecutor executor, JsonArray blocks)
        {
            for (int i = 0; i < blocks.Count; i++)
            {
                JsonObject block = blocks.GetObject(i, "payload.actions");
                ResidentId id = (ResidentId)block.GetLong("resident", "payload.actions");
                try
                {
                    executor.GetResidentPosition(id);
                }
                catch (ArgumentException)
                {
                    throw new JsonSaveException("payload.actions: unknown resident "
                        + ((long)id).ToString());
                }
                JsonObject pose = block.GetObject("pose", "payload.actions");
                executor.SetResidentPosition(id, new ApproachPose(
                    pose.GetFloat("x", "payload.actions.pose"),
                    pose.GetFloat("y", "payload.actions.pose"),
                    pose.GetFloat("z", "payload.actions.pose")));
            }
        }

        // Player queues are replayed through Submit in saved order: the
        // current first as Immediate, then pending as Append. CommandIds are
        // preserved; ActionInstanceIds are freshly allocated from the
        // persisted high-water marks, so no id is ever reused. Non-player
        // entries are discarded like unaccepted proposals, and currents the
        // executor had already finished are dropped instead of re-executed.
        private static Dictionary<long, bool> SavedHoldActions(JsonObject payload)
        {
            Dictionary<long, bool> held = new Dictionary<long, bool>();
            JsonObject objects = payload.GetObject("objects", "payload");
            JsonArray holds = objects.GetArray("holds", "payload.objects");
            for (int i = 0; i < holds.Count; i++)
            {
                JsonObject row = holds.GetObject(i, "payload.objects.holds");
                held[row.GetLong("action", "payload.objects.holds")] = true;
            }
            return held;
        }

        private static List<ResumeItem> ReplayQueues(WorldState world,
            CommandArbitrator arbitrator, JsonArray blocks,
            Dictionary<long, bool> heldActions, List<string> notes,
            Dictionary<long, long> actionMap)
        {
            List<ResumeItem> resume = new List<ResumeItem>();
            int discardedNonPlayer = 0;
            for (int i = 0; i < blocks.Count; i++)
            {
                JsonObject block = blocks.GetObject(i, "payload.actions");
                ResidentId id = (ResidentId)block.GetLong("resident", "payload.actions");
                if (!block.IsNull("current", "payload.actions"))
                {
                    JsonObject saved = block.GetObject("current", "payload.actions");
                    JsonObject execution = SavedExecution(saved);
                    if (execution != null
                        && execution.GetBool("terminal", "payload.actions.execution"))
                    {
                        notes.Add("resident " + ((long)id).ToString()
                            + ": dropped terminal current "
                            + saved.GetLong("action", "payload.actions") + " ("
                            + OutcomeName(saved) + "); completed work is not re-executed");
                    }
                    else if (!IsPlayerEntry(saved))
                    {
                        discardedNonPlayer++;
                        notes.Add("resident " + ((long)id).ToString()
                            + ": discarded non-player current "
                            + saved.GetLong("action", "payload.actions"));
                    }
                    else
                    {
                        ActionEntry entry = ReplayEntry(world, arbitrator, id, saved,
                            QueueMode.Immediate, notes);
                        if (entry != null)
                        {
                            actionMap[saved.GetLong("action", "payload.actions")] =
                                (long)entry.Action;
                            ResumeItem item = new ResumeItem();
                            item.Resident = id;
                            item.Entry = entry;
                            item.Execution = execution;
                            item.HadHolds = heldActions.ContainsKey(
                                saved.GetLong("action", "payload.actions"));
                            resume.Add(item);
                        }
                    }
                }
                JsonArray pending = block.GetArray("pending", "payload.actions");
                for (int p = 0; p < pending.Count; p++)
                {
                    JsonObject saved = pending.GetObject(p, "payload.actions.pending");
                    if (!IsPlayerEntry(saved))
                    {
                        discardedNonPlayer++;
                        continue;
                    }
                    JsonObject pendingExecution = SavedExecution(saved);
                    if (pendingExecution != null && !pendingExecution.GetBool(
                        "terminal", "payload.actions.execution"))
                    {
                        notes.Add("resident " + ((long)id).ToString()
                            + ": pending entry "
                            + saved.GetLong("action", "payload.actions")
                            + " had live execution state; it re-queues fresh");
                    }
                    // No auto-promotion: a terminal-at-save current restores
                    // as an empty current with pending intact, and the game
                    // loop promotes the head through the normal path.
                    ActionEntry pendingEntry = ReplayEntry(world, arbitrator, id, saved,
                        QueueMode.Append, notes);
                    if (pendingEntry != null)
                    {
                        actionMap[saved.GetLong("action", "payload.actions")] =
                            (long)pendingEntry.Action;
                    }
                }
            }
            if (discardedNonPlayer > 0)
            {
                notes.Add("discarded " + discardedNonPlayer.ToString()
                    + " non-player queued entries; fresh decisions replace them");
            }
            return resume;
        }

        private static bool IsPlayerEntry(JsonObject saved)
        {
            return saved.GetInt("owner", "payload.actions") == (int)ActionOwner.Player;
        }

        private static JsonObject SavedExecution(JsonObject saved)
        {
            if (saved.IsNull("execution", "payload.actions"))
            {
                return null;
            }
            return saved.GetObject("execution", "payload.actions");
        }

        private static string OutcomeName(JsonObject saved)
        {
            JsonObject execution = SavedExecution(saved);
            if (execution == null || execution.IsNull("outcome", "payload.actions.execution"))
            {
                return "unknown";
            }
            return ((TerminalOutcome)execution.GetInt("outcome",
                "payload.actions.execution")).ToString();
        }

        // One replayed Submit. Sequences chain from live state, so every
        // replay is exactly current plus one. A rejection drops that entry
        // with a note (deceased resident, host-missing target); anything
        // else would be a corrupt payload, which the hash already excludes.
        private static ActionEntry ReplayEntry(WorldState world, CommandArbitrator arbitrator,
            ResidentId id, JsonObject saved, QueueMode mode, List<string> notes)
        {
            int kind = saved.GetInt("kind", "payload.actions");
            if (!Enum.IsDefined(typeof(CommandKind), (CommandKind)kind))
            {
                notes.Add("resident " + ((long)id).ToString() + ": dropped queue entry "
                    + saved.GetLong("action", "payload.actions") + " (unknown kind "
                    + kind.ToString() + ")");
                return null;
            }
            if ((CommandKind)kind != CommandKind.DirectActivity
                && (CommandKind)kind != CommandKind.TravelTo)
            {
                notes.Add("resident " + ((long)id).ToString() + ": dropped queue entry "
                    + saved.GetLong("action", "payload.actions") + " (kind "
                    + ((CommandKind)kind).ToString() + " is not resumable)");
                return null;
            }
            int owner = saved.GetInt("owner", "payload.actions");
            if (!Enum.IsDefined(typeof(ActionOwner), (ActionOwner)owner))
            {
                notes.Add("resident " + ((long)id).ToString() + ": dropped queue entry "
                    + saved.GetLong("action", "payload.actions") + " (unknown owner)");
                return null;
            }
            CommandSource source = CommandSource.PlayerHuman;
            if (!saved.IsNull("source", "payload.actions"))
            {
                int sourceValue = saved.GetInt("source", "payload.actions");
                if (!Enum.IsDefined(typeof(CommandSource), (CommandSource)sourceValue))
                {
                    notes.Add("resident " + ((long)id).ToString() + ": dropped queue entry "
                        + saved.GetLong("action", "payload.actions") + " (unknown source)");
                    return null;
                }
                source = (CommandSource)sourceValue;
            }
            long? target = null;
            if (!saved.IsNull("target", "payload.actions"))
            {
                target = saved.GetLong("target", "payload.actions");
            }
            ResidentRecord record;
            if (!world.TryGetResident(id, out record))
            {
                throw new JsonSaveException("payload.actions: unknown resident "
                    + ((long)id).ToString());
            }
            // Replay accepts everything the live game accepted: automation is
            // enabled per submit so automated-source entries survive human
            // takeovers in the same queue, and the saved value is restored
            // after the last replay.
            arbitrator.EnableAutomation();
            CommandParameters parameters;
            ActivityId? activity = null;
            if ((CommandKind)kind == CommandKind.DirectActivity)
            {
                parameters = new CommandParameters.Direct(mode);
                activity = new ActivityId(saved.GetString("activity", "payload.actions"));
            }
            else
            {
                int targetKind = saved.Has("target_kind") ? saved.GetInt("target_kind", "payload.actions") : 0;
                if (!Enum.IsDefined(typeof(TravelTargetKind), (TravelTargetKind)targetKind))
                    throw new JsonSaveException("payload.actions: invalid travel target kind");
                parameters = new CommandParameters.Travel(mode, (TravelTargetKind)targetKind);
            }
            CommandResult result = arbitrator.Submit(new CommandEnvelope(
                (CommandId)saved.GetLong("command", "payload.actions"),
                world.Epoch,
                world.GameSeconds,
                source,
                record.ResidentSequence + 1u,
                (CommandKind)kind,
                id,
                activity,
                target,
                parameters));
            if (!result.Accepted)
            {
                notes.Add("resident " + ((long)id).ToString() + ": dropped queue entry "
                    + saved.GetLong("action", "payload.actions") + " ("
                    + result.Reason.ToString() + ")");
                return null;
            }
            return FindReplayed(arbitrator, id,
                saved.GetLong("command", "payload.actions"));
        }

        // The replayed entry keeps its saved CommandId, which locates it
        // whether promotion ran (unpaused) or it waits at the pending head
        // (paused save).
        private static ActionEntry FindReplayed(CommandArbitrator arbitrator,
            ResidentId id, long command)
        {
            ActionEntry current = arbitrator.GetCurrent(id);
            if (current != null && (long)current.Command == command)
            {
                return current;
            }
            ActionEntry[] pending = arbitrator.GetPending(id);
            for (int i = 0; i < pending.Length; i++)
            {
                if ((long)pending[i].Command == command)
                {
                    return pending[i];
                }
            }
            return null;
        }

        private static void RestoreWills(WorldState world, CommandArbitrator arbitrator,
            JsonArray blocks, List<string> notes)
        {
            for (int i = 0; i < blocks.Count; i++)
            {
                JsonObject block = blocks.GetObject(i, "payload.actions");
                ResidentId id = (ResidentId)block.GetLong("resident", "payload.actions");
                ResidentRecord known;
                if (!world.TryGetResident(id, out known))
                {
                    throw new JsonSaveException("payload.actions: unknown resident "
                        + ((long)id).ToString());
                }
                if (!block.IsNull("will", "payload.actions"))
                {
                    JsonObject saved = block.GetObject("will", "payload.actions");
                    ResidentRecord record;
                    world.TryGetResident(id, out record);
                    CommandResult result;
                    arbitrator.BeginWillRestore();
                    try
                    {
                        result = arbitrator.Submit(new CommandEnvelope(
                            world.AllocateCommand(Writer),
                            world.Epoch,
                            world.GameSeconds,
                            CommandSource.PlayerHuman,
                            record.ResidentSequence + 1u,
                            CommandKind.WillEdit,
                            id,
                            null,
                            null,
                            new CommandParameters.Will(DeserializeWill(saved))));
                    }
                    finally
                    {
                        arbitrator.EndWillRestore();
                    }
                    if (!result.Accepted)
                    {
                        notes.Add("resident " + ((long)id).ToString()
                            + ": will not restored (" + result.Reason.ToString() + ")");
                    }
                }
                if (!block.IsNull("caregiver", "payload.actions"))
                {
                    ResidentId guardian = (ResidentId)block.GetLong("caregiver", "payload.actions");
                    ResidentRecord record;
                    world.TryGetResident(id, out record);
                    CommandResult result = arbitrator.Submit(new CommandEnvelope(
                        world.AllocateCommand(Writer),
                        world.Epoch,
                        world.GameSeconds,
                        CommandSource.PlayerHuman,
                        record.ResidentSequence + 1u,
                        CommandKind.CaregiverAssign,
                        id,
                        null,
                        null,
                        new CommandParameters.Caregiver(guardian)));
                    if (!result.Accepted)
                    {
                        notes.Add("resident " + ((long)id).ToString()
                            + ": caregiver not restored (" + result.Reason.ToString() + ")");
                    }
                }
            }
        }

        private static WillRecord DeserializeWill(JsonObject saved)
        {
            JsonArray shares = saved.GetArray("shares", "payload.actions.will");
            List<MoneyShare> money = new List<MoneyShare>();
            for (int i = 0; i < shares.Count; i++)
            {
                JsonObject share = shares.GetObject(i, "payload.actions.will");
                money.Add(new MoneyShare(
                    share.GetString("recipient", "payload.actions.will"),
                    share.GetInt("percent", "payload.actions.will")));
            }
            JsonArray heirlooms = saved.GetArray("heirlooms", "payload.actions.will");
            List<ObjectId> items = new List<ObjectId>();
            for (int i = 0; i < heirlooms.Count; i++)
            {
                items.Add((ObjectId)heirlooms.GetLong(i, "payload.actions.will"));
            }
            JsonArray households = saved.GetArray("careHouseholds", "payload.actions.will");
            List<HouseholdId> care = new List<HouseholdId>();
            for (int i = 0; i < households.Count; i++)
            {
                care.Add((HouseholdId)households.GetLong(i, "payload.actions.will"));
            }
            return new WillRecord(
                money.AsReadOnly(),
                saved.GetInt("homePercent", "payload.actions.will"),
                items.AsReadOnly(),
                saved.GetString("note", "payload.actions.will"),
                saved.GetString("funeral", "payload.actions.will"),
                care.AsReadOnly());
        }

        private static void RestoreAutomation(WorldState world, CommandArbitrator arbitrator,
            bool saved, List<string> notes)
        {
            if (saved && !arbitrator.AutomationEnabled)
            {
                arbitrator.EnableAutomation();
                return;
            }
            if (!saved && arbitrator.AutomationEnabled)
            {
                ResidentRecord[] residents = world.ResidentsSnapshot();
                if (residents.Length == 0)
                {
                    notes.Add("automation was disabled at save but no resident"
                        + " exists to retake control; it stays enabled");
                    return;
                }
                ResidentId lowest = residents[0].Id;
                for (int i = 1; i < residents.Length; i++)
                {
                    if ((long)residents[i].Id < (long)lowest)
                    {
                        lowest = residents[i].Id;
                    }
                }
                arbitrator.Takeover(lowest);
                notes.Add("automation restored to disabled via takeover");
            }
        }

        // Re-dispatch interprets the saved stage: holders before waiters so
        // re-acquisition matches, and the saved perform/enqueue clocks are
        // written back so elapsed progress is preserved exactly. A dispatch
        // that fails (object gone, unreachable) reconciles the action to
        // failed with a note; its ledger entry stays Reserved and no reward
        // is paid, paused or not. The Queued flag always matches the live
        // waiters list, never the stale saved value alone.
        // Slot actions saved mid-perform re-travel after re-acquiring their
        // hold, so one pump tick re-arrives the co-located ones and their
        // saved perform clock is written back again (arrival re-enters
        // perform, which would otherwise restart the clock). Genuine
        // travelers keep their saved position; no game second passes.
        private static void ResumeExecutions(ActivityExecutor executor, Clock clock,
            List<ResumeItem> resume, JsonArray blocks, List<string> notes)
        {
            for (int i = 1; i < resume.Count; i++)
            {
                ResumeItem key = resume[i];
                int j = i - 1;
                while (j >= 0 && !resume[j].HadHolds && key.HadHolds)
                {
                    resume[j + 1] = resume[j];
                    j--;
                }
                resume[j + 1] = key;
            }
            for (int i = 0; i < resume.Count; i++)
            {
                ResumeItem item = resume[i];
                ExecutionRecord record = executor.Dispatch(item.Resident, item.Entry);
                item.Dispatched = true;
                if (item.Execution == null)
                {
                    continue;
                }
                record.PerformStartGameSeconds =
                    item.Execution.GetLong("performStart", "payload.actions.execution");
                record.EnqueueGameSeconds =
                    item.Execution.GetLong("enqueue", "payload.actions.execution");
                record.LedgerReserved =
                    item.Execution.GetBool("ledgerReserved", "payload.actions.execution");
                bool savedQueued =
                    item.Execution.GetBool("queued", "payload.actions.execution");
                if (record.IsTerminal)
                {
                    notes.Add("resident " + ((long)item.Resident).ToString()
                        + ": action " + ((long)item.Entry.Action).ToString()
                        + " reconciled to " + record.Outcome.Value.ToString()
                        + " (" + BlockedName(record) + "); no reward paid");
                }
                else if (savedQueued && record.Holds.Length == 0
                    && record.Stage == ExecutionStage.Reserve)
                {
                    record.Queued = true;
                }
                else if (savedQueued)
                {
                    record.Queued = false;
                    notes.Add("resident " + ((long)item.Resident).ToString()
                        + ": action " + ((long)item.Entry.Action).ToString()
                        + " stopped waiting at load; its slot was free");
                }
                else if (record.Queued)
                {
                    notes.Add("resident " + ((long)item.Resident).ToString()
                        + ": action " + ((long)item.Entry.Action).ToString()
                        + " queued on resume though it held at save");
                }
                else
                {
                    record.Queued = false;
                }
            }
            PumpRearrival(executor, clock, resume, blocks, notes);
        }

        private static void PumpRearrival(ActivityExecutor executor, Clock clock,
            List<ResumeItem> resume, JsonArray blocks, List<string> notes)
        {
            bool needed = false;
            for (int i = 0; i < resume.Count; i++)
            {
                ResumeItem item = resume[i];
                if (item.Execution == null)
                {
                    continue;
                }
                ExecutionRecord record = executor.GetExecution(item.Entry.Action);
                if (record != null && !record.IsTerminal
                    && record.Stage == ExecutionStage.Travel
                    && item.Execution.GetInt("stage", "payload.actions.execution")
                        == (int)ExecutionStage.Perform)
                {
                    needed = true;
                }
            }
            if (!needed)
            {
                return;
            }
            List<ExecutionStage> before = new List<ExecutionStage>();
            for (int i = 0; i < resume.Count; i++)
            {
                ExecutionRecord record = executor.GetExecution(resume[i].Entry.Action);
                before.Add(record == null ? ExecutionStage.Validate : record.Stage);
            }
            bool paused = clock.Paused;
            if (paused)
            {
                clock.Resume();
            }
            executor.Tick(StateWriter.ActivityExecutor);
            if (paused)
            {
                clock.Pause();
            }
            for (int i = 0; i < resume.Count; i++)
            {
                ResumeItem item = resume[i];
                ExecutionRecord record = executor.GetExecution(item.Entry.Action);
                if (record == null || record.IsTerminal || item.Execution == null)
                {
                    continue;
                }
                if (before[i] == ExecutionStage.Travel
                    && record.Stage == ExecutionStage.Perform
                    && item.Execution.GetInt("stage", "payload.actions.execution")
                        == (int)ExecutionStage.Perform)
                {
                    record.PerformStartGameSeconds = item.Execution.GetLong(
                        "performStart", "payload.actions.execution");
                    notes.Add("resident " + ((long)item.Resident).ToString()
                        + ": action " + ((long)item.Entry.Action).ToString()
                        + " re-arrived on resume; perform clock preserved");
                }
            }
            for (int i = 0; i < blocks.Count; i++)
            {
                JsonObject block = blocks.GetObject(i, "payload.actions");
                ResidentId id = (ResidentId)block.GetLong("resident", "payload.actions");
                ResidentStatus status = executor.GetResidentStatus(id);
                if (!status.IsIdle && !status.Execution.IsTerminal
                    && status.Execution.Stage == ExecutionStage.Travel)
                {
                    JsonObject pose = block.GetObject("pose", "payload.actions");
                    executor.SetResidentPosition(id, new ApproachPose(
                        pose.GetFloat("x", "payload.actions.pose"),
                        pose.GetFloat("y", "payload.actions.pose"),
                        pose.GetFloat("z", "payload.actions.pose")));
                }
            }
        }

        private static string BlockedName(ExecutionRecord record)
        {
            if (!record.BlockedReason.HasValue)
            {
                return "no reason";
            }
            return record.BlockedReason.Value.ToString();
        }
    }
}
