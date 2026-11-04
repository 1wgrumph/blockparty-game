// CONTENT-IMPORT@1: one glTF per base body and life stage (BP-PRES-03).
// Paths are the STAGE-<stage> producers' declared write paths in plan.json;
// the variant is a pure function of the stored body and stage, so it is
// stable across save/load and changes only at an AGING boundary.
using Blockparty.Simulation;

namespace Blockparty.Content
{
    public static class StageVariants
    {
        public static string Resolve(BaseBody body, LifeStage stage)
        {
            return "Assets/Blockparty/Art/Characters/" + stage + "/" + body.ToString().ToLowerInvariant() + ".gltf";
        }
    }
}
