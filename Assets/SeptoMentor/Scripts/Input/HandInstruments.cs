using System;
using UnityEngine;
using UnityEngine.XR;

namespace SeptoMentor
{
    /// <summary>
    /// The resident's RIGHT hand works the instruments (the left holds the endoscope).
    /// Scalpel, Freer elevator, through-cutting forceps and Frazier suction lie on the Mayo stand:
    /// reach over one and press the grip to take it, press the grip again to hand it back
    /// (B swaps to the next one without reaching). The trigger closes the forceps jaws (bite) and runs the suction.
    /// The instrument in the hand is mirrored into the anatomy (VirtualInstrument), so it appears in the
    /// endoscope image exactly where it is in the patient's nose, and SeptoplastySim makes the tissue respond.
    /// </summary>
    [DefaultExecutionOrder(-90)]   // after the scope, before the provider renders
    public class HandInstruments : MonoBehaviour
    {
        public SyntheticSegmentationProvider provider;
        public OperatingRoom room;
        public SeptoplastySim sim;
        public Transform head;
        public XRNode hand = XRNode.RightHand;
        public float grabRadius = 0.12f;
        [Tooltip("Grip to instrument tip, m.")]
        public float reach = 0.19f;
        [Tooltip("Steadies the instrument against tracking jitter and hand tremor.")]
        public PoseFilter smoothing = new PoseFilter();

        /// <summary>(voice key, message, severity) for the monitor / voice.</summary>
        public event Action<string, string, Severity> Hint;

        public static readonly Structure[] Tools = { Structure.Scalpel, Structure.FreerElevator, Structure.ScissorsForceps, Structure.Suction };

        public bool Active { get; private set; }
        public Structure Held => _held >= 0 ? Tools[_held] : Structure.Background;

        class Tool
        {
            public InstrumentModels.Model model;
            public Pose rest;
            public Material steel, handle;
            public float returning = -1;   // seconds into the trip back to the tray
            public Pose from;
        }

        Tool[] _tools;
        int _held = -1, _hover = -1;
        bool _grip, _trigger;
        float[] _dist;

        void Start()
        {
            var shader = room != null && room.surfaceShader != null ? room.surfaceShader : Shader.Find("SeptoMentor/ORSurface");
            var dark = Mat(shader, new Color(0.04f, 0.04f, 0.05f), 0.3f, 0f);
            var tray = OperatingRoom.MayoTray;
            _tools = new Tool[Tools.Length];
            var labels = new GameObject("TrayLabels").transform;
            labels.SetParent(transform, false);
            for (int i = 0; i < Tools.Length; i++)
            {
                var t = _tools[i] = new Tool
                {
                    steel = Mat(shader, new Color(0.78f, 0.8f, 0.83f), 0.9f, 0.85f),
                    handle = Mat(shader, Tools[i] == Structure.FreerElevator ? new Color(0.55f, 0.57f, 0.6f) : new Color(0.7f, 0.72f, 0.75f), 0.6f, 0.6f),
                };
                // Laid out side by side on their sides, tips away from the surgeon, handles toward them.
                t.rest = new Pose(tray + new Vector3(-0.2f + 0.08f * i, 0.006f, 0.1f), Quaternion.LookRotation(Vector3.forward, Vector3.left));
                t.model = InstrumentModels.Build(Tools[i], transform, (go, f) =>
                    go.GetComponent<Renderer>().sharedMaterial = f == InstrumentModels.Finish.Dark ? dark : f == InstrumentModels.Finish.Handle ? t.handle : t.steel);
                t.model.root.SetPositionAndRotation(t.rest.position, t.rest.rotation);

                var label = new GameObject("Label").transform;
                label.SetParent(labels, false);
                label.position = t.rest.position + new Vector3(0, 0.002f, -0.24f);
                label.rotation = Quaternion.Euler(60, 0, 0);
                label.localScale = Vector3.one * 0.001f;
                var l = new Label3D(label, "Name", 90);
                l.SetBox(80, 30);
                l.Set(InstrumentModels.DisplayName(Tools[i]).Replace(" ", "\n"), new Color(0.9f, 0.95f, 1f));
            }
        }

        static Material Mat(Shader shader, Color c, float gloss, float metal)
        {
            var m = new Material(shader);
            m.SetColor("_Color", c);
            m.SetFloat("_Gloss", gloss);
            m.SetFloat("_Metal", metal);
            return m;
        }

        /// <summary>Voice command: take this instrument (handing back the one in the hand), or Background = hand it back.</summary>
        public void Request(Structure s)
        {
            int i = Array.IndexOf(Tools, s);
            if (i == _held) return;
            Release();
            if (i >= 0) Take(i);
        }

        /// <summary>B button: hand back the current instrument and take the next one (none -> scalpel -> ... -> none).</summary>
        public Structure Cycle()
        {
            int next = _held + 1;
            Release();
            if (next < Tools.Length) Take(next);
            return Held;
        }

        void Take(int i)
        {
            _held = i;
            _tools[i].returning = -1;
            Haptics.Pulse(hand, 0.4f, 0.04f);
            Hint?.Invoke("tool_" + Tools[i], InstrumentModels.DisplayName(Tools[i]) + Usage(Tools[i]), Severity.Info);
        }

