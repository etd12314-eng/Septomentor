using System;
using UnityEngine;

namespace SeptoMentor
{
    /// <summary>
    /// Voice in/out.
    /// OUT: Android TextToSpeech via JNI. NOTE: Quest OS may ship without a TTS engine;
    ///      if Speak() logs "TTS init failed", route Speak() to Meta Voice SDK TTS instead.
    /// IN:  Meta Voice SDK hook. Wire AppVoiceExperience: call RegisterRecognizer(() => voice.Activate())
    ///      and forward its full-transcription event to OnTranscription(text). Not included here.
    /// </summary>
    public class SpeechIO : MonoBehaviour
    {
        public bool speakEnabled = true;
        [Tooltip("BCP-47 tag, e.g. en-US")]
        public string language = "en-US";

        public event Action<string> QuestionHeard;
        public bool HasRecognizer => _startRecognizer != null;

        Action _startRecognizer;

#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaObject _tts;
        bool _ready;

        class InitListener : AndroidJavaProxy
        {
            readonly Action<int> _cb;
            public InitListener(Action<int> cb) : base("android.speech.tts.TextToSpeech$OnInitListener") { _cb = cb; }
            public void onInit(int status) => _cb(status);
        }

        void Start()
        {
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                {
                    var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                    _tts = new AndroidJavaObject("android.speech.tts.TextToSpeech", activity, new InitListener(OnInit));
                }
            }
            catch (Exception e) { Debug.LogWarning("[SeptoMentor] TTS unavailable: " + e.Message); }
        }

        void OnInit(int status)
        {
            // Runs on a Java thread: only touch the Java object here.
            if (status != 0) { Debug.LogWarning("[SeptoMentor] TTS init failed (status " + status + ")"); return; }
            var parts = language.Split('-');
            using (var locale = new AndroidJavaObject("java.util.Locale", parts[0], parts.Length > 1 ? parts[1] : ""))
                _tts.Call<int>("setLanguage", locale);
            _ready = true;
        }

        public void Speak(string text)
        {
            if (!speakEnabled || !_ready || string.IsNullOrEmpty(text)) return;
            using (var bundle = new AndroidJavaObject("android.os.Bundle"))
                _tts.Call<int>("speak", text, 0 /* QUEUE_FLUSH */, bundle, "septo-" + Time.frameCount);
        }

        void OnDestroy()
        {
            if (_tts == null) return;
            _tts.Call("shutdown");
            _tts.Dispose();
        }
#else
        public void Speak(string text)
        {
            if (speakEnabled && !string.IsNullOrEmpty(text)) Debug.Log("[SeptoMentor][TTS] " + text);
        }
#endif

        /// <summary>Called by a Voice SDK adapter to provide speech recognition.</summary>
        public void RegisterRecognizer(Action startListening) => _startRecognizer = startListening;

        /// <summary>Returns false if no recognizer is wired (caller should fall back).</summary>
        public bool StartListening()
        {
            if (_startRecognizer == null) return false;
            _startRecognizer();
            return true;
        }

        /// <summary>Hook for the Voice SDK's full-transcription event.</summary>
        public void OnTranscription(string text)
        {
            if (!string.IsNullOrWhiteSpace(text)) QuestionHeard?.Invoke(text.Trim());
        }
    }
}
