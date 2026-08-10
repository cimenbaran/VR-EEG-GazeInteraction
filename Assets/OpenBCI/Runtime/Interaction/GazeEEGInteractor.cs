using UnityEngine;
using OpenBCI.Core;
using OpenBCI.Logging;

namespace OpenBCI.Interaction
{
    /// <summary>
    /// Drives gaze + EEG interaction across the project's three phases:
    ///
    ///   Select : gaze at a cube, hold EEG focus above threshold to select it.
    ///   Choose : same logic, with multiple cubes — gaze picks which one.
    ///   Move   : after selection, EEG continuously drives the cube's EEGMover.
    ///
    /// Phase Select and Choose share identical code; the only difference is how many
    /// EEGSelectables exist in the scene. Move builds on top of a completed selection.
    /// </summary>
    public class GazeEEGInteractor : MonoBehaviour
    {
        const string Cat = "Interactor";

        public enum Phase { Select = 1, Choose = 2, Move = 3 }

        [Header("Phase")]
        public Phase phase = Phase.Select;

        [Header("References")]
        [Tooltip("EEG source. Auto-found in the scene if left empty.")]
        public OpenBCIReceiver receiver;
        [Tooltip("Gaze source (MonoBehaviour implementing IGazeProvider). Auto-resolved if empty.")]
        public MonoBehaviour gazeProviderBehaviour;

        [Header("Gaze Raycast")]
        public LayerMask selectableMask = ~0;
        public float maxDistance = 20f;

        [Header("EEG Control")]
        public EEGMetric controlMetric = EEGMetric.Engagement;
        [Tooltip("Normalized [0,1] level that counts as 'focused'.")]
        [Range(0f, 1f)] public float confirmThreshold = 0.65f;
        [Tooltip("Seconds of sustained focus-on-target required to confirm a selection.")]
        public float dwellTime = 1.2f;

        [Header("Normalization")]
        [Tooltip("Calibrated = baseline frozen after calibration (recommended for control). " +
                 "Adaptive = baseline keeps chasing the signal (output drifts back to 0.5). " +
                 "FixedRange = direct linear map of the raw metric.")]
        public NormalizerMode normalizerMode = NormalizerMode.Calibrated;
        [Tooltip("Seconds of resting baseline measured before control becomes active.")]
        public float calibrationDuration = 5f;
        [Tooltip("Std devs above baseline that map to 1.0. Lower = more sensitive.")]
        public float spreadStds = 1.5f;
        [Tooltip("Output smoothing time constant (s). Higher = smoother but laggier.")]
        public float outputSmoothing = 0.4f;
        [Tooltip("Raw metric range mapped to [0,1] when mode is FixedRange.")]
        public float rawMin = 0f, rawMax = 2f;
        [Tooltip("Feed log10(metric) to the normalizer instead of the raw ratio. Band power is " +
                 "log-normally distributed, so the linear ratio is heavily skewed and its " +
                 "standard deviation is dominated by a few large samples — which makes the " +
                 "z-score scale meaningless and the control value flip between 0 and 1.")]
        public bool useLogScaling = true;
        [Tooltip("Flip the control direction. Turn this on if focusing drives the bar DOWN — " +
                 "the sign of a composite index depends on electrode placement and montage.")]
        public bool invertControl = false;

        [Header("Stability")]
        [Tooltip("Once focused, control must fall this far below the threshold to disengage.")]
        [Range(0f, 0.3f)] public float hysteresis = 0.05f;
        [Tooltip("How fast dwell drains when focus drops, as a multiple of fill speed. " +
                 "0 = hold progress, 1 = drain as fast as it filled.")]
        [Range(0f, 4f)] public float dwellDecayRate = 0.5f;

        [Header("Behaviour")]
        [Tooltip("Looking away from the selected cube and re-confirming on another switches selection.")]
        public bool allowReselect = true;
        [Tooltip("In Move phase, only drive the selected object while gaze is actually resting on it.")]
        public bool requireGazeToMove = true;
        [Tooltip("When gaze leaves the selected object: off = freeze it where it is, " +
                 "on = ease it back to its resting position.")]
        public bool releaseToRestOnGazeLoss = false;

