using UnityEngine;
using Blockparty.Audio;
using Blockparty.Contracts;

// AUDIO-MIX@1: thin bridge applying MixSettings group levels to the Unity
// mixer's exposed group volumes. Linear [0, 1] maps to decibels; silence
// floors at -80 dB. No static mutable state.
public sealed class AudioMixer : MonoBehaviour
{
    [SerializeField]
    private UnityEngine.Audio.AudioMixer mixer;

    public void Apply(MixSettings mix)
    {
        if (mix == null || mixer == null)
        {
            return;
        }
        mixer.SetFloat("AmbienceVolume", ToDecibels(mix.EffectiveLevel(AudioGroup.Ambience)));
        mixer.SetFloat("WorldVolume", ToDecibels(mix.EffectiveLevel(AudioGroup.World)));
        mixer.SetFloat("VoiceVolume", ToDecibels(mix.EffectiveLevel(AudioGroup.Voice)));
        mixer.SetFloat("InterfaceVolume", ToDecibels(mix.EffectiveLevel(AudioGroup.Interface)));
    }

    public void ApplySettings(SettingsRecord settings)
    {
        Apply(new MixSettings(settings));
    }

    private static float ToDecibels(float linear)
    {
        if (linear <= 0f)
        {
            return -80f;
        }
        if (linear >= 1f)
        {
            return 0f;
        }
        return 20f * Mathf.Log10(linear);
    }
}
