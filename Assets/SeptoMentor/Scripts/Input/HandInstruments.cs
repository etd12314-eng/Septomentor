using System;
using UnityEngine;
using UnityEngine.XR;

namespace SeptoMentor
{
    /// <summary>
    /// The resident's RIGHT hand works the instruments (the left holds the endoscope).
    /// The procedure's tray lies on the Mayo stand (septoplasty: scalpel, Freer, through-cutting forceps, Frazier suction;
    /// FESS: sickle knife, ball probe, Freer, Blakesley straight and 45 up, backbiter, microdebrider, curved suction):
    /// reach over one and press the grip to take it; to hand it back press the grip again over the tray
    /// (squeezing the grip while operating does not drop it; B swaps to the next one without reaching).
    /// Near the face NostrilGuide leads the instrument into the nostril and keeps its shaft in it, and the part
    /// hidden in the nose shows through the skin (XRayShaft). The trigger closes the forceps jaws (bite), runs the suction and
    /// the microdebrider. The instrument in the hand is mirrored into the anatomy (VirtualInstrument), so it appears in
    /// the endoscope image exactly where it is in the patient's nose, and SeptoplastySim / FessSim make the tissue respond.
    /// </summary>
    [DefaultExecutionOrder(-90)]   // after the scope, before the provider renders
    public class HandInstruments : MonoBehaviour
    {
        public SyntheticSegmentationProvider provider;
        public OperatingRoom room;
        public SeptoplastySim sim;
        public FessSim fess;
        public Transform head;
        public XRNode hand = XRNode.RightHand;
        public float grabRadius = 0.12f;
        [Tooltip("Steadies the instrument against tracking jitter and hand tremor.")]
        public PoseFilter smoothing = new PoseFilter();

        /// <summary>(voice key, message, severity) for the monitor / voice.</summary>
        public event Action<string, string, Severity> Hint;

        public static readonly Tool[] SeptoplastyTray = { Tool.Scalpel, Tool.Freer, Tool.ThroughCut, Tool.Suction };
        public static readonly Tool[] FessTray =
            { Tool.SickleKnife, Tool.BallProbe, Tool.Freer, Tool.Blakesley, Tool.BlakesleyUp, Tool.JCurette, Tool.Backbiter, Tool.Microdebrider, Tool.CurvedSuction };

        public Procedure Procedure { get; private set; } = Procedure.Septoplasty;
        public Tool[] Tools { get; private set; } = SeptoplastyTray;
        public bool Active { get; private set; }
        public Tool Held => _held >= 0 ? Tools[_held] : Tool.None;

        class Slot
        {
            public InstrumentModels.Model model;
            public Pose rest;
            public Material steel, handle;
            public float returning = -1;   // seconds into the trip back to the tray
            public Pose from;
        }

        Slot[] _tools;
        int _held = -1, _hover = -1;
        readonly NostrilGuide _guide = new NostrilGuide();
        Material _xray;
        float _nextGripHint;
        bool _rimBuzz;
        /// <summary>Instrument shaft radius for the nostril (m).</summary>
        const float ShaftRadius = 0.0015f;
        bool _grip, _trigger;
        float[] _dist;
        Transform _trayRoot;

        void Start() => BuildTray();

        /// <summary>Lays out the procedure's instruments on the Mayo stand (the one in the hand goes back first).</summary>
        public void SetProcedure(Procedure p)
        {
            if (p == Procedure && _tools != null) return;
            Release();
            Procedure = p;
            Tools = p == Procedure.Fess ? FessTray : SeptoplastyTray;
            BuildTray();
        }

