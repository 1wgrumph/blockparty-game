using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// MISSING-UI (M5-OD-30): the Settings colour filter. Greyscale removes colour
// for the "status cues in greyscale" checks; the three dichromacy modes use the
// Machado, Oliveira and Fuhrmann (2009) severity-1.0 matrices through URP's
// channel mixer. Presentation only: the world and the HUD state are untouched.
public static class ColorFilter
{
    public static readonly string[] Names = { "Off", "Greyscale", "Protanopia", "Deuteranopia", "Tritanopia" };

    static readonly float[][] Matrices =
    {
        null,
        null,
        new[] { 0.152286f, 1.052583f, -0.204868f, 0.114503f, 0.786281f, 0.099216f, -0.003882f, -0.048116f, 1.051998f },
        new[] { 0.367322f, 0.860646f, -0.227968f, 0.280085f, 0.672501f, 0.047413f, -0.011820f, 0.042940f, 0.968881f },
        new[] { 1.255528f, -0.076749f, -0.178779f, -0.078411f, 0.930809f, 0.147602f, 0.004733f, 0.691367f, 0.303900f },
    };

    // Saturation is the AlphaDirector grade (8) unless greyscale (-100).
    public static void Apply(VolumeProfile profile, int mode)
    {
        if (profile == null) return;
        ColorAdjustments adjust;
        if (profile.TryGet(out adjust)) adjust.saturation.Override(mode == 1 ? -100f : 8f);
        ChannelMixer mixer;
        if (!profile.TryGet(out mixer)) mixer = profile.Add<ChannelMixer>(true);
        float[] m = Matrices[mode] ?? new[] { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f };
        mixer.active = Matrices[mode] != null;
        mixer.redOutRedIn.Override(m[0] * 100f); mixer.redOutGreenIn.Override(m[1] * 100f); mixer.redOutBlueIn.Override(m[2] * 100f);
        mixer.greenOutRedIn.Override(m[3] * 100f); mixer.greenOutGreenIn.Override(m[4] * 100f); mixer.greenOutBlueIn.Override(m[5] * 100f);
        mixer.blueOutRedIn.Override(m[6] * 100f); mixer.blueOutGreenIn.Override(m[7] * 100f); mixer.blueOutBlueIn.Override(m[8] * 100f);
    }
}
