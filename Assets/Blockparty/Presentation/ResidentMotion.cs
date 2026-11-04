using System;
using System.Collections.Generic;
using UnityEngine;

// CONTENT-IMPORT@1 ANIM-CATALOG playback on one dressed stage body.
// Each clip is sampled on a hidden copy of the ANIM action rig and copied onto the
// body's bones with ANIM-CATALOG's own retarget (render_captures.stage_tracks):
// rotation = restBody * inverse(restRig) * value; translation = restBody +
// (value - restRig) * stageScale, stageScale = stage height / adult height; scale =
// restBody * value / restRig. Feelings add the catalog render channels on top:
// face morph weights (emo_* blend shapes) and posture/gaze bone offsets.
public sealed class ResidentMotion : MonoBehaviour
{
    sealed class Bone
    {
        public Transform rig, body;
        public Quaternion delta;
        public Quaternion rigRotation;
        public Vector3 rigPosition, bodyPosition, rigScale, bodyScale;
    }

    public struct Step
    {
        public AnimationClip clip;
        public bool loop;
        public float seconds;   // loops hold this long; one-shots play their length
        public string phase;    // flow phase this step plays (null: the Play phase)
    }

    Bone[] bones;
    readonly Dictionary<string, Bone> byName = new Dictionary<string, Bone>();
    GameObject rig;
    float stageScale = 1f;
    readonly List<Step> steps = new List<Step>();
    int step;
    float clock;
    SkinnedMeshRenderer face;
    readonly Dictionary<string, Quaternion> offsets = new Dictionary<string, Quaternion>();

    public bool Autoplay = true;
    public string Flow { get; private set; }
    string phase;
    public string Phase { get { return step < steps.Count && steps[step].phase != null ? steps[step].phase : phase; } }
    public AnimationClip Current { get { return step < steps.Count ? steps[step].clip : null; } }
    public float Time { get { return clock; } }
    public bool Done { get { return step >= steps.Count; } }

    // rigPrefab: the imported ANIM action rig; bodyRoot: the resident instance.
    public void Bind(GameObject rigPrefab, float scale, SkinnedMeshRenderer faceRenderer)
    {
        stageScale = scale;
        face = faceRenderer;
        rig = Instantiate(rigPrefab, transform);
        rig.name = "AnimRig";
        foreach (var animator in rig.GetComponentsInChildren<Animator>(true)) animator.enabled = false;
        var bodyBones = new Dictionary<string, Transform>();
        foreach (var t in GetComponentsInChildren<Transform>(true))
            if (!t.IsChildOf(rig.transform) && !bodyBones.ContainsKey(t.name)) bodyBones[t.name] = t;
        var list = new List<Bone>();
        foreach (var t in rig.GetComponentsInChildren<Transform>(true))
        {
            Transform target;
            if (t == rig.transform || !bodyBones.TryGetValue(t.name, out target)) continue;
            var bone = new Bone
            {
                rig = t, body = target,
                delta = target.localRotation * Quaternion.Inverse(t.localRotation), rigRotation = t.localRotation,
                rigPosition = t.localPosition, bodyPosition = target.localPosition,
                rigScale = t.localScale, bodyScale = target.localScale,
            };
            list.Add(bone);
            byName[t.name] = bone;
        }
        bones = list.ToArray();
        // Rest face direction in the head joint's frame (the instance is still in its bind pose here).
        Transform head;
        if (bodyBones.TryGetValue("head", out head)) headForward = Quaternion.Inverse(head.rotation) * transform.forward;
        if (!byName.ContainsKey("pelvis") || !byName.ContainsKey("Root"))
            Debug.LogError("CONTENT-IMPORT motion rig does not map Root/pelvis onto " + name);
    }

    public int MappedBones { get { return bones == null ? 0 : bones.Length; } }

    public void Play(string flow, string phase, params Step[] sequence)
    {
        Flow = flow;
        this.phase = phase;
        steps.Clear();
        steps.AddRange(sequence);
        step = 0;
        clock = 0f;
        Apply();
    }

    // Deterministic pose for captures: clip at an absolute time, autoplay off.
    public void Hold(string flow, string phase, AnimationClip clip, float time)
    {
        Autoplay = false;
        Play(flow, phase, new Step { clip = clip, loop = true, seconds = float.MaxValue });
        clock = time;
        Apply();
    }

    void LateUpdate() { Advance(UnityEngine.Time.deltaTime); }

