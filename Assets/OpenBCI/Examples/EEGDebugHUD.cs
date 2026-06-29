using UnityEngine;
using OpenBCI.Core;
using OpenBCI.Interaction;

namespace OpenBCI.Examples
{
    /// <summary>
    /// On-screen IMGUI debug overlay (Game view / desktop). Shows live band powers
    /// with their band colors, the composite metrics, and the interactor state
    /// (control value, confirm threshold, hover/selection, dwell charge).
    ///
    /// This is a flat-screen debug aid for development on the Mac/editor — it does
    /// not render inside the headset. Drop on any GameObject.
    /// </summary>
    public class EEGDebugHUD : MonoBehaviour
    {
        [Header("References (auto-found if empty)")]
        public OpenBCIReceiver receiver;
        public GazeEEGInteractor interactor;

        [Header("Layout")]
        public Vector2 origin = new(16, 16);
        public float width = 420f;
        [Tooltip("Overall UI scale for high-DPI / large displays.")]
        public float scale = 1.4f;

        [Header("Band Bar Range (log10 power)")]
        public float bandMin = -1f;
        public float bandMax = 5f;

        Texture2D _tex;
        GUIStyle _label, _value, _header;
        float _builtScale = -1f;

        void Awake()
        {
            _tex = new Texture2D(1, 1);
            _tex.SetPixel(0, 0, Color.white);
            _tex.Apply();
        }

        void ResolveRefs()
        {
            // re-find each frame until resolved — handles refs created after Awake
            // (e.g. the interactor that EEGCubeDemo spawns in Start()).
            if (receiver == null) receiver = FindAnyObjectByType<OpenBCIReceiver>();
            if (interactor == null) interactor = FindAnyObjectByType<GazeEEGInteractor>();
        }

