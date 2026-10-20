using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace SeptoMentor
{
    /// <summary>
    /// Minimal HMD pose driver (avoids depending on a specific TrackedPoseDriver package).
    /// Updated in Update and again just before render for low latency.
    /// </summary>
    public class HeadTracker : MonoBehaviour
    {
        static readonly List<XRInputSubsystem> Subsystems = new List<XRInputSubsystem>();

        void OnEnable()
        {
            Application.onBeforeRender += Apply;
            SubsystemManager.GetSubsystems(Subsystems);
            foreach (var s in Subsystems) s.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor);
        }

        void OnDisable() => Application.onBeforeRender -= Apply;

        void Update() => Apply();

        void Apply()
        {
            var head = InputDevices.GetDeviceAtXRNode(XRNode.CenterEye);
            if (!head.isValid) return;
            if (head.TryGetFeatureValue(CommonUsages.centerEyePosition, out Vector3 p)) transform.localPosition = p;
            if (head.TryGetFeatureValue(CommonUsages.centerEyeRotation, out Quaternion r)) transform.localRotation = r;
        }
    }
}
