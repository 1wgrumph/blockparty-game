using System;
using UnityEngine;

// WORLD-FIXES (#216): life-sim roof cutaway for the Alpha block. Every building
// keeps its roof; a building's roof renderers hide while the camera looks down
// (at least minPitch) and is zoomed in close over that building (the ground
// under the view centre lies inside it and the view is narrower than
// closeExtent), or while the selected resident stands inside it, and show
// again otherwise. A camera inside a building below its eaves (the class-stage
// cameras) keeps that ceiling. Presentation only: no collider, simulation or
// input is touched. AlphaRoofSceneBuild fills the building list at scene build.
public sealed class RoofReveal : MonoBehaviour
{
    [Serializable]
    public sealed class Building
    {
        public string id;
        public Rect area;          // footprint on the ground plane (x, z)
        public float top;          // eave height; a camera below it inside the area is indoors
        public Renderer[] roof;    // roof, ceiling and rooftop dressing
    }

    public Building[] buildings = new Building[0];
    public Camera viewCamera;
    // AlphaResidentLooks' transform: its "SelectionRing" child marks the selected resident.
    public Transform selection;
    // Half-height (m) of the view at the ground under the view centre at which roofs hide.
    public float closeExtent = 15f;
    public float margin = 1.0f;
    // Below this downward pitch (deg) the view is along the street, not over a roof;
    // the default Alpha camera looks down 22 deg, the resident follow camera 26 deg.
    public float minPitch = 21f;

    bool[] hidden = new bool[0];
    Transform ring;

    void LateUpdate() { Evaluate(viewCamera != null ? viewCamera : Camera.main); }

    public bool IsHidden(string id)
    {
        for (int i = 0; i < buildings.Length; i++)
            if (buildings[i].id == id) return i < hidden.Length && hidden[i];
        return false;
    }

    public void Evaluate(Camera cam)
    {
        if (cam == null) return;
        if (hidden.Length != buildings.Length) hidden = new bool[buildings.Length];
        if (ring == null && selection != null) ring = selection.Find("SelectionRing");
        bool selected = ring != null && ring.gameObject.activeInHierarchy;
        Vector3 c = cam.transform.position, f = cam.transform.forward;
        bool aimed = f.y < -Mathf.Sin(minPitch * Mathf.Deg2Rad);
        Vector3 ground = aimed ? c + f * (-c.y / f.y) : c;
        float extent = aimed ? Vector3.Distance(c, ground) * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) : float.MaxValue;
        for (int i = 0; i < buildings.Length; i++)
        {
            var b = buildings[i];
            // Hysteresis: a hidden roof needs a 10 % wider view or 0.5 m more distance to return.
            float slack = hidden[i] ? 0.5f : 0f;
            Rect near = Grow(b.area, margin + slack);
            bool close = extent <= closeExtent * (hidden[i] ? 1.1f : 1f) && near.Contains(new Vector2(ground.x, ground.z));
            bool indoors = c.y < b.top + 0.5f && b.area.Contains(new Vector2(c.x, c.z));
            bool occupied = selected && Grow(b.area, slack).Contains(new Vector2(ring.position.x, ring.position.z));
            bool hide = !indoors && (close || occupied);
            if (hide == hidden[i]) continue;
            hidden[i] = hide;
            foreach (var r in b.roof) if (r != null) r.enabled = !hide;
        }
    }

    static Rect Grow(Rect r, float d) { return new Rect(r.xMin - d, r.yMin - d, r.width + 2f * d, r.height + 2f * d); }
}
