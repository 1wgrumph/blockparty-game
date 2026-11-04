// SAM-LOOK (owner, 2026-11-04): the engine side of the Look tool in live play.
// G or the HUD "Look" button opens a typed box; Enter (or SamLook.Find from
// voice) captures the current view, sends it through CaptureSearch (SAM@1) and
// maps the largest outline's centre to the world: a Physics raycast against
// the resident colliders, else the ground-plane point matched to the nearest
// block-site slot, site or activity stage (owner amendment 2026-11-04: SAM
// returns outlines only; the engine decides what that point is). A resident is
// selected, then followed by the existing engine camera follow; a place is
// named with what can be done there. One SAM call per look, none per frame.
// Opt-in evidence (never a network call):
//   --sam-look-fake=<dir>   recorded CONTRACT-BP-07 frames: <phrase-slug>.json
//                           holds {"outlines":[...]} in capture pixels; a phrase
//                           without a file returns no outlines (a miss)
//   --sam-look-fake=nokey   the transport reports no key (local fallback)
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Blockparty.AI;
using Blockparty.Contracts;
using Blockparty.Presentation;
using Blockparty.Simulation;
using UnityEngine;

public sealed partial class AlphaDirector
{
    const KeyCode LookKey = KeyCode.G;
    const int LookMaxSide = 1024;         // live SAM capture long side, pixels
    const float LookFollowDelay = 1.2f;   // real seconds the outline shows before the follow camera moves
    static readonly ServiceEndpoint LookEndpoint = new ServiceEndpoint("SAM 3.1",
        "https://api.meta.ai/v1/responses", "sam-3.1", true);

    SamLook samLook;
    ProposalBoundary lookBoundary;
    LiveTransport lookLive;
    string lookFake;
    Vector3 lookCameraPos;
    Quaternion lookCameraRot;
    float lookShownAt;

    // StartLive / ReloadLive: a fresh boundary on the current world.
    void SamLookBind()
    {
        // Its own never-paused Clock: the boundary holds service results while its clock is paused,
        // and CaptureSearch only applies a result on Complete, so a paused game would lose every look.
        // Outlines are presentation-only; nothing here waits on the simulation.
        lookBoundary = new ProposalBoundary(sim.World, new Clock(sim.World), sim.Commands, new Autonomy(sim.World, sim.Commands));
        samLook = new SamLook(lookBoundary);
        SamLook.Active = samLook;
        if (lookFake == null) lookFake = Arg(Environment.GetCommandLineArgs(), "--sam-look-fake");
        if (lookFake.Length == 0 && lookLive == null) lookLive = new LiveTransport(this);
    }

    bool LookOpen { get { return samLook != null && samLook.Open; } }

    void OpenLook()
    {
        if (samLook == null || samLook.Busy) return;
        samLook.Open = true;
        samLook.Draft = string.Empty;
        Log("look open");
    }

    // Update, before every other live key: true while the Look box owns the keyboard.
    bool SamLookKeys()
    {
        if (samLook == null) return false;
        samLook.Tick(Time.unscaledDeltaTime);
        string phrase;
        if (samLook.TakePending(out phrase)) StartCoroutine(SamLookRun(phrase));
        if (samLook.Open)
        {
            if (Input.GetKeyDown(KeyCode.Escape)) { samLook.Open = false; Log("look closed"); }
            return true;
        }
        return false;
    }

    // HandleInput: G opens the box (checked after the remappable actions).
    void SamLookHotkey()
    {
        if (Input.GetKeyDown(LookKey)) OpenLook();
    }

    IEnumerator SamLookRun(string phrase)
    {
        yield return null;                       // one frame for the box and old outlines to leave the screen
        yield return new WaitForEndOfFrame();
        int w, h;
        byte[] rgba = CaptureRgba(out w, out h);
        lookCameraPos = alphaCamera.transform.position;
        lookCameraRot = alphaCamera.transform.rotation;
        GatewayRequest request = rgba == null ? null
            : samLook.Begin(phrase, rgba, w, h, LookEndpoint, true);   // key resolved inside the send, like crafts
        if (request != null)
        {
            if (lookFake.Length > 0) LookFakeSettle(request, phrase);
            else yield return lookLive.SearchSam(samLook.Search, lookBoundary, request, samLook.Capture, phrase, rgba, w, h);
        }
        LookKind kind = rgba == null ? LookKind.Unavailable
            : request == null ? samLook.Kind : samLook.Settle(LookResolve, LookPlaces());
        lookShownAt = Time.unscaledTime;
        notice = samLook.Reply;
        Log("look phrase=" + Q(phrase) + " kind=" + kind + " outlines=" + samLook.Outlines.Count
            + " best=" + samLook.Best + " target=" + Q(samLook.TargetName) + " reply=" + Q(samLook.Reply)
            + " transport=" + (lookFake.Length > 0 ? "fake" : "live"));
        if (kind == LookKind.Resident && samLook.Resident.HasValue)
        {
            ResidentId found = samLook.Resident.Value;
            Select(found);
            notice = samLook.Reply;   // Select's own "Selected ..." notice would replace the reply
            yield return new WaitForSecondsRealtime(LookFollowDelay);
            if (navigation.SelectedResident.HasValue && navigation.SelectedResident.Value == found)
            {
                CommandResult follow = navigation.FollowResident(found);
                Log("look follow resident=" + (long)found + " accepted=" + B(follow.Accepted));
            }
        }
    }

