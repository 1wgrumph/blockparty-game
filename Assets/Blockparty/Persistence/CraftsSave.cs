// CRAFTS@1 persistence: crafts.json beside the SaveV1 save in the same
// directory. SaveV1 owns the world; this file carries only the craft pieces,
// queued requests and applied completion ids, stamped with the world id and
// the game time of the save it belongs to. A file whose stamp differs from the
// loaded world (a stale or foreign sidecar) or whose rows contradict the world
// is refused whole, so pieces are never bound to the wrong save or duplicated.
// Save keeps the world and crafts files a matching pair: crafts.json.tmp is
// written and flushed before the world save, so a failed craft write leaves
// the previous world and crafts untouched; a failed world save removes the
// staged file; then the staged file replaces crafts.json. If that last replace
// fails, the staged file still matches the new world and Read uses it.
// shortcut: no backup copy; when SaveV1 falls back to its backup save the
// stamp differs and crafts load empty. Upgrade by moving crafts into SaveV1.
using System;
using System.IO;
using System.Text;
using Blockparty.Activities;
using Blockparty.Contracts;
using Blockparty.Simulation;

namespace Blockparty.Persistence
{
    public static class CraftsSave
    {
        public const string FileName = "crafts.json";
        public const string StagedName = FileName + ".tmp";
        private const string Schema = "blockparty-crafts-2";

        // Stages crafts, runs saveWorld (the SaveV1 save; null saves crafts only),
        // then commits crafts. With no crafts bound it is the world save alone.
        public static SaveWriteResult Save(string directory, Crafts crafts, WorldState world,
            Func<SaveWriteResult> saveWorld)
        {
            if (crafts == null) return saveWorld == null ? new SaveWriteResult(false, "nothing to save") : saveWorld();
            if (world == null) throw new ArgumentNullException("world");
            SaveFileStore store = new SaveFileStore();
            string path = Path.Combine(directory, FileName);
            string staged = Path.Combine(directory, StagedName);
            try
            {
                store.EnsureDirectory(directory);
                store.WriteTemp(staged, new UTF8Encoding(false).GetBytes(Render(crafts, world)));
            }
            catch (Exception e)
            {
                return new SaveWriteResult(false, "crafts: " + e.GetType().Name + ": " + e.Message);
            }
            SaveWriteResult saved = saveWorld == null ? new SaveWriteResult(true, path) : saveWorld();
            if (!saved.Ok)
            {
                try { File.Delete(staged); } catch (Exception) { }
                return saved;
            }
            try
            {
                store.Replace(staged, path, null);
            }
            catch (Exception)
            {
                // The flushed staged file matches the saved world; Read falls back to it.
            }
            return saved;
        }

        private static string Render(Crafts crafts, WorldState world)
        {
            CraftsSnapshot snapshot = crafts.Export();
            JsonObject root = new JsonObject();
            root.Add("schema", Schema);
            root.Add("worldId", world.WorldId);
            root.Add("gameSeconds", world.GameSeconds);
            JsonArray pieces = new JsonArray();
            foreach (CraftsSnapshot.PieceRow row in snapshot.Pieces)
            {
                JsonObject o = new JsonObject();
                o.Add("item", row.Item);
                o.Add("author", row.Author);
                o.Add("kind", row.Kind);
                o.Add("home", row.Home);
                o.Add("action", row.Action);
                o.Add("prompt", row.Prompt);
                o.Add("imagePath", row.ImagePath);
                o.Add("image", row.ImageBase64);
                o.Add("disclosure", row.Disclosure);
                o.Add("seed", row.Seed);
                o.Add("skin", row.SkinTone);
                o.Add("hair", row.HairColor);
                pieces.Add(o);
            }
            root.Add("pieces", pieces);
            JsonArray pending = new JsonArray();
            foreach (CraftsSnapshot.PendingRow row in snapshot.Pending)
            {
                JsonObject o = new JsonObject();
                o.Add("resident", row.Resident);
                o.Add("kind", row.Kind);
                o.Add("command", row.Command);
                pending.Add(o);
            }
            root.Add("pending", pending);
            JsonArray applied = new JsonArray();
            foreach (long id in snapshot.Applied)
            {
                JsonObject o = new JsonObject();
                o.Add("action", id);
                applied.Add(o);
            }
            root.Add("applied", applied);
            return SaveV1.RenderJson(root);
        }

