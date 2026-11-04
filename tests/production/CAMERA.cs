/*
CAMERA@1 test-first contract: plan.json CAMERA; m3/test-engineer.json A4(camera)
and issue_acceptance_157 camera. Separate suite required by the task; existing
UI-NAV and INPUT-ACCESS cover navigation and setting validation/persistence.
Resource group: isolated-csharp. No editor, display, server, files or test doubles.

Planned public plain-C# API (reflection follows INPUT-ACCESS.cs so missing
capabilities produce named FAILs while the suite itself still compiles):
 CameraControl(Accessibility settings, InputMap input, WorldNavigation navigation,
               ActivityExecutor executor)
 bool TryActivate(string binding, double inputSeconds)
 void PanAtEdge(float normalizedX, float normalizedY, double inputSeconds)
 void AdvanceRealSeconds(double seconds); void RequestShake(float amplitude)
 numeric properties PanX, PanZ, Zoom, Rotation, ShakeAmplitude
 ResidentId? FollowedResident; ApproachPose FollowPosition
PanX/PanZ are horizontal view offsets, Zoom is viewing distance/scale, Rotation
is azimuth. TryActivate routes the current binding through the supplied input
map. PanAtEdge accepts viewport coordinates 0..1; center is not an edge.
AdvanceRealSeconds advances presentation only; follow reads the live NAV pose.
Numeric units/base rates are unspecified: tests assert displacement ratios,
not invented speeds. InvertX reverses horizontal pan, InvertY vertical pan.
Reduced motion applies essential movement directly with no residual easing.

Local traceability (does NOT complete Unity/build SEIT cases):
 A4-speed-*, A4-duration-*, A4-invert-*, A4-edge-*, A4-reduced-*,
 input-remap-*, camera-presentation-only -> SEIT-PRES-05-N1 (camera projection).
 follow-identity-crossings -> SEIT-UI-05-P1/N1 (real NAV poses, five game minutes).
 repeated-play-entry -> same local cases, deterministic instance-state check.
Rendered CameraBridge, screenshots, actual devices and build flows remain
CONTROLS-PLAY obligations. No new durable camera state is required here.

negative-fixture changes the requested 2x speed back to 1x before input,
simulating an ignored setting with the REAL Accessibility/CameraControl path.
It uses the SAME displacement-ratio oracle as normal speed checks and must fail.
*/
using System;
using System.Reflection;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;
using Blockparty.UI;

public static class CameraChecks
{
    private static int failures;
    private static readonly string[] Actions = {
        "CameraPanRight", "CameraPanLeft", "CameraPanUp", "CameraPanDown",
        "CameraZoomIn", "CameraZoomOut", "CameraRotateLeft", "CameraRotateRight"
    };

