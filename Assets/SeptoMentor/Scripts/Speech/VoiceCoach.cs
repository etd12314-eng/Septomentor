using System.Collections.Generic;
using UnityEngine;

namespace SeptoMentor
{
    /// <summary>
    /// Spoken alerts and instructions in Turkish, from clips made offline (tools/make_voice.py, Piper TTS) and stored in
    /// Resources/Voice/&lt;key&gt;.wav, so they work on the headset without a TTS engine or network.
    /// Warnings cut in over anything less urgent; the rest queue (at most three). The same key is not repeated
    /// within a few seconds. A key without a clip falls back to SpeechIO (Android TTS, if the headset has one).
    /// </summary>
    public class VoiceCoach : MonoBehaviour
    {
        public SpeechIO speech;
        public bool muted;
        [Range(0, 1)] public float volume = 1f;

        /// <summary>True while a clip plays (and shortly after): the command recognizer ignores the microphone then.</summary>
        public bool Speaking => _source != null && _source.isPlaying || Time.unscaledTime < _quietUntil;

        struct Line { public string key, text; public Severity sev; }

        AudioSource _source;
        AudioClip _beep;
        readonly Queue<Line> _queue = new Queue<Line>();
        readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>();
        readonly Dictionary<string, float> _lastSaid = new Dictionary<string, float>();
        Severity _playing;
        float _quietUntil;
        Line? _last;

        void Awake()
        {
            _source = gameObject.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.spatialBlend = 0f;   // a voice in the ear, like the mentor standing next to you
            _source.priority = 0;
            _beep = Tone(1200f, 0.07f);
        }

        /// <summary>Speaks the clip for <paramref name="key"/>; <paramref name="text"/> is the fallback for TTS.</summary>
        public void Say(string key, string text, Severity sev = Severity.Info)
        {
            if (muted && key != "voice_off" && key != "voice_on") return;
            key = Normalize(key);
            if (_lastSaid.TryGetValue(key, out float t) && Time.unscaledTime - t < (sev >= Severity.Warning ? 2.5f : 5f)) return;
            _lastSaid[key] = Time.unscaledTime;
            var line = new Line { key = key, text = text, sev = sev };
            if (sev >= Severity.Warning && _source.isPlaying && _playing < Severity.Warning)
            {
                _source.Stop();
                _queue.Clear();
            }
            if (_queue.Count >= 3) _queue.Dequeue();
            _queue.Enqueue(line);
        }

        /// <summary>Several clips in a row (e.g. the phase, then the progress).</summary>
        public void SayAll(params string[] keys)
        {
            foreach (var k in keys) { _lastSaid.Remove(k); Say(k, null); }
        }

        public void Repeat()
        {
            if (_last == null) return;
            _lastSaid.Remove(_last.Value.key);
            Say(_last.Value.key, _last.Value.text, _last.Value.sev);
        }

        /// <summary>Short tone: a voice command was heard.</summary>
        public void Beep() => _source.PlayOneShot(_beep, 0.5f * volume);

        public bool Has(string key) => Clip(Normalize(key)) != null;

        static string Normalize(string key)
        {
            if (key == null) return "";
            if (key.StartsWith("turbinate_")) return "turbinate";
            if (key.StartsWith("force_")) return "force";
            return key;
        }

        AudioClip Clip(string key)
        {
            if (!_clips.TryGetValue(key, out var c)) _clips[key] = c = Resources.Load<AudioClip>("Voice/" + key);
            return c;
        }

        void Update()
        {
            if (_source.isPlaying || _queue.Count == 0) return;
            var line = _queue.Dequeue();
            var clip = Clip(line.key);
            if (clip != null)
            {
                _source.clip = clip;
                _source.volume = volume;
                _source.Play();
                _quietUntil = Time.unscaledTime + clip.length + 0.4f;
            }
            else if (!string.IsNullOrEmpty(line.text) && line.sev >= Severity.Caution && speech != null)
            {
                speech.Speak(line.text);
                _quietUntil = Time.unscaledTime + 0.08f * line.text.Length;
            }
            else return;
            _playing = line.sev;
            if (!line.key.StartsWith("pct_") && !line.key.StartsWith("name_")) _last = line;
        }

        static AudioClip Tone(float hz, float seconds)
        {
            int rate = 44100, n = Mathf.CeilToInt(rate * seconds);
            var data = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)rate, env = Mathf.Sin(Mathf.PI * i / n);
                data[i] = 0.4f * env * Mathf.Sin(2 * Mathf.PI * hz * t);
            }
            var clip = AudioClip.Create("Beep", n, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
