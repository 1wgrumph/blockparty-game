// CAMERA@1: presentation-only camera model (BP-PRES-05, BP-UI-05).
// Pan/zoom/rotate route through the INPUT-ACCESS action map at the
// configured speeds, honouring edge-scroll, axis inversion and reduced
// motion. Displacement is proportional to speed x input-seconds and is
// applied immediately with no easing, so reduced motion holds zero shake
// and no drift. Follow mirrors the live WorldNavigation target and NAV
// pose. Never touches the world, clock or resident positions. Plain C# 9,
// no UnityEngine dependency. Fast Enter Play Mode: all state on the instance.
using System;
using Blockparty.Contracts;
using Blockparty.Simulation;

namespace Blockparty.UI
{
    public sealed class CameraControl
    {
        private const double UnitsPerInputSecond = 1.0;
        private const double DefaultZoom = 10.0;

        private static readonly string[] CameraActions =
        {
            "CameraPanRight", "CameraPanLeft", "CameraPanUp", "CameraPanDown",
            "CameraZoomIn", "CameraZoomOut", "CameraRotateLeft", "CameraRotateRight"
        };

        private readonly Accessibility accessibility;
        private readonly InputMap input;
        private readonly WorldNavigation navigation;
        private readonly ActivityExecutor executor;
        private double panX;
        private double panZ;
        private double zoom = DefaultZoom;
        private double rotation;
        private float shake;

        public CameraControl(Accessibility accessibility, InputMap input,
            WorldNavigation navigation, ActivityExecutor executor)
        {
            if (accessibility == null)
            {
                throw new ArgumentNullException("accessibility");
            }
            if (input == null)
            {
                throw new ArgumentNullException("input");
            }
            if (navigation == null)
            {
                throw new ArgumentNullException("navigation");
            }
            if (executor == null)
            {
                throw new ArgumentNullException("executor");
            }
            this.accessibility = accessibility;
            this.input = input;
            this.navigation = navigation;
            this.executor = executor;
        }

        public double PanX { get { return panX; } }
        public double PanZ { get { return panZ; } }
        public double Zoom { get { return zoom; } }
        public double Rotation { get { return rotation; } }
        public float ShakeAmplitude
        {
            get { return accessibility.Settings.ReducedMotion ? 0f : shake; }
        }
        public ResidentId? FollowedResident { get { return navigation.FollowedResident; } }
        public ApproachPose FollowPosition
        {
            get
            {
                ResidentId? followed = navigation.FollowedResident;
                return followed.HasValue
                    ? executor.GetResidentPosition(followed.Value)
                    : default(ApproachPose);
            }
        }

        // Routes the live binding through the input map: only the eight
        // camera actions move the camera, and obsolete bindings are refused
        // without side effects (the game loop owns map activation effects).
        public bool TryActivate(string binding, double inputSeconds)
        {
            string action = ResolveCameraAction(binding);
            if (action == null)
            {
                return false;
            }
            SettingsRecord settings = accessibility.Settings;
            double panStep = settings.CameraPanSpeed * inputSeconds * UnitsPerInputSecond;
            double zoomStep = settings.CameraZoomSpeed * inputSeconds * UnitsPerInputSecond;
            double rotateStep = settings.CameraRotateSpeed * inputSeconds * UnitsPerInputSecond;
            double horizontal = settings.InvertX ? -panStep : panStep;
            double vertical = settings.InvertY ? -panStep : panStep;
            switch (action)
            {
                case "CameraPanRight": panX += horizontal; break;
                case "CameraPanLeft": panX -= horizontal; break;
                case "CameraPanUp": panZ += vertical; break;
                case "CameraPanDown": panZ -= vertical; break;
                case "CameraZoomIn": zoom -= zoomStep; break;
                case "CameraZoomOut": zoom += zoomStep; break;
                case "CameraRotateLeft": rotation -= rotateStep; break;
                case "CameraRotateRight": rotation += rotateStep; break;
                default: return false;
            }
            return true;
        }

        // Viewport coordinates 0..1; the center is not an edge.
        public void PanAtEdge(float normalizedX, float normalizedY, double inputSeconds)
        {
            SettingsRecord settings = accessibility.Settings;
            if (!settings.EdgeScroll)
            {
                return;
            }
            double step = settings.CameraPanSpeed * inputSeconds * UnitsPerInputSecond;
            double horizontal = settings.InvertX ? -step : step;
            double vertical = settings.InvertY ? -step : step;
            if (normalizedX <= 0f)
            {
                panX -= horizontal;
            }
            else if (normalizedX >= 1f)
            {
                panX += horizontal;
            }
            if (normalizedY <= 0f)
            {
                panZ -= vertical;
            }
            else if (normalizedY >= 1f)
            {
                panZ += vertical;
            }
        }

        // Mouse drag-to-pan (M5-OD-54). A press becomes a drag only after the
        // pointer travels past the threshold, so a still click keeps selecting.
        public const float DragThresholdPixels = 6f;
        // Wheel zoom stays inside the range the FOV clamp can show (55*10/80 .. 55*10/30).
        public const double MinWheelZoom = 550.0 / 80.0;
        public const double MaxWheelZoom = 550.0 / 30.0;
        private bool pointerDown;
        private bool dragging;
        private float pressX;
        private float pressY;

        public bool Dragging { get { return dragging; } }

        public void PointerDown(float x, float y)
        {
            pointerDown = true;
            dragging = false;
            pressX = x;
            pressY = y;
        }

        // Returns true while the press is a drag (threshold crossed at any point).
        public bool PointerMove(float x, float y)
        {
            if (!pointerDown)
            {
                return false;
            }
            if (!dragging)
            {
                float dx = x - pressX;
                float dy = y - pressY;
                dragging = dx * dx + dy * dy > DragThresholdPixels * DragThresholdPixels;
            }
            return dragging;
        }

        // Returns true when the press ended as a click (no drag); false after a drag or with no press.
        public bool PointerUp()
        {
            bool click = pointerDown && !dragging;
            pointerDown = false;
            dragging = false;
            return click;
        }

        public void CancelPointer()
        {
            pointerDown = false;
            dragging = false;
        }

        // Ground-plane displacement chosen by the presentation layer (grab-the-ground);
        // applied directly, so reduced motion needs no special case.
        public void PanByGround(double deltaX, double deltaZ)
        {
            panX += deltaX;
            panZ += deltaZ;
        }

        public void ZoomByWheel(double notches)
        {
            double step = accessibility.Settings.CameraZoomSpeed * notches;
            zoom = Math.Min(MaxWheelZoom, Math.Max(MinWheelZoom, zoom - step));
        }

        public void RequestShake(float amplitude)
        {
            if (accessibility.Settings.ReducedMotion)
            {
                shake = 0f;
                return;
            }
            if (amplitude > shake)
            {
                shake = amplitude;
            }
        }

        // Presentation tick only: settles shake, never moves the pose or sim.
        public void AdvanceRealSeconds(double seconds)
        {
            if (accessibility.Settings.ReducedMotion)
            {
                shake = 0f;
                return;
            }
            if (seconds > 0d)
            {
                shake = Math.Max(0f, shake - (float)(seconds * 2.0));
            }
        }

        private string ResolveCameraAction(string binding)
        {
            if (string.IsNullOrEmpty(binding))
            {
                return null;
            }
            for (int i = 0; i < CameraActions.Length; i++)
            {
                if (input.HintFor(CameraActions[i]) == binding)
                {
                    return CameraActions[i];
                }
            }
            return null;
        }
    }
}