    private static void Check(bool ok, string why)
    {
        if (!ok) throw new InvalidOperationException(why);
    }
    private static void Run(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception error)
        {
            while (error is TargetInvocationException && error.InnerException != null)
                error = error.InnerException;
            failures++;
            Console.WriteLine("FAIL " + name + ": " + error.Message);
        }
    }
    private static object Call(object obj, string name, params object[] args)
    {
        MethodInfo method = obj.GetType().GetMethod(name);
        Check(method != null, "missing CAMERA " + obj.GetType().Name + "." + name);
        return method.Invoke(obj, args);
    }
    private static T Property<T>(object obj, string name)
    {
        PropertyInfo property = obj.GetType().GetProperty(name);
        Check(property != null, "missing CAMERA " + obj.GetType().Name + "." + name);
        return (T)property.GetValue(obj);
    }
    private static double Number(object obj, string name)
    {
        double value = Convert.ToDouble(Property<object>(obj, name));
        Check(!double.IsNaN(value) && !double.IsInfinity(value), "non-finite " + name);
        return value;
    }
    private static void Near(double actual, double expected, string why)
    {
        Check(Math.Abs(actual - expected) <= 1e-6, why + ": actual=" + actual + " expected=" + expected);
    }
    private sealed class Fixture
    {
        public readonly WorldState World = WorldState.CreateSeeded(7L, 42L, 0, null);
        public readonly Accessibility Access = new Accessibility(
            new SettingsRecord(SaveV1.SettingsSchemaVersion, .7f, 1f, false));
        public readonly Clock Clock;
        public readonly CommandArbitrator Commands;
        public readonly ActivityExecutor Executor;
        public readonly WorldNavigation Navigation;
        public readonly InputMap Input;
        public readonly object Camera;
        public Fixture()
        {
            Clock = new Clock(World);
            Commands = new CommandArbitrator(World, Clock);
            Executor = new ActivityExecutor(World, Commands, Clock);
            Navigation = new WorldNavigation(World, Commands, Executor);
            Input = new InputMap(Access, Clock);
            Type type = typeof(Accessibility).Assembly.GetType("Blockparty.UI.CameraControl");
            Check(type != null, "missing CAMERA Blockparty.UI.CameraControl (pan/zoom/rotate model)");
            Camera = Activator.CreateInstance(type, new object[] { Access, Input, Navigation, Executor });
        }
        public ResidentRecord Add(string name, float x, float z)
        {
            HomeRecord home = World.AllocateHome(StateWriter.SimCore, 8);
            HouseholdRecord household = World.AllocateHousehold(StateWriter.SimCore, name, home.Id);
            ResidentRecord resident = World.AllocateResident(StateWriter.SimCore, name,
                LifeStage.Adult, household.Id, home.Id);
            Executor.SetResidentPosition(resident.Id, new ApproachPose(x, 0f, z));
            return resident;
        }
        public void Activate(string action, double seconds)
        {
            string binding = Input.HintFor(action);
            Check(Access.ResolveInput(binding) == action && Input.TryActivate(binding), "input-map setup: " + action);
            Check((bool)Call(Camera, "TryActivate", binding, seconds), "camera rejected mapped " + action);
        }
        public double[] Pose()
        {
            return new[] { Number(Camera,"PanX"), Number(Camera,"PanZ"),
                Number(Camera,"Zoom"), Number(Camera,"Rotation") };
        }
    }
    private static int Axis(string action)
    {
        return action.StartsWith("CameraZoom") ? 2 : action.StartsWith("CameraRotate") ? 3
            : action == "CameraPanUp" || action == "CameraPanDown" ? 1 : 0;
    }
    private static void Speed(Fixture f, string action, float speed)
    {
        bool ok = Axis(action) == 2 ? f.Access.TrySetCameraZoomSpeed(speed)
            : Axis(action) == 3 ? f.Access.TrySetCameraRotateSpeed(speed)
            : f.Access.TrySetCameraPanSpeed(speed);
        Check(ok, "speed fixture outside INPUT-ACCESS range");
    }
    private static double Move(Fixture f, string action, double seconds)
    {
        double[] before = f.Pose();
        f.Activate(action, seconds);
        double[] after = f.Pose();
        for (int axis = 0; axis < before.Length; axis++)
            if (axis != Axis(action)) Near(after[axis], before[axis], "action changed unrelated axis");
        return after[Axis(action)] - before[Axis(action)];
    }
    private static void Ratio(double actual, double baseline, double ratio)
    {
        Check(Math.Abs(baseline) > 1e-9, "essential camera input produced zero displacement");
        Near(actual / baseline / ratio, 1d, "displacement did not scale with configured speed/input-second");
    }
    private static void SpeedCheck(string action, bool ignoredSpeed = false)
    {
        var f = new Fixture();
        f.Access.SetReducedMotion(true);
        Speed(f, action, 1f);
        double baseline = Move(f, action, .125d);
        foreach (float speed in ignoredSpeed ? new[] { 2f } : new[] { .5f, 2f, 4f })
        {
            Speed(f, action, speed);
            if (ignoredSpeed) Speed(f, action, 1f);
            Ratio(Move(f, action, .125d), baseline, speed);
        }
    }
    private static void Duration(string action)
    {
        var first = new Fixture(); var second = new Fixture();
        first.Access.SetReducedMotion(true); second.Access.SetReducedMotion(true);
        Ratio(Move(second, action, .25d), Move(first, action, .125d), 2d);
    }
    private static void Inversion(string action)
    {
        var normal = new Fixture(); var inverted = new Fixture();
        normal.Access.SetReducedMotion(true); inverted.Access.SetReducedMotion(true);
        if (Axis(action) == 0) inverted.Access.SetInvertX(true);
        else inverted.Access.SetInvertY(true);
        Ratio(Move(inverted, action, .125d), Move(normal, action, .125d), -1d);
    }
    private static void SamePose(double[] expected, double[] actual, string why)
    {
        for (int axis = 0; axis < expected.Length; axis++) Near(actual[axis], expected[axis], why);
    }
    private static void Edge(float x, float y)
    {
        var f = new Fixture(); f.Access.SetReducedMotion(true);
        f.Access.SetEdgeScroll(false);
        double[] before = f.Pose();
        Call(f.Camera,"PanAtEdge",x,y,.125d);
        SamePose(before, f.Pose(), "disabled edge scroll moved camera");
        f.Access.SetEdgeScroll(true);
        Call(f.Camera,"PanAtEdge",x,y,.125d);
        double[] after = f.Pose();
        int axis = x == .5f ? 1 : 0;
        Check(Math.Abs(after[axis]-before[axis]) > 1e-9, "enabled edge scroll had no effect");
        Near(after[1-axis],before[1-axis],"edge changed unrelated pan axis");
        Near(after[2],before[2],"edge zoomed"); Near(after[3],before[3],"edge rotated");
        before = f.Pose();
        Call(f.Camera,"PanAtEdge",.5f,.5f,.125d);
        SamePose(before,f.Pose(),"center pointer edge-scrolled");
    }
    private static void Reduced(string action)
    {
        var f = new Fixture();
        Call(f.Camera,"RequestShake",1f);
        f.Access.SetReducedMotion(true);
        Call(f.Camera,"AdvanceRealSeconds",0d);
        Near(Number(f.Camera,"ShakeAmplitude"),0d,"reduced motion retained pre-existing shake");
        Move(f,action,.125d);
        double[] destination = f.Pose();
        for (int step = 0; step < 16; step++)
        {
            Call(f.Camera,"RequestShake",1f);
            Call(f.Camera,"AdvanceRealSeconds",.125d);
            Near(Number(f.Camera,"ShakeAmplitude"),0d,"reduced motion introduced shake");
            SamePose(destination,f.Pose(),"reduced motion easing drift/overshoot after input");
        }
    }
    private static void Remap(string action, string binding)
    {
        var f = new Fixture(); f.Access.SetReducedMotion(true);
        string original = f.Input.HintFor(action);
        Check(f.Access.TryRemap(action,binding),"real remap refused");
        Check(f.Input.HintFor(action) == binding && !f.Input.TryActivate(original),"input map did not refresh");
        double[] before = f.Pose();
        Check(!(bool)Call(f.Camera,"TryActivate",original,.125d),"camera accepted obsolete binding");
        SamePose(before,f.Pose(),"obsolete binding moved camera");
        Check(Math.Abs(Move(f,action,.125d)) > 1e-9,"remapped input did not move camera");
    }
    private static string Follow()
    {
        var f = new Fixture();
        ResidentRecord target = f.Add("Sam",0f,0f), twin = f.Add("Sam",-.5f,0f),
            b = f.Add("B",-2f,0f), c = f.Add("C",-3f,0f);
        Check(f.Navigation.FollowResident(target.Id).Accepted,"follow setup failed");
        f.Access.SetReducedMotion(true);
        string trace = "";
        for (int step = 0; step < 50; step++)
        {
            float x = -.5f + step * .02f;
            f.Executor.SetResidentPosition(target.Id,new ApproachPose(step*.1f,0f,step*.2f));
            f.Executor.SetResidentPosition(twin.Id,new ApproachPose(x,0f,0f));
            f.Executor.SetResidentPosition(b.Id,new ApproachPose(-x,0f,0f));
            f.Executor.SetResidentPosition(c.Id,new ApproachPose(x,0f,.5f));
            f.Clock.AdvanceRealSeconds(StateWriter.SimCore,.1d);
            f.Executor.Tick(StateWriter.ActivityExecutor); f.Navigation.Refresh();
            f.Activate(Actions[step % Actions.Length],.125d);
            Call(f.Camera,"AdvanceRealSeconds",.1d);
            Check(f.Navigation.FollowedResident == target.Id
                && Property<ResidentId?>(f.Camera,"FollowedResident") == target.Id,
                "pan/zoom/rotate retargeted follow to crossing resident");
            ApproachPose actual = Property<ApproachPose>(f.Camera,"FollowPosition");
            ApproachPose expected = f.Executor.GetResidentPosition(target.Id);
            Near(actual.X,expected.X,"follow anchor stale X");
            Near(actual.Y,expected.Y,"follow anchor stale Y");
            Near(actual.Z,expected.Z,"follow anchor stale Z");
            trace += ((long)Property<ResidentId?>(f.Camera,"FollowedResident").Value) + ";";
        }
        Check(f.World.GameSeconds == 300L,"five game minutes not exercised");
        f.Navigation.StopFollowing(); Call(f.Camera,"AdvanceRealSeconds",0d);
        Check(!Property<ResidentId?>(f.Camera,"FollowedResident").HasValue,"StopFollowing retained camera target");
        return trace;
    }
    private static void PresentationOnly()
    {
        foreach (bool paused in new[] { false, true })
        {
            var f = new Fixture(); ResidentRecord resident = f.Add("Resident",3f,4f);
            if (paused) f.Clock.Pause();
            int hash = f.World.ComputeStateHash(); long seconds = f.World.GameSeconds;
            ApproachPose position = f.Executor.GetResidentPosition(resident.Id);
            foreach (string action in Actions) { f.Activate(action,.125d); Call(f.Camera,"AdvanceRealSeconds",.125d); }
            Check(f.World.ComputeStateHash() == hash && f.World.GameSeconds == seconds
                && f.Clock.Paused == paused,"camera changed simulation/time/pause");
            ApproachPose after = f.Executor.GetResidentPosition(resident.Id);
            Near(after.X,position.X,"camera moved resident X"); Near(after.Y,position.Y,"camera moved resident Y");
            Near(after.Z,position.Z,"camera moved resident Z");
        }
    }
    public static int Main(string[] args)
    {
        if (Array.IndexOf(args,"negative-fixture") >= 0)
            Run("A4-speed-setting-ignored-negative-fixture SEIT-PRES-05-N1", () => SpeedCheck("CameraPanRight",true));
        else
        {
            foreach (string action in Actions)
            {
                Run("A4-speed-"+action+" SEIT-PRES-05-N1", () => SpeedCheck(action));
                Run("A4-duration-"+action+" SEIT-PRES-05-N1", () => Duration(action));
                Run("A4-reduced-"+action+" SEIT-PRES-05-N1", () => Reduced(action));
                foreach (string binding in new[] { "F24", "Mouse4" })
                    Run("input-remap-"+action+"-"+binding+" SEIT-PRES-05-N1", () => Remap(action,binding));
            }
            foreach (string action in new[] { "CameraPanRight", "CameraPanLeft", "CameraPanUp", "CameraPanDown" })
                Run("A4-invert-"+action+" SEIT-PRES-05-N1", () => Inversion(action));
            foreach (float edge in new[] { 0f, 1f })
            {
                Run("A4-edge-x-"+edge+" SEIT-PRES-05-N1", () => Edge(edge,.5f));
                Run("A4-edge-y-"+edge+" SEIT-PRES-05-N1", () => Edge(.5f,edge));
            }
            Run("follow-identity-crossings SEIT-UI-05-P1 SEIT-UI-05-N1", () => Follow());
            Run("camera-presentation-only SEIT-PRES-05-N1", PresentationOnly);
            Run("repeated-play-entry SEIT-UI-05-P1", () => {
                var old = new Fixture(); double[] baseline = old.Pose();
                old.Activate("CameraPanRight",.125d);
                Check(Math.Abs(old.Pose()[0]-baseline[0]) > 1e-9,"normal-motion pan had no effect");
                ResidentRecord resident = old.Add("Prior play",0f,0f);
                Check(old.Navigation.FollowResident(resident.Id).Accepted,"prior-play follow setup failed");
                Call(old.Camera,"AdvanceRealSeconds",0d);
                var fresh = new Fixture();
                SamePose(baseline,fresh.Pose(),"prior play entry leaked camera pose");
                Check(!Property<ResidentId?>(fresh.Camera,"FollowedResident").HasValue,"prior play entry leaked follow");
                Check(Follow() == Follow(),"same seed/inputs produced different target trace");
            });
        }
        if (failures != 0) return 1;
        Console.WriteLine("CAMERA ALL PASS");
        return 0;
    }
}
