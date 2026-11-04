using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Blockparty.Content;
using Blockparty.Contracts;
using Blockparty.Simulation;
using Newtonsoft.Json.Linq;
using UnityEngine;

// CONTENT-IMPORT@1 main-scene director (Main.unity). MainSceneBuild stores every
// imported stage variant under its StageVariants.Resolve path; the director seeds
// one household spanning all eight life stages and dresses each resident with the
// variant for its stored base body and stage, re-dressing when the stage changes.
// CLOTH-CATALOG appearance is applied on that resolved body from the resident's
// stored record, as CLOTH's own fit renders do: worn garments, hair and
// accessory come from the wardrobe cell of the same body/stage, the body faces
// they cover are culled (masking, never push-out), skin+face and iris textures
// replace the defaults, and hair is tinted and masked inside worn headwear.
// Presentation: headwear is tilted back just enough to keep both irises clear
// of the brim (HatTilt), residents stand on validated spawn slots, and they
// render on ResidentLayer so a warm camera-aligned fill lights only them; skin
// darker than DeepSkinLuminance also takes DeepSkinFill on DeepSkinLayer, whose
// sheen keeps facial planes on the darkest tones (CLOTH's studio fill does this).
// Motion: every resident plays ANIM-CATALOG clips through FlowClips (the catalog's
// flow -> clip_ids resolver) on its resolved body (ResidentMotion retargets them).
// Newborns and infants are carried or held by a caregiver (the landed hold take);
// the others cycle standing activities their stage allows and the seven feelings,
// with the feeling face morphs from ANIM's facial assets (feelings.bytes).
// No statics: a Fast Enter Play Mode re-entry starts from the serialized scene.
public sealed class MainDirector : MonoBehaviour
{
    public const long WorldId = 801L;
    public const long Seed = 42L;

    public string[] variantPaths = new string[0];
    public GameObject[] variantPrefabs = new GameObject[0];
    public Transform residentRoot;
    public Transform propLibrary;
    public Vector3 courtyard = new Vector3(3f, 0f, 25f);
    // Household slots on walkable ground outside buildings, chosen by MainSceneBuild.
    public Vector3[] spawn = new Vector3[0];
    // Warm fill that lights only ResidentLayer, aimed from the viewing camera.
    public Light residentFill;
    public Light deepSkinFill;

    // Unnamed, unused built-in user layer (TagManager unchanged).
    public const int ResidentLayer = 3;
    public const int DeepSkinLayer = 6;
    // Mean linear albedo luminance below which a skin tone takes DeepSkinFill. No SKIN-TONES
    // tone does: Deep (0.034) is the darkest and holds L* 24 in Alpha shade (SKIN-TONES lstar.json).
    public const float DeepSkinLuminance = 0.03f;
    // Eyes stay clear of headwear up to this camera elevation: Main.unity's
    // MainCamera looks down at 41 degrees, every capture camera lower.
    public const float EyeClearElevation = 42f;
    // Viewing azimuths around the face that must see both irises.
    public const float EyeClearAzimuth = 75f;
    const float MaxHatTilt = 30f;

    // CLOTH-CATALOG inputs, bound by MainSceneBuild. Texture arrays are indexed
    // (int)BaseBody * OptionCount(dimension) + preset index.
    public TextAsset clothCatalog;
    public GameObject wardrobe;
    public Texture2D[] skinTones = new Texture2D[0];
    public Texture2D[] faces = new Texture2D[0];
    public Texture2D[] eyeColors = new Texture2D[0];
    public Color[] hairColors = new Color[0];

    // ANIM-CATALOG inputs, bound by MainSceneBuild.
    public TextAsset animCatalog;
    public TextAsset actionCatalog;
    public TextAsset stagePresets;
    public TextAsset feelings;
    public GameObject actionRig;
    public AnimationClip[] clips = new AnimationClip[0];
    // ANIM-CATALOG's carrier pose: the landed ANIM-UAL hold take at 0.3 s (render_captures.add_carrier).
    public AnimationClip holdClip;
    public const float HoldTime = 0.3f;
    // Carried infant root, ahead of the carrier root (render_captures: carrier +0.12, infant -0.12 Blender Y).
    public const float CarryAhead = 0.24f;
    // Render-mask shader for measured captures (FrameCapture), bound so the player build includes it.
    public Shader maskShader;
    public const float LoopSeconds = 6f, FeelingSeconds = 3f;
    public static readonly string[] Phases = { "approach", "start", "loop", "finish", "interruption" };
    public static readonly string[] Feelings = { "Neutral", "Grateful", "Hurt", "Angry", "Affectionate", "Embarrassed", "Proud" };
    // render_captures.add_icon glyphs, one per feeling.
    public static readonly string[] FeelingGlyphs = { "...", "THANKS", ":/", "!", "<3", "?", "*" };
    // Standing activities a resident can play on its own spot (ANIM author_activity TASKS 'standing', no prop).
    public static readonly string[] SpotActivities = { "ACT-PARTY-02", "ACT-REL-05", "ACT-REL-01", "ACT-SPORT-01",
        "ACT-REL-04", "ACT-PARTY-01", "ACT-REL-03" };

    WorldState world;
    AppearanceStore looks;
    readonly Dictionary<ResidentId, GameObject> dressed = new Dictionary<ResidentId, GameObject>();
    readonly Dictionary<ResidentId, string> dressedPath = new Dictionary<ResidentId, string>();
    Cloth cloth;
    Dictionary<string, Transform> wardrobeNodes;
    readonly Dictionary<string, Mesh> meshes = new Dictionary<string, Mesh>();
    readonly Dictionary<string, Material> materials = new Dictionary<string, Material>();
    readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>();

    readonly Dictionary<ResidentId, int> cycle = new Dictionary<ResidentId, int>();
    readonly Dictionary<ResidentId, ResidentId> carrierOf = new Dictionary<ResidentId, ResidentId>();
    Dictionary<string, AnimationClip> clipByName;
    JObject anim;
    Dictionary<string, JObject> actions;
    Dictionary<string, List<KeyValuePair<string, KeyValuePair<int[], Vector3[]>>>> morphs;

