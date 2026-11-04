using Blockparty.Contracts;
using UnityEngine;

// PROOF-SCENE shared layout: sim poses and their scene-graph twins.
// Route (-3,-3) -> (3.6,-0.4) -> (0,2.6) clears the 2x2 obstacle at the
// origin by 0.39 m / 0.59 m on the two legs (segment-AABB check). The
// approach sits over the seat front; the resident turns its back to the
// backrest at align, like walking up to a real chair.
public static class ProofSceneLayout
{
    public static readonly ApproachPose Start = new ApproachPose(-3f, 0f, -3f);
    public static readonly ApproachPose Waypoint = new ApproachPose(3.6f, 0f, -0.4f);
    public static readonly ApproachPose Approach = new ApproachPose(0f, 0f, 2.6f);

    public static readonly Vector3 ObstacleCenter = new Vector3(0f, 1f, 0f);
    public static readonly Vector3 ObstacleSize = new Vector3(2f, 2f, 2f);

    // Seat top at 0.45 m; the Sitting clips rest the pelvis at 0.54 m.
    public static readonly Vector3 ChairSeatCenter = new Vector3(0f, 0.415f, 2.75f);
    public static readonly Vector3 ChairSeatSize = new Vector3(0.55f, 0.07f, 0.55f);
    public static readonly Vector3 ChairBackCenter = new Vector3(0f, 0.75f, 3.0f);
    public static readonly Vector3 ChairBackSize = new Vector3(0.55f, 0.6f, 0.06f);

    public static readonly Vector3 CameraPosition = new Vector3(7.5f, 4.5f, -0.5f);
    public static readonly Vector3 CameraLookAt = new Vector3(-0.3f, 0.6f, 0f);

    public const float MinLegClearanceM = 0.35f;
}
