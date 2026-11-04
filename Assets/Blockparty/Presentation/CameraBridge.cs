using Blockparty.UI;
using UnityEngine;

// CAMERA@1: mirrors the plain-C# CameraControl pose onto a Unity Camera.
// Default projection only (pan offsets, viewing distance, yaw); rendered
// behaviour and device acceptance stay CONTROLS-PLAY obligations.
public sealed class CameraBridge : MonoBehaviour
{
    public Camera target;

    CameraControl control;

    public void Bind(CameraControl camera)
    {
        control = camera;
    }

    void LateUpdate()
    {
        if (control == null)
        {
            return;
        }
        Camera unityCamera = target != null ? target : GetComponent<Camera>();
        if (unityCamera == null)
        {
            return;
        }
        Vector3 position = unityCamera.transform.position;
        position.x = (float)control.PanX;
        position.y = (float)control.Zoom;
        position.z = (float)control.PanZ;
        unityCamera.transform.position = position;
        Vector3 angles = unityCamera.transform.eulerAngles;
        angles.y = (float)control.Rotation;
        unityCamera.transform.eulerAngles = angles;
    }
}