    // Latest cue captions (what each resident just started), newest last; MainPlay shows them.
    public readonly List<string> Cues = new List<string>();
    const int CueLines = 3;

    public WorldState World { get { return world; } }
    public GameObject Body(ResidentId id) { GameObject body; return dressed.TryGetValue(id, out body) ? body : null; }

    public GameObject Variant(BaseBody body, LifeStage stage)
    {
        int index = Array.IndexOf(variantPaths, StageVariants.Resolve(body, stage));
        return index < 0 ? null : variantPrefabs[index];
    }

    // BP-PRES-03: newborns and infants are carried or lie in a crib, never walk.
    public static string Presentation(LifeStage stage)
    {
        return stage <= LifeStage.Infant ? "carried/crib" : "standing";
    }

    void Start()
    {
        Populate();
    }

    void Update()
    {
        if (world == null) return;
        Dress();
        Animate();
    }

    void LateUpdate()
    {
        if (Camera.main == null) return;
        AimFill(residentFill, Camera.main.transform);
        AimFill(deepSkinFill, Camera.main.transform);
        // Emote glyphs face the viewer.
        foreach (var body in dressed.Values)
        {
            var icon = body != null ? body.transform.Find("EmoteIcon") : null;
            if (icon != null) icon.rotation = Camera.main.transform.rotation;
        }
    }

    // The fill comes from 20 degrees above the camera's view axis, as CLOTH's front key/fill.
    public static void AimFill(Light fill, Transform view)
    {
        if (fill != null) fill.transform.rotation = view.rotation * Quaternion.Euler(20f, 0f, 0f);
    }

    public void Populate()
    {
        Clear();
        var stages = (LifeStage[])Enum.GetValues(typeof(LifeStage));
        world = WorldState.CreateSeeded(WorldId, Seed, stages.Length, null);
        looks = new AppearanceStore();
        var residents = world.ResidentsSnapshot();
        for (int i = 0; i < residents.Length; i++)
        {
            world.SetStage(StateWriter.SimCore, residents[i].Id, stages[i], 0f);
            looks.Register(residents[i].Id, new WorldRng(Seed + 7919L * (long)residents[i].Id));
        }
        Dress();
    }

    // Instantiates the resolved variant for every resident whose body/stage path or
    // worn clothing differs from what it wears; the AGING boundary is the only path change.
    public void Dress()
    {
        var residents = world.ResidentsSnapshot();
        for (int i = 0; i < residents.Length; i++)
        {
            var id = residents[i].Id;
            AppearanceRecord look;
            if (!looks.TryGet(id, out look)) continue;
            int clothing = looks.WornClothing(id, OutfitCategory.Everyday);
            string path = StageVariants.Resolve(look.BaseBody, residents[i].Stage) + "|" + clothing;
            string current;
            if (dressedPath.TryGetValue(id, out current) && current == path) continue;
            GameObject old;
            if (dressed.TryGetValue(id, out old)) Discard(old);
            var body = Wear(look.BaseBody, residents[i].Stage, look, clothing, residentRoot);
            if (body == null)
            {
                dressedPath[id] = path;
                continue;
            }
            body.name = "Resident-" + (long)id + "-" + look.BaseBody + "-" + residents[i].Stage;
            body.transform.position = Slot(i);
            // Facing the default camera (MainCamera looks along +Z).
            body.transform.rotation = Quaternion.LookRotation(Vector3.back, Vector3.up);
            Motion(body, look.BaseBody, residents[i].Stage);
            dressed[id] = body;
            dressedPath[id] = path;
            cycle.Remove(id);
            carrierOf.Clear();
        }
    }

    // ------------------------------------------------------------------ motion
    // ANIM-CATALOG resolver: the imported clip names of one flow phase (catalog entry
    // flow -> clip_ids). Flow ids are ACT codes, commands, life stages and feelings.
    public string[] FlowClips(string flow, string phase)
    {
        var catalog = Anim();
        foreach (var entry in catalog["entries"])
        {
            if ((string)entry["id"] != flow) continue;
            var names = new List<string>();
            foreach (var clip in entry["flow"][phase]) names.Add((string)catalog["clip_ids"][(string)clip]);
            return names.ToArray();
        }
        return new string[0];
    }

    public JObject Anim() { return anim ?? (anim = JObject.Parse(animCatalog.text)); }

    public JToken FlowEntry(string flow)
    {
        foreach (var entry in Anim()["entries"]) if ((string)entry["id"] == flow) return entry;
        return null;
    }

    public AnimationClip Clip(string clipName)
    {
        if (clipByName == null)
        {
            clipByName = new Dictionary<string, AnimationClip>();
            foreach (var clip in clips) if (clip != null) clipByName[clip.name] = clip;
        }
        AnimationClip found;
        if (!clipByName.TryGetValue(clipName, out found)) Debug.LogError("CONTENT-IMPORT missing imported clip " + clipName);
        return found;
    }

    public AnimationClip FlowClip(string flow, string phase)
    {
        var names = FlowClips(flow, phase);
        return names.Length == 0 ? null : Clip(names[0]);
    }

    // ANIM stage_tracks translation scale: stage height over the same body's adult height.
    public float StageScale(BaseBody body, LifeStage stage)
    {
        float height = 0f, adult = 0f;
        foreach (var entry in JObject.Parse(stagePresets.text)["entries"])
        {
            if (!string.Equals((string)entry["body"], body.ToString(), StringComparison.OrdinalIgnoreCase)) continue;
            if ((string)entry["stage"] == stage.ToString()) height = (float)entry["stage_height_m"];
            if ((string)entry["stage"] == "Adult") adult = (float)entry["stage_height_m"];
        }
        if (height <= 0f || adult <= 0f) throw new InvalidOperationException("no stage preset " + body + "/" + stage);
        return height / adult;
    }

