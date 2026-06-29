using UnityEngine;
using OpenBCI.Logging;

namespace OpenBCI.Core
{
    /// <summary>The five classic EEG frequency bands.</summary>
    public enum EEGBand { Delta, Theta, Alpha, Beta, Gamma }

    /// <summary>Distinct colors per band so editor logs are easy to read at a glance.</summary>
    public static class EEGColors
    {
        public static readonly Color Delta = new(0.65f, 0.45f, 1.00f); // violet
        public static readonly Color Theta = new(0.35f, 0.65f, 1.00f); // blue
        public static readonly Color Alpha = new(0.40f, 0.85f, 0.45f); // green
        public static readonly Color Beta  = new(1.00f, 0.70f, 0.20f); // amber
        public static readonly Color Gamma = new(1.00f, 0.40f, 0.55f); // pink/red

        public static Color For(EEGBand band) => band switch
        {
            EEGBand.Delta => Delta,
            EEGBand.Theta => Theta,
            EEGBand.Alpha => Alpha,
            EEGBand.Beta  => Beta,
            EEGBand.Gamma => Gamma,
            _ => Color.white
        };

        /// <summary>Rich-text "Name=value" tag in the band's color, e.g. for a log line.</summary>
        public static string Tag(EEGBand band, float value) =>
            OpenBCILogger.Colorize($"{band}={value:F2}", For(band));
    }

    /// <summary>
    /// A control signal derivable from the bands. Raw bands, plus two common
    /// composite indices used in BCI:
    ///   RelaxIndex  = alpha / (alpha + beta)        -> high when relaxed
    ///   Engagement  = beta  / (alpha + theta)       -> high when concentrating
    /// </summary>
    public enum EEGMetric { Delta, Theta, Alpha, Beta, Gamma, RelaxIndex, Engagement }

    /// <summary>One decoded packet from the bridge. Band powers are in log10 power.</summary>
    public struct EEGFeatures
    {
        public double Timestamp;
        public float Delta, Theta, Alpha, Beta, Gamma;
        public float[] Raw;   // latest raw µV sample, one entry per EEG channel
    }

    /// <summary>
    /// Converts a noisy, person-specific raw metric into a stable [0,1] control value
    /// using an exponential moving average of the mean and variance. 0.5 ≈ your recent
    /// average; 0 / 1 ≈ a couple of standard deviations below / above it.
    ///
    /// This is what lets a fixed UI threshold (e.g. "confirm at 0.7") work across
    /// different people and sessions without manual calibration.
    /// </summary>
    public class AdaptiveNormalizer
    {
        readonly float _alpha;       // EMA smoothing factor (0..1), smaller = slower
        readonly float _spreadStds;  // how many std devs map to the 0..1 edges
        float _mean;
        float _var;
        bool _seeded;

        public AdaptiveNormalizer(float smoothing = 0.02f, float spreadStds = 2f)
        {
            _alpha = Mathf.Clamp01(smoothing);
            _spreadStds = Mathf.Max(0.01f, spreadStds);
        }

        /// <summary>The last normalized output, in [0,1].</summary>
        public float Value { get; private set; } = 0.5f;

        public void Reset()
        {
            _seeded = false;
            _mean = _var = 0f;
            Value = 0.5f;
        }

        /// <summary>Feed a new raw sample, get back a calibrated [0,1] value.</summary>
        public float Normalize(float raw)
        {
            if (float.IsNaN(raw) || float.IsInfinity(raw)) return Value;

            if (!_seeded)
            {
                _mean = raw;
                _var = 0f;
                _seeded = true;
                Value = 0.5f;
                return Value;
            }

            float delta = raw - _mean;
            _mean += _alpha * delta;
            // EMA of variance (West's incremental form)
            _var = (1f - _alpha) * (_var + _alpha * delta * delta);

            float std = Mathf.Sqrt(Mathf.Max(_var, 1e-12f));
            float z = (raw - _mean) / std;                 // standard score
            Value = Mathf.Clamp01(0.5f + z / (2f * _spreadStds));
            return Value;
        }
    }
}
