using UnityEngine;
using UnityEngine.InputSystem;
using OpenBCI.Interaction;

namespace OpenBCI.Examples
{
    /// <summary>
    /// Keyboard test harness for a <see cref="GazeEEGInteractor"/>.
    ///
    /// - Hold <see cref="focusKey"/> (default Space) to simulate focus without electrodes:
    ///   the value ramps up while held and decays on release, mimicking how real EEG
    ///   focus rises over time so dwell-based selection behaves realistically.
    /// - Press <see cref="recalibrateKey"/> (default R) to re-measure the resting EEG
    ///   baseline. This works with the real board too — it is not part of the simulation.
    /// </summary>
    public class KeyboardFocusSimulator : MonoBehaviour
    {
        [Tooltip("Interactor to drive. Auto-found if left empty.")]
        public GazeEEGInteractor interactor;

        [Tooltip("Hold this key to simulate focus.")]
        public Key focusKey = Key.Space;

        [Tooltip("How fast the value rises while held (units/sec).")]
        public float riseSpeed = 1.5f;
        [Tooltip("How fast the value falls while released (units/sec).")]
        public float fallSpeed = 1.5f;

        [Tooltip("If false, the simulator stops overriding and EEG takes over again.")]
        public bool enableSimulation = true;

        [Header("Calibration")]
        [Tooltip("Press to re-measure the resting EEG baseline. Sit still while it runs.")]
        public Key recalibrateKey = Key.R;

        float _value;

        void Awake()
        {
            if (interactor == null) interactor = FindAnyObjectByType<GazeEEGInteractor>();
        }

        void Update()
        {
            // re-resolve until found: the interactor may be created after Awake
            // (e.g. by EEGCubeDemo in Start).
            if (interactor == null)
            {
                interactor = FindAnyObjectByType<GazeEEGInteractor>();
                if (interactor == null) return;
            }

            // Recalibration applies to the real EEG path, so handle it regardless
            // of whether simulation is currently overriding the control value.
            if (Keyboard.current != null && Keyboard.current[recalibrateKey].wasPressedThisFrame)
                interactor.Recalibrate();

            if (!enableSimulation)
            {
                interactor.useManualControl = false;
                return;
            }

            bool held = Keyboard.current != null && Keyboard.current[focusKey].isPressed;
            float delta = (held ? riseSpeed : -fallSpeed) * Time.deltaTime;
            _value = Mathf.Clamp01(_value + delta);

            interactor.useManualControl = true;
            interactor.manualControl = _value;
        }

        void OnDisable()
        {
            if (interactor != null) interactor.useManualControl = false;
        }
    }
}
