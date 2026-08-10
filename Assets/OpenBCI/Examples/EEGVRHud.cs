using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using OpenBCI.Core;
using OpenBCI.Interaction;

namespace OpenBCI.Examples
{
    /// <summary>
    /// In-headset HUD showing the live EEG state: band powers, composite metrics,
    /// the normalized control value against its confirm threshold, and the current
    /// hover/selection.
    ///
    /// This exists because <see cref="EEGDebugHUD"/> draws with IMGUI (OnGUI), which
    /// only ever reaches the Game view — it is never composited into the headset.
    /// This version builds a world-space Canvas instead, so it renders in VR.
    ///
    /// The panel is not rigidly parented to the camera. It lag-follows with a dead
    /// zone, which is far more comfortable than a helmet-locked overlay: small head
    /// movements leave it alone, and larger ones let it drift back into view.
    ///
    /// Drop on any GameObject; everything is constructed at runtime.
    /// </summary>
    public class EEGVRHud : MonoBehaviour
    {
        [Header("References (auto-found if empty)")]
        public OpenBCIReceiver receiver;
        public GazeEEGInteractor interactor;
        [Tooltip("Camera the HUD positions itself against. Defaults to Camera.main (the XR camera).")]
        public Camera targetCamera;

        [Header("Placement")]
        [Tooltip("Metres in front of the eyes.")]
        public float distance = 1.6f;
        [Tooltip("Metres below eye level. Keeps the HUD out of the way of the cubes.")]
        public float verticalOffset = -0.35f;
        [Tooltip("Metres to the right of centre. 0 = dead ahead.")]
        public float horizontalOffset = 0f;
        [Tooltip("Panel width in metres.")]
        public float panelWidth = 0.85f;

        [Header("Follow Comfort")]
        [Tooltip("Degrees of head turn tolerated before the HUD starts following.")]
        public float deadZoneAngle = 12f;
        [Tooltip("Higher = the HUD catches up faster. Low values feel calmer.")]
        public float followSpeed = 2.5f;
        [Tooltip("Rigidly lock to the head instead of lag-following. Less comfortable.")]
        public bool hardLock = false;

        [Header("Band Bar Range (log10 power)")]
        public float bandMin = -1f;
        public float bandMax = 5f;

        [Header("Visibility")]
        public bool visible = true;

        // ── built UI ─────────────────────────────────────────────────────────
        Canvas _canvas;
        RectTransform _root;
        TextMeshProUGUI _status, _phaseLine, _calibLine, _hoverLine, _selectedLine;
        readonly List<BarRow> _bandRows = new();
        BarRow _relaxRow, _engageRow, _controlRow, _chargeRow;
        RectTransform _thresholdMark;

        Vector3 _targetPos;
        Quaternion _targetRot;
        bool _placed;

        const float PxPerRow = 46f;
        const float PanelPx = 720f;

        class BarRow
        {
            public TextMeshProUGUI Label;
            public RectTransform Fill;
            public RectTransform Track;
            public TextMeshProUGUI Value;

            public void Set(float fill01, string valueText, Color c)
            {
                float w = Track.rect.width * Mathf.Clamp01(fill01);
                Fill.sizeDelta = new Vector2(w, Fill.sizeDelta.y);
                var img = Fill.GetComponent<Image>();
                if (img != null) img.color = c;
                Value.text = valueText;
            }
        }

        void Awake()
        {
            Build();
        }

        void ResolveRefs()
        {
            if (receiver == null) receiver = FindAnyObjectByType<OpenBCIReceiver>();
            if (interactor == null) interactor = FindAnyObjectByType<GazeEEGInteractor>();
            if (targetCamera == null) targetCamera = Camera.main;
        }

        void LateUpdate()
        {
            ResolveRefs();
            if (_canvas == null) return;

            if (_canvas.enabled != visible) _canvas.enabled = visible;
            if (!visible) return;

            Follow();
            Refresh();
        }

