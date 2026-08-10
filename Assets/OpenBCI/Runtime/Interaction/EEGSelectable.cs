using UnityEngine;

namespace OpenBCI.Interaction
{
    /// <summary>
    /// Marks an object as gaze-targetable and EEG-selectable. Provides visual
    /// feedback for three states: hovered (gaze on it), charging (EEG confirm in
    /// progress), and selected. Requires a Collider for the gaze raycast.
    ///
    /// The "normal" appearance is captured from the object's own material at Awake,
    /// so deselecting always returns the object to how it originally looked rather
    /// than to a hardcoded grey.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class EEGSelectable : MonoBehaviour
    {
        [Header("Feedback Colors")]
        [Tooltip("Leave off to use the object's own material color as its resting look.")]
        public bool overrideNormalColor = false;
        [Tooltip("Only used when Override Normal Color is on.")]
        public Color normalColor   = new(0.55f, 0.55f, 0.60f);
        public Color hoverColor    = new(0.30f, 0.65f, 1.00f);
        public Color selectedColor = new(1.00f, 0.55f, 0.15f);

        [Tooltip("Emission strength applied while charging / selected.")]
        public float emissionBoost = 2f;

        [Header("Gaze Outline")]
        [Tooltip("Draw a colored outline hull around this object while gaze rests on it.")]
        public bool showOutline = true;
        public Color outlineColor = new(0.30f, 0.80f, 1.00f);
        [Tooltip("Outline thickness as a fraction of the object's size.")]
        [Range(0.001f, 0.2f)] public float outlineWidth = 0.04f;
        [Tooltip("Outline color once this object is the active selection.")]
        public Color outlineSelectedColor = new(1.00f, 0.55f, 0.15f);

        public bool IsHovered { get; private set; }
        public bool IsSelected { get; private set; }
        /// <summary>0..1 confirm progress while the user holds focus on this object.</summary>
        public float ChargeProgress { get; private set; }

        Renderer _renderer;
        MaterialPropertyBlock _mpb;
        Color _capturedNormalColor = new(0.55f, 0.55f, 0.60f);

        GameObject _outlineGO;
        Renderer _outlineRenderer;
        MaterialPropertyBlock _outlineMpb;

        static readonly int ColorId    = Shader.PropertyToID("_BaseColor");
        static readonly int ColorIdStd = Shader.PropertyToID("_Color");
        static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        void Awake()
        {
            _renderer = GetComponentInChildren<Renderer>();
            _mpb = new MaterialPropertyBlock();
            CaptureNormalColor();
            BuildOutline();
            Apply();
        }

        /// <summary>Read the object's authored color so we can restore it on deselect.</summary>
        void CaptureNormalColor()
        {
            if (overrideNormalColor) { _capturedNormalColor = normalColor; return; }
            if (_renderer == null || _renderer.sharedMaterial == null) return;

            var mat = _renderer.sharedMaterial;
            if (mat.HasProperty(ColorId))         _capturedNormalColor = mat.GetColor(ColorId);
            else if (mat.HasProperty(ColorIdStd)) _capturedNormalColor = mat.GetColor(ColorIdStd);
        }

        /// <summary>The color this object returns to when it is neither hovered nor selected.</summary>
        public Color NormalColor => overrideNormalColor ? normalColor : _capturedNormalColor;

        // ── outline ──────────────────────────────────────────────────────────
        // Inverted-hull outline: a slightly enlarged copy of the mesh drawn with
        // front-face culling, so only the back faces peek out around the silhouette.
        // Uses URP/Unlit (which exposes _Cull) rather than a custom shader.
        void BuildOutline()
        {
            if (!showOutline) return;
            var srcFilter = _renderer != null ? _renderer.GetComponent<MeshFilter>() : null;
            if (srcFilter == null || srcFilter.sharedMesh == null) return;

            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) return;

            _outlineGO = new GameObject("GazeOutline");
            _outlineGO.transform.SetParent(_renderer.transform, false);
            _outlineGO.transform.localPosition = Vector3.zero;
            _outlineGO.transform.localRotation = Quaternion.identity;
            _outlineGO.transform.localScale = Vector3.one * (1f + outlineWidth);

            _outlineGO.AddComponent<MeshFilter>().sharedMesh = srcFilter.sharedMesh;
            _outlineRenderer = _outlineGO.AddComponent<MeshRenderer>();

            var mat = new Material(shader) { name = "GazeOutline (Instance)" };
            if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Front);
            _outlineRenderer.sharedMaterial = mat;
            _outlineRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _outlineRenderer.receiveShadows = false;

            _outlineMpb = new MaterialPropertyBlock();
            _outlineGO.SetActive(false);
        }

        void ApplyOutline()
        {
            if (_outlineRenderer == null) return;

            bool visible = showOutline && (IsHovered || IsSelected);
            if (_outlineGO.activeSelf != visible) _outlineGO.SetActive(visible);
            if (!visible) return;

            Color c = IsSelected
                ? outlineSelectedColor
                : Color.Lerp(outlineColor, outlineSelectedColor, ChargeProgress);

            _outlineRenderer.GetPropertyBlock(_outlineMpb);
            _outlineMpb.SetColor(ColorId, c);
            _outlineMpb.SetColor(ColorIdStd, c);
            _outlineRenderer.SetPropertyBlock(_outlineMpb);
        }

        // ── state ────────────────────────────────────────────────────────────
        public void SetHover(bool hovered)
        {
            if (IsHovered == hovered) return;
            IsHovered = hovered;
            if (!hovered) ChargeProgress = 0f;
            Apply();
        }

        public void SetCharge(float progress01)
        {
            ChargeProgress = Mathf.Clamp01(progress01);
            Apply();
        }

        public void SetSelected(bool selected)
        {
            if (IsSelected == selected) return;
            IsSelected = selected;
            // Clearing charge on deselect matters: a stale ChargeProgress of 1 would
            // otherwise keep a still-hovered cube lerped all the way to selectedColor,
            // leaving it orange after another cube took the selection.
            ChargeProgress = selected ? 1f : 0f;
            Apply();
        }

        void Apply()
        {
            ApplyOutline();
            if (_renderer == null) return;

            Color baseCol = NormalColor;
            if (IsSelected)      baseCol = selectedColor;
            else if (IsHovered)  baseCol = Color.Lerp(hoverColor, selectedColor, ChargeProgress);

            _renderer.GetPropertyBlock(_mpb);
            _mpb.SetColor(ColorId, baseCol);
            _mpb.SetColor(ColorIdStd, baseCol); // covers Built-in/Standard shader too

            float glow = IsSelected ? emissionBoost
                       : IsHovered  ? emissionBoost * ChargeProgress
                       : 0f;
            _mpb.SetColor(EmissionId, baseCol * glow);
            _renderer.SetPropertyBlock(_mpb);
        }

        void OnDestroy()
        {
            if (_outlineRenderer != null && _outlineRenderer.sharedMaterial != null)
                Destroy(_outlineRenderer.sharedMaterial);
        }
    }
}