        static string Usage(Structure s)
        {
            switch (s)
            {
                case Structure.Scalpel: return ": draw it along the incision line in front of the deviation, down to the cartilage.";
                case Structure.FreerElevator: return ": slide it under the flap through the incision and sweep it flat on the cartilage.";
                case Structure.ScissorsForceps: return ": trigger = bite. Remove the deviated cartilage, keep the L-strut.";
                case Structure.Suction: return ": trigger = suction. Clear the blood; it also lays the flap back.";
            }
            return "";
        }

        void Release()
        {
            if (_held < 0) return;
            var t = _tools[_held];
            t.from = new Pose(t.model.root.position, t.model.root.rotation);
            t.returning = 0;
            _held = -1;
            Haptics.Pulse(hand, 0.25f, 0.03f);
        }

        void Update()
        {
            if (_tools == null) return;
            var dev = InputDevices.GetDeviceAtXRNode(hand);
            Vector3 pos = default; Quaternion rot = Quaternion.identity;
            bool tracked = dev.isValid && dev.TryGetFeatureValue(CommonUsages.isTracked, out bool tr) && tr &&
                           dev.TryGetFeatureValue(CommonUsages.devicePosition, out pos) &&
                           dev.TryGetFeatureValue(CommonUsages.deviceRotation, out rot);
            Active = tracked && provider != null && provider.Instrument != null;
            AnimateReturns();
            if (!Active)
            {
                if (provider != null && provider.Instrument != null) provider.Instrument.SetExternal(null);
                smoothing.Reset();
                return;
            }

            var origin = head != null ? head.parent : null;
            var wPos = origin != null ? origin.TransformPoint(pos) : pos;
            var wRot = origin != null ? origin.rotation * rot : rot;
            if (_held >= 0) smoothing.Filter(ref wPos, ref wRot, Time.deltaTime);
            else smoothing.Reset();

            dev.TryGetFeatureValue(CommonUsages.gripButton, out bool grip);
            dev.TryGetFeatureValue(CommonUsages.trigger, out float trig);
            bool gripDown = grip && !_grip; _grip = grip;
            bool trigNow = trig > (_trigger ? 0.5f : 0.7f);
            bool trigDown = trigNow && !_trigger; _trigger = trigNow;

            Hover(wPos);
            if (gripDown)
            {
                if (_held >= 0) Release();
                else if (_hover >= 0) Take(_hover);
            }

            var vi = provider.Instrument;
            var patient = provider.patient;
            vi.SetExternal(patient != null ? patient.transform : null);
            if (_held < 0) { vi.Set(Structure.Background); return; }

            var t = _tools[_held];
            var tip = wPos + wRot * new Vector3(0, 0, reach);
            t.model.root.SetPositionAndRotation(tip, wRot);
            t.model.SetJaw(1f - trig);

            vi.Set(Held);
            vi.jawOpen = 1f - trig;
            if (HandheldScope.ToAnatomy(room, patient, tip, wRot, out var local, out var localRot))
            {
                vi.SetExternalPose(local, localRot);
                if (sim != null) sim.Step(Held, local, trigNow, trigDown, hand);
            }
        }

        /// <summary>
        /// Highlights the instrument the open hand is over (grip would take it). Hysteresis: the highlighted one stays
        /// until the hand is 3 cm farther out or clearly nearer another, so it does not flicker (and buzz) between neighbours.
        /// </summary>
        void Hover(Vector3 handPos)
        {
            int best = -1;
            float bestD = grabRadius;
            if (_held < 0)
            {
                if (_dist == null) _dist = new float[_tools.Length];
                var d = _dist;
                for (int i = 0; i < _tools.Length; i++)
                {
                    var r = _tools[i].model.root;
                    d[i] = _tools[i].returning >= 0 ? float.MaxValue : Vector3.Distance(handPos, r.position - r.forward * 0.13f);   // middle of the handle
                    if (d[i] < bestD) { bestD = d[i]; best = i; }
                }
                if (_hover >= 0 && d[_hover] < grabRadius + 0.03f && (best < 0 || d[_hover] < bestD + 0.02f)) best = _hover;
            }
            if (best == _hover) return;
            if (_hover >= 0) Glow(_tools[_hover], 0f);
            _hover = best;
            if (_hover >= 0) { Glow(_tools[_hover], 0.35f); Haptics.Pulse(hand, 0.15f, 0.02f); }
        }

        static void Glow(Tool t, float amount)
        {
            t.steel.SetFloat("_Emission", amount);
            t.handle.SetFloat("_Emission", amount);
        }

        void AnimateReturns()
        {
            foreach (var t in _tools)
            {
                if (t.returning < 0) continue;
                t.returning += Time.deltaTime;
                float u = Mathf.SmoothStep(0, 1, Mathf.Clamp01(t.returning / 0.35f));
                t.model.root.SetPositionAndRotation(Vector3.Lerp(t.from.position, t.rest.position, u) + Vector3.up * 0.05f * Mathf.Sin(u * Mathf.PI),
                                                    Quaternion.Slerp(t.from.rotation, t.rest.rotation, u));
                t.model.SetJaw(1f);
                if (u >= 1f) t.returning = -1;
            }
        }
    }
}
