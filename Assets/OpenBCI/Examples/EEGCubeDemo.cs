using UnityEngine;
using OpenBCI.Core;
using OpenBCI.Interaction;

namespace OpenBCI.Examples
{
    /// <summary>
    /// One-component demo: spawns cubes on a circle around the player and wires up
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
        [Tooltip("Arc distance between adjacent cube centers, in meters. Small counts stay clustered in front of the player.")]
        public float spacing = 0.6f;
        public float cubeSize = 0.3f;
        [Tooltip("Radius of the circle the cubes sit on, centered on the player at world origin.")]
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

            Vector3 center = new Vector3(0f, height, 0f);
            float radius = Mathf.Max(0.01f, distance);

            int n = Mathf.Max(1, cubeCount);
            // Fixed angular gap so small counts stay clustered in front of the player.
            float anglePerCube = spacing / radius;
            float totalArc = (n - 1) * anglePerCube;
            float startAngle;
            if (totalArc >= 2f * Mathf.PI)
            {
                // Arc would wrap past a full circle: fall back to an even ring.
                anglePerCube = 2f * Mathf.PI / n;
                startAngle = 0f;
            }
            else
            {
                // Center the arc on world +Z, fanning out symmetrically.
                startAngle = -totalArc * 0.5f;
            }

            for (int i = 0; i < n; i++)
            {
                float angleDeg = (startAngle + i * anglePerCube) * Mathf.Rad2Deg;
                Vector3 dir = Quaternion.AngleAxis(angleDeg, Vector3.up) * Vector3.forward;

                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = $"EEGCube_{i}";
                cube.transform.position = center + dir * radius;
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