    public bool Allowed(string activity, LifeStage stage)
    {
        if (actions == null)
        {
            actions = new Dictionary<string, JObject>();
            foreach (JObject row in JObject.Parse(actionCatalog.text)["activities"]) actions[(string)row["id"]] = row;
        }
        JObject a;
        if (!actions.TryGetValue(activity, out a)) return false;
        var min = (LifeStage)Enum.Parse(typeof(LifeStage), (string)a["min_stage"]);
        var max = (LifeStage)Enum.Parse(typeof(LifeStage), (string)a["max_stage"]);
        return stage >= min && stage <= max;
    }

    public ResidentMotion Motion(GameObject body, BaseBody baseBody, LifeStage stage)
    {
        var motion = body.GetComponent<ResidentMotion>();
        if (motion != null) return motion;
        if (actionRig == null || animCatalog == null)
        {
            Debug.LogError("CONTENT-IMPORT ANIM-CATALOG not bound");
            return null;
        }
        motion = body.AddComponent<ResidentMotion>();
        motion.Bind(actionRig, StageScale(baseBody, stage), BodySkin(body));
        return motion;
    }

    static SkinnedMeshRenderer BodySkin(GameObject body)
    {
        SkinnedMeshRenderer skin = null;
        foreach (var r in body.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            if (r.sharedMesh != null && r.sharedMesh.blendShapeCount > 0) skin = r;
        return skin;
    }

    public ResidentMotion.Step Once(AnimationClip clip, string phase = null) { return new ResidentMotion.Step { clip = clip, loop = false, phase = phase }; }
    public ResidentMotion.Step Loop(AnimationClip clip, float seconds, string phase = null) { return new ResidentMotion.Step { clip = clip, loop = true, seconds = seconds, phase = phase }; }

    // Plays one activity flow on its spot: start, loop, finish (approach and
    // interruption need navigation and a scheduler, which this scene does not run).
    public void PlayFlow(ResidentMotion motion, string flow, float loopSeconds)
    {
        motion.Feel(null, null, null);
        motion.Play(flow, "start", Once(FlowClip(flow, "start"), "start"), Loop(FlowClip(flow, "loop"), loopSeconds, "loop"),
            Once(FlowClip(flow, "finish"), "finish"));
    }

    // A feeling: its reaction clips plus the catalog render channels (face, posture, gaze).
    public void PlayFeeling(ResidentMotion motion, string feeling, float seconds)
    {
        var entry = FlowEntry(feeling);
        var channels = entry["render_channels"];
        var weights = new Dictionary<string, float>();
        var targets = (JArray)channels["face"]["target_paths"];
        var values = (JArray)channels["face"]["values"];
        for (int i = 0; i < targets.Count; i++) weights[(string)targets[i]] = (float)values[i];
        motion.Play(feeling, "start", Once(FlowClip(feeling, "start"), "start"), Loop(FlowClip(feeling, "loop"), seconds, "loop"),
            Once(FlowClip(feeling, "finish"), "finish"));
        motion.Feel(weights, channels["posture"]["values"].ToObject<float[]>(), channels["gaze"]["values"].ToObject<float[]>());
        Icon(motion.gameObject, feeling);
    }

    // The feeling's emote glyph above the head (ANIM icon channel: shape index, scale).
    public GameObject Icon(GameObject body, string feeling)
    {
        var old = body.transform.Find("EmoteIcon");
        if (old != null) Discard(old.gameObject);
        int index = Array.IndexOf(Feelings, feeling);
        if (index < 0) return null;
        var head = body.GetComponent<ResidentMotion>().BodyBone("head");
        var holder = new GameObject("EmoteIcon");
        holder.transform.SetParent(body.transform, false);
        var text = holder.AddComponent<TextMesh>();
        text.text = FeelingGlyphs[index];
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.GetComponent<MeshRenderer>().sharedMaterial = text.font.material;
        text.anchor = TextAnchor.LowerCenter;
        text.alignment = TextAlignment.Center;
        text.fontSize = 64;
        text.color = Color.white;
        float scale = (float)FlowEntry(feeling)["render_channels"]["icon"]["values"][1];
        text.characterSize = 0.045f * scale;
        holder.transform.position = head.position + Vector3.up * 0.28f;
        if (Camera.main != null) holder.transform.rotation = Camera.main.transform.rotation;
        return holder;
    }

    // Caregiver for each newborn/infant: the first free resident at YoungAdult or older.
    void AssignCarriers()
    {
        var residents = world.ResidentsSnapshot();
        var busy = new HashSet<ResidentId>();
        foreach (var dependent in residents)
        {
            if (dependent.Stage > LifeStage.Infant || !dressed.ContainsKey(dependent.Id)) continue;
            foreach (var carer in residents)
            {
                if (carer.Stage < LifeStage.YoungAdult || busy.Contains(carer.Id) || !dressed.ContainsKey(carer.Id)) continue;
                busy.Add(carer.Id);
                carrierOf[dependent.Id] = carer.Id;
                break;
            }
        }
    }

    public bool Carries(ResidentId id) { return carrierOf.ContainsValue(id); }

    // A carried or held dependent: CarryAhead in front of its carrier, facing the same way,
    // its pelvis resting on the carrier's hands (ANIM authored the lift for its adult male
    // carrier; this keeps the hold on shorter carriers' arms).
    public static void Carry(GameObject dependent, GameObject carrier)
    {
        var t = carrier.transform;
        dependent.transform.SetPositionAndRotation(t.position + t.forward * CarryAhead, t.rotation);
        var held = dependent.GetComponent<ResidentMotion>();
        var carer = carrier.GetComponent<ResidentMotion>();
        if (held == null || carer == null) return;
        held.Apply();
        var left = carer.BodyBone("hand_l");
        var right = carer.BodyBone("hand_r");
        var pelvis = held.BodyBone("pelvis");
        if (left == null || right == null || pelvis == null) return;
        dependent.transform.position += Vector3.up * ((left.position.y + right.position.y) * 0.5f - pelvis.position.y);
    }

    // Edit-mode clock for captures: the same cycle LateUpdate drives in play.
    public void Tick(float dt)
    {
        Animate();
        foreach (var body in dressed.Values)
        {
            var motion = body != null ? body.GetComponent<ResidentMotion>() : null;
            if (motion != null) motion.Advance(dt);
        }
        Animate();
    }

    void Animate()
    {
        if (carrierOf.Count == 0) AssignCarriers();
        var residents = world.ResidentsSnapshot();
        for (int i = 0; i < residents.Length; i++)
        {
            var id = residents[i].Id;
            GameObject body;
            if (!dressed.TryGetValue(id, out body)) continue;
            var motion = body.GetComponent<ResidentMotion>();
            if (motion == null) continue;
            ResidentId carer;
            if (carrierOf.TryGetValue(id, out carer))
            {
                // Carried (newborn) or held (infant, which ANIM aliases to the carried pose).
                if (motion.Flow != residents[i].Stage.ToString())
                    motion.Play(residents[i].Stage.ToString(), "approach",
                        Loop(FlowClip(residents[i].Stage.ToString(), "approach"), float.MaxValue));
                Carry(body, dressed[carer]);
                continue;
            }
            if (Carries(id))
            {
                if (motion.Flow != "carry") motion.Hold("carry", "hold", holdClip, HoldTime);
                continue;
            }
            if (!motion.Done && motion.Flow != null) continue;
            int n;
            cycle.TryGetValue(id, out n);
            cycle[id] = n + 1;
            var old = body.transform.Find("EmoteIcon");
            if (old != null) Discard(old.gameObject);
            if (n % 2 == 1)
            {
                PlayFeeling(motion, Feelings[(i + n / 2) % Feelings.Length], FeelingSeconds);
                Cue(residents[i], Feelings[(i + n / 2) % Feelings.Length]);
                continue;
            }
            var allowed = new List<string>();
            foreach (string act in SpotActivities) if (Allowed(act, residents[i].Stage)) allowed.Add(act);
            if (allowed.Count == 0) { motion.Play(residents[i].Stage.ToString(), "start", Loop(FlowClip(residents[i].Stage.ToString(), "start"), LoopSeconds)); continue; }
            PlayFlow(motion, allowed[(i + n / 2) % allowed.Count], LoopSeconds);
            Cue(residents[i], allowed[(i + n / 2) % allowed.Count]);
        }
    }

    // Caption of a started flow: the action catalog's activity name (its loop transition,
    // e.g. "ExerciseSession loop" -> "Exercise Session"), or the feeling shown.
    void Cue(ResidentRecord resident, string flow)
    {
        JObject act;
        string text = "feels " + flow;
        if (actions != null && actions.TryGetValue(flow, out act))
        {
            string name = ((string)act.SelectToken("flow.loop.transition") ?? flow).Replace(" loop", "");
            text = System.Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
        }
        Cues.Add("Resident " + (long)resident.Id + " (" + resident.Stage + "): " + text);
        if (Cues.Count > CueLines) Cues.RemoveAt(0);
    }

    // ------------------------------------------------------------------ feelings
    // ANIM-CATALOG facial morphs (feelings.bytes from build_feelings.py) added as blend shapes.
    Mesh Morphed(Mesh mesh, string cell)
    {
        if (feelings == null) return mesh;
        if (morphs == null) morphs = ReadMorphs(feelings.bytes);
        List<KeyValuePair<string, KeyValuePair<int[], Vector3[]>>> targets;
        if (!morphs.TryGetValue(cell, out targets)) { Debug.LogError("CONTENT-IMPORT no feeling morphs for " + cell); return mesh; }
        int count = mesh.vertexCount;
        foreach (var target in targets)
        {
            var deltas = new Vector3[count];
            var index = target.Value.Key;
            for (int k = 0; k < index.Length; k++)
            {
                if (index[k] >= count) { Debug.LogError("CONTENT-IMPORT feeling morph outside " + cell); return mesh; }
                deltas[index[k]] = target.Value.Value[k];
            }
            mesh.AddBlendShapeFrame(target.Key, 100f, deltas, null, null);
        }
        return mesh;
    }

    static Dictionary<string, List<KeyValuePair<string, KeyValuePair<int[], Vector3[]>>>> ReadMorphs(byte[] data)
    {
        var result = new Dictionary<string, List<KeyValuePair<string, KeyValuePair<int[], Vector3[]>>>>();
        using (var reader = new BinaryReader(new MemoryStream(data)))
        {
            if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != "BPFEEL1\0") throw new InvalidDataException("feelings.bytes magic");
            int cells = reader.ReadInt32();
            for (int c = 0; c < cells; c++)
            {
                string body = Text(reader), stage = Text(reader);
                reader.ReadInt32();
                int targets = reader.ReadInt32();
                var list = new List<KeyValuePair<string, KeyValuePair<int[], Vector3[]>>>();
                for (int t = 0; t < targets; t++)
                {
                    string name = Text(reader);
                    int n = reader.ReadInt32();
                    var index = new int[n];
                    var delta = new Vector3[n];
                    for (int k = 0; k < n; k++)
                    {
                        index[k] = reader.ReadInt32();
                        delta[k] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    }
                    list.Add(new KeyValuePair<string, KeyValuePair<int[], Vector3[]>>(name, new KeyValuePair<int[], Vector3[]>(index, delta)));
                }
                result[body.ToLowerInvariant() + "-" + stage] = list;
            }
        }
        return result;
    }