    // Plays the queued steps forward (LateUpdate in play; MainSceneBuild ticks it in edit mode).
    public void Advance(float dt)
    {
        if (!Autoplay || bones == null || Done) return;
        clock += dt;
        var s = steps[step];
        float length = s.loop ? s.seconds : s.clip.length;
        if (clock >= length && step < steps.Count - 1)
        {
            clock -= length;
            step++;
        }
        else if (clock >= length && !s.loop) step = steps.Count;
        if (!Done) Apply();
    }

    public void Apply()
    {
        if (bones == null || Done) return;
        var s = steps[step];
        if (s.clip == null) return;
        float t = s.loop ? Mathf.Repeat(clock, Mathf.Max(s.clip.length, 1e-3f)) : Mathf.Min(clock, s.clip.length);
        foreach (var b in bones)
        {
            b.rig.localPosition = b.rigPosition;
            b.rig.localRotation = b.rigRotation;
            b.rig.localScale = b.rigScale;
        }
        s.clip.SampleAnimation(rig, t);
        foreach (var b in bones)
        {
            var rotation = b.delta * b.rig.localRotation;
            Quaternion offset;
            if (offsets.TryGetValue(b.rig.name, out offset)) rotation = offset * rotation;
            b.body.localRotation = rotation;
            b.body.localPosition = b.bodyPosition + (b.rig.localPosition - b.rigPosition) * stageScale;
            var r = b.rig.localScale;
            b.body.localScale = new Vector3(b.bodyScale.x * r.x / b.rigScale.x, b.bodyScale.y * r.y / b.rigScale.y,
                b.bodyScale.z * r.z / b.rigScale.z);
        }
    }

    // ANIM-CATALOG render_channels: face = emo_* morph weights (0..1 per target name),
    // posture = spine pitch/roll and gaze = neck/head yaw/pitch, as render_captures.feeling_frame.
    public void Feel(IDictionary<string, float> faceWeights, float[] posture, float[] gaze)
    {
        offsets.Clear();
        if (posture != null && gaze != null)
        {
            float p = posture[0], q = posture[1], r = posture[2];
            offsets["spine_01"] = Euler(p * 15f, 0f, 0f);
            offsets["spine_02"] = Euler(q * 8f, 0f, 0f);
            offsets["spine_03"] = Euler(q * 10f, 0f, r * 8f);
            offsets["neck_01"] = Euler(0f, gaze[0] * 12f, 0f);
            offsets["head"] = Euler(gaze[2] * 10f, gaze[1] * 15f, 0f);
        }
        if (face == null || face.sharedMesh == null) return;
        var mesh = face.sharedMesh;
        for (int i = 0; i < mesh.blendShapeCount; i++)
        {
            string shape = mesh.GetBlendShapeName(i);
            if (shape.StartsWith("emo_blink", StringComparison.Ordinal)) continue;
            float w;
            face.SetBlendShapeWeight(i, faceWeights != null && faceWeights.TryGetValue(shape, out w) ? w * 100f : 0f);
        }
        Apply();
    }

    public void Blink(bool closed)
    {
        if (face == null || face.sharedMesh == null) return;
        foreach (string shape in new[] { "emo_blink_left", "emo_blink_right" })
        {
            int i = face.sharedMesh.GetBlendShapeIndex(shape);
            if (i >= 0) face.SetBlendShapeWeight(i, closed ? 100f : 0f);
        }
    }

    // render_captures.euler_quat on glTF local axes, mirrored into Unity (glTFast reflects X).
    static Quaternion Euler(float xDeg, float yDeg, float zDeg)
    {
        float x = xDeg * Mathf.Deg2Rad / 2f, y = yDeg * Mathf.Deg2Rad / 2f, z = zDeg * Mathf.Deg2Rad / 2f;
        float cx = Mathf.Cos(x), sx = Mathf.Sin(x), cy = Mathf.Cos(y), sy = Mathf.Sin(y), cz = Mathf.Cos(z), sz = Mathf.Sin(z);
        var q = new Quaternion(sx * cy * cz - cx * sy * sz, cx * sy * cz + sx * cy * sz,
            cx * cy * sz - sx * sy * cz, cx * cy * cz + sx * sy * sz);
        return new Quaternion(q.x, -q.y, -q.z, q.w);
    }

    Vector3 headForward = Vector3.forward;

    // The face's forward in world space for the current pose (follows head pitch and turn).
    public Vector3 FaceForward()
    {
        var head = BodyBone("head");
        return head == null ? transform.forward : head.rotation * headForward;
    }

    public Transform BodyBone(string boneName)
    {
        Bone b;
        return byName.TryGetValue(boneName, out b) ? b.body : null;
    }
}
