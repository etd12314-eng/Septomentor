using System;
using UnityEngine;
using UnityEngine.XR;

namespace SeptoMentor
{
    /// <summary>
    /// The LEFT Touch controller holds a rigid 0-degree endoscope (the right hand works the instruments) 
    /// (camera head + 18 cm, 4 mm shaft).
    /// The scope tip, relative to the patient's head on the OR table, drives the virtual endoscope
    /// inside the anatomy: the head's nostril is mapped 1:1 onto the start of the anatomy's scope path.
    /// Tissue contact gives a haptic buzz; rubbing the mucosa for long raises a caution.
    /// With the controller put down (or hand-held mode off) the provider's autopilot / thumbsticks take over.
    /// </summary>
    [DefaultExecutionOrder(-100)]   // before the provider moves and renders the endoscope
    public class HandheldScope : MonoBehaviour
    {
        public SyntheticSegmentationProvider provider;
        public OperatingRoom room;
        public Transform head;
        public bool handheld = true;
        public float shaftLength = 0.18f;
        [Tooltip("Shaft start in front of the grip (camera head + coupler), m.")]
        public float shaftOffset = 0.06f;
        public XRNode hand = XRNode.LeftHand;

        /// <summary>(message, severity) for the monitor / speech.</summary>
        public event Action<string, Severity> Hint;

        public bool Active { get; private set; }
        public bool InNose { get; private set; }
        public bool Touching { get; private set; }

        Transform _visual;
        Vector3 _nostril;   // head frame
        bool _sideKnown, _rightNostril;
        float _touchSince = -1, _nextBuzz, _nextHint;
        public static readonly Quaternion HeadToAnatomy = Quaternion.Euler(0, 180f, 0);   // (right, sup, ant) -> (left, sup, post)

        /// <summary>
        /// Maps a pose in the operating room into the patient's local (anatomy) space. Phantom-aligned anatomy shares
        /// the OR head's geometry (1:1); otherwise the right nostril is pinned to the start of the scope path.
        /// </summary>
        public static bool ToAnatomy(OperatingRoom room, VirtualPatient patient, Vector3 world, Quaternion rot, out Vector3 local, out Quaternion localRot)
        {
            local = default; localRot = Quaternion.identity;
            if (room == null || room.PatientHead == null || patient == null || patient.AnatomyRoot == null) return false;
            var ph = room.PatientHead;
            var tipH = ph.InverseTransformPoint(world);
            local = patient.PhantomAligned ? HeadToAnatomy * tipH : HeadToAnatomy * (tipH - OperatingRoom.Nostril(true)) + patient.pathStart;
            localRot = HeadToAnatomy * (Quaternion.Inverse(ph.rotation) * rot);
            return true;
        }

        void Start() => BuildVisual();

        void BuildVisual()
        {
            var shader = Shader.Find("SeptoMentor/ORSurface");
            Material M(Color c, float gloss) { var m = new Material(shader); m.SetColor("_Color", c); m.SetFloat("_Gloss", gloss); return m; }
            var black = M(new Color(0.08f, 0.08f, 0.09f), 0.5f);
            var steel = M(new Color(0.8f, 0.82f, 0.85f), 0.9f);
            var cable = M(new Color(0.55f, 0.57f, 0.6f), 0.3f);

            _visual = new GameObject("Endoscope").transform;
            _visual.SetParent(transform, false);
            Part(PrimitiveType.Cylinder, new Vector3(0, 0, 0.0f), new Vector3(0.032f, 0.035f, 0.032f), Quaternion.Euler(90, 0, 0), black);      // camera head
            Part(PrimitiveType.Cylinder, new Vector3(0, 0, 0.045f), new Vector3(0.022f, 0.012f, 0.022f), Quaternion.Euler(90, 0, 0), steel);    // coupler
            Part(PrimitiveType.Cylinder, new Vector3(0, 0.014f, 0.058f), new Vector3(0.007f, 0.012f, 0.007f), Quaternion.identity, steel);      // light post
            Part(PrimitiveType.Cylinder, new Vector3(0, 0.03f, -0.12f), new Vector3(0.008f, 0.15f, 0.008f), Quaternion.Euler(-80, 0, 0), cable); // cable
            Part(PrimitiveType.Cylinder, new Vector3(0, 0, shaftOffset + shaftLength * 0.5f), new Vector3(0.004f, shaftLength * 0.5f, 0.004f),
                 Quaternion.Euler(90, 0, 0), steel);                                                                                            // shaft
            _visual.gameObject.SetActive(false);
        }

