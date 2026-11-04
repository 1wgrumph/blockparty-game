using System.Collections.Generic;
using System.Reflection;
using Blockparty.Contracts;
using Blockparty.Simulation;
using UnityEngine;
using UnityEngine.Rendering;

// POLISH-PEOPLE (#216): the Alpha residents as dressed, animated characters.
// Each Resident_N root's Body wears the CONTENT-IMPORT stage variant for the
// resident's stored base body and life stage, dressed by MainDirector.Wear
// from its appearance record and re-dressed when the stage or worn clothing
// changes (as MainDirector.Dress does). Motion is the ANIM-CATALOG stage
// flow: "start" (Idle_Loop) when standing, "loop" (Walk_Loop) while the root
// moves; newborns and infants keep their baby loop (BP-PRES-03). Live play
// moves roots in 0.25 s ticks, so there the Body glides over each step and
// faces its travel; evidence placements land exactly. The click collider sits
// on Body, so a resident is picked where it is drawn. A gold ring marks the
// selected resident. The GYM-TRAIL rider (GymTrailDirector's own body and
// clips) wears its resident's look the same way: the dressed model copies the
// rider's animated joints each frame and the rider's bare skin is hidden.
// MainDirector's camera-aimed resident fills light the characters as in Main.
public sealed class AlphaResidentLooks : MonoBehaviour
{
    // Bound by AlphaSceneBuild from Main.unity's director and kept disabled:
    // only Wear, Motion, StageScale and FlowClip run here.
    public MainDirector wardrobe;
    public Material ringMaterial;

    const float Teleport = 8f;      // a longer root jump is a placement (Ultra steps ~5 m per tick)
    const float WalkHold = 0.35f;   // keeps the walk across one 0.25 s live tick

    sealed class Look
    {
        public Transform root, body;
        public GameObject model;
        public ResidentMotion motion;
        public string key;
        public LifeStage stage;
        public Vector3 last, offset;
        public float speed, lastMove = -10f, yaw = 180f;
        public bool walking;
        public Transform[] from, to;   // rider joints -> dressed model joints (follow only)
        public readonly float[] mood = new float[MoodSize], target = new float[MoodSize];
        public bool moodShown, hat;
    }

    readonly Dictionary<Transform, Look> looks = new Dictionary<Transform, Look>();
    Transform ring, selected;
    bool glide;

    // Called from AlphaDirector.Mirror with the same id-sorted rows it places.
    public void Sync(Transform[] roots, ResidentRecord[] rows, AppearanceStore store,
        ResidentId? selectedId, Transform skip, bool live)
    {
        glide = live;
        selected = null;
        if (wardrobe == null || store == null) return;
        for (int i = 0; i < rows.Length && i < roots.Length; i++)
        {
            Transform root = roots[i];
            if (root == null) continue;
            bool follow = root == skip;
            if (selectedId.HasValue && rows[i].Id == selectedId.Value) selected = root;
            AppearanceRecord appearance;
            if (!store.TryGet(rows[i].Id, out appearance)) continue;
            int clothing = store.WornClothing(rows[i].Id, OutfitCategory.Everyday);
            string key = (long)rows[i].Id + "|" + appearance.BaseBody + "|" + rows[i].Stage + "|" + clothing;
            Look look;
            if (!looks.TryGetValue(root, out look))
            {
                look = new Look { root = root, body = follow ? root : root.Find("Body"), last = root.position };
                if (look.body == null) continue;
                Camera view = Camera.main;
                if (view != null) look.yaw = Yaw(view.transform.position - root.position);
                looks[root] = look;
            }
            TargetMood(rows[i], look.target);
            if (look.key == key) continue;
            Drop(look.model);
            SeatHats(appearance.BaseBody, rows[i].Stage);
            look.model = wardrobe.Wear(appearance.BaseBody, rows[i].Stage, appearance, clothing, look.body);
            look.key = key;
            if (look.model == null) continue;
            RestFace(look.model);
            look.moodShown = false;
            look.hat = WearsHat(look.model);
            look.model.name = "Model";
            look.stage = rows[i].Stage;
            if (follow) { Follow(look); continue; }
            look.motion = wardrobe.Motion(look.model, appearance.BaseBody, rows[i].Stage);
            float scale = wardrobe.StageScale(appearance.BaseBody, rows[i].Stage);
            // Roots wait unseen and unclickable until a roster resident takes them.
            var hit = look.body.GetComponent<CapsuleCollider>();
            if (hit != null)
            {
                hit.height = 1.75f * scale;
                hit.center = new Vector3(0f, hit.height * 0.5f, 0f);
                hit.enabled = true;
            }
            Transform contact = look.body.Find("Contact");
            if (contact != null)
            {
                contact.localScale = new Vector3(0.9f * scale, 0.9f * scale, 1f);
                contact.gameObject.SetActive(true);
            }
            // Out of the bind pose at once: evidence runs capture in the same frame.
            Play(look, false, i * 0.37f);
        }
        // Slots past the roster (an earlier save reloaded) wait unseen and unclickable again.
        for (int i = rows.Length; i < roots.Length; i++)
        {
            Look look;
            if (roots[i] == null || roots[i] == skip || !looks.TryGetValue(roots[i], out look)) continue;
            Drop(look.model);
            var hit = look.body.GetComponent<CapsuleCollider>();
            if (hit != null) hit.enabled = false;
            Transform contact = look.body.Find("Contact");
            if (contact != null) contact.gameObject.SetActive(false);
            looks.Remove(roots[i]);   // a later arrival takes the slot with a fresh look
        }
    }

