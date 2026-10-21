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

    /// <summary>Thumb-index pinch with hysteresis (close below 1.5 cm, open above 3 cm).</summary>
    public class Pinch
    {
        public readonly bool right;
        public bool Held { get; private set; }
        public bool Down { get; private set; }
        public bool Up { get; private set; }

        public Pinch(bool right) { this.right = right; }

        public void Update(bool allowed = true)
        {
            float d = HandTracking.PinchDistance(right);
            bool now = allowed && d < (Held ? 0.03f : 0.015f);
            Down = now && !Held;
            Up = !now && Held;
            Held = now;
        }
    }
}