        void Part(PrimitiveType t, Vector3 pos, Vector3 scale, Quaternion rot, Material m)
        {
            var go = GameObject.CreatePrimitive(t);
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(_visual, false);
            go.transform.localPosition = pos; go.transform.localRotation = rot; go.transform.localScale = scale;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        void Update()
        {
            var dev = InputDevices.GetDeviceAtXRNode(hand);
            Vector3 pos = default; Quaternion rot = Quaternion.identity;
            bool tracked = dev.isValid && dev.TryGetFeatureValue(CommonUsages.isTracked, out bool t) && t &&
                           dev.TryGetFeatureValue(CommonUsages.devicePosition, out pos) &&
                           dev.TryGetFeatureValue(CommonUsages.deviceRotation, out rot);
            Active = handheld && tracked && room != null && room.PatientHead != null && provider != null && provider.patient != null;
            if (_visual != null) _visual.gameObject.SetActive(tracked && handheld);
            if (!Active)
            {
                if (provider != null) provider.externalPose = false;
                InNose = Touching = false;
                return;
            }

            var origin = head != null ? head.parent : null;
            var wPos = origin != null ? origin.TransformPoint(pos) : pos;
            var wRot = origin != null ? origin.rotation * rot : rot;
            transform.SetPositionAndRotation(wPos, wRot);

            var tip = wPos + wRot * new Vector3(0, 0, shaftOffset + shaftLength);
            var ph = room.PatientHead;
            var tipH = ph.InverseTransformPoint(tip);
            var rotH = Quaternion.Inverse(ph.rotation) * wRot;

            var patient = provider.patient;
            if (!_sideKnown && patient.AnatomyRoot != null)
            {
                // Use the nostril on the same side as the anatomy's scope path (the head frame is mirrored into it).
                _rightNostril = patient.PhantomAligned || patient.pathStart.x < patient.LocalBounds.center.x;
                _nostril = OperatingRoom.Nostril(_rightNostril);
                _sideKnown = true;
            }
            if (!_sideKnown) { provider.externalPose = false; return; }

            // Phantom-aligned anatomy shares the head's geometry: map 1:1. Otherwise pin the nostril to the path start.
            var local = patient.PhantomAligned ? HeadToAnatomy * tipH : HeadToAnatomy * (tipH - _nostril) + patient.pathStart;
            provider.externalPose = true;
            provider.externalPosition = local;
            provider.externalRotation = HeadToAnatomy * rotH;

            var rel = tipH - _nostril;
            InNose = rel.z < 0.004f && Mathf.Abs(rel.x) < 0.02f && rel.y > -0.015f && rel.y < 0.05f && rel.z > -0.12f;
            ContactFeedback(patient.transform.TransformPoint(local));

            if (!InNose && Time.time > _nextHint && tipH.magnitude < 0.25f)
            {
                _nextHint = Time.time + 10f;
                Hint?.Invoke($"Guide the scope into the patient's {(_rightNostril ? "right" : "left")} nostril, along the nasal floor.", Severity.Tip);
            }
        }

        void ContactFeedback(Vector3 endoWorld)
        {
            Touching = InNose && Physics.CheckSphere(endoWorld, 0.0015f);
            if (!Touching) { _touchSince = -1; return; }
            if (_touchSince < 0) _touchSince = Time.time;
            // Soft, growing buzz: a scope rubbing the mucosa smears the lens and makes it bleed.
            Haptics.Rumble(hand, 0.25f + 0.25f * Mathf.Clamp01((Time.time - _touchSince) / 1.2f));
            if (Time.time > _nextBuzz) { _nextBuzz = Time.time + 0.6f; Haptics.Pulse(hand, 0.45f, 0.03f); }
            if (Time.time - _touchSince > 1.2f && Time.time > _nextHint)
            {
                _nextHint = Time.time + 6f;
                Hint?.Invoke("The scope tip is rubbing the mucosa - pull back slightly and keep it centred (bleeding, smeared lens).", Severity.Caution);
            }
        }
    }
}