        // ── placement ────────────────────────────────────────────────────────
        void Follow()
        {
            if (targetCamera == null) return;
            var cam = targetCamera.transform;

            // Anchor to a yaw-only frame: pitching your head shouldn't drag the HUD up
            // and down, which is the main source of discomfort with head-locked UI.
            Vector3 flatForward = Vector3.ProjectOnPlane(cam.forward, Vector3.up);
            if (flatForward.sqrMagnitude < 1e-4f) flatForward = Vector3.forward;
            flatForward.Normalize();
            Quaternion yaw = Quaternion.LookRotation(flatForward, Vector3.up);

            Vector3 desiredPos = cam.position
                               + flatForward * distance
                               + Vector3.up * verticalOffset
                               + (yaw * Vector3.right) * horizontalOffset;
            Quaternion desiredRot = yaw;

            if (!_placed || hardLock)
            {
                _targetPos = desiredPos;
                _targetRot = desiredRot;
                _placed = true;
                _root.position = _targetPos;
                _root.rotation = _targetRot;
                return;
            }

            // Only re-target once the head has turned past the dead zone.
            Vector3 toHud = Vector3.ProjectOnPlane(_root.position - cam.position, Vector3.up);
            if (toHud.sqrMagnitude > 1e-4f &&
                Vector3.Angle(flatForward, toHud.normalized) > deadZoneAngle)
            {
                _targetPos = desiredPos;
                _targetRot = desiredRot;
            }

            float a = 1f - Mathf.Exp(-followSpeed * Time.deltaTime);
            _root.position = Vector3.Lerp(_root.position, _targetPos, a);
            _root.rotation = Quaternion.Slerp(_root.rotation, _targetRot, a);
        }

        // ── live values ──────────────────────────────────────────────────────
        void Refresh()
        {
            bool connected = receiver != null && receiver.HasData;
            _status.text = connected ? "● EEG STREAM LIVE" : "● NO DATA";
            _status.color = connected ? new Color(0.4f, 0.85f, 0.4f) : new Color(1f, 0.4f, 0.4f);

            for (int i = 0; i < _bandRows.Count; i++)
            {
                var band = (EEGBand)i;
                float v = receiver != null ? BandValue(band) : 0f;
                _bandRows[i].Set(Mathf.InverseLerp(bandMin, bandMax, v), v.ToString("F2"), EEGColors.For(band));
            }

            if (receiver != null)
            {
                float relax = receiver.GetMetricRaw(EEGMetric.RelaxIndex);
                float engage = receiver.GetMetricRaw(EEGMetric.Engagement);
                _relaxRow.Set(Mathf.Clamp01(relax), relax.ToString("F2"), new Color(0.4f, 0.85f, 0.45f));
                // Engagement is an unbounded ratio, so show the bar against a soft
                // ceiling of 2 rather than clamping everything above 1 to a full bar.
                _engageRow.Set(Mathf.Clamp01(engage / 2f), engage.ToString("F2"), new Color(1f, 0.7f, 0.2f));
            }

            if (interactor == null)
            {
                _phaseLine.text = "No interactor in scene";
                _calibLine.gameObject.SetActive(false);
                return;
            }

            _phaseLine.text = $"Phase {interactor.phase}   •   {interactor.controlMetric}" +
                              (interactor.invertControl ? "  (inverted)" : "");

            bool calibrating = interactor.IsCalibrating;
            _calibLine.gameObject.SetActive(calibrating);
            if (calibrating)
                _calibLine.text = $"CALIBRATING — rest…  {interactor.CalibrationProgress * 100f:F0}%";

            _controlRow.Set(interactor.ControlValue, interactor.ControlValue.ToString("F2"),
                            new Color(0.45f, 0.78f, 1f));
            var tp = _thresholdMark.anchoredPosition;
            _thresholdMark.anchoredPosition =
                new Vector2(_controlRow.Track.rect.width * Mathf.Clamp01(interactor.confirmThreshold), tp.y);

            _hoverLine.text = "Hover:  " + (interactor.Hovered != null ? interactor.Hovered.name : "—");
            _selectedLine.text = "Select: " + (interactor.Selected != null ? interactor.Selected.name : "—")
                               + (interactor.IsDriving ? "   ▲ driving" : "");

            float charge = interactor.Hovered != null ? interactor.Hovered.ChargeProgress : 0f;
            _chargeRow.Set(charge, charge.ToString("F2"), new Color(1f, 0.55f, 0.15f));
        }

        float BandValue(EEGBand band) => band switch
        {
            EEGBand.Delta => receiver.Delta,
            EEGBand.Theta => receiver.Theta,
            EEGBand.Alpha => receiver.Alpha,
            EEGBand.Beta  => receiver.Beta,
            EEGBand.Gamma => receiver.Gamma,
            _ => 0f
        };

