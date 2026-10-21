using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
#if UNITY_ANDROID || UNITY_EDITOR
using Unity.Collections;
using UnityEngine.XR.OpenXR.Features.Meta;
#endif

namespace SeptoMentor
{
    /// <summary>
    /// Quest 3 runs apps at 72 Hz unless they ask for more. 90 Hz makes small, fast hand motions (instrument
    /// tips, the scope) visibly smoother. The eye buffers stay at 1.25x (about the panel's native pixel count;
    /// more only costs frame time). If the app cannot hold 90 Hz it drops back to 72 Hz once, for good:
    /// a steady 72 looks better than a stuttering 90. Changing the refresh rate does not rebuild swapchains.
    /// </summary>
    public class DisplayRate : MonoBehaviour
    {
        public float preferredHz = 90f, fallbackHz = 72f;

        static DisplayRate _instance;
        static readonly List<XRDisplaySubsystem> Displays = new List<XRDisplaySubsystem>();
        bool _requested, _gaveUp;
        float _windowStart, _nextTry;
        int _frames, _slow;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Create()
        {
            if (_instance != null || Application.isEditor) return;
            var go = new GameObject("DisplayRate");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<DisplayRate>();
        }

        void Update()
        {
            var display = Running();
            if (display == null) return;
            if (!_requested)
            {
                if (Time.unscaledTime < _nextTry) return;
                _nextTry = Time.unscaledTime + 2f;
                _requested = Request(display, preferredHz);
                _windowStart = Time.unscaledTime + 3f;   // let the runtime settle first
                return;
            }
            if (_gaveUp || Time.unscaledTime < _windowStart) return;

            // Over 5 s, more than a quarter of the frames late (> 1.3 refresh periods) = cannot hold the rate.
            _frames++;
            if (Time.unscaledDeltaTime > 1.3f / preferredHz) _slow++;
            if (Time.unscaledTime - _windowStart < 5f) return;
            if (_slow > _frames / 4)
            {
                _gaveUp = true;
                Request(display, fallbackHz);
                Debug.Log($"[SeptoMentor] {_slow}/{_frames} late frames at {preferredHz} Hz: back to {fallbackHz} Hz.");
            }
            _windowStart = Time.unscaledTime;
            _frames = _slow = 0;
        }

        static XRDisplaySubsystem Running()
        {
            Displays.Clear();
            SubsystemManager.GetSubsystems(Displays);
            foreach (var d in Displays) if (d.running) return d;
            return null;
        }

        static bool Request(XRDisplaySubsystem display, float hz)
        {
#if UNITY_ANDROID || UNITY_EDITOR
            try
            {
                if (!display.TryGetSupportedDisplayRefreshRates(Allocator.Temp, out var rates)) return true;   // feature off: stop asking
                bool ok = false;
                foreach (var r in rates) if (Mathf.Abs(r - hz) < 0.5f) ok = true;
                var list = string.Join(", ", rates.ToArray());
                rates.Dispose();
                bool done = ok && display.TryRequestDisplayRefreshRate(hz);
                Debug.Log($"[SeptoMentor] display refresh rates [{list}] Hz; request {hz} Hz: {(done ? "ok" : "not available")}");
            }
            catch (System.Exception e) { Debug.LogWarning("[SeptoMentor] display refresh rate: " + e.Message); }
#endif
            return true;
        }
    }
}
