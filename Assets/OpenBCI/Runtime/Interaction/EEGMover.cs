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

        [Header("Gaze Lock")]
        [Tooltip("While selected, span the whole travel range with an invisible collider so the " +
                 "interactor's gaze ray keeps landing on this object even after it has moved. " +
                 "Without it, a rising cube slides out from under a still head and the drive cuts out.")]
        public bool gazeLockVolume = true;
        [Tooltip("Extra padding (m) around the gaze-lock collider.")]
        public float gazeLockPadding = 0.05f;

        Vector3 _restPosition;
        bool _hasRest;
        float _target;   // 0..1 desired displacement fraction
        float _current;  // smoothed displacement fraction
        bool _active;

        Vector3 _trackCenter;   // world midpoint of the rest -> max-displacement path
        GameObject _gazeLock;

        void OnEnable()
        {
            CaptureRest();
        }

        /// <summary>Record the current position as the resting (control = rest) origin.</summary>
        public void CaptureRest()
        {
            _restPosition = transform.position;
            _hasRest = true;
            _trackCenter = _restPosition + axis.normalized * (maxDisplacement * 0.5f);
            if (_gazeLock != null) _gazeLock.transform.position = _trackCenter;
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

        /// <summary>
        /// Stop responding to control but stay where you are. Used when gaze leaves the
        /// selected object — the object freezes instead of sagging back to rest.
        /// </summary>
        public void Hold()
        {
            _active = false;
            _target = _current;
        }

        /// <summary>
        /// Enable/disable the gaze-lock collider. The interactor turns this on for the
        /// active selection so that raycasting the gaze ray keeps resolving to this object
        /// while it travels, instead of missing once the mesh has moved.
        /// </summary>
        public void SetGazeLock(bool on)
        {
            if (!gazeLockVolume)
            {
                if (_gazeLock != null) _gazeLock.SetActive(false);
                return;
            }
            if (on && _gazeLock == null) BuildGazeLock();
            if (_gazeLock != null && _gazeLock.activeSelf != on) _gazeLock.SetActive(on);
        }

        void BuildGazeLock()
        {
            // Deliberately NOT parented to this object: it must stay put over the track
            // while the object slides along it. A GazeLockProxy carries the back-reference
            // so the interactor can resolve a ray hit here to this EEGSelectable.
            _gazeLock = new GameObject("GazeLock") { layer = gameObject.layer };
            _gazeLock.transform.SetParent(transform.parent, worldPositionStays: true);
            _gazeLock.transform.position = _trackCenter;
            _gazeLock.transform.rotation = Quaternion.identity;
            _gazeLock.AddComponent<GazeLockProxy>().target = GetComponent<EEGSelectable>();

            var box = _gazeLock.AddComponent<BoxCollider>();
            Vector3 ls = _gazeLock.transform.lossyScale;
            Vector3 objWorld = TryGetComponent<Renderer>(out var r) ? r.bounds.size : transform.lossyScale;
            Vector3 axisN = axis.normalized;
            Vector3 travel = new Vector3(Mathf.Abs(axisN.x), Mathf.Abs(axisN.y), Mathf.Abs(axisN.z))
                             * Mathf.Abs(maxDisplacement);
            Vector3 worldSize = objWorld + travel + Vector3.one * Mathf.Max(0f, gazeLockPadding);
            box.size = new Vector3(
                worldSize.x / Mathf.Max(1e-4f, ls.x),
                worldSize.y / Mathf.Max(1e-4f, ls.y),
                worldSize.z / Mathf.Max(1e-4f, ls.z));

            _gazeLock.SetActive(false);
        }

        void Update()
        {
            if (!_hasRest) return;
            // ease toward target whether active or releasing
            _current = Mathf.Lerp(_current, _target, 1f - Mathf.Exp(-smoothing * Time.deltaTime));
            transform.position = _restPosition + axis.normalized * (_current * maxDisplacement);
        }

        void OnDestroy()
        {
            if (_gazeLock != null) Destroy(_gazeLock);
        }
    }
}