    static string Text(BinaryReader reader) { return Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32())); }

    // Every slot is validated open ground (MainSceneBuild.Spawn); later-born residents
    // take the extra validated slots after the eight household rows.
    // shortcut: a household larger than the validated pool reuses slots; grow the pool then.
    Vector3 Slot(int i)
    {
        if (spawn.Length == 0) return courtyard;
        return spawn[i % spawn.Length];
    }

    // The resolved stage variant wearing this appearance record and clothing combo.
    public GameObject Wear(BaseBody body, LifeStage stage, AppearanceRecord look, int clothing, Transform parent)
    {
        var prefab = Variant(body, stage);
        if (prefab == null)
        {
            Debug.LogError("CONTENT-IMPORT missing imported variant " + StageVariants.Resolve(body, stage));
            return null;
        }
        var instance = Instantiate(prefab, parent);
        SkinnedMeshRenderer skin = null;
        if (clothCatalog == null || wardrobe == null) Debug.LogError("CONTENT-IMPORT CLOTH-CATALOG not bound");
        else skin = Dress(instance, body, stage, look, clothing);
        foreach (var t in instance.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = ResidentLayer;
        if (skin != null && Luminance(skinTones[(int)body * ResidentFactory.SkinToneCount + look.SkinTone]) < DeepSkinLuminance)
            skin.gameObject.layer = DeepSkinLayer;
        return instance;
    }

    // A stand-alone frame (stage portrait) wears a seeded look drawn like a resident's.
    public GameObject WearSample(BaseBody body, LifeStage stage, long id, Transform parent)
    {
        var store = new AppearanceStore();
        var resident = new ResidentId(id);
        store.Register(resident, new WorldRng(Seed + 7919L * id));
        AppearanceRecord look;
        store.TryGet(resident, out look);
        return Wear(body, stage, look, store.WornClothing(resident, OutfitCategory.Everyday), parent);
    }

    sealed class Cloth
    {
        public string[] hair, top, bottom, footwear, accessory;
        public readonly HashSet<string> headwear = new HashSet<string>();
        public readonly Dictionary<string, Dictionary<string, int[]>> culled = new Dictionary<string, Dictionary<string, int[]>>();
    }

    Cloth Catalog()
    {
        if (cloth != null) return cloth;
        var doc = JObject.Parse(clothCatalog.text);
        var dims = doc["dimensions"];
        var c = new Cloth
        {
            hair = Ids(dims["hair_style"]), top = Ids(dims["top"]), bottom = Ids(dims["bottom"]),
            footwear = Ids(dims["footwear"]), accessory = Ids(dims["accessory"]),
        };
        foreach (var row in dims["accessory"]) if (row["masks_hair"] != null) c.headwear.Add((string)row["id"]);
        foreach (var fit in doc["fits"])
        {
            var items = new Dictionary<string, int[]>();
            foreach (var item in (JObject)fit["items"]) items[item.Key] = item.Value["culled_faces"].ToObject<int[]>();
            c.culled[(string)fit["body"] + "-" + (string)fit["stage"]] = items;
        }
        wardrobeNodes = new Dictionary<string, Transform>();
        foreach (var t in wardrobe.GetComponentsInChildren<Transform>(true)) wardrobeNodes[t.name] = t;
        return cloth = c;
    }

    static string[] Ids(JToken rows)
    {
        var ids = new List<string>();
        foreach (var row in rows)
        {
            if ((int)row["index"] != ids.Count) throw new InvalidOperationException("CLOTH catalog index order " + row["id"]);
            ids.Add((string)row["id"]);
        }
        return ids.ToArray();
    }

    readonly Dictionary<Texture2D, float> luminance = new Dictionary<Texture2D, float>();

    // Mean linear luminance (Rec. 709) of a readable sRGB albedo texture.
    float Luminance(Texture2D texture)
    {
        return Cached(luminance, texture, () =>
        {
            double sum = 0;
            var pixels = texture.GetPixels();
            foreach (var c in pixels) sum += 0.2126 * c.linear.r + 0.7152 * c.linear.g + 0.0722 * c.linear.b;
            return (float)(sum / pixels.Length);
        });
    }

    SkinnedMeshRenderer Dress(GameObject instance, BaseBody body, LifeStage stage, AppearanceRecord look, int clothing)
    {
        var c = Catalog();
        string cell = body.ToString().ToLowerInvariant() + "-" + stage;
        int top, bottom, footwear;
        ResidentFactory.UnpackClothing(clothing, out top, out bottom, out footwear);
        string accessory = c.accessory[look.Accessory], hair = c.hair[look.HairStyle];
        string[] worn = { c.top[top], c.bottom[bottom], c.footwear[footwear], accessory };
        SkinnedMeshRenderer skin = null, eyes = null;
        foreach (var r in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (skin == null || r.sharedMesh.vertexCount > skin.sharedMesh.vertexCount) skin = r;
            if (r.sharedMaterial != null && r.sharedMaterial.name.StartsWith("Eye", StringComparison.Ordinal)) eyes = r;
        }
        var source = skin.sharedMesh;
        var items = c.culled[cell];
        skin.sharedMesh = Cached(meshes, cell + "|" + string.Join("|", worn), () => Morphed(Culled(source, worn, items), cell));
        int b = (int)body;
        // glTFast imports the body and eye primitives as separate renderers.
        foreach (var r in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            var shared = r.sharedMaterials;
            for (int m = 0; m < shared.Length; m++)
            {
                var original = shared[m];
                if (original.name.StartsWith("Eye", StringComparison.Ordinal))
                    shared[m] = Cached(materials, "eye|" + original.GetInstanceID() + "|" + b + "|" + look.EyeColor, () =>
                        Textured(original, eyeColors[b * ResidentFactory.EyeColorCount + look.EyeColor]));
                else if (r == skin)
                    shared[m] = Cached(materials, "skin|" + original.GetInstanceID() + "|" + b + "|" + look.SkinTone + "|" + look.Face, () =>
                        Textured(original, Cached(textures, b + "|" + look.SkinTone + "|" + look.Face, () =>
                            Composite(skinTones[b * ResidentFactory.SkinToneCount + look.SkinTone],
                                faces[b * ResidentFactory.FaceCount + look.Face]))));
            }
            r.sharedMaterials = shared;
        }
        string headwear = c.headwear.Contains(accessory) ? accessory : null;
        Tilt tilt = null;
        if (headwear != null && eyes != null && wardrobeNodes.ContainsKey(headwear + "__" + cell))
            tilt = Cached(tilts, cell + "|" + headwear, () =>
                HatTilt(Parts(wardrobeNodes[headwear + "__" + cell]), eyes.sharedMesh, HeadCentre(source, skin.bones), cell + " " + headwear));
        Attach(skin, cell, hair, hairColors[b * ResidentFactory.HairColorCount + look.HairColor], headwear, tilt, source);
        foreach (string item in worn) Attach(skin, cell, item, null, null, item == headwear ? tilt : null, source);
        return skin;
    }

    static Mesh[] Parts(Transform node)
    {
        var parts = new List<Mesh>();
        foreach (var r in node.GetComponentsInChildren<SkinnedMeshRenderer>(true)) parts.Add(r.sharedMesh);
        return parts.ToArray();
    }

    // Headwear fit: rotation about the hat's rearmost vertex, raising its front.
    public sealed class Tilt { public Vector3 pivot, axis; public float degrees; }

    readonly Dictionary<string, Tilt> tilts = new Dictionary<string, Tilt>();

    // Smallest whole-degree back tilt after which no headwear triangle lies on a ray
    // from either iris top toward any camera within EyeClearElevation/EyeClearAzimuth.
    // All meshes share the STAGE-RIG bind space (as HairUnder already assumes).
    public static Tilt HatTilt(Mesh[] hat, Mesh eyes, Vector3 head, string label)
    {
        var eyeVertices = eyes.vertices;
        var centre = Vector3.zero;
        foreach (var v in eyeVertices) centre += v;
        centre /= eyeVertices.Length;
        var forward = Vector3.ProjectOnPlane(centre - head, Vector3.up).normalized;
        var side = Vector3.Cross(Vector3.up, forward);
        var irises = new List<Vector3>();
        foreach (float sign in new[] { -1f, 1f })
        {
            var half = new List<Vector3>();
            foreach (var v in eyeVertices) if (Vector3.Dot(v - head, side) * sign > 0f) half.Add(v);
            float front = float.MinValue, back = float.MaxValue;
            foreach (var v in half) { float f = Vector3.Dot(v, forward); front = Mathf.Max(front, f); back = Mathf.Min(back, f); }
            var cap = Vector3.zero; float top = float.MinValue, bottom = float.MaxValue; int n = 0;
            foreach (var v in half)
                if (Vector3.Dot(v, forward) >= front - 0.25f * (front - back)) { cap += v; n++; top = Mathf.Max(top, v.y); bottom = Mathf.Min(bottom, v.y); }
            // Upper middle of the visible eye cap: the iris top.
            irises.Add(cap / n + Vector3.up * 0.25f * (top - bottom));
        }
        var rays = new List<Vector3>();
        for (float el = 0f; el <= EyeClearElevation + 0.01f; el += 7f)
            for (float az = -EyeClearAzimuth; az <= EyeClearAzimuth + 0.01f; az += 15f)
                rays.Add(Quaternion.AngleAxis(az, Vector3.up) * Quaternion.AngleAxis(-el, side) * forward);
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        foreach (var mesh in hat)
        {
            int offset = vertices.Count;
            vertices.AddRange(mesh.vertices);
            for (int s = 0; s < mesh.subMeshCount; s++) foreach (int index in mesh.GetTriangles(s)) triangles.Add(offset + index);
        }
        var pivot = vertices[0];
        foreach (var v in vertices) if (Vector3.Dot(v, forward) < Vector3.Dot(pivot, forward)) pivot = v;
        var tilt = new Tilt { pivot = pivot, axis = Vector3.Cross(forward, Vector3.up) };
        var moved = new Vector3[vertices.Count];
        for (tilt.degrees = 0f; tilt.degrees <= MaxHatTilt; tilt.degrees += 1f)
        {
            var q = Quaternion.AngleAxis(tilt.degrees, tilt.axis);
            for (int i = 0; i < moved.Length; i++) moved[i] = pivot + q * (vertices[i] - pivot);
            if (!Blocks(moved, triangles, irises, rays)) break;
        }
        if (tilt.degrees > MaxHatTilt)
        {
            tilt.degrees = MaxHatTilt;
            Debug.LogWarning("CONTENT-IMPORT hat fit " + label + " still covers an iris at " + MaxHatTilt + " degrees");
        }
        Debug.Log("CONTENT-IMPORT hat fit " + label + " tilt=" + tilt.degrees);
        return tilt;
    }

    static bool Blocks(Vector3[] v, List<int> tris, List<Vector3> origins, List<Vector3> rays)
    {
        for (int t = 0; t < tris.Count; t += 3)
        {
            Vector3 a = v[tris[t]], e1 = v[tris[t + 1]] - a, e2 = v[tris[t + 2]] - a;
            foreach (var o in origins)
                foreach (var d in rays)
                {
                    // Moller-Trumbore ray/triangle test.
                    var p = Vector3.Cross(d, e2);
                    float det = Vector3.Dot(e1, p);
                    if (Mathf.Abs(det) < 1e-9f) continue;
                    float inv = 1f / det;
                    var s = o - a;
                    float u = Vector3.Dot(s, p) * inv;
                    if (u < 0f || u > 1f) continue;
                    var q = Vector3.Cross(s, e1);
                    float w = Vector3.Dot(d, q) * inv;
                    if (w < 0f || u + w > 1f) continue;
                    if (Vector3.Dot(e2, q) * inv > 1e-4f) return true;
                }
        }
        return false;
    }

    static Mesh Tilted(Mesh source, Tilt tilt)
    {
        var q = Quaternion.AngleAxis(tilt.degrees, tilt.axis);
        var mesh = Instantiate(source);
        mesh.name = source.name + "-tilt" + tilt.degrees;
        var vertices = mesh.vertices;
        for (int i = 0; i < vertices.Length; i++) vertices[i] = tilt.pivot + q * (vertices[i] - tilt.pivot);
        mesh.vertices = vertices;
        var normals = mesh.normals;
        for (int i = 0; i < normals.Length; i++) normals[i] = q * normals[i];
        mesh.normals = normals;
        var tangents = mesh.tangents;
        for (int i = 0; i < tangents.Length; i++)
        {
            var t = q * (Vector3)tangents[i];
            tangents[i] = new Vector4(t.x, t.y, t.z, tangents[i].w);
        }
        mesh.tangents = tangents;
        mesh.RecalculateBounds();
        return mesh;
    }

    static readonly int BaseColorTexture = Shader.PropertyToID("baseColorTexture");
    static readonly int BaseColorFactor = Shader.PropertyToID("baseColorFactor");

    static T Cached<K, T>(Dictionary<K, T> cache, K key, Func<T> make)
    {
        T value;
        if (!cache.TryGetValue(key, out value)) cache[key] = value = make();
        return value;
    }

    static Material Textured(Material source, Texture2D texture)
    {
        var material = new Material(source) { name = source.name + "-" + texture.name };
        material.SetTexture(BaseColorTexture, texture);
        material.SetVector(BaseColorFactor, Vector4.one);
        return material;
    }

    // Skin tile under the face overlay on the shared stage UV atlas (CLOTH make_body_material).
    static Texture2D Composite(Texture2D skin, Texture2D face)
    {
        int w = face.width, h = face.height;
        var over = face.GetPixels32();
        var result = new Color32[over.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                result[i] = Color32.Lerp(skin.GetPixelBilinear((x + 0.5f) / w, (y + 0.5f) / h), over[i], over[i].a / 255f);
                result[i].a = 255;
            }
        var texture = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = skin.name + "+" + face.name, wrapMode = TextureWrapMode.Clamp };
        texture.SetPixels32(result);
        texture.Apply(true, true);
        return texture;
    }

    // Body faces under every worn garment are removed; the catalog indexes the
    // proxy body triangles, which the STAGE variants share one for one.
    static Mesh Culled(Mesh source, string[] worn, Dictionary<string, int[]> items)
    {
        var tris = source.GetTriangles(0);
        var drop = new bool[tris.Length / 3];
        foreach (string item in worn)
            foreach (int face in items[item])
            {
                if (face >= drop.Length) throw new InvalidOperationException("CLOTH culled face " + face + " outside " + source.name);
                drop[face] = true;
            }
        var kept = new List<int>(tris.Length);
        for (int t = 0; t < drop.Length; t++)
            if (!drop[t]) { kept.Add(tris[3 * t]); kept.Add(tris[3 * t + 1]); kept.Add(tris[3 * t + 2]); }
        var mesh = Instantiate(source);
        mesh.name = source.name + "-dressed";
        mesh.SetTriangles(kept, 0);
        return mesh;
    }

    // One wardrobe item of this body/stage cell, skinned to the resident's own bones
    // (the wardrobe cell copies the proxy skin joints in order, as do the STAGE variants).
    void Attach(SkinnedMeshRenderer body, string cell, string item, Color? tint, string headwear, Tilt tilt, Mesh bodyMesh)
    {
        Transform node;
        if (!wardrobeNodes.TryGetValue(item + "__" + cell, out node))
        {
            Debug.LogError("CONTENT-IMPORT missing wardrobe item " + item + "__" + cell);
            return;
        }
        foreach (var source in node.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (source.bones.Length != body.bones.Length)
            {
                Debug.LogError("CONTENT-IMPORT " + item + "__" + cell + " joint count differs from the stage body");
                continue;
            }
            var part = new GameObject(item);
            part.transform.SetParent(body.transform.parent, false);
            var renderer = part.AddComponent<SkinnedMeshRenderer>();
            var mesh = source.sharedMesh;
            if (headwear != null)
            {
                Transform hat;
                if (wardrobeNodes.TryGetValue(headwear + "__" + cell, out hat))
                {
                    // Hair is masked inside the headwear as worn (after its fit tilt).
                    var hatMesh = hat.GetComponentInChildren<SkinnedMeshRenderer>(true).sharedMesh;
                    if (tilt != null) hatMesh = Cached(meshes, "tilt|" + hatMesh.GetInstanceID() + "|" + tilt.degrees, () => Tilted(hatMesh, tilt));
                    mesh = Cached(meshes, cell + "|" + item + "|" + headwear + "|" + mesh.GetInstanceID() + "|" + hatMesh.GetInstanceID(),
                        () => HairUnder(source.sharedMesh, hatMesh, HeadCentre(bodyMesh, body.bones)));
                }
            }
            else if (tilt != null)
                mesh = Cached(meshes, "tilt|" + mesh.GetInstanceID() + "|" + tilt.degrees, () => Tilted(source.sharedMesh, tilt));
            renderer.sharedMesh = mesh;
            var shared = source.sharedMaterials;
            if (tint.HasValue && shared.Length > 0)
            {
                // CLOTH tints the hair item's first material; trims keep their colour.
                var original = shared[0];
                shared[0] = Cached(materials, "hair|" + original.GetInstanceID() + "|" + tint.Value, () =>
                {
                    var tinted = new Material(original) { name = original.name + "-tint" };
                    tinted.SetVector(BaseColorFactor, (Vector4)tint.Value);
                    return tinted;
                });
            }
            renderer.sharedMaterials = shared;
            renderer.bones = body.bones;
            renderer.rootBone = body.rootBone;
            renderer.updateWhenOffscreen = true;
        }
    }

    // Bounds centre of the body vertices the head joint dominates (CLOTH head_c).
    static Vector3 HeadCentre(Mesh body, Transform[] bones)
    {
        var weights = body.boneWeights;
        var vertices = body.vertices;
        var bounds = new Bounds();
        bool any = false;
        foreach (int i in DominantIs(weights, bones, "head"))
        {
            if (!any) { bounds = new Bounds(vertices[i], Vector3.zero); any = true; }
            else bounds.Encapsulate(vertices[i]);
        }
        return bounds.center;
    }

    static IEnumerable<int> DominantIs(BoneWeight[] weights, Transform[] bones, string name)
    {
        for (int i = 0; i < weights.Length; i++)
        {
            var w = weights[i];
            int best = w.boneIndex0; float top = w.weight0;
            if (w.weight1 > top) { best = w.boneIndex1; top = w.weight1; }
            if (w.weight2 > top) { best = w.boneIndex2; top = w.weight2; }
            if (w.weight3 > top) best = w.boneIndex3;
            if (bones[best].name == name) yield return i;
        }
    }

    // CLOTH headwear rule (render_captures.headwear_mask), rim bins widened by one:
    // hair inside the headwear rim is cut with a 3 degree overlap, detached hair pieces reaching the rim are
    // removed whole, and so are islands the cut leaves under 25% of the main shell.
    static Mesh HairUnder(Mesh hair, Mesh hat, Vector3 centre)
    {
        const int Bins = 36;
        var rim = new float[Bins];
        foreach (var p in hat.vertices)
        {
            float theta; int bin;
            Angles(p - centre, Bins, out theta, out bin);
            rim[bin] = Mathf.Max(rim[bin], theta);
        }
        // Headwear rings are coarser than 36 bins, so some bins hold no headwear vertex and
        // CLOTH's per-bin rim would leave hair poking through there; widen each bin by one.
        var raw = (float[])rim.Clone();
        for (int k = 0; k < Bins; k++)
            rim[k] = Mathf.Max(raw[k], Mathf.Max(raw[(k + 1) % Bins], raw[(k + Bins - 1) % Bins]));
        var vertices = hair.vertices;
        var near = new bool[vertices.Length];
        var inside = new bool[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            float theta; int bin;
            Angles(vertices[i] - centre, Bins, out theta, out bin);
            near[i] = theta < rim[bin] + 3f;
            inside[i] = theta < rim[bin] - 3f;
        }
        var tris = new List<int>();
        var submesh = new List<int>();
        for (int s = 0; s < hair.subMeshCount; s++)
            foreach (int index in hair.GetTriangles(s)) { tris.Add(index); if (tris.Count % 3 == 0) submesh.Add(s); }
        int count = submesh.Count;
        var drop = new bool[count];
        for (int t = 0; t < count; t++)
            drop[t] = inside[tris[3 * t]] || inside[tris[3 * t + 1]] || inside[tris[3 * t + 2]];
        var all = new List<int>();
        for (int t = 0; t < count; t++) all.Add(t);
        var comp = Components(vertices, tris, all);
        int main = Largest(comp);
        var reaches = new HashSet<int>();
        for (int t = 0; t < count; t++)
            if (comp[t] != main && (near[tris[3 * t]] || near[tris[3 * t + 1]] || near[tris[3 * t + 2]])) reaches.Add(comp[t]);
        for (int t = 0; t < count; t++) if (reaches.Contains(comp[t])) drop[t] = true;
        var kept = new List<int>();
        for (int t = 0; t < count; t++) if (comp[t] == main && !drop[t]) kept.Add(t);
        if (kept.Count > 0)
        {
            var island = Components(vertices, tris, kept);
            var size = new Dictionary<int, int>();
            foreach (int id in island) size[id] = (size.ContainsKey(id) ? size[id] : 0) + 1;
            int biggest = 0;
            foreach (int n in size.Values) biggest = Math.Max(biggest, n);
            for (int k = 0; k < kept.Count; k++) if (size[island[k]] < 0.25f * biggest) drop[kept[k]] = true;
        }
        var mesh = Instantiate(hair);
        mesh.name = hair.name + "-under-" + hat.name;
        var per = new List<int>[hair.subMeshCount];
        for (int s = 0; s < per.Length; s++) per[s] = new List<int>();
        for (int t = 0; t < count; t++)
            if (!drop[t]) { per[submesh[t]].Add(tris[3 * t]); per[submesh[t]].Add(tris[3 * t + 1]); per[submesh[t]].Add(tris[3 * t + 2]); }
        for (int s = 0; s < per.Length; s++) mesh.SetTriangles(per[s], s);
        return mesh;
    }

    static void Angles(Vector3 d, int bins, out float theta, out int bin)
    {
        d.Normalize();
        theta = Mathf.Acos(Mathf.Clamp(d.y, -1f, 1f)) * Mathf.Rad2Deg;
        float phi = Mathf.Atan2(d.x, d.z);
        bin = ((int)Mathf.Floor((phi + Mathf.PI) / (2f * Mathf.PI) * bins) % bins + bins) % bins;
    }

    // Connected component per listed triangle over vertices welded at 1e-6 m.
    static int[] Components(Vector3[] vertices, List<int> tris, List<int> which)
    {
        var weld = new Dictionary<Vector3Int, int>();
        var key = new int[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            var k = Vector3Int.RoundToInt(vertices[i] * 1e6f);
            int id;
            if (!weld.TryGetValue(k, out id)) weld[k] = id = weld.Count;
            key[i] = id;
        }
        var parent = new int[weld.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        Func<int, int> find = null;
        find = x => { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; };
        foreach (int t in which)
        {
            int a = key[tris[3 * t]], b = key[tris[3 * t + 1]], c = key[tris[3 * t + 2]];
            parent[find(a)] = find(b);
            parent[find(b)] = find(c);
        }
        var result = new int[which.Count];
        for (int k = 0; k < which.Count; k++) result[k] = find(key[tris[3 * which[k]]]);
        return result;
    }

    static int Largest(int[] comp)
    {
        var size = new Dictionary<int, int>();
        int best = -1, top = 0;
        foreach (int id in comp)
        {
            int n = size[id] = (size.ContainsKey(id) ? size[id] : 0) + 1;
            if (n > top) { top = n; best = id; }
        }
        return best;
    }

    public void Clear()
    {
        foreach (var body in dressed.Values) Discard(body);
        dressed.Clear();
        dressedPath.Clear();
        cycle.Clear();
        carrierOf.Clear();
        Cues.Clear();
        if (residentRoot != null)
            for (int i = residentRoot.childCount - 1; i >= 0; i--) Discard(residentRoot.GetChild(i).gameObject);
        world = null;
        looks = null;
    }

    static void Discard(GameObject target)
    {
        if (Application.isPlaying) Destroy(target);
        else DestroyImmediate(target);
    }
}
