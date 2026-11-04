using System;
using System.Globalization;
using System.IO;
using Blockparty.Audio;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;
using Blockparty.UI;
using UnityEngine;

// #243 music for the Alpha player. Once AlphaDirector enters live play, the title theme
// (intro) plays once, then the gameplay loops play in order and repeat. Evidence runs
// (--alpha-run=<mode>, including smoke, which enters live play) get no music. Game pause pauses
// the music and holds the track deadline. Volume reads the persisted SaveV1 settings record in
// the Alpha save directory (TitleFlow's convention), reloaded when the file changes; with no
// file it reads the live Accessibility settings. The mixer has no Music group;
// music follows the Ambience level (master x ambience) through MixSettings, like the
// catalog's music-theme. Clips stream from Assets/Blockparty/Audio/Resources/Music.
// Attaches itself after scene load and reads AlphaDirector.MusicView. Statics here never change.
public sealed class MusicPlayer : MonoBehaviour
{
    static readonly string[] Loops = { "loop-01", "loop-02", "loop-05", "loop-06", "loop-07" };
    const string RunKey = "--alpha-run=";

    AlphaDirector director;
    AudioSource source;
    int loop = -1;
    float endsAt, nextVolume, pausedAt;
    bool paused;
    string settingsDir;
    DateTime settingsStamp;
    SettingsRecord? saved;

    // Same parse as AlphaDirector.Arg: the first --alpha-run=<mode> decides; an empty mode is normal play.
    static bool EvidenceRun(string[] args)
    {
        foreach (string arg in args)
            if (arg.StartsWith(RunKey, StringComparison.Ordinal)) return arg.Length > RunKey.Length;
        return false;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Attach()
    {
        if (EvidenceRun(Environment.GetCommandLineArgs())) return;
        var found = FindAnyObjectByType<AlphaDirector>();
        if (found != null && found.GetComponent<MusicPlayer>() == null)
            found.gameObject.AddComponent<MusicPlayer>().director = found;
    }

    void Update()
    {
        var view = director == null ? null : director.MusicView;
        if (!view.HasValue) return;
        float now = Time.unscaledTime;
        if (source == null)
        {
            source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0f;
            settingsDir = view.Value.SaveDirectory;
            ApplyVolume(view.Value.Settings);
            Play("intro", "title");
        }
        if (now >= nextVolume) { ApplyVolume(view.Value.Settings); nextVolume = now + 0.25f; }
        bool pause = view.Value.Paused;
        if (pause != paused)
        {
            paused = pause;
            if (paused) { source.Pause(); pausedAt = now; }
            else { source.UnPause(); endsAt += now - pausedAt; }
            Debug.Log("MUSIC " + (paused ? "pause" : "resume") + " t=" + now.ToString("F2", CultureInfo.InvariantCulture));
        }
        if (paused) return;
        // Clip length, not isPlaying, advances the playlist: a player without an audio device never plays.
        if (now >= endsAt)
        {
            loop = (loop + 1) % Loops.Length;
            Play(Loops[loop], "gameplay");
        }
    }

    void ApplyVolume(SettingsRecord live)
    {
        source.volume = new MixSettings(Persisted() ?? live).EffectiveLevel(AudioGroup.Ambience);
    }

    SettingsRecord? Persisted()
    {
        if (string.IsNullOrEmpty(settingsDir)) return null;
        string path = Path.Combine(settingsDir, SaveV1.SettingsFileName);
        DateTime stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default(DateTime);
        if (stamp != settingsStamp)
        {
            settingsStamp = stamp;
            SettingsLoadResult loaded = stamp == default(DateTime) ? null : SaveV1.LoadSettings(settingsDir);
            saved = loaded != null && loaded.Ok ? loaded.Settings : (SettingsRecord?)null;
        }
        return saved;
    }

    void Play(string track, string context)
    {
        var clip = Resources.Load<AudioClip>("Music/" + track);
        if (clip == null)
        {
            Debug.LogError("MUSIC missing clip Music/" + track);
            enabled = false;
            return;
        }
        source.clip = clip;
        source.Play();
        endsAt = Time.unscaledTime + clip.length;
        Debug.Log("MUSIC start context=" + context + " track=" + track
            + " length=" + clip.length.ToString("F2", CultureInfo.InvariantCulture)
            + " volume=" + source.volume.ToString("F2", CultureInfo.InvariantCulture)
            + " playing=" + (source.isPlaying ? "true" : "false")
            + " t=" + Time.unscaledTime.ToString("F2", CultureInfo.InvariantCulture));
    }
}
