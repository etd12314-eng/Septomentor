using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace SeptoMentor
{
    public enum VoiceCommand
    {
        Next, Previous, Restart, Scalpel, Elevator, Forceps, Suction, Release, LabelsOn, LabelsOff, Recenter,
        Status, Hint, Repeat, Mute, Unmute, ScopeHand, ScopeAuto, Ask, Help,
        Sickle, Probe, BlakesleyStraight, BlakesleyUp, Backbiter, Shaver, CurvedSuction, Curette,   // FESS instruments
        Septoplasty, Fess, Scope0, Scope30, Scope70, Quiet, NormalTalk, Verbose, Targets,
    }

    /// <summary>
    /// Hands-free Turkish voice commands, fully offline on the headset: Vosk (libvosk.so, Plugins/Android) with the small
    /// Turkish model (StreamingAssets/vosk-model-small-tr, copied to persistent storage on first run) and a fixed command
    /// grammar, so it only ever hears the command words. Always listening (both hands are busy with the scope and the
    /// instruments); the microphone is ignored while the trainer itself speaks.
    /// The small model knows everyday words only, so the commands use them: "neşter ver", "kaldırıcı ver", "makas ver",
    /// "emme ver" for the instruments; for sinus surgery "eğri bıçak" (sickle knife), "sonda" (ball probe), "düz / yukarı /
    /// ters pens" (Blakesley straight / 45 up / backbiter), "tıraş" or "motor" (microdebrider), "eğri emme" (curved suction);
    /// "birinci / ikinci ameliyat" (septoplasty / FESS), "sıfır / otuz / yetmiş derece" (scope angle), "az / normal / çok konuş"
    /// (how much the trainer says), "hedefler" (the ostia still to find).
    /// </summary>
    public class VoiceCommands : MonoBehaviour
    {
        public VoiceCoach voice;
        public bool listen = true;

        public event Action<VoiceCommand, string> Command;   // command, heard text

        public string Status { get; private set; } = "voice: starting";

        const string ModelDir = "vosk-model-small-tr";
        const int Rate = 16000;
        static readonly string[] ModelFiles =
        {
            "Gr.fst", "HCLr.fst", "disambig_tid.int", "final.mdl", "mfcc.conf", "word_boundary.int",
            "ivector/final.dubm", "ivector/final.ie", "ivector/final.mat", "ivector/global_cmvn.stats",
            "ivector/online_cmvn.conf", "ivector/splice.conf",
        };

        // Phrases the recognizer may output ([unk] soaks up everything else).
        static readonly string[] Grammar =
        {
            "sonraki adım", "önceki adım", "baştan başla", "neşter ver", "bıçak ver", "kaldırıcı ver", "kürek ver",
            "makas ver", "pens ver", "emme ver", "kanamayı temizle", "aleti bırak", "etiket aç", "etiket kapat",
            "merkeze al", "durum nedir", "ne yapmalıyım", "ipucu", "tekrar", "sesi kapat", "sesi aç",
            "kamera elde", "kamera otomatik", "soru sor", "yardım",
            "eğri bıçak ver", "sonda ver", "düz pens ver", "yukarı pens ver", "ters pens ver", "tıraş ver", "motor ver",
            "eğri emme ver", "kaşık ver", "birinci ameliyat", "ikinci ameliyat", "sıfır derece", "otuz derece", "yetmiş derece",
            "az konuş", "normal konuş", "çok konuş", "hedefler", "[unk]",
        };

        readonly ConcurrentQueue<short[]> _audio = new ConcurrentQueue<short[]>();
        readonly ConcurrentQueue<string> _heard = new ConcurrentQueue<string>();
        Thread _worker;
        volatile bool _run;
        AudioClip _mic;
        string _device;
        int _micRate, _readPos;
        float[] _buf = new float[0];

#if UNITY_ANDROID && !UNITY_EDITOR
        const string Lib = "vosk";
        [DllImport(Lib)] static extern void vosk_set_log_level(int level);
        [DllImport(Lib)] static extern IntPtr vosk_model_new(byte[] path);
        [DllImport(Lib)] static extern void vosk_model_free(IntPtr model);
        [DllImport(Lib)] static extern IntPtr vosk_recognizer_new_grm(IntPtr model, float rate, byte[] grammar);
        [DllImport(Lib)] static extern void vosk_recognizer_free(IntPtr rec);
        [DllImport(Lib)] static extern int vosk_recognizer_accept_waveform_s(IntPtr rec, short[] data, int length);
        [DllImport(Lib)] static extern IntPtr vosk_recognizer_result(IntPtr rec);
        [DllImport(Lib)] static extern void vosk_recognizer_reset(IntPtr rec);
        const bool Supported = true;
#else
        const bool Supported = false;
#endif

        IEnumerator Start()
        {
            if (!Supported) { Status = "voice commands: headset only"; yield break; }
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
            {
                UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);
                float until = Time.realtimeSinceStartup + 30f;
                while (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone) &&
                       Time.realtimeSinceStartup < until) yield return null;
            }
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
            {
                Status = "voice commands off: no microphone permission";
                yield break;
            }
#endif
            Status = "voice: preparing the speech model";
            string dst = Path.Combine(Application.persistentDataPath, ModelDir);
            string marker = Path.Combine(dst, ".copied-" + Application.version);
            if (!File.Exists(marker))
            {
                foreach (var f in ModelFiles)
                {
                    using (var req = UnityWebRequest.Get(Path.Combine(Application.streamingAssetsPath, ModelDir, f)))
                    {
                        yield return req.SendWebRequest();
                        if (req.result != UnityWebRequest.Result.Success)
                        {
                            Status = "voice commands off: speech model missing (" + f + ")";
                            Debug.LogWarning("[SeptoMentor] " + Status + ": " + req.error);
                            yield break;
                        }
                        var path = Path.Combine(dst, f);
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        File.WriteAllBytes(path, req.downloadHandler.data);
                    }
                }
                File.WriteAllText(marker, "ok");
            }

            _device = Microphone.devices.Length > 0 ? Microphone.devices[0] : null;
            if (_device == null) { Status = "voice commands off: no microphone"; yield break; }
            Microphone.GetDeviceCaps(_device, out int minF, out int maxF);
            _micRate = minF == 0 && maxF == 0 || Rate >= minF && Rate <= maxF ? Rate : maxF;
            _mic = Microphone.Start(_device, true, 2, _micRate);

            _run = true;
            _worker = new Thread(() => Worker(dst)) { IsBackground = true, Name = "Vosk" };
            _worker.Start();
        }

        void Worker(string modelPath)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            IntPtr model = IntPtr.Zero, rec = IntPtr.Zero;
            try
            {
                vosk_set_log_level(-1);
                model = vosk_model_new(Utf8(modelPath));
                if (model == IntPtr.Zero) { Status = "voice commands off: model failed to load"; return; }
                var grammar = "[" + string.Join(",", Array.ConvertAll(Grammar, g => "\"" + g + "\"")) + "]";
                rec = vosk_recognizer_new_grm(model, Rate, Utf8(grammar));
                Status = "voice commands ready (say \"yardım\")";
                _heard.Enqueue("__ready__");
                while (_run)
                {
                    if (!_audio.TryDequeue(out var chunk)) { Thread.Sleep(15); continue; }
                    if (chunk == null) { vosk_recognizer_reset(rec); continue; }   // the trainer spoke: drop what was heard
                    if (vosk_recognizer_accept_waveform_s(rec, chunk, chunk.Length) != 0)
                        _heard.Enqueue(TextOf(Marshal.PtrToStringUTF8(vosk_recognizer_result(rec))));
                }
            }
            catch (Exception e) { Status = "voice commands off: " + e.Message; Debug.LogWarning("[SeptoMentor] Vosk: " + e); }
            finally
            {
                if (rec != IntPtr.Zero) vosk_recognizer_free(rec);
                if (model != IntPtr.Zero) vosk_model_free(model);
            }
