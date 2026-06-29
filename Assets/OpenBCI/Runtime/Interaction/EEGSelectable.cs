using UnityEngine;

namespace OpenBCI.Interaction
{
    /// <summary>
    /// Marks an object as gaze-targetable and EEG-selectable. Provides visual
    /// feedback for three states: hovered (gaze on it), charging (EEG confirm in
    /// progress), and selected. Requires a Collider for the gaze raycast.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class EEGSelectable : MonoBehaviour
    {
        [Header("Feedback Colors")]
        public Color normalColor   = new(0.55f, 0.55f, 0.60f);
        public Color hoverColor    = new(0.30f, 0.65f, 1.00f);
        public Color selectedColor = new(1.00f, 0.55f, 0.15f);

        [Tooltip("Emission strength applied while charging / selected.")]
        public float emissionBoost = 2f;

        public bool IsHovered { get; private set; }
        public bool IsSelected { get; private set; }
        /// <summary>0..1 confirm progress while the user holds focus on this object.</summary>
        public float ChargeProgress { get; private set; }

        Renderer _renderer;
        MaterialPropertyBlock _mpb;
        static readonly int ColorId    = Shader.PropertyToID("_BaseColor");
        static readonly int ColorIdStd = Shader.PropertyToID("_Color");
        static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        void Awake()
        {
            _renderer = GetComponentInChildren<Renderer>();
            _mpb = new MaterialPropertyBlock();
            Apply();
        }

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
            if (selected) ChargeProgress = 1f;
            Apply();
        }

        void Apply()
        {
            if (_renderer == null) return;

            Color baseCol = normalColor;
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
    }
}