        void BuildTray()
        {
            if (_trayRoot != null) Destroy(_trayRoot.gameObject);
            _trayRoot = new GameObject("Tray").transform;
            _trayRoot.SetParent(transform, false);
            _held = _hover = -1;
            _dist = null;
            var shader = room != null && room.surfaceShader != null ? room.surfaceShader : Shader.Find("SeptoMentor/ORSurface");
            var dark = Mat(shader, new Color(0.04f, 0.04f, 0.05f), 0.3f, 0f);
            var tray = OperatingRoom.MayoTray;
            _tools = new Slot[Tools.Length];
            float spacing = Mathf.Min(0.08f, 0.46f / Tools.Length);
            for (int i = 0; i < Tools.Length; i++)
            {
                var t = _tools[i] = new Slot
                {
                    steel = Mat(shader, new Color(0.78f, 0.8f, 0.83f), 0.9f, 0.85f),
                    handle = Mat(shader, Tools[i] == Tool.Freer || Tools[i] == Tool.Microdebrider ? new Color(0.55f, 0.57f, 0.6f) : new Color(0.7f, 0.72f, 0.75f), 0.6f, 0.6f),
                };
                // Laid out side by side on their sides, tips away from the surgeon, handles toward them.
                t.rest = new Pose(tray + new Vector3(-0.08f + (i - (Tools.Length - 1) * 0.5f) * spacing, 0.006f, 0.1f), Quaternion.LookRotation(Vector3.forward, Vector3.left));
                t.model = InstrumentModels.Build(Tools[i], _trayRoot, (go, f) =>
                    go.GetComponent<Renderer>().sharedMaterial = f == InstrumentModels.Finish.Dark ? dark : f == InstrumentModels.Finish.Handle ? t.handle : t.steel);
                t.model.root.SetPositionAndRotation(t.rest.position, t.rest.rotation);

                var label = new GameObject("Label").transform;
                label.SetParent(_trayRoot, false);
                label.position = t.rest.position + new Vector3(0, 0.002f, -0.24f);
                label.rotation = Quaternion.Euler(60, 0, 0);
                label.localScale = Vector3.one * 0.001f;
                var l = new Label3D(label, "Name", Tools.Length > 5 ? 70 : 90);
                l.SetBox(spacing * 1000f, 30);
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

        /// <summary>Voice command: take this instrument (handing back the one in the hand), or None = hand it back.</summary>
        public void Request(Tool s)
        {
            int i = Array.IndexOf(Tools, s);
            if (i == _held) return;
            Release();
            if (i >= 0) Take(i);
        }

        /// <summary>B button: hand back the current instrument and take the next one (none -> scalpel -> ... -> none).</summary>
        public Tool Cycle()
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
            SetXRay(_tools[i], true);
            _guide.Reset();
            Haptics.Pulse(hand, 0.4f, 0.04f);
            Hint?.Invoke("tool_" + Tools[i], InstrumentModels.DisplayName(Tools[i]) + Usage(Tools[i]), Severity.Info);
        }

        string Usage(Tool s)
        {
            switch (s)
            {
                case Tool.Scalpel: return ": draw it along the incision line, about 1 cm behind the caudal septal edge, down to the cartilage.";
                case Tool.Freer: return Procedure == Procedure.Fess ? ": medialise the middle turbinate gently; palpate the uncinate."
                                                                     : ": slide it under the flap through the incision and sweep it flat on the cartilage.";
                case Tool.ThroughCut: return ": trigger = bite. Excise the spur, keep the L-strut.";
                case Tool.Suction: return ": trigger = suction. Clear the blood; it also lays the flap back.";
                case Tool.SickleKnife: return ": incise the uncinate along its attachment, point toward the lateral wall - not deeper.";
                case Tool.BallProbe: return ": slide behind the uncinate and feel for the ostia (maxillary, bulla, sphenoid, frontal).";
                case Tool.Blakesley: return ": trigger = bite. Uncinate remnants, bulla, ethmoid cells - stay medial to the orbit.";
                case Tool.BlakesleyUp: return ": trigger = bite. Up-biting for the agger nasi and the frontal recess.";
                case Tool.Backbiter: return ": trigger = bite backward. Takes the uncinate and widens the antrostomy forward.";
                case Tool.Microdebrider: return ": trigger = run. Shaves soft tissue and thin bone; keep the window in view.";
                case Tool.CurvedSuction: return ": trigger = suction. Reaches round into the maxillary sinus and the frontal recess.";
                case Tool.JCurette: return ": hook it behind a cell wall and pull forward (trigger) - it cracks the ethmoid cells open.";
            }
            return "";
        }

        void Release()
        {
            if (_held < 0) return;
            var t = _tools[_held];
            t.from = new Pose(t.model.root.position, t.model.root.rotation);
            t.returning = 0;
            SetXRay(t, false);
            _held = -1;
            _guide.Reset();
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
                _guide.Reset();
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
                if (_held >= 0)
                {
                    if (OverTray(wPos)) Release();
                    else if (Time.time > _nextGripHint)
                    {
                        _nextGripHint = Time.time + 20f;
                        Hint?.Invoke("tool_return", "To put the instrument down, press the grip over the tray (or B for the next one).", Severity.Tip);
                    }
                }
                else if (_hover >= 0) Take(_hover);
            }

            var vi = provider.Instrument;
            var patient = provider.patient;
            vi.SetExternal(patient != null ? patient.transform : null);
            if (_held < 0) { vi.Set(Tool.None); return; }

            var t = _tools[_held];
            var tip = wPos + wRot * new Vector3(0, 0, t.model.grip);   // the hand holds each instrument where a surgeon would
            tip += GuideIntoNostril(tip, wRot * Vector3.forward);
            t.model.root.SetPositionAndRotation(tip, wRot);
            t.model.SetJaw(1f - trig);
            t.model.Spin(trigNow, Time.deltaTime);

            vi.Set(Held);
            vi.jawOpen = 1f - trig;
            vi.running = trigNow;
            if (HandheldScope.ToAnatomy(room, patient, tip, wRot, out var local, out var localRot))
            {
                // FESS: the tip may press ~1 mm into a wall (to bite it) but slides along it instead of passing through.
                if (Procedure == Procedure.Fess && patient.PhantomAligned) local = HandheldScope.Collide(local, NasalAnatomy.InstrumentDepth, -1.2f);
                vi.SetExternalPose(local, localRot);
                if (Procedure == Procedure.Fess) { if (fess != null) fess.Tick(Held, local, localRot, trigNow, trigDown, hand); }
                else if (sim != null) sim.Step(InstrumentModels.LabelOf(Held), local, trigNow, trigDown, hand);
            }
        }

