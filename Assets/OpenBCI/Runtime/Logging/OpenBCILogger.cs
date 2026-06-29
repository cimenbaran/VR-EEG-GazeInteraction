using System;
using System.Collections.Generic;
using UnityEngine;

namespace OpenBCI.Logging
{
    /// <summary>Severity of a log entry. Higher = more important.</summary>
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Success = 2,
        Warning = 3,
        Error = 4
    }

    /// <summary>A single recorded log line.</summary>
    public readonly struct LogEntry
    {
        public readonly DateTime Time;
        public readonly LogLevel Level;
        public readonly string Category;
        public readonly string Message;
        public readonly Color Color;

        public LogEntry(DateTime time, LogLevel level, string category, string message, Color color)
        {
            Time = time;
            Level = level;
            Category = category;
            Message = message;
            Color = color;
        }

        public override string ToString() =>
            $"[{Time:HH:mm:ss.fff}] [{Level}] [{Category}] {Message}";
    }

    /// <summary>
    /// Central color-coded logger for the OpenBCI plugin.
    ///
    /// - Echoes to the Unity Console with rich-text colors per level.
    /// - Keeps a rolling in-memory history (for export or an in-VR log panel).
    /// - Raises <see cref="OnLogged"/> for any subscriber (e.g. a UI panel).
    ///
    /// Use the static helpers, or create a <see cref="ScopedLogger"/> to bake in a category.
    /// </summary>
    public static class OpenBCILogger
    {
        /// <summary>Entries below this level are dropped entirely.</summary>
        public static LogLevel MinLevel = LogLevel.Debug;

        /// <summary>Mirror entries to Unity's Console.</summary>
        public static bool EchoToUnityConsole = true;

        /// <summary>Max entries kept in <see cref="History"/>.</summary>
        public static int HistoryCapacity = 1000;

        /// <summary>Fired for every entry that passes <see cref="MinLevel"/>.</summary>
        public static event Action<LogEntry> OnLogged;

        static readonly Queue<LogEntry> _history = new();
        static readonly object _historyLock = new();

        static readonly Dictionary<LogLevel, Color> _levelColors = new()
        {
            { LogLevel.Debug,   new Color(0.60f, 0.60f, 0.60f) }, // gray
            { LogLevel.Info,    new Color(0.45f, 0.78f, 1.00f) }, // light blue
            { LogLevel.Success, new Color(0.40f, 0.85f, 0.40f) }, // green
            { LogLevel.Warning, new Color(1.00f, 0.75f, 0.20f) }, // amber
            { LogLevel.Error,   new Color(1.00f, 0.35f, 0.35f) }, // red
        };

        /// <summary>Snapshot of the current history (oldest first).</summary>
        public static LogEntry[] History
        {
            get { lock (_historyLock) { return _history.ToArray(); } }
        }

        public static void ClearHistory()
        {
            lock (_historyLock) { _history.Clear(); }
        }

        public static Color ColorFor(LogLevel level) =>
            _levelColors.TryGetValue(level, out var c) ? c : Color.white;

        /// <summary>Wrap text in a rich-text color tag for inline coloring within a message.</summary>
        public static string Colorize(string text, Color color) =>
            $"<color=#{ColorUtility.ToHtmlStringRGB(color)}>{text}</color>";

        // ── primary entry point ──────────────────────────────────────────────
        public static void Log(LogLevel level, string category, string message)
        {
            if (level < MinLevel) return;

            var color = ColorFor(level);
            var entry = new LogEntry(DateTime.Now, level, category, message, color);

            lock (_historyLock)
            {
                _history.Enqueue(entry);
                while (_history.Count > HistoryCapacity) _history.Dequeue();
            }

            if (EchoToUnityConsole)
            {
                string hex = ColorUtility.ToHtmlStringRGB(color);
                string line = $"<color=#{hex}>[{category}]</color> {message}";
                switch (level)
                {
                    case LogLevel.Warning: UnityEngine.Debug.LogWarning(line); break;
                    case LogLevel.Error:   UnityEngine.Debug.LogError(line);   break;
                    default:               UnityEngine.Debug.Log(line);        break;
                }
            }

            OnLogged?.Invoke(entry);
        }

        // ── convenience helpers ──────────────────────────────────────────────
        public static void Debug(string category, string message)   => Log(LogLevel.Debug, category, message);
        public static void Info(string category, string message)    => Log(LogLevel.Info, category, message);
        public static void Success(string category, string message) => Log(LogLevel.Success, category, message);
        public static void Warning(string category, string message) => Log(LogLevel.Warning, category, message);
        public static void Error(string category, string message)   => Log(LogLevel.Error, category, message);

        /// <summary>Create a logger that bakes in a fixed category.</summary>
        public static ScopedLogger Scope(string category) => new(category);
    }

    /// <summary>A thin wrapper that remembers a category, so callers omit it each time.</summary>
    public readonly struct ScopedLogger
    {
        readonly string _category;
        public ScopedLogger(string category) => _category = category;

        public void Debug(string message)   => OpenBCILogger.Debug(_category, message);
        public void Info(string message)    => OpenBCILogger.Info(_category, message);
        public void Success(string message) => OpenBCILogger.Success(_category, message);
        public void Warning(string message) => OpenBCILogger.Warning(_category, message);
        public void Error(string message)   => OpenBCILogger.Error(_category, message);
    }
}
