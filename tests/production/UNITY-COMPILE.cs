// UNITY-COMPILE staging contract (scratch project only; never commit Assets copies):
// Copy BoundaryTypes.cs, WorldState.cs and Clock.cs unchanged from their canonical
// Assets/Blockparty/{Contracts,Simulation} paths into Assets/Tests/UnityCompile/Foundation/.
// Foundation/Blockparty.UnityCompile.Foundation.asmdef:
// {"name":"Blockparty.UnityCompile.Foundation","autoReferenced":false}
// This isolates the foundation's existing assembly-wide static-state checks from
// editor/test statics and leaves the project's Assembly-CSharp compilation intact.
// Copy tests/production/{UNITY-COMPILE,IFC-LOCK,STATE,CLOCK}.cs unchanged into
// Assets/Tests/UnityCompile/Editor/ and fixtures/UNITY-COMPILE/NonCompiling.cs.txt
// into Assets/Tests/UnityCompile/Editor/Fixtures/ (keep the .txt extension).
// Editor/Blockparty.UnityCompile.Tests.asmdef:
// {"name":"Blockparty.UnityCompile.Tests","references":["Blockparty.UnityCompile.Foundation"],
//  "includePlatforms":["Editor"],"optionalUnityReferences":["TestAssemblies"],"autoReferenced":false}
// Run -runTests -testPlatform EditMode -testFilter Blockparty.UnityCompileTests
// with Unity 6000.3.10f1; project settings must already disable domain reload.
// Fullnames (prefix Blockparty.UnityCompileTests.UnityCompileEvidenceTests.):
// FoundationChecksPassAcrossTwoPlayEntries -> task SEIT-VER-01-P1
// NonCompilingFixtureIsRejected -> task SEIT-VER-01-N1 (expected PASS: rejected)
// StaticLeakFailsOnSecondPlayEntry -> task SEIT-VER-01-N1 (deliberate RED)
// These task mappings supplement, not replace, the SEIT rows' spec inspections.
// Resource group: editor; one job per project, externally locked by the gate.
// A cold content import can exceed 30 s; reuse this warm editor for all cases.
#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blockparty.UnityCompileTests
{
    public class UnityCompileEvidenceTests
    {
        private static void RequirePinnedEditorAndNoDomainReload()
        {
            Assert.AreEqual("6000.3.10f1", Application.unityVersion);
            Assert.IsTrue(EditorSettings.enterPlayModeOptionsEnabled, "Fast Enter Play Mode enabled");
            Assert.IsTrue((EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) != 0,
                "Domain reload must be disabled in the supplied project settings");
        }

        [UnityTest]
        public IEnumerator FoundationChecksPassAcrossTwoPlayEntries()
        {
            RequirePinnedEditorAndNoDomainReload();
            for (int entry = 1; entry <= 2; entry++)
            {
                yield return new EnterPlayMode(false);
                Assert.IsTrue(Application.isPlaying, "Foundation checks must execute in play mode");
                RequirePinnedEditorAndNoDomainReload();
                RunChecks("IFC-LOCK", IfcLockChecks.Main, entry);
                RunChecks("STATE", StateChecks.Main, entry);
                RunChecks("CLOCK", ClockChecks.Main, entry);
                yield return new ExitPlayMode();
                Assert.IsFalse(Application.isPlaying);
            }
        }

        private static void RunChecks(string id, Func<int> checks, int entry)
        {
            TextWriter previous = Console.Out;
            using (var output = new StringWriter())
            {
                int exit;
                try
                {
                    Console.SetOut(output);
                    exit = checks();
                }
                finally
                {
                    Console.SetOut(previous);
                }
                string transcript = output.ToString();
                TestContext.WriteLine("UNITY-COMPILE entry=" + entry + " " + id + " exit=" + exit);
                TestContext.WriteLine(transcript);
                Assert.AreEqual(0, exit, id + " entry=" + entry + "\n" + transcript);
                StringAssert.Contains(id + " ALL PASS", transcript);
                Assert.IsFalse(transcript.Split('\n').Any(line => line.StartsWith("FAIL ", StringComparison.Ordinal)),
                    id + " emitted a failed check\n" + transcript);
            }
        }

        [UnityTest]
        public IEnumerator StaticLeakFailsOnSecondPlayEntry()
        {
            RequirePinnedEditorAndNoDomainReload();
            LeakingFixture.Entries = 0; // Reset once, deliberately omit the play-entry reset.
            yield return new EnterPlayMode(false);
            Assert.IsTrue(Application.isPlaying);
            int first = ++LeakingFixture.Entries;
            yield return new ExitPlayMode();
            yield return new EnterPlayMode(false);
            Assert.IsTrue(Application.isPlaying);
            int second = ++LeakingFixture.Entries;
            yield return new ExitPlayMode();
            TestContext.WriteLine("UNITY-COMPILE deliberate leak first=" + first + " second=" + second);
            Assert.AreEqual(1, first, "Leak fixture must start fresh");
            Assert.AreEqual(1, second, "UNITY-COMPILE deliberate static leak survived the second play entry");
        }

        [UnityTest]
        public IEnumerator NonCompilingFixtureIsRejected()
        {
            RequirePinnedEditorAndNoDomainReload();
            string fixture = Path.Combine(Application.dataPath,
                "Tests/UnityCompile/Editor/Fixtures/NonCompiling.cs.txt");
            Assert.IsTrue(File.Exists(fixture), "Gate must stage the canonical non-compiling fixture");
            string temp = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Temp",
                "UnityCompile-" + System.Diagnostics.Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                string source = Path.Combine(temp, "NonCompiling.cs");
                string assembly = Path.Combine(temp, "NonCompiling.dll");
                File.Copy(fixture, source);
                var builder = new AssemblyBuilder(assembly, new[] { source });
                builder.flags = AssemblyBuilderFlags.EditorAssembly;
                CompilerMessage[] messages = null;
                builder.buildFinished += (path, result) => messages = result;
                Assert.IsTrue(builder.Build(), "Unity must accept the compiler job before testing rejection");
                // Observe completion on this thread: the status getter raises buildFinished, and a
                // re-entrant editor compile poll during that callback removes the builder while an
                // outer poll enumerates it. Yielding would let that outer poll observe it first.
                var timer = System.Diagnostics.Stopwatch.StartNew();
                while (builder.status != AssemblyBuilderStatus.Finished)
                {
                    Assert.Less(timer.Elapsed.TotalSeconds, 180, "Unity fixture compilation deadline");
                    System.Threading.Thread.Sleep(10);
                }
                while (EditorApplication.isCompiling)
                {
                    Assert.Less(timer.Elapsed.TotalSeconds, 180, "Unity fixture compilation deadline");
                    System.Threading.Thread.Sleep(10);
                }
                Assert.IsNotNull(messages, "Unity compiler must report a result");
                string diagnostics = string.Join("\n", messages.Select(message => message.message));
                TestContext.WriteLine("UNITY-COMPILE fixture compiler diagnostics:\n" + diagnostics);
                Assert.IsTrue(messages.Any(message => message.type == CompilerMessageType.Error
                    && message.message.Contains("CS0246")
                    && message.message.Contains("UNITY_COMPILE_EXPECTED_MISSING_TYPE")), diagnostics);
                Assert.IsFalse(File.Exists(assembly), "Invalid fixture must not produce an assembly");
            }
            finally
            {
                Directory.Delete(temp, true);
            }
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator ExitOwnedPlayMode()
        {
            if (Application.isPlaying)
                yield return new ExitPlayMode();
        }
    }

    internal static class LeakingFixture
    {
        internal static int Entries;
    }
}
#else
public static class UnityCompileStandalone
{
    public static int Main()
    {
        System.Console.WriteLine("UNITY-COMPILE standalone: Unity-only EditMode suite; run via tools/verify.py");
        return 0;
    }
}
#endif
