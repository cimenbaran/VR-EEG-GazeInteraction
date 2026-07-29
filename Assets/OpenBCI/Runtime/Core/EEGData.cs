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
    public enum NormalizerMode
    {
        /// <summary>
        /// Baseline is measured for a fixed period, then FROZEN. A sustained rise in the
        /// raw metric produces a sustained high output. Use this for control.
        /// </summary>
        Calibrated = 0,

        /// <summary>
        /// Baseline continuously chases the signal, so the output reflects *change*
        /// rather than level — a sustained high signal decays back toward 0.5.
        /// Useful for exploring a signal, wrong for holding a control.
        /// </summary>
        Adaptive = 1,

        /// <summary>
        /// No statistics: the raw metric is mapped linearly from [rawMin, rawMax] to [0,1].
        /// Fully predictable and direct, but you must pick the range for your setup.
        /// </summary>
        FixedRange = 2
    }

    /// <summary>
    /// Converts a noisy, person-specific raw metric into a stable [0,1] control value.
    ///
    /// The default <see cref="NormalizerMode.Calibrated"/> mode measures your resting
    /// baseline for a few seconds and then locks it, so holding an elevated metric holds
    /// an elevated output (unlike a continuously-adapting baseline, which drifts back to
    /// the middle). Output smoothing is applied on top to remove frame-to-frame jitter.
    /// </summary>
    public class SignalNormalizer
    {
        public NormalizerMode Mode = NormalizerMode.Calibrated;

        /// <summary>Seconds of resting data collected before the baseline is frozen.</summary>
        public float CalibrationDuration = 5f;

        /// <summary>How many standard deviations above/below baseline map to 1 / 0.</summary>
        public float SpreadStds = 1.5f;

        /// <summary>Smoothing time constant (seconds) applied to the output. 0 disables.</summary>
        public float OutputSmoothing = 0.4f;

        /// <summary>Range used by <see cref="NormalizerMode.FixedRange"/>.</summary>
        public float RawMin = 0f, RawMax = 1f;

        /// <summary>Adaptation rate used by <see cref="NormalizerMode.Adaptive"/>.</summary>
        public float AdaptiveRate = 0.02f;

        // baseline statistics (Welford accumulation during calibration)
        double _mean, _m2;
        int _count;
        float _frozenMean, _frozenStd;

        public bool IsCalibrating { get; private set; } = true;
        public float CalibrationProgress { get; private set; }
        /// <summary>Last smoothed output, in [0,1].</summary>
        public float Value { get; private set; } = 0.5f;
        /// <summary>Output before smoothing, in [0,1].</summary>
        public float RawValue { get; private set; } = 0.5f;

        float _elapsed;

        /// <summary>Discard the baseline and start measuring it again.</summary>
        public void Recalibrate()
        {
            _mean = _m2 = 0;
            _count = 0;
            _elapsed = 0f;
            _frozenMean = 0f;
            _frozenStd = 0f;
            IsCalibrating = true;
            CalibrationProgress = 0f;
            Value = RawValue = 0.5f;
        }

        /// <summary>Feed a new raw sample plus the elapsed time, get back a [0,1] value.</summary>
        public float Normalize(float raw, float deltaTime)
        {
            if (float.IsNaN(raw) || float.IsInfinity(raw)) return Value;

            switch (Mode)
            {
                case NormalizerMode.FixedRange:
                    IsCalibrating = false;
                    CalibrationProgress = 1f;
                    RawValue = Mathf.InverseLerp(RawMin, RawMax, raw);
                    break;

                case NormalizerMode.Adaptive:
                    IsCalibrating = false;
                    CalibrationProgress = 1f;
                    RawValue = AdaptiveStep(raw);
                    break;

                default: // Calibrated
                    RawValue = CalibratedStep(raw, deltaTime);
                    break;
            }

            RawValue = Mathf.Clamp01(RawValue);

            // dt-aware exponential smoothing so the result is framerate independent
            if (OutputSmoothing > 1e-4f)
            {
                float a = 1f - Mathf.Exp(-deltaTime / OutputSmoothing);
                Value = Mathf.Lerp(Value, RawValue, a);
            }
            else Value = RawValue;

            return Value;
        }

        float CalibratedStep(float raw, float deltaTime)
        {
            if (IsCalibrating)
            {
                // Welford's online mean/variance over the calibration window
                _count++;
                double d = raw - _mean;
                _mean += d / _count;
                _m2 += d * (raw - _mean);

                _elapsed += deltaTime;
                CalibrationProgress = Mathf.Clamp01(_elapsed / Mathf.Max(0.1f, CalibrationDuration));

                if (CalibrationProgress >= 1f && _count > 1)
                {
                    _frozenMean = (float)_mean;
                    _frozenStd = Mathf.Sqrt((float)(_m2 / (_count - 1)));
                    if (_frozenStd < 1e-6f) _frozenStd = Mathf.Max(1e-6f, Mathf.Abs(_frozenMean) * 0.1f);
                    IsCalibrating = false;
                }
                return 0.5f; // neutral output while measuring the baseline
            }

            // baseline is frozen: sustained elevation -> sustained high output
            float z = (raw - _frozenMean) / _frozenStd;
            return 0.5f + z / (2f * Mathf.Max(0.01f, SpreadStds));
        }

        float AdaptiveStep(float raw)
        {
            float a = Mathf.Clamp01(AdaptiveRate);
            if (_count == 0)
            {
                _mean = raw; _m2 = 0; _count = 1;
                return 0.5f;
            }
            float delta = raw - (float)_mean;
            _mean += a * delta;
            _m2 = (1 - a) * (_m2 + a * delta * delta);
            float std = Mathf.Sqrt(Mathf.Max((float)_m2, 1e-12f));
            return 0.5f + (raw - (float)_mean) / std / (2f * Mathf.Max(0.01f, SpreadStds));
        }
    }
}
