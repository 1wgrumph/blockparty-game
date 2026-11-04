using NUnit.Framework;
using UnityEngine;

namespace Blockparty.ToolchainTests
{
    // Staged into the project by tools/verify.py for the licensed `-runTests` step;
    // canonical source lives here so every package keeps one tests/production file.
    public class ToolchainEvidenceTests
    {
        [Test]
        public void EditorVersionIsPinned()
        {
            Assert.AreEqual("6000.3.10f1", Application.unityVersion, "TOOLCHAIN editor pin");
        }

        [Test]
        public void UrpRuntimePresent()
        {
            Assert.IsNotNull(
                System.Type.GetType("UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset, Unity.RenderPipelines.Universal.Runtime"),
                "TOOLCHAIN URP 17.3.0 runtime");
        }

        [Test]
        public void McpBridgeAssemblyPresent()
        {
            Assert.IsNotNull(
                System.Type.GetType("MCPForUnity.Editor.Services.BridgeControlService, MCPForUnity.Editor"),
                "TOOLCHAIN MCP bridge");
        }
    }
}
