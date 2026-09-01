using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using OpenBCI.Logging;

namespace OpenBCI.Core
{
    /// <summary>
    /// Listens on a UDP port for JSON packets from cyton_bridge.py and exposes
    /// EEG band powers, composite metrics and raw samples to the scene.
    ///
    /// Attach to a persistent GameObject (e.g. one named "OpenBCI").
    /// All public getters are safe to call from the main thread (Update-synced).
    /// </summary>
    public class OpenBCIReceiver : MonoBehaviour
    {
        const string Cat = "OpenBCI";

        [Header("Network")]
        [Tooltip("UDP port matching cyton_bridge.py --port (default 12345)")]
        public int port = 12345;

        [Header("Debug")]
        public bool logPackets = false;
        [Tooltip("Periodically log band powers, each band in its own color.")]
        public bool logBands = false;
        [Tooltip("Seconds between band log lines.")]
        public float bandLogInterval = 1f;
        float _nextBandLog;

        // ── public state ─────────────────────────────────────────────────────
        public bool HasData { get; private set; }
        public double Timestamp { get; private set; }

        /// <summary>
        /// Increments once per decoded packet. Consumers that accumulate statistics
        /// (e.g. <see cref="SignalNormalizer"/>) must step on this rather than per
        /// frame — the bridge sends a few packets per second while Update runs at
        /// headset framerate, so per-frame stepping would feed the same sample
        /// dozens of times and destroy the variance estimate.
        /// </summary>
        public int PacketCount { get; private set; }

        /// <summary>Band powers in log10 power (as sent by the bridge).</summary>
        public float Delta { get; private set; }
        public float Theta { get; private set; }
        public float Alpha { get; private set; }
        public float Beta  { get; private set; }
        public float Gamma { get; private set; }

        /// <summary>Latest raw µV sample per EEG channel (read-only snapshot).</summary>
        public float[] RawSample { get; private set; } = Array.Empty<float>();

        /// <summary>Raised once, the first time a packet arrives.</summary>
        public event Action OnFirstData;

        // ── internals ────────────────────────────────────────────────────────
        UdpClient _udp;
        Thread _thread;
        volatile bool _running;
        ScopedLogger _log;

        readonly object _lock = new();
        EEGFeatures _pending;
        bool _hasPending;

        // ── metric access ────────────────────────────────────────────────────
        /// <summary>Linear power for a band (undoes the bridge's log10).</summary>
        public float GetBandLinear(EEGBand band)
        {
            float logp = band switch
            {
                EEGBand.Delta => Delta,
                EEGBand.Theta => Theta,
                EEGBand.Alpha => Alpha,
                EEGBand.Beta  => Beta,
                EEGBand.Gamma => Gamma,
                _ => 0f
            };
            return Mathf.Pow(10f, logp);
        }

        /// <summary>Raw (un-normalized) value for any metric. Feed into an AdaptiveNormalizer.</summary>
        public float GetMetricRaw(EEGMetric metric)
        {
            float a = GetBandLinear(EEGBand.Alpha);
            float b = GetBandLinear(EEGBand.Beta);
            float t = GetBandLinear(EEGBand.Theta);
            return metric switch
            {
                EEGMetric.Delta      => GetBandLinear(EEGBand.Delta),
                EEGMetric.Theta      => t,
                EEGMetric.Alpha      => a,
                EEGMetric.Beta       => b,
                EEGMetric.Gamma      => GetBandLinear(EEGBand.Gamma),
                EEGMetric.RelaxIndex => (a + b) > 0f ? a / (a + b) : 0.5f,
                EEGMetric.Engagement => (a + t) > 0f ? b / (a + t) : 0.5f,
                _ => 0f
            };
        }

