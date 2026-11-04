// ACCESS@1 + INPUT-ACCESS@1: accessibility settings model (BP-PRES-02,
// BP-PRES-05). Plain C# 9 with no engine reference. Text scale is the SDoc
// 100..150% candidate range; every InputActions entry is remappable to one
// keyboard key or Mouse0..Mouse4, refusing unknown actions, blank/chord keys
// and conflicts without changing any state. Cursor/camera setters validate
// against the declared ranges below (negated form: NaN fails every check).
// Fast Enter Play Mode: all state on the instance, no static mutable state.
using System.Collections.Generic;
using Blockparty.Contracts;

namespace Blockparty.UI
{
    public sealed class Accessibility
    {
        public const float CursorSizeMin = 0.5f;
        public const float CursorSizeMax = 2f;
        public const float CameraSpeedMin = 0.25f;
        public const float CameraSpeedMax = 4f;

        private SettingsRecord settings;

        public Accessibility(SettingsRecord settings)
        {
            this.settings = settings;
        }

        public SettingsRecord Settings
        {
            get { return settings; }
        }

        public bool TrySetTextScale(float scale)
        {
            // Negated in-range form: NaN fails every comparison.
            if (!(scale >= 1f && scale <= 1.5f))
            {
                return false;
            }
            settings = WithTextScale(scale);
            return true;
        }

        public float ScaledTextSize(float unscaledSize)
        {
            return unscaledSize * settings.TextScale;
        }

        public bool TrySetCursorSize(float value)
        {
            if (!(value >= CursorSizeMin && value <= CursorSizeMax))
            {
                return false;
            }
            settings = Rebuild(settings, cursorSize: value);
            return true;
        }

        public bool TrySetCameraPanSpeed(float value)
        {
            if (!(value >= CameraSpeedMin && value <= CameraSpeedMax))
            {
                return false;
            }
            settings = Rebuild(settings, cameraPanSpeed: value);
            return true;
        }

        public bool TrySetCameraZoomSpeed(float value)
        {
            if (!(value >= CameraSpeedMin && value <= CameraSpeedMax))
            {
                return false;
            }
            settings = Rebuild(settings, cameraZoomSpeed: value);
            return true;
        }

        public bool TrySetCameraRotateSpeed(float value)
        {
            if (!(value >= CameraSpeedMin && value <= CameraSpeedMax))
            {
                return false;
            }
            settings = Rebuild(settings, cameraRotateSpeed: value);
            return true;
        }

        public void SetHighContrastCursor(bool enabled) { settings = Rebuild(settings, highContrastCursor: enabled); }
        public void SetEdgeScroll(bool enabled) { settings = Rebuild(settings, edgeScroll: enabled); }
        public void SetInvertX(bool enabled) { settings = Rebuild(settings, invertX: enabled); }
        public void SetInvertY(bool enabled) { settings = Rebuild(settings, invertY: enabled); }
        public void SetSnapSelection(bool enabled) { settings = Rebuild(settings, snapSelection: enabled); }
        public void SetNarrationEnabled(bool enabled) { settings = Rebuild(settings, narrationEnabled: enabled); }
        public void SetToggleControls(bool enabled) { settings = Rebuild(settings, toggleControls: enabled); }

        public bool TryRemap(string action, string key)
        {
            if (!InputActions.IsKnown(action) || !IsSingleInput(key))
            {
                return false;
            }
            for (int i = 0; i < InputActions.Names.Length; i++)
            {
                string other = InputActions.Names[i];
                if (other != action && BindingFor(other) == key)
                {
                    return false;
                }
            }
            Dictionary<string, string> remaps = new Dictionary<string, string>();
            if (settings.InputRemaps != null)
            {
                foreach (KeyValuePair<string, string> pair in settings.InputRemaps)
                {
                    remaps[pair.Key] = pair.Value;
                }
            }
            remaps[action] = key;
            settings = Rebuild(settings, inputRemaps: remaps);
            return true;
        }

        public string BindingFor(string action)
        {
            if (!InputActions.IsKnown(action))
            {
                return string.Empty;
            }
            string key;
            if (settings.InputRemaps != null
                && settings.InputRemaps.TryGetValue(action, out key)
                && !string.IsNullOrWhiteSpace(key))
            {
                return key;
            }
            return InputActions.DefaultBinding(action);
        }

        public string ResolveInput(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }
            for (int i = 0; i < InputActions.Names.Length; i++)
            {
                if (BindingFor(InputActions.Names[i]) == key)
                {
                    return InputActions.Names[i];
                }
            }
            return string.Empty;
        }

        // One key or mouse button only: chords/sequences are never required.
        internal static bool IsSingleInput(string key)
        {
            return !string.IsNullOrWhiteSpace(key)
                && key.IndexOf('+') < 0 && key.IndexOf(',') < 0;
        }

        public void SetNonColorCues(bool enabled)
        {
            settings = Rebuild(settings, nonColorCues: enabled);
        }

        public string NonColorIndicator(string status)
        {
            if (!settings.NonColorCues)
            {
                return string.Empty;
            }
            return "[" + (status ?? string.Empty) + "]";
        }

        public void SetCaptions(bool enabled)
        {
            settings = Rebuild(settings, captions: enabled);
        }

        public string CaptionFor(string catalogCaption)
        {
            if (!settings.Captions)
            {
                return string.Empty;
            }
            return catalogCaption;
        }

        public void SetReducedMotion(bool enabled)
        {
            settings = Rebuild(settings, reducedMotion: enabled);
        }

        private SettingsRecord WithTextScale(float scale)
        {
            return Rebuild(settings, textScale: scale);
        }

        // One rebuild path so every edit preserves every other field.
        private static SettingsRecord Rebuild(SettingsRecord s,
            float? textScale = null, bool? reducedMotion = null,
            bool? nonColorCues = null, bool? captions = null,
            IReadOnlyDictionary<string, string> inputRemaps = null,
            float? cursorSize = null, float? cameraPanSpeed = null,
            float? cameraZoomSpeed = null, float? cameraRotateSpeed = null,
            bool? highContrastCursor = null, bool? edgeScroll = null,
            bool? invertX = null, bool? invertY = null, bool? snapSelection = null,
            bool? narrationEnabled = null, bool? toggleControls = null)
        {
            return new SettingsRecord(s.SchemaVersion, s.MixerLevel,
                textScale ?? s.TextScale, reducedMotion ?? s.ReducedMotion,
                s.AmbienceLevel, s.WorldLevel, s.VoiceLevel, s.InterfaceLevel,
                nonColorCues ?? s.NonColorCues, captions ?? s.Captions,
                inputRemaps ?? s.InputRemaps,
                cursorSize ?? s.CursorSize, cameraPanSpeed ?? s.CameraPanSpeed,
                cameraZoomSpeed ?? s.CameraZoomSpeed, cameraRotateSpeed ?? s.CameraRotateSpeed,
                highContrastCursor ?? s.HighContrastCursor, edgeScroll ?? s.EdgeScroll,
                invertX ?? s.InvertX, invertY ?? s.InvertY,
                snapSelection ?? s.SnapSelection, narrationEnabled ?? s.NarrationEnabled,
                toggleControls ?? s.ToggleControls);
        }
    }
}