        [Header("Testing")]
        [Tooltip("When true, ControlValue comes from ManualControl instead of EEG — lets you simulate focus without electrodes.")]
        public bool useManualControl = false;
        [Range(0f, 1f)] public float manualControl = 0f;

        // ── exposed runtime state (handy for UI / debugging) ─────────────────
        public float ControlValue { get; private set; }   // normalized 0..1
        /// <summary>The un-normalized metric straight off the receiver, for debugging.</summary>
        public float RawMetric { get; private set; }
        public EEGSelectable Hovered { get; private set; }
        public EEGSelectable Selected { get; private set; }
        /// <summary>True while the selected object is actually being driven by EEG.</summary>
        public bool IsDriving { get; private set; }

        IGazeProvider _gaze;
        SignalNormalizer _normalizer;
        ScopedLogger _log;
        float _dwell;
        bool _focusLatched;   // hysteresis state
        int _lastPacket = -1;
        float _lastPacketTime;

        /// <summary>True while the resting baseline is still being measured.</summary>
        public bool IsCalibrating => _normalizer != null && _normalizer.IsCalibrating
                                     && normalizerMode == NormalizerMode.Calibrated
                                     && !useManualControl;
        public float CalibrationProgress => _normalizer?.CalibrationProgress ?? 1f;

        /// <summary>Re-measure the resting baseline. Call this while the user rests.</summary>
        public void Recalibrate()
        {
            _normalizer?.Recalibrate();
            _log.Info("Recalibrating baseline — sit still and rest.");
        }

        void Awake()
        {
            _log = OpenBCILogger.Scope(Cat);
            _normalizer = new SignalNormalizer();
            SyncNormalizerSettings();

            if (receiver == null) receiver = FindAnyObjectByType<OpenBCIReceiver>();
            if (receiver == null) _log.Warning("No OpenBCIReceiver found in scene.");

            ResolveGazeProvider();
        }

        void ResolveGazeProvider()
        {
            if (gazeProviderBehaviour is IGazeProvider explicitProvider)
            {
                _gaze = explicitProvider;
                return;
            }
            // search the scene for any IGazeProvider
            foreach (var mb in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (mb is IGazeProvider gp) { _gaze = gp; return; }
            }
            // fall back: attach a head-gaze provider on the main camera
            var cam = Camera.main;
            if (cam != null)
            {
                _gaze = cam.gameObject.AddComponent<HeadGazeProvider>();
                _log.Info("No gaze provider found — added HeadGazeProvider to Main Camera.");
            }
            else
            {
                _log.Warning("No gaze provider and no Main Camera — gaze disabled.");
            }
        }

        void Update()
        {
            UpdateControlValue();
            var target = Raycast();
            UpdateHover(target);
            UpdateSelection(target);
            UpdateMovePhase();
        }

        void SyncNormalizerSettings()
        {
            if (_normalizer == null) return;
            _normalizer.Mode = normalizerMode;
            _normalizer.CalibrationDuration = calibrationDuration;
            _normalizer.SpreadStds = spreadStds;
            _normalizer.OutputSmoothing = outputSmoothing;
            _normalizer.RawMin = rawMin;
            _normalizer.RawMax = rawMax;
        }

        void UpdateControlValue()
        {
            if (useManualControl)
            {
                ControlValue = Mathf.Clamp01(manualControl);
                return;
            }
            if (receiver == null || !receiver.HasData) return;

            SyncNormalizerSettings();   // let inspector tweaks apply live

            // Step the normalizer once per EEG packet, not once per frame. The bridge
            // sends a handful of packets a second; Update runs at ~72-90 Hz in the
            // headset. Re-feeding the same sample every frame inflates the sample
            // count and collapses the measured spread, after which the z-score maps
            // ordinary noise onto the full 0..1 range.
            int packet = receiver.PacketCount;
            if (packet == _lastPacket) return;

            float dt = _lastPacketTime > 0f ? Time.time - _lastPacketTime : Time.deltaTime;
            _lastPacket = packet;
            _lastPacketTime = Time.time;

            RawMetric = receiver.GetMetricRaw(controlMetric);

            // Band power is log-normal, so the composite ratios are strongly right-skewed.
            // Taking the log first makes the distribution roughly symmetric, which is what
            // the mean/std baseline in SignalNormalizer actually assumes.
            float raw = useLogScaling ? Mathf.Log10(Mathf.Max(RawMetric, 1e-6f)) : RawMetric;

            float value = _normalizer.Normalize(raw, dt);
            ControlValue = invertControl ? 1f - value : value;
        }

