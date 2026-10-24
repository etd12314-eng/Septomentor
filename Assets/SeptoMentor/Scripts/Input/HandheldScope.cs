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
    /// With the procedural (phantom-aligned) anatomy both nostrils are open: the scope enters whichever it is brought to.
    /// Tissue contact gives a haptic buzz (after a short tolerance); rubbing the mucosa for long raises a caution.
    /// Near the face NostrilGuide leads the scope into the nostril and keeps it there; the shaft in the nose shows
    /// through the skin (XRayShaft).
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
        [Tooltip("Steadies the endoscope image against tracking jitter and hand tremor.")]
        public PoseFilter smoothing = new PoseFilter { minCutoff = 1.2f };
        [Tooltip("Mucosal contact must last this long (s) before the controller vibrates.")]
        public float contactTolerance = 0.25f;

        /// <summary>(message, severity) for the monitor / speech.</summary>
        public event Action<string, string, Severity> Hint;   // voice key, message, severity

        public bool Active { get; private set; }
        public bool InNose { get; private set; }
        public bool Touching { get; private set; }

        Transform _visual;
        readonly NostrilGuide _guide = new NostrilGuide();
        Vector3 _nostril;   // head frame
        bool _sideKnown, _rightNostril;
        float _touchSince = -1, _nextBuzz, _nextHint;
        public static readonly Quaternion HeadToAnatomy = Quaternion.Euler(0, 180f, 0);   // (right, sup, ant) -> (left, sup, post)

        [Tooltip("Keeps the endoscope tip in the nasal cavity (it slides along the walls; no entering closed sinuses). FESS.")]
        public bool collide;

        /// <summary>
        /// Motion scaling inside the nose: past the nostril, positions are scaled about it by this factor (like a fulcrum),
        /// so a hand movement makes a smaller, steadier movement of the scope or instrument tip.
        /// </summary>
        public static float NoseScale = 0.7f;

        /// <summary>Head-frame point scaled about the nearer nostril once it is inside the nose (continuous at the nostril).</summary>
        public static Vector3 ScaleInNose(Vector3 h)
        {
            Vector3 nr = OperatingRoom.Nostril(true), nl = OperatingRoom.Nostril(false);
            var n = (h - nr).sqrMagnitude < (h - nl).sqrMagnitude ? nr : nl;
            var rel = h - n;
            float w = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(-rel.z / 0.015f));   // head frame +Z = anterior: inside is -Z
            return n + rel * Mathf.Lerp(1f, NoseScale, w);
        }

        /// <summary>Pushes a patient-local point (m) out of the tissue given by <paramref name="depth"/> (work frame mm).</summary>
        public static Vector3 Collide(Vector3 local, Func<Vector3, float> depth, float clearanceMm)
        {
            var mm = NasalAnatomy.PushOut(NasalAnatomy.ToWork(local * 1000f), depth, clearanceMm);
            return NasalAnatomy.FromWork(mm) * 0.001f;
        }

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
            local = patient.PhantomAligned ? HeadToAnatomy * ScaleInNose(tipH) : HeadToAnatomy * (tipH - OperatingRoom.Nostril(true)) + patient.pathStart;
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
            var shaft = Part(PrimitiveType.Cylinder, new Vector3(0, 0, shaftOffset + shaftLength * 0.5f), new Vector3(0.004f, shaftLength * 0.5f, 0.004f),
                             Quaternion.Euler(90, 0, 0), steel);                                                                                // shaft
            var xray = Shader.Find("SeptoMentor/XRayShaft");
            if (xray != null) shaft.sharedMaterials = new[] { steel, new Material(xray) };
            _visual.gameObject.SetActive(false);
        }

        MeshRenderer Part(PrimitiveType t, Vector3 pos, Vector3 scale, Quaternion rot, Material m)
        {
            var go = GameObject.CreatePrimitive(t);
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(_visual, false);
            go.transform.localPosition = pos; go.transform.localRotation = rot; go.transform.localScale = scale;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return r;
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
                smoothing.Reset();
                _guide.Reset();
                return;
            }

            var origin = head != null ? head.parent : null;
            var wPos = origin != null ? origin.TransformPoint(pos) : pos;
            var wRot = origin != null ? origin.rotation * rot : rot;
            smoothing.Filter(ref wPos, ref wRot, Time.deltaTime);

            var tip = wPos + wRot * new Vector3(0, 0, shaftOffset + shaftLength);
            var ph = room.PatientHead;
            var rotH = Quaternion.Inverse(ph.rotation) * wRot;
            // The nostril holds the 4 mm shaft: the whole scope shifts so it passes through the opening.
            var shiftH = _guide.Apply(ph.InverseTransformPoint(tip), rotH * Vector3.forward, 0.002f, Time.deltaTime, out bool entered);
            if (entered) Haptics.Pulse(hand, 0.3f, 0.025f);
            var shift = ph.TransformVector(shiftH);
            wPos += shift; tip += shift;
            transform.SetPositionAndRotation(wPos, wRot);
            var tipH = ph.InverseTransformPoint(tip);

            var patient = provider.patient;
            if (patient.PhantomAligned && !InNose)
            {
                // Both cavities are modelled: follow the nostril the scope is brought to (kept while it is inside).
                bool right = (tipH - OperatingRoom.Nostril(true)).sqrMagnitude < (tipH - OperatingRoom.Nostril(false)).sqrMagnitude;
                if (right != _rightNostril || !_sideKnown) { _rightNostril = right; _nostril = OperatingRoom.Nostril(right); }
                _sideKnown = true;
            }
            else if (!_sideKnown && patient.AnatomyRoot != null)
            {
                // Loaded anatomy: one cavity, on the side of its scope path (the head frame is mirrored into it).
                _rightNostril = patient.pathStart.x < patient.LocalBounds.center.x;
                _nostril = OperatingRoom.Nostril(_rightNostril);
                _sideKnown = true;
            }
            if (!_sideKnown) { provider.externalPose = false; return; }

            // Phantom-aligned anatomy shares the head's geometry: map 1:1. Otherwise pin the nostril to the path start.
            var local = patient.PhantomAligned ? HeadToAnatomy * ScaleInNose(tipH) : HeadToAnatomy * (tipH - _nostril) + patient.pathStart;
            if (collide && patient.PhantomAligned) local = Collide(local, NasalAnatomy.ScopeDepth, 0.8f);
            provider.externalPose = true;
            provider.externalPosition = local;
            provider.externalRotation = HeadToAnatomy * rotH;

            var rel = tipH - _nostril;
            bool wasIn = InNose;
            InNose = rel.z < 0.004f && Mathf.Abs(rel.x) < 0.02f && rel.y > -0.015f && rel.y < 0.05f && rel.z > -0.12f;
            if (InNose && !wasIn && patient.PhantomAligned)
                Hint?.Invoke(_rightNostril ? "side_right" : "side_left",
                             _rightNostril ? "Right nasal cavity (the side not operated on)." : "Left nasal cavity (operated side).", Severity.Info);
            ContactFeedback(patient.transform.TransformPoint(local));

            if (!InNose && Time.time > _nextHint && tipH.magnitude < 0.25f)
            {
                _nextHint = Time.time + 10f;
                Hint?.Invoke("scope_guide", patient.PhantomAligned
                    ? "Guide the scope into either nostril along the nasal floor; the septoplasty is done through the left."
                    : $"Guide the scope into the patient's {(_rightNostril ? "right" : "left")} nostril, along the nasal floor.", Severity.Tip);
            }
        }

        void ContactFeedback(Vector3 endoWorld)
        {
            Touching = InNose && Physics.CheckSphere(endoWorld, 0.001f);
            if (!Touching) { _touchSince = -1; return; }
            if (_touchSince < 0) { _touchSince = Time.time; _nextBuzz = Time.time + contactTolerance; }
            float held = Time.time - _touchSince;
            if (held < contactTolerance) return;   // grazes shorter than the tolerance stay silent
            // Soft, growing buzz: a scope rubbing the mucosa smears the lens and makes it bleed.
            Haptics.Rumble(hand, 0.15f + 0.2f * Mathf.Clamp01((held - contactTolerance) / 1.2f));
            if (Time.time > _nextBuzz) { _nextBuzz = Time.time + 1f; Haptics.Pulse(hand, 0.3f, 0.03f); }
            if (held > 1.5f && Time.time > _nextHint)
            {
                _nextHint = Time.time + 6f;
                Hint?.Invoke("scope_rub", "The scope tip is rubbing the mucosa - pull back slightly and keep it centred (bleeding, smeared lens).", Severity.Caution);
            }
        }
    }
}