        // Returns null on success or when no file exists; otherwise the refusal
        // notice. crafts.json is read first; the staged file only when crafts.json
        // is missing or belongs to another save (a save whose final replace
        // failed). A refused file leaves crafts unchanged.
        public static string Read(string directory, Crafts crafts, WorldState world)
        {
            if (crafts == null) throw new ArgumentNullException("crafts");
            if (world == null) throw new ArgumentNullException("world");
            string path = Path.Combine(directory, FileName);
            string staged = Path.Combine(directory, StagedName);
            string refused = File.Exists(path) ? ReadFile(path, crafts, world) : null;
            if ((refused == Mismatch || !File.Exists(path)) && File.Exists(staged))
            {
                string fallback = ReadFile(staged, crafts, world);
                if (fallback != Mismatch) return fallback;
            }
            return refused;
        }

        private const string Mismatch = "Crafts not restored: they belong to a different save.";

        private static string ReadFile(string path, Crafts crafts, WorldState world)
        {
            try
            {
                JsonObject root = JsonParser.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject;
                if (root == null) return "Crafts not restored: not a JSON object.";
                if (root.GetString("schema", "crafts") != Schema) return "Crafts not restored: unknown schema.";
                if (root.GetLong("worldId", "crafts") != world.WorldId
                    || root.GetLong("gameSeconds", "crafts") != world.GameSeconds)
                {
                    return Mismatch;
                }
                CraftsSnapshot snapshot = new CraftsSnapshot();
                JsonArray pieces = root.GetArray("pieces", "crafts");
                for (int i = 0; i < pieces.Count; i++)
                {
                    JsonObject o = pieces.GetObject(i, "crafts.pieces");
                    CraftsSnapshot.PieceRow row = new CraftsSnapshot.PieceRow();
                    row.Item = o.GetLong("item", "crafts.pieces");
                    row.Author = o.GetLong("author", "crafts.pieces");
                    row.Kind = o.GetString("kind", "crafts.pieces");
                    row.Home = o.GetLong("home", "crafts.pieces");
                    row.Action = o.GetLong("action", "crafts.pieces");
                    row.Prompt = o.GetString("prompt", "crafts.pieces");
                    row.ImagePath = o.GetString("imagePath", "crafts.pieces");
                    row.ImageBase64 = o.GetString("image", "crafts.pieces");
                    row.Disclosure = o.GetString("disclosure", "crafts.pieces");
                    row.Seed = o.GetInt("seed", "crafts.pieces");
                    row.SkinTone = o.GetInt("skin", "crafts.pieces");
                    row.HairColor = o.GetInt("hair", "crafts.pieces");
                    snapshot.Pieces.Add(row);
                }
                JsonArray pending = root.GetArray("pending", "crafts");
                for (int i = 0; i < pending.Count; i++)
                {
                    JsonObject o = pending.GetObject(i, "crafts.pending");
                    CraftsSnapshot.PendingRow row = new CraftsSnapshot.PendingRow();
                    row.Resident = o.GetLong("resident", "crafts.pending");
                    row.Kind = o.GetString("kind", "crafts.pending");
                    row.Command = o.GetLong("command", "crafts.pending");
                    snapshot.Pending.Add(row);
                }
                JsonArray applied = root.GetArray("applied", "crafts");
                for (int i = 0; i < applied.Count; i++)
                {
                    snapshot.Applied.Add(applied.GetObject(i, "crafts.applied").GetLong("action", "crafts.applied"));
                }
                crafts.Import(StateWriter.SimCore, snapshot);
                return null;
            }
            catch (Exception e) when (e is JsonSaveException || e is ArgumentException || e is IOException
                || e is UnauthorizedAccessException)
            {
                return "Crafts not restored: " + e.Message;
            }
        }
    }
}
