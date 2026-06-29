using UnityEngine;

namespace OpenBCI.Interaction
{
    /// <summary>
    /// Gaze ray from a head-mounted camera's forward direction. In a Quest/OpenXR
    /// rig the XR camera is tagged MainCamera, so this works with no extra setup.
    /// Assign <see cref="gazeCamera"/> explicitly to override.
    /// </summary>
    public class HeadGazeProvider : MonoBehaviour, IGazeProvider
    {
        [Tooltip("Camera to cast from. Defaults to Camera.main if left empty.")]
        public Camera gazeCamera;

        void Awake()
        {
            if (gazeCamera == null) gazeCamera = Camera.main;
        }

        public bool TryGetGazeRay(out Ray ray)
        {
            if (gazeCamera == null)
            {
                gazeCamera = Camera.main;
                if (gazeCamera == null) { ray = default; return false; }
            }

            var t = gazeCamera.transform;
            ray = new Ray(t.position, t.forward);
            return true;
        }
    }
}
