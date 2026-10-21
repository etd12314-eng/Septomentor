using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace SeptoMentor
{
    /// <summary>
    /// Touch-controller vibration. Two kinds:
    ///   Rumble(node, amplitude) - call every frame while it should last (tissue contact, suction);
    ///   Pulse(node, amplitude, seconds, delay) - one-shot events (cut, bite, warning patterns).
    /// A pulse wins over the rumble while it plays. Quest controllers take amplitude only (no frequency).
    /// </summary>
    public static class Haptics
    {
        struct Scheduled { public XRNode node; public float at, amp, dur; }

        static readonly List<Scheduled> Queue = new List<Scheduled>();
        static readonly float[] RumbleAmp = new float[2], NextRumble = new float[2], BusyUntil = new float[2];
        static Runner _runner;

        public static void Rumble(XRNode node, float amplitude)
        {
            Ensure();
            int i = Index(node);
            if (i >= 0) RumbleAmp[i] = Mathf.Max(RumbleAmp[i], Mathf.Clamp01(amplitude));
        }

        public static void Pulse(XRNode node, float amplitude, float seconds, float delay = 0f)
        {
            Ensure();
            Queue.Add(new Scheduled { node = node, at = Time.unscaledTime + delay, amp = Mathf.Clamp01(amplitude), dur = seconds });
        }

        /// <summary>Repeated pulses: count x (amplitude, seconds) separated by gap seconds.</summary>
        public static void Pattern(XRNode node, int count, float amplitude, float seconds, float gap)
        {
            for (int k = 0; k < count; k++) Pulse(node, amplitude, seconds, k * (seconds + gap));
        }

        public static void Both(float amplitude, float seconds, float delay = 0f)
        {
            Pulse(XRNode.LeftHand, amplitude, seconds, delay);
            Pulse(XRNode.RightHand, amplitude, seconds, delay);
        }

        static int Index(XRNode n) => n == XRNode.LeftHand ? 0 : n == XRNode.RightHand ? 1 : -1;

        static void Send(XRNode node, float amp, float dur)
        {
            var d = InputDevices.GetDeviceAtXRNode(node);
            if (d.isValid && d.TryGetHapticCapabilities(out var caps) && caps.supportsImpulse) d.SendHapticImpulse(0, amp, dur);
        }

        static void Ensure()
        {
            if (_runner != null) return;
            var go = new GameObject("Haptics");
            Object.DontDestroyOnLoad(go);
            _runner = go.AddComponent<Runner>();
        }

        class Runner : MonoBehaviour
        {
            void LateUpdate()
            {
                float now = Time.unscaledTime;
                for (int k = Queue.Count - 1; k >= 0; k--)
                {
                    var s = Queue[k];
                    if (now < s.at) continue;
                    Queue.RemoveAt(k);
                    Send(s.node, s.amp, s.dur);
                    int i = Index(s.node);
                    if (i >= 0) BusyUntil[i] = Mathf.Max(BusyUntil[i], now + s.dur);
                }
                for (int i = 0; i < 2; i++)
                {
                    float a = RumbleAmp[i];
                    RumbleAmp[i] = 0;
                    if (a <= 0.01f || now < BusyUntil[i] || now < NextRumble[i]) continue;
                    // Overlapping short impulses feel continuous and stop by themselves when the rumble does.
                    Send(i == 0 ? XRNode.LeftHand : XRNode.RightHand, a, 0.07f);
                    NextRumble[i] = now + 0.05f;
                }
            }
        }
    }
}
