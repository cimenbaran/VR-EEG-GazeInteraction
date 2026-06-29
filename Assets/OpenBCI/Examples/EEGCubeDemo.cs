using UnityEngine;
using OpenBCI.Core;
using OpenBCI.Interaction;

namespace OpenBCI.Examples
{
    /// <summary>
    /// One-component demo: spawns a row of cubes in front of the camera and wires up
    /// the gaze + EEG interactor. Drop this on an empty GameObject and press Play.
    ///
    /// Set <see cref="phase"/> to walk through the project's three milestones:
    ///   1 cube  + Select  -> select a cube with EEG
    ///   N cubes + Choose  -> choose between cubes with gaze, confirm with EEG
    ///   N cubes + Move    -> selected cube rises/falls with your focus level
    /// </summary>
    public class EEGCubeDemo : MonoBehaviour
    {
        [Header("Scene")]
        [Tooltip("How many cubes to spawn (1 for the Select milestone).")]
        public int cubeCount = 3;
        public float spacing = 0.6f;
        public float cubeSize = 0.3f;
        public float distance = 2f;
        public float height = 1.4f;

        [Header("Interaction")]
        public GazeEEGInteractor.Phase phase = GazeEEGInteractor.Phase.Choose;
        public EEGMetric controlMetric = EEGMetric.Engagement;

        [Header("EEG Source")]
        [Tooltip("Existing receiver. If empty, one is created automatically.")]
        public OpenBCIReceiver receiver;
        public int udpPort = 12345;

        void Start()
        {
            if (receiver == null)
            {
                receiver = FindAnyObjectByType<OpenBCIReceiver>();
                if (receiver == null)
                {
                    var go = new GameObject("OpenBCI");
                    receiver = go.AddComponent<OpenBCIReceiver>();
                    receiver.port = udpPort;
                }
            }

            var cam = Camera.main;
            Vector3 origin = cam != null
                ? cam.transform.position + cam.transform.forward * distance
                : new Vector3(0, height, distance);
            origin.y = height;
            Vector3 right = cam != null ? cam.transform.right : Vector3.right;

            int n = Mathf.Max(1, cubeCount);
            float start = -(n - 1) * 0.5f * spacing;
            for (int i = 0; i < n; i++)
            {
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = $"EEGCube_{i}";
                cube.transform.position = origin + right * (start + i * spacing);
                cube.transform.localScale = Vector3.one * cubeSize;
                cube.AddComponent<EEGSelectable>();
                cube.AddComponent<EEGMover>();
            }

            var interactor = gameObject.AddComponent<GazeEEGInteractor>();
            interactor.receiver = receiver;
            interactor.phase = phase;
            interactor.controlMetric = controlMetric;
        }
    }
}
