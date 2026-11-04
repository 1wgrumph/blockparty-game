// KEY-SETTINGS: sanctioned key loader (OD-3 split from DEMO-CAPTURE). The ONLY
// reader of the player's key: environment MODEL_API_KEY first, then the local
// settings file. Reports presence to AI-BOUNDARY as HasCredential so game code
// paths that log, persist, or ship can never carry the value. Every string
// form is redacted: ToString carries presence only, Redact replaces embedded
// values with [REDACTED]. Plain C# 9, no UnityEngine dependency.
using System;

namespace Blockparty.Services
{
    public enum KeySource
    {
        None,
        Environment,
        SettingsFile,
    }

    // One resolution: presence plus source publicly, the value internally.
    public sealed class KeyResolution
    {
        private readonly string key;

        public readonly bool HasCredential;
        public readonly KeySource Source;

        internal KeyResolution(string value, KeySource source)
        {
            key = value;
            Source = source;
            HasCredential = !string.IsNullOrEmpty(value) && source != KeySource.None;
        }

        public override string ToString()
        {
            return HasCredential ? "key set (" + Source.ToString() + ")" : "no key";
        }

        // Replaces every embedded occurrence (a Bearer line included) without
        // ever returning the value itself.
        public string Redact(string text)
        {
            if (string.IsNullOrEmpty(text) || !HasCredential)
            {
                return text ?? string.Empty;
            }
            return text.Replace(key, "[REDACTED]");
        }

        // Transport-only handoff: the Authorization sender reads the value here.
        internal bool TryGetKey(out string value)
        {
            value = key;
            return HasCredential;
        }
    }

    public static class KeyProvider
    {
        // The variable NAME only; values never appear in sources, logs, or ships.
        public const string EnvVarName = "MODEL_API_KEY";

        // Source order per CREDENTIAL-DESIGN: env first, settings file second.
        public static KeyResolution Resolve(string settingsPath)
        {
            if (string.IsNullOrWhiteSpace(settingsPath))
            {
                throw new ArgumentException("Settings path must be non-blank", "settingsPath");
            }
            string env = Environment.GetEnvironmentVariable(EnvVarName);
            if (!string.IsNullOrWhiteSpace(env))
            {
                return new KeyResolution(env.Trim(), KeySource.Environment);
            }
            KeySettings settings = KeySettings.Load(settingsPath);
            string fileKey;
            if (settings.TryGetKey(out fileKey))
            {
                return new KeyResolution(fileKey, KeySource.SettingsFile);
            }
            return new KeyResolution(null, KeySource.None);
        }
    }
}
