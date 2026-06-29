using UnityEngine;

namespace OpenBCI.Interaction
{
    /// <summary>
    /// Moves an object along an axis based on a [0,1] EEG control value, driven by
    /// the interactor while this object is the active selection. Heavy smoothing is
    /// deliberate: raw EEG is jittery and jitter is nausea-inducing in VR.
    ///
    /// Default mapping: concentrate -> the object rises (telekinesis-style lift).
    /// </summary>
    public class EEGMover : MonoBehaviour
    {
        [Tooltip("Local-space axis the control value moves the object along.")]
        public Vector3 axis = Vector3.up;

        [Tooltip("Displacement (m) at control = 1, measured from the resting position.")]
        public float maxDisplacement = 0.6f;

        [Tooltip("Control value below this maps to 'no movement' (deadzone for resting state).")]
        [Range(0f, 1f)] public float restValue = 0.5f;

        [Tooltip("Higher = snappier, lower = smoother/laggier.")]
        public float smoothing = 3f;

        Vector3 _restPosition;
        bool _hasRest;
        float _target;   // 0..1 desired displacement fraction
        float _current;  // smoothed displacement fraction
        bool _active;

        void OnEnable()
        {
            CaptureRest();
        }

        /// <summary>Record the current position as the resting (control = rest) origin.</summary>
        public void CaptureRest()
        {
            _restPosition = transform.position;
            _hasRest = true;
        }

        /// <summary>Called by the interactor each frame while this object is selected.</summary>
        public void SetControl(float value01)
        {
            _active = true;
            if (!_hasRest) CaptureRest();
            // remap [restValue..1] -> [0..1] so resting EEG keeps the object at rest
            float span = Mathf.Max(1e-3f, 1f - restValue);
            _target = Mathf.Clamp01((value01 - restValue) / span);
        }

        /// <summary>Called when this object is deselected; it eases back to rest.</summary>
        public void Release()
        {
            _active = false;
            _target = 0f;
        }

        void Update()
        {
            if (!_hasRest) return;
            // ease toward target whether active or releasing
            _current = Mathf.Lerp(_current, _target, 1f - Mathf.Exp(-smoothing * Time.deltaTime));
            transform.position = _restPosition + axis.normalized * (_current * maxDisplacement);
        }
    }
}
