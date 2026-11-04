// BUG-MINIGOLF-SETTINGS: the live build's settings record. Settings live in
// SaveV1's separately versioned record; a missing or refused record keeps
// the defaults. Plain C# 9, no UnityEngine dependency.
using Blockparty.Audio;
using Blockparty.Contracts;
using Blockparty.Persistence;

namespace Blockparty.UI
{
    public static class LiveSettings
    {
        public static Accessibility Load(string directory, SettingsRecord defaults)
        {
            SettingsLoadResult loaded = SaveV1.LoadSettings(directory);
            return new Accessibility(loaded.Ok ? loaded.Settings : defaults);
        }

        public static SaveWriteResult Save(Accessibility access, string directory)
        {
            return SaveV1.SaveSettings(access.Settings, directory);
        }

        // MISSING-UI mute is the saved master mixer level at 0. Accessibility
        // keeps the audible level (1 after a muted load) so unmuting restores it.
        public static Accessibility Load(string directory, SettingsRecord defaults, out bool muted)
        {
            SettingsRecord settings = Load(directory, defaults).Settings;
            muted = settings.MixerLevel == 0f;
            return new Accessibility(muted ? WithMaster(settings, 1f) : settings);
        }

        public static SaveWriteResult Save(Accessibility access, string directory, bool muted)
        {
            return SaveV1.SaveSettings(muted ? WithMaster(access.Settings, 0f) : access.Settings, directory);
        }

        static SettingsRecord WithMaster(SettingsRecord settings, float level)
        {
            MixSettings mix = new MixSettings(settings);
            mix.TrySetMasterLevel(level);
            return mix.Settings;
        }
    }
}