#endif
        }

        static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

        /// <summary>The "text" field of Vosk's JSON result.</summary>
        static string TextOf(string json)
        {
            if (string.IsNullOrEmpty(json)) return "";
            int k = json.IndexOf("\"text\"", StringComparison.Ordinal);
            if (k < 0) return "";
            int a = json.IndexOf('"', json.IndexOf(':', k) + 1), b = a < 0 ? -1 : json.IndexOf('"', a + 1);
            return a < 0 || b < 0 ? "" : json.Substring(a + 1, b - a - 1);
        }

        void Update()
        {
            PumpMicrophone();
            while (_heard.TryDequeue(out var text))
            {
                if (text == "__ready__") { voice?.Reply("voice_ready"); continue; }
                text = text.Replace("[unk]", "").Trim();
                if (text.Length == 0) continue;
                var cmd = Match(text);
                Debug.Log($"[SeptoMentor] voice: \"{text}\" -> {(cmd.HasValue ? cmd.Value.ToString() : "?")}");
                if (cmd.HasValue) { voice?.Beep(); Command?.Invoke(cmd.Value, text); }
            }
        }

        void PumpMicrophone()
        {
            if (_mic == null || !_run) return;
            int pos = Microphone.GetPosition(_device);
            if (pos < 0 || pos == _readPos) return;
            int n = pos > _readPos ? pos - _readPos : _mic.samples - _readPos + pos;
            if (_buf.Length < n) _buf = new float[n];
            if (pos > _readPos) _mic.GetData(_buf, _readPos);
            else
            {
                var tail = new float[_mic.samples - _readPos];
                _mic.GetData(tail, _readPos);
                var head = new float[pos];
                if (pos > 0) _mic.GetData(head, 0);
                Array.Copy(tail, _buf, tail.Length);
                Array.Copy(head, 0, _buf, tail.Length, pos);
            }
            _readPos = pos;
            if (!listen || voice != null && voice.Speaking) { _audio.Enqueue(null); return; }

            // To 16 kHz 16-bit (linear resampling when the microphone runs at another rate).
            int m = _micRate == Rate ? n : Mathf.FloorToInt(n * (float)Rate / _micRate);
            var pcm = new short[m];
            for (int i = 0; i < m; i++)
            {
                float src = _micRate == Rate ? i : i * (float)_micRate / Rate;
                int j = Mathf.Min((int)src, n - 1), j1 = Mathf.Min(j + 1, n - 1);
                float v = Mathf.Lerp(_buf[j], _buf[j1], src - j);
                pcm[i] = (short)Mathf.Clamp(v * 32767f, -32768f, 32767f);
            }
            _audio.Enqueue(pcm);
        }

        /// <summary>Keyword match on what was heard (word order and extra words do not matter).</summary>
        public static VoiceCommand? Match(string t)
        {
            bool Has(string w) => t.Contains(w);
            if (Has("konuş")) return Has("az") ? VoiceCommand.Quiet : Has("çok") ? VoiceCommand.Verbose : VoiceCommand.NormalTalk;
            if (Has("derece")) return Has("yetmiş") ? VoiceCommand.Scope70 : Has("otuz") ? VoiceCommand.Scope30 : VoiceCommand.Scope0;
            if (Has("ameliyat")) return Has("ikinci") ? VoiceCommand.Fess : VoiceCommand.Septoplasty;
            if (Has("hedef")) return VoiceCommand.Targets;
            if (Has("eğri")) return Has("emme") ? VoiceCommand.CurvedSuction : VoiceCommand.Sickle;
            if (Has("sonda")) return VoiceCommand.Probe;
            if (Has("kaşık")) return VoiceCommand.Curette;
            if (Has("tıraş") || Has("motor")) return VoiceCommand.Shaver;
            if (Has("pens") && Has("yukarı")) return VoiceCommand.BlakesleyUp;
            if (Has("pens") && Has("ters")) return VoiceCommand.Backbiter;
            if (Has("pens") && Has("düz")) return VoiceCommand.BlakesleyStraight;
            if (Has("sonraki")) return VoiceCommand.Next;
            if (Has("önceki")) return VoiceCommand.Previous;
            if (Has("baştan")) return VoiceCommand.Restart;
            if (Has("neşter") || Has("bıçak")) return VoiceCommand.Scalpel;
            if (Has("kaldırıcı") || Has("kürek")) return VoiceCommand.Elevator;
            if (Has("makas") || Has("pens")) return VoiceCommand.Forceps;
            if (Has("emme") || Has("kanama")) return VoiceCommand.Suction;
            if (Has("bırak")) return VoiceCommand.Release;
            if (Has("etiket")) return Has("kapat") ? VoiceCommand.LabelsOff : VoiceCommand.LabelsOn;
            if (Has("merkez")) return VoiceCommand.Recenter;
            if (Has("durum")) return VoiceCommand.Status;
            if (Has("yapmalıyım") || Has("ipucu")) return VoiceCommand.Hint;
            if (Has("tekrar")) return VoiceCommand.Repeat;
            if (Has("sesi")) return Has("kapat") ? VoiceCommand.Mute : VoiceCommand.Unmute;
            if (Has("kamera")) return Has("otomatik") ? VoiceCommand.ScopeAuto : VoiceCommand.ScopeHand;
            if (Has("soru")) return VoiceCommand.Ask;
            if (Has("yardım")) return VoiceCommand.Help;
            return null;
        }

        void OnDestroy()
        {
            _run = false;
            if (_device != null && Microphone.IsRecording(_device)) Microphone.End(_device);
        }
    }
}
