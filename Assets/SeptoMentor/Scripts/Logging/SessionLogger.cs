using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace SeptoMentor
{
    /// <summary>
    /// JSONL debrief log in persistentDataPath/sessions. One JSON object per line.
    /// Contains events and text only - never video frames. Pull with:
    ///   adb pull /sdcard/Android/data/&lt;package&gt;/files/sessions/
    /// </summary>
    public class SessionLogger : MonoBehaviour
    {
        [Serializable]
        class Entry
        {
            public string t;          // ISO-8601 UTC
            public double session_s;  // seconds since start
            public string type;       // session_start, phase, mentor, ai, question, input, session_end
            public string phase;
            public string rule_id;
            public string severity;
            public string text;
            public bool simulation;
        }

        public SegmentationProvider provider;
        public string Path { get; private set; }

        StreamWriter _w;
        double _t0;

        void Awake()
        {
            var dir = System.IO.Path.Combine(Application.persistentDataPath, "sessions");
            Directory.CreateDirectory(dir);
            Path = System.IO.Path.Combine(dir, $"session_{DateTime.UtcNow:yyyyMMdd_HHmmss}.jsonl");
            _w = new StreamWriter(Path, false, new UTF8Encoding(false)) { AutoFlush = true };
            _t0 = Time.realtimeSinceStartupAsDouble;
            Log("session_start", null, null, null, $"app {Application.version}, device {SystemInfo.deviceModel}");
        }

        public void Log(string type, string phase, string ruleId, string severity, string text)
        {
            if (_w == null) return;
            var e = new Entry
            {
                t = DateTime.UtcNow.ToString("o"),
                session_s = Math.Round(Time.realtimeSinceStartupAsDouble - _t0, 3),
                type = type, phase = phase, rule_id = ruleId, severity = severity, text = text,
                simulation = provider != null && provider.IsSimulation,
            };
            _w.WriteLine(JsonUtility.ToJson(e));
        }

        public void LogMentor(MentorEvent e) => Log("mentor", e.phase.ToString(), e.ruleId, e.severity.ToString(), e.message);

        void OnApplicationPause(bool paused) { if (paused) _w?.Flush(); }

        void OnDestroy()
        {
            Log("session_end", null, null, null, null);
            _w?.Dispose();
            _w = null;
        }
    }
}