        void EnsureStyles()
        {
            if (_label != null && Mathf.Approximately(_builtScale, scale)) return;
            _builtScale = scale;
            _label  = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(14 * scale), fontStyle = FontStyle.Bold };
            _value  = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(20 * scale), fontStyle = FontStyle.Bold };
            _header = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(16 * scale), fontStyle = FontStyle.Bold };
        }

        void OnGUI()
        {
            ResolveRefs();
            EnsureStyles();
            float pad = 10 * scale;
            float x = origin.x + pad;
            float y = origin.y + pad;
            float w = width * scale;
            float rowH = 30 * scale;

            // background panel (sized to fit all rows incl. interactor section)
            float panelH = (15 * rowH) + pad * 2;
            DrawRect(new Rect(origin.x, origin.y, w + pad * 2, panelH), new Color(0, 0, 0, 0.78f));

            // ── connection status ───────────────────────────────────────────
            bool connected = receiver != null && receiver.HasData;
            GUI.color = connected ? new Color(0.4f, 0.85f, 0.4f) : new Color(1f, 0.4f, 0.4f);
            GUI.Label(new Rect(x, y, w, rowH), connected ? "● EEG STREAM LIVE" : "● NO DATA", _header);
            GUI.color = Color.white;
            y += rowH * 1.2f;

            // ── band powers ─────────────────────────────────────────────────
            DrawBand(ref x, ref y, w, rowH, EEGBand.Delta, receiver != null ? receiver.Delta : 0);
            DrawBand(ref x, ref y, w, rowH, EEGBand.Theta, receiver != null ? receiver.Theta : 0);
            DrawBand(ref x, ref y, w, rowH, EEGBand.Alpha, receiver != null ? receiver.Alpha : 0);
            DrawBand(ref x, ref y, w, rowH, EEGBand.Beta,  receiver != null ? receiver.Beta  : 0);
            DrawBand(ref x, ref y, w, rowH, EEGBand.Gamma, receiver != null ? receiver.Gamma : 0);
            y += rowH * 0.3f;

            // ── composite metrics ───────────────────────────────────────────
            if (receiver != null)
            {
                DrawMeter(ref x, ref y, w, rowH, "Relax",   receiver.GetMetricRaw(EEGMetric.RelaxIndex), new Color(0.4f, 0.85f, 0.45f), false);
                DrawMeter(ref x, ref y, w, rowH, "Engage",  receiver.GetMetricRaw(EEGMetric.Engagement), new Color(1f, 0.7f, 0.2f), false);
            }

            // ── interactor state ────────────────────────────────────────────
            if (interactor != null)
            {
                y += rowH * 0.3f;
                GUI.Label(new Rect(x, y, w, rowH), $"Phase: {interactor.phase}   Metric: {interactor.controlMetric}", _label);
                y += rowH;

                // control value with threshold marker
                DrawMeter(ref x, ref y, w, rowH, "Control", interactor.ControlValue, new Color(0.45f, 0.78f, 1f), true,
                          interactor.confirmThreshold);

                string hov = interactor.Hovered != null ? interactor.Hovered.name : "—";
                string sel = interactor.Selected != null ? interactor.Selected.name : "—";
                GUI.Label(new Rect(x, y, w, rowH), $"Hover: {hov}", _label); y += rowH;
                GUI.color = new Color(1f, 0.7f, 0.3f);
                GUI.Label(new Rect(x, y, w, rowH), $"Selected: {sel}", _label);
                GUI.color = Color.white;
                y += rowH;

                if (interactor.Hovered != null)
                    DrawMeter(ref x, ref y, w, rowH, "Charge", interactor.Hovered.ChargeProgress, new Color(1f, 0.55f, 0.15f), false);
            }
        }

        void DrawBand(ref float x, ref float y, float w, float rowH, EEGBand band, float logValue)
        {
            Color c = EEGColors.For(band);
            float labelW = 70 * scale;
            float valueW = 90 * scale;
            float barX = x + labelW;
            float barW = w - labelW - valueW;

            GUI.color = c;
            GUI.Label(new Rect(x, y, labelW, rowH), band.ToString(), _label);

            // bar
            float t = Mathf.InverseLerp(bandMin, bandMax, logValue);
            DrawRect(new Rect(barX, y + rowH * 0.25f, barW, rowH * 0.5f), new Color(1, 1, 1, 0.12f));
            GUI.color = c;
            DrawRect(new Rect(barX, y + rowH * 0.25f, barW * Mathf.Clamp01(t), rowH * 0.5f), c);

            // big value
            GUI.Label(new Rect(barX + barW + 6, y, valueW, rowH), logValue.ToString("F2"), _value);
            GUI.color = Color.white;
            y += rowH;
        }

        void DrawMeter(ref float x, ref float y, float w, float rowH, string name, float value01,
                       Color c, bool showThreshold, float threshold = 0f)
        {
            float labelW = 90 * scale;
            float valueW = 90 * scale;
            float barX = x + labelW;
            float barW = w - labelW - valueW;
            float v = Mathf.Clamp01(value01);

            GUI.color = Color.white;
            GUI.Label(new Rect(x, y, labelW, rowH), name, _label);

            DrawRect(new Rect(barX, y + rowH * 0.2f, barW, rowH * 0.6f), new Color(1, 1, 1, 0.12f));
            GUI.color = c;
            DrawRect(new Rect(barX, y + rowH * 0.2f, barW * v, rowH * 0.6f), c);

            if (showThreshold)
            {
                GUI.color = Color.white;
                float tx = barX + barW * Mathf.Clamp01(threshold);
                DrawRect(new Rect(tx - 1, y + rowH * 0.1f, 2 * scale, rowH * 0.8f), Color.white);
            }

            GUI.color = Color.white;
            GUI.Label(new Rect(barX + barW + 6, y, valueW, rowH), v.ToString("F2"), _value);
            y += rowH;
        }

        void DrawRect(Rect r, Color c)
        {
            GUI.color = c;
            GUI.DrawTexture(r, _tex);
            GUI.color = Color.white;
        }
    }
}