    static void Drop(GameObject model)
    {
        if (model == null) return;
        // Out of the hierarchy now, so Follow never maps joints that Destroy removes later.
        model.SetActive(false);
        model.transform.SetParent(null, false);
        Destroy(model);
    }

    // Same-named joints (the GYM-TRAIL cells share the stage rig); the rider's own skin hides.
    static void Follow(Look look)
    {
        var joints = new Dictionary<string, Transform>();
        foreach (var t in look.model.GetComponentsInChildren<Transform>(true))
            if (t != look.model.transform && !joints.ContainsKey(t.name)) joints[t.name] = t;
        var from = new List<Transform>();
        var to = new List<Transform>();
        foreach (var t in look.root.GetComponentsInChildren<Transform>(true))
        {
            Transform joint;
            if (t.IsChildOf(look.model.transform) || !joints.TryGetValue(t.name, out joint)) continue;
            from.Add(t);
            to.Add(joint);
        }
        look.from = from.ToArray();
        look.to = to.ToArray();
        foreach (var skin in look.root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            if (!skin.transform.IsChildOf(look.model.transform)) skin.enabled = false;
    }

    void Play(Look look, bool walking, float time)
    {
        look.walking = walking;
        if (look.motion == null) return;
        string flow = look.stage.ToString();
        string phase = walking || look.stage <= LifeStage.Infant ? "loop" : "start";
        AnimationClip clip = wardrobe.FlowClip(flow, phase);
        if (clip == null) return;
        // Hold seeds the loop clock so residents do not idle in lockstep; then it plays on.
        look.motion.Hold(flow, phase, clip, Mathf.Repeat(time, Mathf.Max(clip.length, 1e-3f)));
        look.motion.Autoplay = true;
    }

    static float Yaw(Vector3 direction) { return Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg; }

    void LateUpdate()
    {
        float dt = Time.deltaTime, now = Time.time;
        foreach (Look look in looks.Values)
        {
            if (look.model == null) continue;
            if (look.from != null)
            {
                // Rotations carry the pose; only Root is translated by the GYM-TRAIL clips.
                for (int j = 0; j < look.from.Length; j++)
                {
                    look.to[j].localRotation = look.from[j].localRotation;
                    if (look.from[j].name == "Root") look.to[j].localPosition = look.from[j].localPosition;
                }
                ShowMood(look, dt);   // face only: the rider's joints stay GYM-TRAIL's
                continue;
            }
            Vector3 step = look.root.position - look.last;
            step.y = 0f;
            look.last = look.root.position;
            if (step.sqrMagnitude > Teleport * Teleport) look.offset = Vector3.zero;
            else if (step.sqrMagnitude > 1e-6f)
            {
                float gap = Mathf.Clamp(now - look.lastMove, 0.05f, 0.5f);
                look.lastMove = now;
                look.yaw = Yaw(step);
                if (glide) { look.offset -= step; look.speed = look.offset.magnitude / gap; }
            }
            look.offset = Vector3.MoveTowards(look.offset, Vector3.zero, look.speed * dt);
            // A class-stage placement turns the root; the body then takes its pose as authored.
            bool staged = look.root.rotation != Quaternion.identity;
            look.body.localPosition = staged ? Vector3.zero : look.offset;
            look.body.rotation = staged ? look.root.rotation : Quaternion.Euler(0f, look.yaw, 0f);
            bool walking = !staged && look.stage > LifeStage.Infant && now - look.lastMove < WalkHold;
            if (walking != look.walking) Play(look, walking, 0f);
            ShowMood(look, dt);
        }
        // The click collider rides on Body: a raycast next frame must see the body where it was drawn.
        Physics.SyncTransforms();
        Ring();
        Camera view = Camera.main;
        if (view != null)
        {
            MainDirector.AimFill(wardrobe.residentFill, view.transform);
            MainDirector.AimFill(wardrobe.deepSkinFill, view.transform);
        }
    }

    void Ring()
    {
        Look look;
        bool show = selected != null && selected.gameObject.activeInHierarchy
            && looks.TryGetValue(selected, out look) && look.model != null;
        if (ring == null)
        {
            if (!show || ringMaterial == null) return;
            ring = new GameObject("SelectionRing").transform;
            ring.SetParent(transform, false);
            ring.gameObject.AddComponent<MeshFilter>().sharedMesh = Annulus(0.40f, 0.50f, 48);
            var renderer = ring.gameObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = ringMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }
        ring.gameObject.SetActive(show);
        if (show) ring.position = looks[selected].body.position + new Vector3(0f, 0.07f, 0f);
    }

    // Flat ring on the ground plane, facing up.
    static Mesh Annulus(float inner, float outer, int segments)
    {
        var vertices = new Vector3[segments * 2];
        var triangles = new int[segments * 6];
        for (int i = 0; i < segments; i++)
        {
            float a = i * Mathf.PI * 2f / segments;
            var d = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
            vertices[i * 2] = d * inner;
            vertices[i * 2 + 1] = d * outer;
            int n = (i + 1) % segments;
            int t = i * 6;
            // Clockwise seen from above (Unity front faces).
            triangles[t] = i * 2; triangles[t + 1] = i * 2 + 1; triangles[t + 2] = n * 2;
            triangles[t + 3] = i * 2 + 1; triangles[t + 4] = n * 2 + 1; triangles[t + 5] = n * 2;
        }
        var mesh = new Mesh { name = "SelectionRing", vertices = vertices, triangles = triangles };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // ---------- FRIENDLY-FACES (owner review 2026-11-04: "the people naturally look mean") ----------
    // Resting face: a blend of ANIM-CATALOG's feeling morphs. By their ARKit units
    // (ANIM-CATALOG faces/manifest.json) it is mouth-corner-puller 0.90 (a smile), brows up 0.39
    // and inner brows up 0.13 (open brows, no frown) and mouth-compression 0.26; Proud above 100
    // stays inside the 1.3x unit range. The weights were picked from two candidate sheets
    // (FRIENDLY-FACES README).
    // Feelings sit on top of this rest (FF-01). ResidentMotion.Feel alone sets every emo_* weight,
    // which would replace the rest, so Alpha shows a feeling through Feel below. A warm feeling
    // shows at its own weight and the rest stays under it as far as the 1.3 ARKit-unit range of
    // mouth-corner-puller allows (MOOD-FACES: at 0.4x, the FF-01 factor, Affectionate looked like
    // the rest); a cold one fades the rest by its strength and shows at its authored strength
    // (Hurt reads sad, Angry cross); no feeling (null) brings the rest back.
    static readonly string[] RestShapes = { "emo_Grateful", "emo_Proud" };
    static readonly float[] RestWeights = { 50f, 130f };
    static readonly string[] WarmShapes = { "emo_Grateful", "emo_Proud", "emo_Affectionate" };
    // mouth-corner-puller per unit weight (ANIM-CATALOG faces/manifest.json); the rest gives 0.895.
    static readonly float[] WarmCorner = { 0.75f, 0.4f, 1.0f };
    static readonly string[] ColdShapes = { "emo_Hurt", "emo_Angry", "emo_Embarrassed" };
    const float RestCorner = 0.895f, CornerCap = 1.3f;

    // The Alpha feeling channel: ResidentMotion.Feel's arguments, with the face composed over the rest.
    public static void Feel(ResidentMotion motion, IDictionary<string, float> faceWeights, float[] posture, float[] gaze)
    {
        if (motion != null) motion.Feel(Compose(faceWeights), posture, gaze);
    }

    // emo_* name -> weight in ResidentMotion.Feel's units (1 = 100); other names pass through.
    public static Dictionary<string, float> Compose(IDictionary<string, float> faceWeights)
    {
        var face = new Dictionary<string, float>();
        float cold = 0f, corner = 0f;
        if (faceWeights != null)
            foreach (var kv in faceWeights)
            {
                int warm = System.Array.IndexOf(WarmShapes, kv.Key);
                if (warm >= 0) corner += WarmCorner[warm] * kv.Value;
                if (System.Array.IndexOf(ColdShapes, kv.Key) >= 0) cold = Mathf.Max(cold, kv.Value);
                face[kv.Key] = kv.Value;
            }
        float keep = Mathf.Min(1f - Mathf.Clamp01(cold), Mathf.Clamp01((CornerCap - corner) / RestCorner));
        for (int s = 0; s < RestShapes.Length; s++)
        {
            float w;
            face.TryGetValue(RestShapes[s], out w);
            face[RestShapes[s]] = w + keep * RestWeights[s] / 100f;
        }
        return face;
    }

    static void RestFace(GameObject model)
    {
        foreach (var skin in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            Mesh mesh = skin.sharedMesh;
            if (mesh == null || mesh.blendShapeCount == 0) continue;
            for (int s = 0; s < RestShapes.Length; s++)
            {
                int index = mesh.GetBlendShapeIndex(RestShapes[s]);
                if (index >= 0) skin.SetBlendShapeWeight(index, RestWeights[s]);
            }
        }
    }

    // Seated hats: MainDirector.HatTilt keeps both irises clear of the brim for cameras up to
    // 42 degrees (Main.unity's) by tilting about the brim's rear edge, which lifts a leaf hat's
    // crown about 9 cm off the forehead. Alpha's highest camera is its 22-degree default view, so
    // here a hat tilts about its crown's rear base by the least whole degree that keeps the irises
    // clear up to 28 degrees in every one of the 16 body/stage cells (FRIENDLY-FACES hat-study.json):
    // leaf hat 6, cap 0. Seeding the director's fit cache before Wear makes it build the seated hat
    // and mask the hair under it with its own rule.
    // shortcut: a fixed seat per known headwear; a new hat keeps MainDirector's fit until it gets a row here.
    static readonly string[] SeatHatIds = { "acc-hat-leaf", "acc-cap-sky" };
    static readonly float[] SeatDegrees = { 6f, 0f };
    readonly HashSet<string> seatedCells = new HashSet<string>();
    Dictionary<string, Transform> wardrobeNodes;

    void SeatHats(BaseBody body, LifeStage stage)
    {
        string cell = body.ToString().ToLowerInvariant() + "-" + stage;
        if (!seatedCells.Add(cell)) return;
        FieldInfo field = typeof(MainDirector).GetField("tilts", BindingFlags.Instance | BindingFlags.NonPublic);
        var tilts = field != null ? field.GetValue(wardrobe) as Dictionary<string, MainDirector.Tilt> : null;
        if (tilts == null || wardrobe.wardrobe == null)
        {
            Debug.LogError("FRIENDLY-FACES hat seat: MainDirector fit cache or wardrobe not found");
            return;
        }
        if (wardrobeNodes == null)
        {
            wardrobeNodes = new Dictionary<string, Transform>();
            foreach (var t in wardrobe.wardrobe.GetComponentsInChildren<Transform>(true)) wardrobeNodes[t.name] = t;
        }
        for (int h = 0; h < SeatHatIds.Length; h++)
        {
            Transform node;
            if (!wardrobeNodes.TryGetValue(SeatHatIds[h] + "__" + cell, out node)) continue;
            // The crown is the first part, as in MainDirector's hair mask.
            var crown = node.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (crown == null || crown.sharedMesh == null) continue;
            Vector3[] vertices = crown.sharedMesh.vertices;
            Vector3 pivot = vertices[0];
            foreach (var v in vertices) if (v.z < pivot.z) pivot = v;   // stage bodies face +Z in bind space
            tilts[cell + "|" + SeatHatIds[h]] = new MainDirector.Tilt
                { pivot = pivot, axis = Vector3.Cross(Vector3.forward, Vector3.up), degrees = SeatDegrees[h] };
        }
    }

    // ---------- MOOD-FACES (research gap 9, M5-OD-52): live feelings on the face ----------
    // Each Mirror tick sets a look's target from its resident's strongest active feeling (highest
    // intensity, newest on a tie; Relationships prunes expired ones) at 0.6/0.8/1.0 for intensity
    // 1/2/3: that feeling's face morph plus ANIM-CATALOG's posture and gaze channels, so a mood
    // also reads in the body. Grateful and Proud read happy, Affectionate flirty, Hurt sad, Angry
    // cross, Embarrassed tense. With no feeling and Energy under 25 a resident looks tired: heavy
    // lids (blink morphs at 50) and a lowered head over the rest smile. LateUpdate moves each look
    // toward its target at BlendRate per second, through Compose, so the rest returns when the
    // feeling ends. Read-only: nothing here writes the simulation.
    const int Tired = 6, Body = 7, MoodSize = 13;   // [0..5] FeelingKind order, [6] tired, [7..12] posture + gaze
    const float TiredBelow = 25f, TiredLids = 50f, BlendRate = 1.8f;
    static readonly float[] TiredBody = { 0.3f, 0.25f, 0f, 0f, 0f, 0.35f };
    static readonly string[] BlinkShapes = { "emo_blink_left", "emo_blink_right" };
    float[][] feelingBody;   // per FeelingKind: catalog posture[3] + gaze[3]

    void TargetMood(ResidentRecord row, float[] target)
    {
        System.Array.Clear(target, 0, MoodSize);
        Feeling strongest = null;
        foreach (Feeling f in row.Feelings)
            if (strongest == null || f.Intensity >= strongest.Intensity) strongest = f;
        if (strongest != null)
        {
            int k = (int)strongest.Kind;
            float strength = 0.4f + 0.2f * strongest.Intensity;
            target[k] = strength;
            float[] body = FeelingBody(k);
            for (int j = 0; j < 6; j++) target[Body + j] = strength * body[j];
        }
        else if (row.GetNeed(Need.Energy) < TiredBelow)
        {
            target[Tired] = 1f;
            for (int j = 0; j < 6; j++) target[Body + j] = TiredBody[j];
        }
    }

    float[] FeelingBody(int kind)
    {
        if (feelingBody == null) feelingBody = new float[6][];
        if (feelingBody[kind] == null)
        {
            var channels = wardrobe.FlowEntry(((FeelingKind)kind).ToString())["render_channels"];
            var body = new float[6];
            channels["posture"]["values"].ToObject<float[]>().CopyTo(body, 0);
            channels["gaze"]["values"].ToObject<float[]>().CopyTo(body, 3);
            feelingBody[kind] = body;
        }
        return feelingBody[kind];
    }

    void ShowMood(Look look, float dt)
    {
        bool moved = false;
        for (int j = 0; j < MoodSize; j++)
        {
            float next = Mathf.MoveTowards(look.mood[j], look.target[j], BlendRate * dt);
            moved |= next != look.mood[j];
            look.mood[j] = next;
        }
        if (!moved && look.moodShown) return;
        look.moodShown = true;
        var face = new Dictionary<string, float>();
        for (int k = 0; k < 6; k++) face["emo_" + (FeelingKind)k] = look.mood[k];
        var posture = new[] { look.mood[Body], look.mood[Body + 1], look.mood[Body + 2] };
        var gaze = new[] { look.mood[Body + 3], look.mood[Body + 4], look.mood[Body + 5] };
        if (look.hat) LevelHead(posture, gaze);
        if (look.motion != null) Feel(look.motion, face, posture, gaze);
        else FaceOnly(look.model, Compose(face));
        foreach (var skin in look.model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            Mesh mesh = skin.sharedMesh;
            if (mesh == null || mesh.blendShapeCount == 0) continue;
            foreach (string shape in BlinkShapes)
            {
                int index = mesh.GetBlendShapeIndex(shape);
                if (index >= 0) skin.SetBlendShapeWeight(index, look.mood[Tired] * TiredLids);
            }
        }
    }

    // MF-01: the GYM-TRAIL rider has no ResidentMotion (its clips drive the joints that Follow
    // copies), so its face takes the composed weights directly, on the renderer ResidentMotion.Feel
    // would use (MainDirector.BodySkin: the last skin with morphs) and by the same rule: every
    // emo_* morph except the blinks is set, unlisted ones to 0.
    static void FaceOnly(GameObject model, Dictionary<string, float> face)
    {
        SkinnedMeshRenderer skin = null;
        foreach (var r in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            if (r.sharedMesh != null && r.sharedMesh.blendShapeCount > 0) skin = r;
        if (skin == null) return;
        Mesh mesh = skin.sharedMesh;
        for (int i = 0; i < mesh.blendShapeCount; i++)
        {
            string shape = mesh.GetBlendShapeName(i);
            if (!shape.StartsWith("emo_", System.StringComparison.Ordinal)
                || shape.StartsWith("emo_blink", System.StringComparison.Ordinal)) continue;
            float w;
            skin.SetBlendShapeWeight(i, face.TryGetValue(shape, out w) ? w * 100f : 0f);
        }
    }

    // MF-02: a seated hat clears the irises only for cameras up to 28 degrees with the head as at
    // rest (SeatHats), and the follow camera sits about 26 degrees up, so any forward bow puts the
    // brim over the eyes. Under a hat the spine keeps half its slump (the body cue) and the head
    // pitches back by the whole remaining spine bow, so it ends level and the face stays as
    // visible as at rest. ResidentMotion.Feel: spine_01 pitch = posture[0] * 15 deg,
    // spine_02 + spine_03 = posture[1] * 18 deg, head = gaze[2] * 10 deg; positive bows.
    // Yaw, roll and an upward (negative) pitch are kept.
    const float HatSlump = 0.5f;

    static void LevelHead(float[] posture, float[] gaze)
    {
        if (posture[0] > 0f) posture[0] *= HatSlump;
        if (posture[1] > 0f) posture[1] *= HatSlump;
        float bow = posture[0] * 15f + posture[1] * 18f + gaze[2] * 10f;
        if (bow > 0f) gaze[2] -= bow / 10f;
    }

    static bool WearsHat(GameObject model)
    {
        foreach (var t in model.GetComponentsInChildren<Transform>(true))
            if (System.Array.IndexOf(SeatHatIds, t.name) >= 0) return true;
        return false;
    }
}