        EEGSelectable Raycast()
        {
            if (_gaze == null || !_gaze.TryGetGazeRay(out var ray)) return null;
            if (Physics.Raycast(ray, out var hit, maxDistance, selectableMask, QueryTriggerInteraction.Ignore))
                return hit.collider.GetComponentInParent<EEGSelectable>();
            return null;
        }

        void UpdateHover(EEGSelectable target)
        {
            if (target == Hovered) return;
            if (Hovered != null) Hovered.SetHover(false);
            Hovered = target;
            if (Hovered != null) Hovered.SetHover(true);
        }

        void UpdateSelection(EEGSelectable target)
        {
            // Don't act on control until the baseline is established.
            if (IsCalibrating)
            {
                _dwell = 0f;
                _focusLatched = false;
                if (target != null) target.SetCharge(0f);
                return;
            }

            // Hysteresis: engaging needs the full threshold, staying engaged needs less.
            // Prevents the dwell from flickering when control hovers near the line.
            float releaseThreshold = confirmThreshold - hysteresis;
            _focusLatched = _focusLatched
                ? ControlValue >= releaseThreshold
                : ControlValue >= confirmThreshold;

            // Already selected this target: nothing to charge.
            bool targetIsSelected = target != null && target == Selected;
            bool canCharge = target != null && !targetIsSelected &&
                             (Selected == null || allowReselect);

            if (!canCharge)
            {
                _dwell = 0f;
                if (target != null && !targetIsSelected) target.SetCharge(0f);
                return;
            }

            if (_focusLatched)
            {
                _dwell += Time.deltaTime;
            }
            else
            {
                // drain instead of hard-resetting, so a momentary dip doesn't wipe progress
                _dwell = Mathf.Max(0f, _dwell - Time.deltaTime * dwellDecayRate);
            }

            target.SetCharge(Mathf.Clamp01(_dwell / Mathf.Max(0.01f, dwellTime)));

            if (_dwell >= dwellTime)
            {
                Select(target);
                _dwell = 0f;
            }
        }

        void Select(EEGSelectable target)
        {
            // Clear every other selectable, not just the tracked one. Selection state can
            // otherwise be left behind on an object (e.g. one selected before this
            // interactor started), leaving it stuck in the selected color.
            foreach (var s in FindObjectsByType<EEGSelectable>(FindObjectsSortMode.None))
            {
                if (s == target || !s.IsSelected) continue;
                s.SetSelected(false);
                if (s.TryGetComponent<EEGMover>(out var prevMover)) prevMover.Release();
            }
            Selected = target;
            Selected.SetSelected(true);
            _log.Success($"Selected '{Selected.name}'");

            if (Selected.TryGetComponent<EEGMover>(out var mover)) mover.CaptureRest();
        }

        void UpdateMovePhase()
        {
            IsDriving = false;
            if (phase != Phase.Move || Selected == null) return;
            if (!Selected.TryGetComponent<EEGMover>(out var mover)) return;

            // Gaze gates the drive: look away and the object stops responding, so you
            // can rest your focus without the selection drifting off on its own.
            bool gazeOnSelected = !requireGazeToMove || Hovered == Selected;
            if (gazeOnSelected)
            {
                IsDriving = true;
                mover.SetControl(ControlValue);
            }
            else if (releaseToRestOnGazeLoss)
            {
                mover.Release();
            }
            else
            {
                mover.Hold();
            }
        }
    }
}
