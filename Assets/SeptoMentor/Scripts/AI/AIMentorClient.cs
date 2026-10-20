using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace SeptoMentor
{
    /// <summary>
    /// Talks to proxy/server.py. The Anthropic API key and the system prompt live ONLY on
    /// the proxy. The APK holds just the proxy URL and a revocable proxy token.
    /// Video frames are never sent unless BOTH the build config (allowFrames) and the
    /// runtime toggle (sendFrames) are true - and patient video needs institutional approval.
    /// </summary>
    public class AIMentorClient : MonoBehaviour
    {
        [Serializable]
        public class Config
        {
            public string endpoint = "";     // e.g. https://mentor.example.org
            public string token = "";        // proxy token (NOT an Anthropic key)
            public bool allowFrames = false;
        }

        [Serializable] class Turn { public string role; public string text; }

        [Serializable]
        class CoachRequest
        {
            public string session_id, trigger, phase, rule_id, severity, message, instrument;
            public string[] visible_structures;
            public string[] recent_events;
            public bool is_simulation, labels_validated;
            public string frame_jpeg_b64;
        }

        [Serializable]
        class AskRequest
        {
            public string session_id, question, phase, instrument;
            public string[] visible_structures;
            public string[] recent_events;
            public Turn[] history;
            public bool is_simulation, labels_validated;
            public string frame_jpeg_b64;
        }

        [Serializable] class Reply { public string text; public string model; public bool refused; }

        public SegmentationProvider provider;
        public MentorEngine engine;

        [Tooltip("Runtime opt-in for sending the current frame. Default OFF.")]
        public bool sendFrames = false;
        public float minCoachIntervalSeconds = 12f;
        public float timeoutSeconds = 20f;

        public event Action<string, string> Answer;   // (kind "coach"/"ask", text)

        public bool Configured => !string.IsNullOrEmpty(_cfg.endpoint);

        Config _cfg = new Config();
        readonly Queue<string> _recent = new Queue<string>();
        readonly List<Turn> _history = new List<Turn>();
        string _sessionId;
        float _lastCoach = -999f;
        bool _coachInFlight;

        void Awake()
        {
            _sessionId = Guid.NewGuid().ToString("N").Substring(0, 12);
            var ta = Resources.Load<TextAsset>("mentor_config");
            if (ta != null)
            {
                try { _cfg = JsonUtility.FromJson<Config>(ta.text) ?? new Config(); }
                catch (Exception e) { Debug.LogWarning("[SeptoMentor] mentor_config.json invalid: " + e.Message); }
            }
            if (!Configured) Debug.Log("[SeptoMentor] AI mentor disabled: no endpoint in Resources/mentor_config.json");
        }

        public void RememberEvent(MentorEvent e)
        {
            _recent.Enqueue($"[{e.phase}] {e.severity}: {e.ruleId}");
            while (_recent.Count > 6) _recent.Dequeue();
        }

        /// <summary>Automatic coaching on warnings and phase changes (proxy uses a fast model).</summary>
        public void Coach(string trigger, MentorEvent e)
        {
            if (!Configured || _coachInFlight) return;
            if (Time.unscaledTime - _lastCoach < minCoachIntervalSeconds) return;
            _lastCoach = Time.unscaledTime;
            var req = new CoachRequest
            {
                session_id = _sessionId,
                trigger = trigger,
                phase = engine.Phase.ToString(),
                rule_id = e.ruleId,
                severity = e.severity.ToString(),
                message = e.message,
                instrument = CurrentInstrument(),
                visible_structures = engine.VisibleStructures().ToArray(),
                recent_events = _recent.ToArray(),
                is_simulation = provider.IsSimulation,
                labels_validated = provider.LabelsValidated,
                frame_jpeg_b64 = MaybeFrame(),
            };
            _coachInFlight = true;
            StartCoroutine(Post("/v1/coach", JsonUtility.ToJson(req), r =>
            {
                _coachInFlight = false;
                if (r != null && !string.IsNullOrEmpty(r.text)) Answer?.Invoke("coach", r.text);
            }));
        }

        /// <summary>Resident question (proxy uses a stronger model).</summary>
        public void Ask(string question)
        {
            if (!Configured) { Answer?.Invoke("ask", "AI mentor not configured (no proxy endpoint in this build)."); return; }
            var req = new AskRequest
            {
                session_id = _sessionId,
                question = question,
                phase = engine.Phase.ToString(),
                instrument = CurrentInstrument(),
                visible_structures = engine.VisibleStructures().ToArray(),
                recent_events = _recent.ToArray(),
                history = _history.ToArray(),
                is_simulation = provider.IsSimulation,
                labels_validated = provider.LabelsValidated,
                frame_jpeg_b64 = MaybeFrame(),
            };
            StartCoroutine(Post("/v1/ask", JsonUtility.ToJson(req), r =>
            {
                string text = r?.text ?? "AI mentor unavailable right now.";
                _history.Add(new Turn { role = "user", text = question });
                _history.Add(new Turn { role = "assistant", text = text });
                while (_history.Count > 8) _history.RemoveAt(0);
                Answer?.Invoke("ask", text);
            }));
        }

        string CurrentInstrument()
        {
            var f = engine.LastFrame;
            return f == null ? "none" : StructureInfo.DisplayName(f.activeInstrument);
        }

        string MaybeFrame()
        {
            if (!(_cfg.allowFrames && sendFrames)) return null;
            var jpg = provider.CaptureJpeg();
            return jpg == null ? null : Convert.ToBase64String(jpg);
        }

        IEnumerator Post(string path, string json, Action<Reply> done)
        {
            using (var req = new UnityWebRequest(_cfg.endpoint.TrimEnd('/') + path, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                if (!string.IsNullOrEmpty(_cfg.token)) req.SetRequestHeader("Authorization", "Bearer " + _cfg.token);
                req.timeout = Mathf.CeilToInt(timeoutSeconds);
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[SeptoMentor] AI mentor {path} failed: {req.responseCode} {req.error}");
                    done(null);
                    yield break;
                }
                Reply r = null;
                try { r = JsonUtility.FromJson<Reply>(req.downloadHandler.text); }
                catch (Exception e) { Debug.LogWarning("[SeptoMentor] bad AI reply: " + e.Message); }
                done(r);
            }
        }
    }
}
