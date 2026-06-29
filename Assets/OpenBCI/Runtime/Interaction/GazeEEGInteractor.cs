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
        [Tooltip("EMA smoothing for the adaptive normalizer (smaller = slower calibration).")]
        public float normalizerSmoothing = 0.02f;

        [Header("Behaviour")]
        [Tooltip("Looking away from the selected cube and re-confirming on another switches selection.")]
        public bool allowReselect = true;

        [Header("Testing")]
        [Tooltip("When true, ControlValue comes from ManualControl instead of EEG — lets you simulate focus without electrodes.")]
        public bool useManualControl = false;
        [Range(0f, 1f)] public float manualControl = 0f;

        // ── exposed runtime state (handy for UI / debugging) ─────────────────
        public float ControlValue { get; private set; }   // normalized 0..1
        public EEGSelectable Hovered { get; private set; }
        public EEGSelectable Selected { get; private set; }

        IGazeProvider _gaze;
        AdaptiveNormalizer _normalizer;
        ScopedLogger _log;
        float _dwell;

        void Awake()
        {
            _log = OpenBCILogger.Scope(Cat);
            _normalizer = new AdaptiveNormalizer(normalizerSmoothing);

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

        void UpdateControlValue()
        {
            if (useManualControl)
            {
                ControlValue = Mathf.Clamp01(manualControl);
                return;
            }
            if (receiver == null || !receiver.HasData) return;
            float raw = receiver.GetMetricRaw(controlMetric);
            ControlValue = _normalizer.Normalize(raw);
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
            bool focused = ControlValue >= confirmThreshold;

            // Already selected this target: nothing to charge.
            bool targetIsSelected = target != null && target == Selected;

            bool canCharge = target != null && !targetIsSelected &&
                             (Selected == null || allowReselect);

            if (canCharge && focused)
            {
                _dwell += Time.deltaTime;
                target.SetCharge(Mathf.Clamp01(_dwell / dwellTime));
                if (_dwell >= dwellTime)
                {
                    Select(target);
                    _dwell = 0f;
                }
            }
            else
            {
                _dwell = 0f;
                if (target != null && !targetIsSelected) target.SetCharge(0f);
            }
        }

        void Select(EEGSelectable target)
        {
            if (Selected != null && Selected != target)
            {
                Selected.SetSelected(false);
                if (Selected.TryGetComponent<EEGMover>(out var prevMover)) prevMover.Release();
            }
            Selected = target;
            Selected.SetSelected(true);
            _log.Success($"Selected '{Selected.name}'");

            if (Selected.TryGetComponent<EEGMover>(out var mover)) mover.CaptureRest();
        }

        void UpdateMovePhase()
        {
            if (phase != Phase.Move || Selected == null) return;
            if (Selected.TryGetComponent<EEGMover>(out var mover))
                mover.SetControl(ControlValue);
        }
    }
}