    byte[] CaptureRgba(out int w, out int h)
    {
        Texture2D tex = ScreenCapture.CaptureScreenshotAsTexture();
        w = h = 0;
        if (tex == null) return null;
        try
        {
            int sw = tex.width, sh = tex.height;
            // Live SAM: at most LookMaxSide on the long side keeps the PNG small; outlines come back in
            // these capture pixels and LookResolve / the HUD scale them to the screen.
            float scale = Mathf.Min(1f, LookMaxSide / (float)Mathf.Max(sw, sh));
            w = Mathf.Max(1, Mathf.RoundToInt(sw * scale));
            h = Mathf.Max(1, Mathf.RoundToInt(sh * scale));
            Color32[] px = tex.GetPixels32();
            byte[] rgba = new byte[w * h * 4];
            // Unity rows run bottom-up; SAM image rows run top-down.
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    Color32 c = px[(sh - 1 - y * sh / h) * sw + x * sw / w];
                    int o = (y * w + x) * 4;
                    rgba[o] = c.r; rgba[o + 1] = c.g; rgba[o + 2] = c.b; rgba[o + 3] = 255;
                }
            return rgba;
        }
        finally { UnityEngine.Object.Destroy(tex); }
    }

    // Recorded frames only; the same CaptureSearch.Complete / Fail path the live transport takes.
    void LookFakeSettle(GatewayRequest request, string phrase)
    {
        if (lookFake == "nokey") { samLook.Search.Fail(request.Id, GatewayOutcome.Unprovisioned); return; }
        string file = Path.Combine(lookFake, Slug(phrase) + ".json");
        string body = File.Exists(file) ? File.ReadAllText(file).Trim() : "{\"outlines\":[]}";
        samLook.Search.Complete(request.Id, "{\"capture\":" + (long)samLook.Capture + "," + body.Substring(1));
    }

    static string Slug(string phrase)
    {
        StringBuilder s = new StringBuilder();
        foreach (char c in phrase.ToLowerInvariant()) s.Append(char.IsLetterOrDigit(c) ? c : '-');
        return s.ToString().Trim('-');
    }

    // Image pixel (y down) -> what the engine sees there, through the camera pose of the capture.
    LookHit LookResolve(float x, float y)
    {
        LookHit hit = new LookHit();
        Vector3 screen = new Vector3(x * Screen.width / samLook.ImageWidth,
            Screen.height - y * Screen.height / samLook.ImageHeight, 0f);
        if (alphaHud.Blocks(screen)) return hit;   // live SAM also outlines HUD art (portraits, tray icons): not the world
        ResidentId? resident = ResidentAt(screen);   // the click picker: collider raycast, then nearest body on screen
        if (resident.HasValue)
        {
            hit.Resident = resident;
            hit.ResidentName = ResidentOf(sim, resident.Value).DisplayName;
            return hit;
        }
        Vector3 ground;
        if (GroundPoint(screen, out ground)) { hit.HasGround = true; hit.X = ground.x; hit.Z = ground.z; }
        return hit;
    }

    List<LookPlace> LookPlaces()
    {
        var places = new List<LookPlace>();
        foreach (BlockSite site in sim.Sites.Sites)
        {
            var acts = new List<string>();
            foreach (BlockSiteSlot slot in site.Slots)
            {
                string act = SamLook.ActivityName(slot.ActivityCode);
                if (!acts.Contains(act)) acts.Add(act);
                places.Add(new LookPlace(slot.ObjectKind + " at the " + SamLook.Pretty(site.Id).ToLowerInvariant(),
                    slot.Pose.X, slot.Pose.Z, 2.5f, new[] { act }));
            }
            places.Add(new LookPlace(SamLook.Pretty(site.Id), site.Origin.X, site.Origin.Z, 9f, acts.ToArray()));
        }
        for (int i = 0; i < ClassNames.Length; i++)
        {
            ClassStage stage = StageOf(ClassNames[i]);
            if (stage == null || stage.actor == null) continue;
            Vector3 p = stage.actor.position;
            places.Add(new LookPlace(SamLook.Pretty(ClassNames[i]) + " spot", p.x, p.z, 4f,
                new[] { SamLook.ActivityName(ActivityCodes[i]) }));
        }
        return places;
    }

    // HUD: outlines stay while the camera holds the capture pose, up to 15 s.
    bool LookOutlinesFresh()
    {
        return samLook != null && samLook.Outlines.Count > 0 && Time.unscaledTime - lookShownAt < 15f
            && (alphaCamera.transform.position - lookCameraPos).sqrMagnitude < 1e-6f
            && Quaternion.Angle(alphaCamera.transform.rotation, lookCameraRot) < 0.01f;
    }
}
