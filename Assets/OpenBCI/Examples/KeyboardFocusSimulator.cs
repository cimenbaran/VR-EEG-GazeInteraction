using UnityEngine;
using UnityEngine.InputSystem;
using OpenBCI.Interaction;

namespace OpenBCI.Examples
{
    /// <summary>
    /// Simulates focus/unfocus without electrodes by feeding a manual control value
    /// into a <see cref="GazeEEGInteractor"/>. Hold the key to "focus" (value ramps up),
    /// release to "unfocus" (ramps down). The gradual ramp mimics how real EEG focus
    /// rises over time, so dwell-based selection still behaves realistically.
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

        float _value;

        void Awake()
        {
            if (interactor == null) interactor = FindAnyObjectByType<GazeEEGInteractor>();
        }

        void Update()
        {
            if (interactor == null) return;

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
