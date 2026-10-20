using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace SeptoMentor
{
    /// <summary>Scene context sent with every AI request (VR simulator or XR navigation).</summary>
    public interface IMentorContext
    {
        string Mode { get; }            // "training" or "xr_navigation"
        string PhaseName { get; }
        string Instrument { get; }
        string[] VisibleStructures { get; }
        string Navigation { get; }      // free-text navigation summary ("" in training)
        bool IsSimulation { get; }
        bool LabelsValidated { get; }
        byte[] CaptureJpeg();
    }

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
            public string mode, navigation;
            public string session_id, trigger, phase, rule_id, severity, message, instrument;
            public string[] visible_structures;
            public string[] recent_events;
            public bool is_simulation, labels_validated;
            public string frame_jpeg_b64;
        }

        [Serializable]
        class AskRequest
        {
            public string mode, navigation;
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
        [Tooltip("Optional MonoBehaviour implementing IMentorContext (XR navigation). Default: provider + engine.")]
        public MonoBehaviour contextSource;

        IMentorContext Ctx => contextSource as IMentorContext ?? (_vr ?? (_vr = new VrContext(this)));
        VrContext _vr;

        class VrContext : IMentorContext
        {
            readonly AIMentorClient _c;
            public VrContext(AIMentorClient c) { _c = c; }
            public string Mode => "training";
            public string PhaseName => _c.engine.Phase.ToString();
            public string Instrument
            {
                get { var f = _c.engine.LastFrame; return f == null ? "none" : StructureInfo.DisplayName(f.activeInstrument); }
            }
            public string[] VisibleStructures => _c.engine.VisibleStructures().ToArray();
            public string Navigation => "";
            public bool IsSimulation => _c.provider.IsSimulation;
            public bool LabelsValidated => _c.provider.LabelsValidated;
            public byte[] CaptureJpeg() => _c.provider.CaptureJpeg();
        }

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
                phase = Ctx.PhaseName, mode = Ctx.Mode, navigation = Ctx.Navigation,
                rule_id = e.ruleId,
                severity = e.severity.ToString(),
                message = e.message,
                instrument = Ctx.Instrument,
                visible_structures = Ctx.VisibleStructures,
                recent_events = _recent.ToArray(),
                is_simulation = Ctx.IsSimulation,
                labels_validated = Ctx.LabelsValidated,
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
                phase = Ctx.PhaseName, mode = Ctx.Mode, navigation = Ctx.Navigation,
                instrument = Ctx.Instrument,
                visible_structures = Ctx.VisibleStructures,
                recent_events = _recent.ToArray(),
                history = _history.ToArray(),
                is_simulation = Ctx.IsSimulation,
                labels_validated = Ctx.LabelsValidated,
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

        string MaybeFrame()
        {
            if (!(_cfg.allowFrames && sendFrames)) return null;
            var jpg = Ctx.CaptureJpeg();
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