        // ── lifecycle ────────────────────────────────────────────────────────
        void OnEnable()
        {
            _log = OpenBCILogger.Scope(Cat);
            try
            {
                _udp = new UdpClient(port);
            }
            catch (Exception ex)
            {
                _log.Error($"Failed to bind UDP port {port}: {ex.Message}");
                enabled = false;
                return;
            }

            _running = true;
            _thread = new Thread(ReceiveLoop) { IsBackground = true };
            _thread.Start();
            _log.Info($"Listening on udp://0.0.0.0:{port}");
        }

        void OnDisable()
        {
            _running = false;
            _udp?.Close();
            _thread?.Join(500);
            HasData = false;
            _log.Info("Receiver stopped");
        }

        void Update()
        {
            lock (_lock)
            {
                if (!_hasPending) return;
                var p = _pending;
                _hasPending = false;

                Timestamp = p.Timestamp;
                Delta = p.Delta;
                Theta = p.Theta;
                Alpha = p.Alpha;
                Beta  = p.Beta;
                Gamma = p.Gamma;
                RawSample = p.Raw;
                PacketCount++;

                if (!HasData)
                {
                    HasData = true;
                    _log.Success($"Stream connected ({p.Raw?.Length ?? 0} channels)");
                    OnFirstData?.Invoke();
                }
            }

            if (logBands && HasData && Time.unscaledTime >= _nextBandLog)
            {
                _nextBandLog = Time.unscaledTime + Mathf.Max(0.05f, bandLogInterval);
                string line = $"{EEGColors.Tag(EEGBand.Delta, Delta)}  " +
                              $"{EEGColors.Tag(EEGBand.Theta, Theta)}  " +
                              $"{EEGColors.Tag(EEGBand.Alpha, Alpha)}  " +
                              $"{EEGColors.Tag(EEGBand.Beta, Beta)}  " +
                              $"{EEGColors.Tag(EEGBand.Gamma, Gamma)}";
                OpenBCILogger.Info(Cat, line);
            }
        }

        // ── receive thread ───────────────────────────────────────────────────
        void ReceiveLoop()
        {
            var ep = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                try
                {
                    byte[] bytes = _udp.Receive(ref ep);
                    string json = Encoding.UTF8.GetString(bytes);
                    if (logPackets) OpenBCILogger.Debug(Cat, json);

                    var p = ParsePacket(json);
                    lock (_lock)
                    {
                        _pending = p;
                        _hasPending = true;
                    }
                }
                catch (SocketException) { /* socket closed on disable */ }
                catch (Exception ex) { OpenBCILogger.Warning(Cat, $"Parse error: {ex.Message}"); }
            }
        }

        // ── minimal JSON parse (dynamic keys make JsonUtility awkward) ─────────
        static EEGFeatures ParsePacket(string json)
        {
            var p = new EEGFeatures
            {
                Timestamp = ReadDouble(json, "timestamp"),
                Delta = (float)ReadDouble(json, "delta"),
                Theta = (float)ReadDouble(json, "theta"),
                Alpha = (float)ReadDouble(json, "alpha"),
                Beta  = (float)ReadDouble(json, "beta"),
                Gamma = (float)ReadDouble(json, "gamma"),
                Raw   = ReadFloatArray(json, "raw")
            };
            return p;
        }

        static double ReadDouble(string json, string key)
        {
            string search = $"\"{key}\":";
            int idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return 0;
            idx += search.Length;
            int end = json.IndexOfAny(new[] { ',', '}', ']' }, idx);
            if (end < 0) end = json.Length;
            return double.TryParse(json.Substring(idx, end - idx).Trim(),
                                   System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture,
                                   out double v) ? v : 0;
        }

        static float[] ReadFloatArray(string json, string key)
        {
            string search = $"\"{key}\":[";
            int start = json.IndexOf(search, StringComparison.Ordinal);
            if (start < 0) return Array.Empty<float>();
            start += search.Length;
            int end = json.IndexOf(']', start);
            if (end < 0) return Array.Empty<float>();
            var parts = json.Substring(start, end - start).Split(',');
            var result = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                float.TryParse(parts[i].Trim(),
                               System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture,
                               out result[i]);
            return result;
        }
    }
}
