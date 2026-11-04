using System;
using System.Collections.Generic;
using Blockparty.Contracts;
using Blockparty.Simulation;
using UnityEngine;
using UnityEngine.Rendering.Universal;

// PORTRAITS (owner playtest 2026-11-04): head-and-shoulders pictures of the live
// residents for the HUD circles (needs card, portrait strip, party guests). One
// disabled orthographic camera renders a resident's dressed model as drawn now
// (skin, hair, outfit, current face) into a small RenderTexture. At most one
// portrait renders per frame: the selected resident as soon as the selection
// changes, otherwise the stalest one, so each refreshes about every Refresh
// seconds. The camera sees only the resident layers, and its near/far slab is
// a thin box around the head, so walls and neighbours stay out of the frame.
public sealed class AlphaPortraits : MonoBehaviour
{
    public const int Size = 160;
    const float Refresh = 2f;
    const float Distance = 1f;

    public AlphaHud.Frame Frame;   // set by AlphaHud each Draw; read here after animation
    Camera cam;
    readonly Dictionary<ResidentId, RenderTexture> textures = new Dictionary<ResidentId, RenderTexture>();
    readonly Dictionary<ResidentId, float> renderedAt = new Dictionary<ResidentId, float>();   // last attempt, for turns
    readonly HashSet<ResidentId> ready = new HashSet<ResidentId>();
    ResidentId? lastSelected;
    float nextAt;

    public Texture Get(ResidentId id)
    {
        RenderTexture rt;
        return ready.Contains(id) && textures.TryGetValue(id, out rt) ? rt : null;
    }

    void Awake()
    {
        cam = gameObject.AddComponent<Camera>();
        cam.enabled = false;
        cam.orthographic = true;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color32(0xF7, 0xD9, 0xA8, 0xFF);   // the HUD avatar disc colour
        cam.cullingMask = (1 << MainDirector.ResidentLayer) | (1 << MainDirector.DeepSkinLayer);
        var data = cam.GetUniversalAdditionalCameraData();
        data.renderShadows = false;
        data.renderPostProcessing = false;
    }

    void LateUpdate()
    {
        AlphaHud.Frame f = Frame;
        if (f == null || f.Residents == null || f.RootOf == null || f.Navigation == null) return;
        ResidentId? selected = f.Navigation.SelectedResident;
        ResidentId? target = null;
        if (selected.HasValue && !Nullable.Equals(selected, lastSelected))
            target = selected;
        else if (Time.unscaledTime >= nextAt)
        {
            float oldest = float.MaxValue;
            foreach (ResidentId id in f.Residents)
            {
                float at;
                if (!renderedAt.TryGetValue(id, out at)) at = float.MinValue;
                if (at < oldest) { oldest = at; target = id; }
            }
            nextAt = Time.unscaledTime + Refresh / Mathf.Max(1, f.Residents.Count);
        }
        lastSelected = selected;
        // A resident out of view (no model yet, hidden) keeps its last picture and is retried in turn.
        if (!target.HasValue) return;
        renderedAt[target.Value] = Time.unscaledTime;
        if (Render(target.Value, f.RootOf(target.Value)) && ready.Add(target.Value))
            Debug.Log("ALPHA portrait ready resident=" + (long)target.Value);
    }

    bool Render(ResidentId id, Transform root)
    {
        // The GYM-TRAIL rider wears its model directly under the root.
        Transform model = root == null ? null : root.Find("Body/Model") ?? root.Find("Model");
        if (model == null || !model.gameObject.activeInHierarchy) return false;
        ResidentMotion motion = model.GetComponent<ResidentMotion>();
        // The GYM-TRAIL rider's model copies the rider's joints and has no ResidentMotion.
        Transform head = motion != null ? motion.BodyBone("head") : Bone(model, "head");
        if (head == null) return false;
        Vector3 forward = model.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
        forward.Normalize();
        // Head-bone height scales the frame, so children and adults fill the circle alike.
        float k = Mathf.Clamp((head.position.y - model.position.y) / 1.45f, 0.3f, 1.3f);
        Vector3 center = head.position + Vector3.up * (0.04f * k);
        cam.orthographicSize = 0.32f * k;   // hair top to shoulders
        cam.transform.position = center + forward * Distance;
        cam.transform.rotation = Quaternion.LookRotation(-forward, Vector3.up);
        cam.nearClipPlane = Distance - 0.35f * Mathf.Max(1f, k);
        cam.farClipPlane = Distance + 0.35f * Mathf.Max(1f, k);
        RenderTexture rt;
        if (!textures.TryGetValue(id, out rt))
        {
            rt = new RenderTexture(Size, Size, 16) { antiAliasing = 4, name = "Portrait_" + (long)id };
            textures[id] = rt;
        }
        cam.targetTexture = rt;
        cam.Render();
        cam.targetTexture = null;
        return true;
    }

    static Transform Bone(Transform t, string name)
    {
        if (t.name == name) return t;
        foreach (Transform c in t)
        {
            Transform hit = Bone(c, name);
            if (hit != null) return hit;
        }
        return null;
    }

    void OnDestroy()
    {
        foreach (RenderTexture rt in textures.Values) if (rt != null) rt.Release();
    }
}