        // ── construction ─────────────────────────────────────────────────────
        void Build()
        {
            var go = new GameObject("EEG VR HUD (World Space)");
            go.transform.SetParent(transform, false);

            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            go.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 3f;

            _root = _canvas.GetComponent<RectTransform>();
            _root.sizeDelta = new Vector2(PanelPx, PxPerRow * 15f);
            // Scale the pixel-space canvas down to the requested physical width.
            float s = panelWidth / PanelPx;
            _root.localScale = new Vector3(s, s, s);

            var bg = NewImage(_root, "Background", new Color(0.02f, 0.03f, 0.06f, 0.88f));
            Stretch(bg.rectTransform);

            float y = -18f;
            _status = NewText(_root, "Status", 26, FontStyles.Bold, ref y, PanelPx - 36f);
            y -= 6f;

            for (int i = 0; i < 5; i++)
                _bandRows.Add(NewBarRow(_root, ((EEGBand)i).ToString(), ref y));

            y -= 8f;
            _relaxRow  = NewBarRow(_root, "Relax", ref y);
            _engageRow = NewBarRow(_root, "Engage", ref y);

            y -= 8f;
            _phaseLine = NewText(_root, "Phase", 20, FontStyles.Normal, ref y, PanelPx - 36f);
            _calibLine = NewText(_root, "Calib", 22, FontStyles.Bold, ref y, PanelPx - 36f);
            _calibLine.color = new Color(1f, 0.85f, 0.3f);

            _controlRow = NewBarRow(_root, "Control", ref y);
            // threshold tick, parented to the control bar's track
            var mark = NewImage(_controlRow.Track, "Threshold", Color.white);
            _thresholdMark = mark.rectTransform;
            _thresholdMark.anchorMin = new Vector2(0f, 0.5f);
            _thresholdMark.anchorMax = new Vector2(0f, 0.5f);
            _thresholdMark.pivot = new Vector2(0.5f, 0.5f);
            _thresholdMark.sizeDelta = new Vector2(3f, 28f);

            _hoverLine    = NewText(_root, "Hover", 20, FontStyles.Normal, ref y, PanelPx - 36f);
            _selectedLine = NewText(_root, "Selected", 20, FontStyles.Bold, ref y, PanelPx - 36f);
            _selectedLine.color = new Color(1f, 0.7f, 0.3f);

            _chargeRow = NewBarRow(_root, "Charge", ref y);

            // size the panel to whatever we actually laid out
            _root.sizeDelta = new Vector2(PanelPx, -y + 18f);
        }

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        static Image NewImage(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        static TextMeshProUGUI NewText(Transform parent, string name, float size,
                                       FontStyles style, ref float y, float width)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.fontSize = size;
            t.fontStyle = style;
            t.color = Color.white;
            t.alignment = TextAlignmentOptions.MidlineLeft;
            t.raycastTarget = false;

            var rt = t.rectTransform;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(18f, y);
            rt.sizeDelta = new Vector2(width, PxPerRow * 0.85f);

            y -= PxPerRow * 0.8f;
            return t;
        }

        BarRow NewBarRow(Transform parent, string label, ref float y)
        {
            const float labelW = 120f;
            const float valueW = 100f;
            float barW = PanelPx - 36f - labelW - valueW;

            float rowY = y;
            var row = new BarRow();

            row.Label = NewText(parent, label + " Label", 20, FontStyles.Bold, ref rowY, labelW);
            row.Label.text = label;

            // track
            var track = NewImage(parent, label + " Track", new Color(1f, 1f, 1f, 0.12f));
            var trackRt = track.rectTransform;
            trackRt.anchorMin = new Vector2(0f, 1f);
            trackRt.anchorMax = new Vector2(0f, 1f);
            trackRt.pivot = new Vector2(0f, 1f);
            trackRt.anchoredPosition = new Vector2(18f + labelW, y - PxPerRow * 0.28f);
            trackRt.sizeDelta = new Vector2(barW, PxPerRow * 0.42f);
            row.Track = trackRt;

            // fill
            var fill = NewImage(trackRt, label + " Fill", Color.white);
            var fillRt = fill.rectTransform;
            fillRt.anchorMin = new Vector2(0f, 0f);
            fillRt.anchorMax = new Vector2(0f, 1f);
            fillRt.pivot = new Vector2(0f, 0.5f);
            fillRt.anchoredPosition = Vector2.zero;
            fillRt.sizeDelta = new Vector2(0f, 0f);
            row.Fill = fillRt;

            float valueY = y;
            row.Value = NewText(parent, label + " Value", 22, FontStyles.Bold, ref valueY, valueW);
            row.Value.rectTransform.anchoredPosition = new Vector2(18f + labelW + barW + 10f, y);

            y -= PxPerRow * 0.8f;
            return row;
        }
    }
}
