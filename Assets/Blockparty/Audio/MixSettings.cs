// AUDIO-MIX@1: independent ambience/world/voice/interface mixer levels.
// Plain C# 9 with no engine reference. Levels are linear [0, 1] stored on
// the SettingsRecord; the effective bus level is master times group. NaN
// fails both range comparisons, so one check refuses every invalid input.
// Fast Enter Play Mode: all state on the instance, no static mutable state.
using Blockparty.Contracts;

namespace Blockparty.Audio
{
    public enum AudioGroup
    {
        Ambience,
        World,
        Voice,
        Interface,
    }

    public sealed class MixSettings
    {
        private SettingsRecord settings;

        public MixSettings(SettingsRecord settings)
        {
            this.settings = settings;
        }

        public SettingsRecord Settings
        {
            get { return settings; }
        }

        public bool TrySetLevel(AudioGroup group, float level)
        {
            if (!IsLevel(level))
            {
                return false;
            }
            switch (group)
            {
                case AudioGroup.Ambience:
                    settings = With(settings, level, settings.WorldLevel,
                        settings.VoiceLevel, settings.InterfaceLevel);
                    return true;
                case AudioGroup.World:
                    settings = With(settings, settings.AmbienceLevel, level,
                        settings.VoiceLevel, settings.InterfaceLevel);
                    return true;
                case AudioGroup.Voice:
                    settings = With(settings, settings.AmbienceLevel,
                        settings.WorldLevel, level, settings.InterfaceLevel);
                    return true;
                case AudioGroup.Interface:
                    settings = With(settings, settings.AmbienceLevel,
                        settings.WorldLevel, settings.VoiceLevel, level);
                    return true;
                default:
                    return false;
            }
        }

        public bool TrySetMasterLevel(float level)
        {
            if (!IsLevel(level))
            {
                return false;
            }
            settings = new SettingsRecord(settings.SchemaVersion, level,
                settings.TextScale, settings.ReducedMotion, settings.AmbienceLevel,
                settings.WorldLevel, settings.VoiceLevel, settings.InterfaceLevel,
                settings.NonColorCues, settings.Captions, settings.InputRemaps,
                settings.CursorSize, settings.CameraPanSpeed, settings.CameraZoomSpeed,
                settings.CameraRotateSpeed, settings.HighContrastCursor, settings.EdgeScroll,
                settings.InvertX, settings.InvertY, settings.SnapSelection,
                settings.NarrationEnabled, settings.ToggleControls);
            return true;
        }

        public float EffectiveLevel(AudioGroup group)
        {
            return settings.MixerLevel * Stored(group);
        }

        private float Stored(AudioGroup group)
        {
            switch (group)
            {
                case AudioGroup.Ambience: return settings.AmbienceLevel;
                case AudioGroup.World: return settings.WorldLevel;
                case AudioGroup.Voice: return settings.VoiceLevel;
                case AudioGroup.Interface: return settings.InterfaceLevel;
                default: return 0f;
            }
        }

        private static SettingsRecord With(SettingsRecord current, float ambience,
            float world, float voice, float iface)
        {
            return new SettingsRecord(current.SchemaVersion, current.MixerLevel,
                current.TextScale, current.ReducedMotion, ambience, world,
                voice, iface, current.NonColorCues, current.Captions,
                current.InputRemaps, current.CursorSize, current.CameraPanSpeed,
                current.CameraZoomSpeed, current.CameraRotateSpeed,
                current.HighContrastCursor, current.EdgeScroll, current.InvertX,
                current.InvertY, current.SnapSelection, current.NarrationEnabled,
                current.ToggleControls);
        }

        private static bool IsLevel(float value)
        {
            return value >= 0f && value <= 1f;
        }
    }
}
