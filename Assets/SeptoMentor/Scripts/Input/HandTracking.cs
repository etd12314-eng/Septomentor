using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

namespace SeptoMentor
{
    /// <summary>
    /// Quest hand tracking via XR Hands (OpenXR "Hand Tracking Subsystem" feature).
    /// Joint poses come in tracking space; the *World variants map them through the camera's parent (XR origin).
    /// When the Touch controllers are held, the runtime reports the hands as untracked.
    /// </summary>
    public static class HandTracking
    {
        static XRHandSubsystem _sub;
        static readonly List<XRHandSubsystem> Found = new List<XRHandSubsystem>();
        static Transform _origin;

        public static XRHandSubsystem Subsystem
        {
            get
            {
                if (_sub != null && _sub.running) return _sub;
                _sub = null;
                Found.Clear();
                SubsystemManager.GetSubsystems(Found);
                foreach (var s in Found) if (s.running) { _sub = s; break; }
                return _sub;
            }
        }

        /// <summary>Tracking-space origin (parent of the XR camera).</summary>
        public static Transform Origin
        {
            get
            {
                if (_origin == null && Camera.main != null) _origin = Camera.main.transform.parent;
                return _origin;
            }
        }

        public static bool IsTracked(bool right)
        {
            var s = Subsystem;
            return s != null && (right ? s.rightHand : s.leftHand).isTracked;
        }

        public static bool AnyTracked => IsTracked(true) || IsTracked(false);

        public static bool TryGetJoint(bool right, XRHandJointID id, out Pose pose)
        {
            pose = Pose.identity;
            var s = Subsystem;
            if (s == null) return false;
            var hand = right ? s.rightHand : s.leftHand;
            return hand.isTracked && hand.GetJoint(id).TryGetPose(out pose);
        }

        public static bool TryGetJointWorld(bool right, XRHandJointID id, out Pose pose)
        {
            if (!TryGetJoint(right, id, out pose)) return false;
            var o = Origin;
            if (o != null) pose = new Pose(o.TransformPoint(pose.position), o.rotation * pose.rotation);
            return true;
        }

        /// <summary>
        /// Palm centre and the direction the palm faces, in world space. The normal is computed from the
        /// knuckle positions (not from joint axes), so it does not depend on the runtime's axis convention.
        /// </summary>
        public static bool TryGetPalmWorld(bool right, out Vector3 centre, out Vector3 normal)
        {
            centre = normal = Vector3.zero;
            if (!TryGetJointWorld(right, XRHandJointID.Wrist, out var w) ||
                !TryGetJointWorld(right, XRHandJointID.IndexProximal, out var i) ||
                !TryGetJointWorld(right, XRHandJointID.LittleProximal, out var l)) return false;
            // Left hand, palm toward the viewer, fingers up: index is on the viewer's right, so
            // (little - index) x (index - wrist) points at the viewer. Mirrored for the right hand.
            var n = Vector3.Cross(l.position - i.position, i.position - w.position);
            if (right) n = -n;
            if (n.sqrMagnitude < 1e-8f) return false;
            normal = n.normalized;
            centre = (w.position + i.position + l.position) / 3f;
            return true;
        }

        /// <summary>One-line state for status displays.</summary>
        public static string StatusText =>
            Subsystem == null ? "hand tracking OFF (Quest Settings > Movement tracking > Hand tracking)"
            : $"hands: L {(IsTracked(false) ? "ok" : "-")}  R {(IsTracked(true) ? "ok" : "-")}";

        /// <summary>Thumb-to-index tip distance in metres (infinity when the hand is not tracked).</summary>
        public static float PinchDistance(bool right)
        {
            return TryGetJoint(right, XRHandJointID.ThumbTip, out var t) && TryGetJoint(right, XRHandJointID.IndexTip, out var i)
                ? Vector3.Distance(t.position, i.position) : float.PositiveInfinity;
        }
    }

    /// <summary>
    /// Thumb-index pinch with hysteresis (closes below 2 cm, opens above 3.5 cm). A pinch survives tracking
    /// drop-outs of up to 0.2 s (the fingers occlude each other while pinching, so the hand often blinks out).
    /// </summary>
    public class Pinch
    {
        public readonly bool right;
        public bool Held { get; private set; }
        public bool Down { get; private set; }
        public bool Up { get; private set; }
        float _lastSeen = -10f;

        public Pinch(bool right) { this.right = right; }

        public void Update(bool allowed = true)
        {
            float d = HandTracking.PinchDistance(right);
            bool now;
            if (float.IsInfinity(d)) now = Held && Time.time - _lastSeen < 0.2f;
            else { _lastSeen = Time.time; now = d < (Held ? 0.035f : 0.02f); }
            now &= allowed;
            Down = now && !Held;
            Up = !now && Held;
            Held = now;
        }
    }

    /// <summary>
    /// Right index fingertip as a pointer: one-euro smoothing, a 0.2 s acquire delay (so a hand seen for one frame
    /// does not take over), a 0.3 s hold over tracking drop-outs, and a short history of raw tip positions so a
    /// captured point can be the average of the last few hundred milliseconds instead of a single noisy frame.
    /// All positions are in tracking space.
    /// </summary>
    public class FingerPointer
    {
        public bool Active { get; private set; }
        public Vector3 Tip { get; private set; }
        public Vector3 Distal { get; private set; }
        public Quaternion Rot { get; private set; } = Quaternion.identity;

        readonly PoseFilter _tip = new PoseFilter { minCutoff = 0.9f, beta = 8f, betaAngular = 0.02f };
        readonly PoseFilter _distal = new PoseFilter { minCutoff = 0.9f, beta = 8f, betaAngular = 0.02f };
        readonly List<(float t, Vector3 p)> _history = new List<(float, Vector3)>();
        float _seenSince = -1f, _lastSeen = -10f;

        public void Update()
        {
            float now = Time.time, dt = Time.deltaTime;
            if (HandTracking.TryGetJoint(true, XRHandJointID.IndexTip, out var t) &&
                HandTracking.TryGetJoint(true, XRHandJointID.IndexDistal, out var d))
            {
                if (_seenSince < 0f || now - _lastSeen > 0.3f) { _seenSince = now; _tip.Reset(); _distal.Reset(); _history.Clear(); }
                _lastSeen = now;
                var tp = t.position; var tr = t.rotation;
                var dp = d.position; var dr = d.rotation;
                _tip.Filter(ref tp, ref tr, dt);
                _distal.Filter(ref dp, ref dr, dt);
                Tip = tp; Rot = tr; Distal = dp;
                _history.Add((now, t.position));
                while (_history.Count > 0 && now - _history[0].t > 0.6f) _history.RemoveAt(0);
                if (now - _seenSince >= 0.2f) Active = true;
            }
            else if (now - _lastSeen > 0.3f)
            {
                Active = false; _seenSince = -1f; _history.Clear();
            }
            // otherwise: brief drop-out, keep the last pose
        }

        /// <summary>Mean raw tip over the last <paramref name="seconds"/>, and the largest distance of a sample from it.</summary>
        public bool Average(float seconds, out Vector3 mean, out float spread)
        {
            mean = Tip; spread = 0f;
            int n = 0; var sum = Vector3.zero;
            foreach (var h in _history) if (Time.time - h.t <= seconds) { sum += h.p; n++; }
            if (n == 0) return false;
            mean = sum / n;
            foreach (var h in _history) if (Time.time - h.t <= seconds) spread = Mathf.Max(spread, Vector3.Distance(h.p, mean));
            return true;
        }
    }
}
