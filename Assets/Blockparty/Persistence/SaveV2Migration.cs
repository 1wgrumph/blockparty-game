// SAVE-V2: plain v1-to-v2 payload migration. Later producers add fields
// one at a time here; no static state, so repeated play entry is safe.
using Blockparty.Contracts;

namespace Blockparty.Persistence
{
    internal static class SaveV2Migration
    {
        public const int LegacyVersion = 1;

        public static bool IsSupportedVersion(int version)
        {
            return version == LegacyVersion || version == SaveFormat.SchemaVersion;
        }

        // v1 appearances predate baseBody: default Male (0), matching the
        // legacy default in DeserializeAppearances. v2 must carry it.
        public static void MigrateToCurrent(JsonObject payload, int fromVersion)
        {
            JsonArray rows = payload.GetArray("appearances", "payload");
            for (int i = 0; i < rows.Count; i++)
            {
                JsonObject row = rows.GetObject(i, "payload.appearances");
                if (row.Has("baseBody")) continue;
                if (fromVersion == LegacyVersion)
                {
                    row.Add("baseBody", 0);
                }
                else
                {
                    throw new JsonSaveException("payload.appearances: row " + i.ToString()
                        + " is missing the migrated field \"baseBody\"");
                }
            }
        }
    }
}
