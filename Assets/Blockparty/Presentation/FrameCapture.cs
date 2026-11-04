// CONTENT-IMPORT capture measurement, shared by the editor captures (MainSceneBuild.Capture)
// and the Main player (MainPlay): render masks of the measured residents and the
// <name>.json sidecar that measure_frames.py reads. Only asset caches are static
// (mask shader, CLOTH catalog), so a Fast Enter Play Mode re-entry carries no game state.
// JSON is built with a small writer: the Editor assembly has no System.Text.Json.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Blockparty.Content;
using Blockparty.Simulation;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class FrameCapture
{
    public const string Host = "build-host";
    public const string ProjectCopy = "m4-copy-c";

    // ------------------------------------------------------------- subjects
    // One measured resident in a capture: its instance, what it plays, and its seat.
    public sealed class Subject
    {
        public GameObject body;
        public string id, flow = "", phase = "", clip = "";
        public BaseBody baseBody;
        public LifeStage stage;
        public float time;
        public bool blink;
        public Transform seat;          // seat slot socket when seated
        public Vector3 seatForward;
        public string coverage;         // JSON: coverage mask file, rect and supersample
        public Dictionary<string, List<SkinnedMeshRenderer>> parts;
    }

    // ---------------------------------------------------------------- render masks
    // Per capture (CHR-BODY-01, CHR-FAC-04): one depth-tested surface-ID mask of every
    // measured resident (masks/<name>.ids.png: R = resident index + 1, G = part id,
    // B = 255; everything else, garments and hair included, writes depth only), and per
    // resident one depth-free coverage mask of its body parts (R bit k = part k rendered;
    // occluded or sub-pixel parts still count, at a supersample that gives the resident
    // at least CoverageHeight mask rows). measure_frames.py counts both with a stdlib PNG reader.
    public const int MaskLayer = 31;
    // A resident faces the camera when the viewer is within this angle of its face's forward.
    public const float FaceAngle = 60f;
    public const int CoverageHeight = 256, MaxSupersample = 32;
    public static readonly string[] MaskParts = { "head", "face", "neck", "left_hand", "right_hand", "left_forearm",
        "right_forearm", "left_eye", "right_eye" };
    public static readonly HashSet<string> HandL = new HashSet<string> { "hand_l", "thumb_01_l", "thumb_02_l", "thumb_03_l",
        "index_01_l", "index_02_l", "index_03_l", "middle_01_l", "middle_02_l", "middle_03_l", "ring_01_l", "ring_02_l",
        "ring_03_l", "pinky_01_l", "pinky_02_l", "pinky_03_l" };
    public static readonly HashSet<string> HandR = new HashSet<string> { "hand_r", "thumb_01_r", "thumb_02_r", "thumb_03_r",
        "index_01_r", "index_02_r", "index_03_r", "middle_01_r", "middle_02_r", "middle_03_r", "ring_01_r", "ring_02_r",
        "ring_03_r", "pinky_01_r", "pinky_02_r", "pinky_03_r" };
    static Shader maskShader;

    public static Material MaskMaterial(Vector4 colour, float zTest, float zWrite, float cull, float colorMask)
    {
        if (maskShader == null) maskShader = Director().maskShader;
        if (maskShader == null) throw new Exception("MainDirector has no mask shader (MaskId.shader)");
        var material = new Material(maskShader);
        material.SetVector("_Colour", colour);
        material.SetFloat("_ZTest", zTest);
        material.SetFloat("_ZWrite", zWrite);
        material.SetFloat("_Cull", cull);
        material.SetFloat("_ColorMask", colorMask);
        return material;
    }

    // Body faces labelled by dominant joint (face = forward 45% of the head, as STAGE's
    // capture labelled them); eye primitives split at the mesh symmetry plane (left = -X in Unity).
    public static Dictionary<string, List<SkinnedMeshRenderer>> Parts(Subject subject)
    {
        if (subject.parts != null) return subject.parts;
        var parts = new Dictionary<string, List<SkinnedMeshRenderer>>();
        SkinnedMeshRenderer body = null;
        var eyes = new List<SkinnedMeshRenderer>();
        foreach (var r in subject.body.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (r.gameObject.layer == MaskLayer || r.sharedMesh == null) continue;
            if (r.sharedMaterial != null && r.sharedMaterial.name.StartsWith("Eye", StringComparison.Ordinal)) eyes.Add(r);
            else if (body == null || r.sharedMesh.vertexCount > body.sharedMesh.vertexCount) body = r;
        }
        if (body == null) throw new Exception(subject.id + " has no body renderer");
        var mesh = body.sharedMesh;
        var labels = Labels(body, body.transform.InverseTransformDirection(subject.body.transform.forward));
        var tris = mesh.GetTriangles(0);
        var groups = new Dictionary<string, List<int>>();
        for (int t = 0; t < tris.Length; t += 3)
        {
            string part = Vote(labels[tris[t]], labels[tris[t + 1]], labels[tris[t + 2]]);
            if (part == null) continue;
            List<int> list;
            if (!groups.TryGetValue(part, out list)) groups[part] = list = new List<int>();
            list.Add(tris[t]); list.Add(tris[t + 1]); list.Add(tris[t + 2]);
        }
        foreach (var pair in groups) AddPart(parts, pair.Key, body, mesh, pair.Value);
        foreach (var eye in eyes)
        {
            var v = eye.sharedMesh.vertices;
            var et = eye.sharedMesh.triangles;
            var left = new List<int>();
            var right = new List<int>();
            for (int t = 0; t < et.Length; t += 3)
                ((v[et[t]].x + v[et[t + 1]].x + v[et[t + 2]].x) < 0f ? left : right).AddRange(new[] { et[t], et[t + 1], et[t + 2] });
            AddPart(parts, "left_eye", eye, eye.sharedMesh, left);
            AddPart(parts, "right_eye", eye, eye.sharedMesh, right);
        }
        return subject.parts = parts;
    }

    public static void AddPart(Dictionary<string, List<SkinnedMeshRenderer>> parts, string part, SkinnedMeshRenderer source, Mesh mesh, List<int> tris)
    {
        if (tris.Count == 0) return;
        // A copy keeps the blend shapes so the part follows face morphs and blinks exactly.
        var copy = UnityEngine.Object.Instantiate(mesh);
        copy.name = mesh.name + "__" + part;
        copy.subMeshCount = 1;
        copy.SetTriangles(tris, 0);
        var go = new GameObject(source.name + "__" + part) { layer = MaskLayer };
        go.transform.SetParent(source.transform.parent, false);
        go.transform.localPosition = source.transform.localPosition;
        go.transform.localRotation = source.transform.localRotation;
        go.transform.localScale = source.transform.localScale;
        var r = go.AddComponent<SkinnedMeshRenderer>();
        r.sharedMesh = copy;
        r.bones = source.bones;
        r.rootBone = source.rootBone;
        r.updateWhenOffscreen = true;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        for (int i = 0; i < copy.blendShapeCount; i++) r.SetBlendShapeWeight(i, source.GetBlendShapeWeight(i));
        r.enabled = false;
        List<SkinnedMeshRenderer> list;
        if (!parts.TryGetValue(part, out list)) parts[part] = list = new List<SkinnedMeshRenderer>();
        list.Add(r);
    }

    public static string[] Labels(SkinnedMeshRenderer smr, Vector3 forward)
    {
        var mesh = smr.sharedMesh;
        var verts = mesh.vertices;
        var weights = mesh.boneWeights;
        var bones = smr.bones;
        var dominant = new string[verts.Length];
        for (int i = 0; i < verts.Length; i++)
        {
            var w = weights[i];
            int best = w.boneIndex0; float top = w.weight0;
            if (w.weight1 > top) { best = w.boneIndex1; top = w.weight1; }
            if (w.weight2 > top) { best = w.boneIndex2; top = w.weight2; }
            if (w.weight3 > top) best = w.boneIndex3;
            if (best >= 0 && best < bones.Length && bones[best] != null) dominant[i] = bones[best].name;
        }
        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = 0; i < verts.Length; i++)
            if (dominant[i] == "head") { float p = Vector3.Dot(verts[i], forward); lo = Mathf.Min(lo, p); hi = Mathf.Max(hi, p); }
        float cut = hi > lo ? hi - 0.45f * (hi - lo) : float.PositiveInfinity;
        var labels = new string[verts.Length];
        for (int i = 0; i < verts.Length; i++)
        {
            string bone = dominant[i];
            if (bone == null) continue;
            if (bone == "head") labels[i] = Vector3.Dot(verts[i], forward) > cut ? "face" : "head";
            else if (bone == "neck_01") labels[i] = "neck";
            else if (HandL.Contains(bone)) labels[i] = "left_hand";
            else if (HandR.Contains(bone)) labels[i] = "right_hand";
            else if (bone == "lowerarm_l") labels[i] = "left_forearm";
            else if (bone == "lowerarm_r") labels[i] = "right_forearm";
        }
        return labels;
    }

    public static string Vote(string a, string b, string c)
    {
        if (a != null && (a == b || a == c)) return a;
        if (b != null && b == c) return b;
        return null;
    }

    public static void PlainMaskCamera(Camera camera)
    {
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
        camera.allowHDR = false;
        camera.allowMSAA = false;
        var data = camera.GetComponent<UniversalAdditionalCameraData>();
        data.renderPostProcessing = false;
        data.antialiasing = AntialiasingMode.None;
        data.renderShadows = false;
    }

    public static Color32[] Read(Camera camera, int w, int h, bool linear)
    {
        var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32,
            linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.Default) { antiAliasing = linear ? 1 : 4 };
        rt.Create();
        camera.targetTexture = rt;
        camera.Render();
        RenderTexture.active = rt;
        var texture = new Texture2D(w, h, TextureFormat.RGBA32, false, linear);
        texture.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        texture.Apply();
        RenderTexture.active = null;
        camera.targetTexture = null;
        var pixels = texture.GetPixels32();
        UnityEngine.Object.DestroyImmediate(texture);
        UnityEngine.Object.DestroyImmediate(rt);
        return pixels;
    }

    public static Color32[] Beauty(Camera camera, int w, int h)
    {
        camera.cullingMask = ~(1 << MaskLayer);
        return Read(camera, w, h, false);
    }

    public static Color32[] IdMask(Camera camera, int w, int h, List<Subject> subjects, int firstIndex)
    {
        var depthOnly = MaskMaterial(Vector4.zero, 4f, 1f, 2f, 0f);
        var saved = new List<KeyValuePair<Renderer, Material[]>>();
        var settings = Save(camera);
        foreach (var s in subjects) Parts(s);   // labelled from the real materials, before the swap
        try
        {
            foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (r.gameObject.layer == MaskLayer) continue;
                saved.Add(new KeyValuePair<Renderer, Material[]>(r, r.sharedMaterials));
                var array = new Material[Mathf.Max(1, r.sharedMaterials.Length)];
                for (int m = 0; m < array.Length; m++) array[m] = depthOnly;
                r.sharedMaterials = array;
            }
            for (int i = 0; i < subjects.Count; i++)
            {
                var parts = Parts(subjects[i]);
                for (int p = 0; p < MaskParts.Length; p++)
                {
                    List<SkinnedMeshRenderer> list;
                    if (!parts.TryGetValue(MaskParts[p], out list)) continue;
                    var id = MaskMaterial(new Vector4((firstIndex + i + 1) / 255f, (p + 1) / 255f, 1f, 1f), 4f, 1f, 2f, 15f);
                    foreach (var r in list) { r.sharedMaterial = id; r.enabled = true; }
                }
            }
            PlainMaskCamera(camera);
            camera.cullingMask = ~0;
            return Read(camera, w, h, true);
        }
        finally
        {
            foreach (var pair in saved) pair.Key.sharedMaterials = pair.Value;
            foreach (var s in subjects) foreach (var list in Parts(s).Values) foreach (var r in list) r.enabled = false;
            Restore(camera, settings);
        }
    }

    // Coverage of one resident's seven body parts: three depth-free renders (ZTest
    // Always, no culling) with parts on the R, G and B channels, packed into R bits.
    public static string Coverage(Camera camera, int w, int h, Subject subject, string dir, string stem, int ox, int oy)
    {
        var parts = Parts(subject);
        // The resident's pixel rectangle from a 1x depth-free render of all its parts
        // (edit-mode skinned bounds are stale); sub-pixel residents fall back to projected joints.
        int px0 = w, py0 = h, px1 = 0, py1 = 0;
        var probe = Save(camera);
        try
        {
            var white = MaskMaterial(Vector4.one, 8f, 0f, 0f, 15f);
            foreach (var list in parts.Values) foreach (var r in list) { r.sharedMaterial = white; r.enabled = true; }
            PlainMaskCamera(camera);
            camera.cullingMask = 1 << MaskLayer;
            var full = Read(camera, w, h, true);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (full[y * w + x].r > 127) { px0 = Math.Min(px0, x); px1 = Math.Max(px1, x + 1); py0 = Math.Min(py0, y); py1 = Math.Max(py1, y + 1); }
        }
        finally
        {
            foreach (var list in parts.Values) foreach (var r in list) r.enabled = false;
            Restore(camera, probe);
        }
        // Joints too (hands and feet can be sub-pixel at 1x).
        {
            var motion = subject.body.GetComponent<ResidentMotion>();
            foreach (string bone in new[] { "head", "hand_l", "hand_r", "foot_l", "foot_r", "pelvis" })
            {
                var t = motion.BodyBone(bone);
                if (t == null) continue;
                var v = camera.WorldToViewportPoint(t.position);
                if (v.z <= camera.nearClipPlane || v.x < 0f || v.x > 1f || v.y < 0f || v.y > 1f) continue;
                px0 = Math.Min(px0, Mathf.FloorToInt(v.x * w)); px1 = Math.Max(px1, Mathf.FloorToInt(v.x * w) + 1);
                py0 = Math.Min(py0, Mathf.FloorToInt(v.y * h)); py1 = Math.Max(py1, Mathf.FloorToInt(v.y * h) + 1);
            }
            if (px1 <= px0) return "null";
        }
        px0 = Math.Max(0, px0 - 2); py0 = Math.Max(0, py0 - 2); px1 = Math.Min(w, px1 + 2); py1 = Math.Min(h, py1 + 2);
        int factor = Mathf.Clamp(Mathf.CeilToInt(CoverageHeight / (float)Mathf.Max(1, py1 - py0)), 1, MaxSupersample);
        int cw = (px1 - px0) * factor, ch = (py1 - py0) * factor;
        var settings = Save(camera);
        var bits = new byte[cw * ch];
        try
        {
            // Sub-frustum of the resident's pixel rectangle at the supersample (the frame's own
            // aspect: an untargeted camera would take the display window's).
            var projection = Matrix4x4.Perspective(camera.fieldOfView, (float)w / h, camera.nearClipPlane, camera.farClipPlane);
            float l = 2f * px0 / w - 1f, r = 2f * px1 / w - 1f, bo = 2f * py0 / h - 1f, t = 2f * py1 / h - 1f;
            var crop = Matrix4x4.identity;
            crop.m00 = 2f / (r - l); crop.m03 = -(r + l) / (r - l);
            crop.m11 = 2f / (t - bo); crop.m13 = -(t + bo) / (t - bo);
            PlainMaskCamera(camera);
            camera.cullingMask = 1 << MaskLayer;
            camera.projectionMatrix = crop * projection;
            for (int pass = 0; pass < 3; pass++)
            {
                for (int c = 0; c < 3; c++)
                {
                    int k = pass * 3 + c;
                    if (k >= 7) continue;
                    List<SkinnedMeshRenderer> list;
                    if (!parts.TryGetValue(MaskParts[k], out list)) continue;
                    var cover = MaskMaterial(Vector4.one, 8f, 0f, 0f, c == 0 ? 8f : c == 1 ? 4f : 2f);
                    foreach (var rr in list) { rr.sharedMaterial = cover; rr.enabled = true; }
                }
                var pixels = Read(camera, cw, ch, true);
                for (int i = 0; i < pixels.Length; i++)
                {
                    if (pixels[i].r > 127) bits[i] |= (byte)(1 << (pass * 3));
                    if (pixels[i].g > 127 && pass * 3 + 1 < 7) bits[i] |= (byte)(1 << (pass * 3 + 1));
                    if (pixels[i].b > 127 && pass * 3 + 2 < 7) bits[i] |= (byte)(1 << (pass * 3 + 2));
                }
                foreach (var list in parts.Values) foreach (var rr in list) rr.enabled = false;
            }
        }
        finally
        {
            camera.ResetProjectionMatrix();
            Restore(camera, settings);
        }
        var image = new Color32[cw * ch];
        for (int i = 0; i < bits.Length; i++) image[i] = new Color32(bits[i], 0, 0, 255);
        string rel = "masks/" + stem + "__" + Safe(subject.id) + ".cover.png";
        WritePng(Path.Combine(dir, rel), image, cw, ch);
        return Obj("path", Q(rel), "rect", "[" + (ox + px0) + "," + (oy + py0) + "," + (ox + px1) + "," + (oy + py1) + "]",
            "supersample", factor.ToString(CultureInfo.InvariantCulture));
    }

    public static string Safe(string id) { return id.Replace('/', '_').Replace(' ', '_'); }

    public sealed class CameraSettings
    {
        public int mask; public CameraClearFlags clear; public Color background; public bool hdr, msaa, post, shadows;
        public AntialiasingMode aa;
    }

    public static CameraSettings Save(Camera camera)
    {
        var data = camera.GetComponent<UniversalAdditionalCameraData>();
        return new CameraSettings { mask = camera.cullingMask, clear = camera.clearFlags, background = camera.backgroundColor,
            hdr = camera.allowHDR, msaa = camera.allowMSAA, post = data.renderPostProcessing, aa = data.antialiasing,
            shadows = data.renderShadows };
    }

    public static void Restore(Camera camera, CameraSettings s)
    {
        var data = camera.GetComponent<UniversalAdditionalCameraData>();
        camera.cullingMask = s.mask; camera.clearFlags = s.clear; camera.backgroundColor = s.background;
        camera.allowHDR = s.hdr; camera.allowMSAA = s.msaa; data.renderPostProcessing = s.post; data.antialiasing = s.aa;
        data.renderShadows = s.shadows;
    }

    public static void WritePng(string path, Color32[] pixels, int w, int h)
    {
        var texture = new Texture2D(w, h, TextureFormat.RGBA32, false);
        texture.SetPixels32(pixels);
        texture.Apply();
        var rgb = new Texture2D(w, h, TextureFormat.RGB24, false);
        rgb.SetPixels32(texture.GetPixels32());
        rgb.Apply();
        File.WriteAllBytes(path, rgb.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(texture);
        UnityEngine.Object.DestroyImmediate(rgb);
    }

    // Measured inputs the PNG tool cannot see: what each resident plays, its worn
    // covering garments (CLOTH culled faces and offset), face-render and blink tags, and its seat.
    public static string SubjectJson(Subject s, int index, Camera camera)
    {
        var motion = s.body.GetComponent<ResidentMotion>();
        string cell = s.baseBody.ToString().ToLowerInvariant() + "-" + s.stage;
        var fit = Cloth()["fits"].First(f => (string)f["body"] + "-" + (string)f["stage"] == cell)["items"];
        var garments = new List<string>();
        var accessories = new List<string>();
        var seen = new HashSet<string>();
        foreach (Transform t in s.body.GetComponentsInChildren<Transform>(true))
        {
            var item = fit[t.name];
            if (item == null || t.GetComponent<SkinnedMeshRenderer>() == null || !seen.Add(t.name)) continue;
            string row = Obj("item", Q(t.name), "culled_body_faces", ((JArray)item["culled_faces"]).Count.ToString(CultureInfo.InvariantCulture),
                "push_out", ((float)item["offset_m"]).ToString("R", CultureInfo.InvariantCulture));
            if (t.name.StartsWith("top-", StringComparison.Ordinal) || t.name.StartsWith("bottom-", StringComparison.Ordinal)
                || t.name.StartsWith("shoe-", StringComparison.Ordinal)) garments.Add(row);
            else if (t.name.StartsWith("acc-", StringComparison.Ordinal)) accessories.Add(row);
        }
        // Faces the camera: the viewer is within FaceAngle of the posed face's forward (head joint frame).
        var head = motion.BodyBone("head");
        bool faceRender = false;
        float faceAngle = 180f;
        Dictionary<string, List<SkinnedMeshRenderer>> parts = Parts(s);
        List<SkinnedMeshRenderer> le, re;
        if (head != null && parts.TryGetValue("left_eye", out le) && parts.TryGetValue("right_eye", out re))
        {
            var eyes = (Centre(le) + Centre(re)) * 0.5f;
            faceAngle = Vector3.Angle(motion.FaceForward(), camera.transform.position - eyes);
            faceRender = faceAngle <= FaceAngle;
        }
        string seat = "null";
        if (s.seat != null)
        {
            var thighL = motion.BodyBone("thigh_l").position;
            var thighR = motion.BodyBone("thigh_r").position;
            var pelvis = motion.BodyBone("pelvis").position;
            var pelvisForward = Vector3.ProjectOnPlane(Vector3.Cross(thighR - thighL, Vector3.up), Vector3.up);
            var slotForward = Vector3.ProjectOnPlane(s.seatForward, Vector3.up);
            var error = Vector3.ProjectOnPlane(pelvis - s.seat.position, Vector3.up).magnitude;
            seat = Obj("socket", Q(s.seat.name), "facing_degrees", Vector3.Angle(pelvisForward, slotForward).ToString("R", CultureInfo.InvariantCulture),
                "approach_error_m", error.ToString("R", CultureInfo.InvariantCulture),
                "pelvis", Floats(new[] { pelvis.x, pelvis.y, pelvis.z }),
                "socket_position", Floats(new[] { s.seat.position.x, s.seat.position.y, s.seat.position.z }));
        }
        return Obj("id", Q(s.id), "index", (index + 1).ToString(CultureInfo.InvariantCulture),
            "body", Q(s.baseBody.ToString()), "stage", Q(s.stage.ToString()),
            "flow", Q(s.flow), "phase", Q(s.phase), "clip", Q(s.clip), "time_s", s.time.ToString("R", CultureInfo.InvariantCulture),
            "variant", Q(StageVariants.Resolve(s.baseBody, s.stage)),
            "faces_camera", faceRender ? "true" : "false", "face_angle_degrees", faceAngle.ToString("R", CultureInfo.InvariantCulture),
            "blink", s.blink ? "true" : "false",
            "garments", "[" + string.Join(",", garments) + "]", "accessories", "[" + string.Join(",", accessories) + "]",
            "seat", seat, "coverage", s.coverage ?? "null");
    }

    public static Vector3 Centre(List<SkinnedMeshRenderer> list)
    {
        var sum = Vector3.zero;
        foreach (var r in list) { r.enabled = true; sum += r.bounds.center; r.enabled = false; }
        return sum / list.Count;
    }

    static JObject cloth;
    public static JObject Cloth() { return cloth ?? (cloth = JObject.Parse(Director().clothCatalog.text)); }

    public static MainDirector Director()
    {
        var director = UnityEngine.Object.FindFirstObjectByType<MainDirector>();
        if (director == null) throw new Exception("no MainDirector in the open scene");
        return director;
    }

    public static string CameraJson(Camera camera)
    {
        var p = camera.transform.position;
        var f = camera.transform.forward;
        return Obj("position", Floats(new[] { p.x, p.y, p.z }), "forward", Floats(new[] { f.x, f.y, f.z }),
            "fov", camera.fieldOfView.ToString("R", CultureInfo.InvariantCulture));
    }

    public static void WriteSidecar(string dir, string name, string camera, string preset, int w, int h, string tree, string[] argv,
        List<string> residents, string idMask, string tool = "MainSceneBuild.Capture")
    {
        File.WriteAllText(Path.Combine(dir, name + ".json"), "{" + camera + ","
            + Obj("light_preset", Q(preset),
                "seed", MainDirector.Seed.ToString(CultureInfo.InvariantCulture),
                "resolution", "[" + w + "," + h + "]",
                "tool", Q("Unity " + Application.unityVersion + " RenderTexture capture, " + tool),
                "tree_hash", Q(tree),
                "host", Q(Host), "project_copy", Q(ProjectCopy), "display", Q(":0"),
                "argv", Strings(argv),
                "measure", Obj("id_mask", Q(idMask), "parts", Strings(MaskParts), "residents", "[" + string.Join(",", residents) + "]"))
                .Substring(1) + "\n");
    }

    // ------------------------------------------------------------- json bits
    public static string Q(string value)
    {
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")
            .Replace("\r", "\\r").Replace("\t", "\\t") + "\"";
    }

    public static string Obj(params string[] pairs)
    {
        var parts = new List<string>();
        for (int i = 0; i + 1 < pairs.Length; i += 2) parts.Add(Q(pairs[i]) + ":" + pairs[i + 1]);
        return "{" + string.Join(",", parts) + "}";
    }

    public static string Floats(float[] values)
    {
        var parts = new List<string>();
        foreach (var value in values) parts.Add(((float)Math.Round(value, 4)).ToString("R", CultureInfo.InvariantCulture));
        return "[" + string.Join(",", parts) + "]";
    }

    public static string Strings(string[] values)
    {
        var parts = new List<string>();
        foreach (var value in values) parts.Add(Q(value));
        return "[" + string.Join(",", parts) + "]";
    }

    // The measured part of one capture whose beauty frame is already written: per resident
    // coverage masks (residents with no part in frame are left out), the surface-ID mask and the sidecar.
    public static void Measure(string dir, string name, Camera camera, int w, int h, List<Subject> subjects,
        string cameraJson, string preset, string tree, string[] argv, string tool)
    {
        var inFrame = new List<Subject>();
        foreach (var s in subjects)
        {
            s.coverage = Coverage(camera, w, h, s, dir, name, 0, 0);
            if (s.coverage != "null") inFrame.Add(s);
        }
        string idMask = "masks/" + name + ".ids.png";
        WritePng(Path.Combine(dir, idMask), IdMask(camera, w, h, inFrame, 0), w, h);
        var rows = new List<string>();
        for (int i = 0; i < inFrame.Count; i++) rows.Add(SubjectJson(inFrame[i], i, camera));
        WriteSidecar(dir, name, cameraJson, preset, w, h, tree, argv, rows, idMask, tool);
    }

    // The household as measured subjects (resident names are Resident-<id>-<body>-<stage>).
    public static List<Subject> HouseholdSubjects(MainDirector director)
    {
        var list = new List<Subject>();
        foreach (Transform child in director.residentRoot)
        {
            var motion = child.GetComponent<ResidentMotion>();
            if (motion == null) continue;
            var bits = child.name.Split('-');
            list.Add(new Subject
            {
                body = child.gameObject, id = child.name,
                baseBody = (BaseBody)Enum.Parse(typeof(BaseBody), bits[2]), stage = (LifeStage)Enum.Parse(typeof(LifeStage), bits[3]),
                flow = motion.Flow ?? "", phase = motion.Phase ?? "", clip = motion.Current != null ? motion.Current.name : "",
                time = motion.Time,
            });
        }
        return list;
    }
}