        /// <summary>Shift (world) that keeps the instrument's shaft in the nostril; haptic click on entering, buzz on the rim.</summary>
        Vector3 GuideIntoNostril(Vector3 tip, Vector3 dir)
        {
            var ph = room != null ? room.PatientHead : null;
            if (ph == null) return Vector3.zero;
            var shift = _guide.Apply(ph.InverseTransformPoint(tip), ph.InverseTransformDirection(dir), ShaftRadius, Time.deltaTime, out bool entered);
            if (entered) Haptics.Pulse(hand, 0.35f, 0.025f);
            bool rim = _guide.RimPress > 0.5f;
            if (rim && !_rimBuzz) Haptics.Pulse(hand, 0.2f, 0.02f);   // levering against the ala / columella
            _rimBuzz = rim;
            return ph.TransformVector(shift);
        }

        /// <summary>Within reach of the Mayo stand's towel (where instruments are put down).</summary>
        bool OverTray(Vector3 handPos)
        {
            foreach (var s in _tools)
                if (Vector3.Distance(handPos, s.rest.position) < 0.22f) return true;
            return false;
        }

        /// <summary>The held instrument's parts hidden in the nose show through the skin.</summary>
        void SetXRay(Slot t, bool on)
        {
            if (on && _xray == null)
            {
                var sh = Shader.Find("SeptoMentor/XRayShaft");
                if (sh == null) return;
                _xray = new Material(sh);
            }
            foreach (var r in t.model.root.GetComponentsInChildren<MeshRenderer>())
            {
                var m = r.sharedMaterial;
                r.sharedMaterials = on ? new[] { m, _xray } : new[] { m };
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
                    d[i] = _tools[i].returning >= 0 ? float.MaxValue : Vector3.Distance(handPos, r.position - r.forward * _tools[i].model.grip);   // where it is held
                    if (d[i] < bestD) { bestD = d[i]; best = i; }
                }
                if (_hover >= 0 && d[_hover] < grabRadius + 0.03f && (best < 0 || d[_hover] < bestD + 0.02f)) best = _hover;
            }
            if (best == _hover) return;
            if (_hover >= 0) Glow(_tools[_hover], 0f);
            _hover = best;
            if (_hover >= 0) { Glow(_tools[_hover], 0.35f); Haptics.Pulse(hand, 0.15f, 0.02f); }
        }

        static void Glow(Slot t, float amount)
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
