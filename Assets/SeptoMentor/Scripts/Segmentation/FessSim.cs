using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using Wall = SeptoMentor.NasalAnatomy.Wall;

namespace SeptoMentor
{
    /// <summary>
    /// Functional endoscopic sinus surgery on the procedural anatomy (NasalAnatomy), either side:
    ///   1. Find the ostia - maxillary (behind the uncinate), ethmoid bulla, sphenoid (sphenoethmoidal recess), frontal
    ///      (frontal recess): touch them with the ball probe, or hold the endoscope on them (close, centred, in view).
    ///   2. Uncinectomy - sickle knife incision, then the backbiter / Blakesley / microdebrider take the uncinate.
    ///   3. Maxillary antrostomy - widen the natural ostium (backbiter, Blakesley, microdebrider).
    ///   4. Anterior ethmoidectomy - open the bulla and the anterior ethmoid cells one by one (Blakesley, J curette,
    ///      shaver); agger nasi (up-biting Blakesley, curette) clears the frontal recess.
    ///   5. Posterior ethmoidectomy - through the basal lamella into the posterior cells.
    /// Each ethmoid cell is its own chunk (Fess_X_Cells_id): opening one shows its mucosa-lined inside and the septa of
    /// the cells behind it. Sinus linings are hidden until their sinus has been opened (the scope sees the nose only), and
    /// the endoscope cannot pass into a sinus or cell that is still closed (NasalAnatomy.ScopeDepth / IsOpened).
    ///   6. Sphenoidotomy - widen the sphenoid ostium.
    /// The removable lamellae are pre-cut chunks (Fess_[L|R]_Group_k); a bite or the shaver takes the chunk at the tip.
    /// Danger zones (lamina papyracea, skull base, anterior ethmoidal artery, lateral sphenoid wall, nasolacrimal duct,
    /// middle turbinate) give warnings. Teaching model: plausible, not validated, distances.
    /// </summary>
    public class FessSim : MonoBehaviour
    {
        public SyntheticSegmentationProvider provider;
        [Tooltip("Plain wall contact shallower than this (mm) does not vibrate.")]
        public float contactTolerance = 0.8f;

        public enum Step { FindOstia, Uncinectomy, Antrostomy, AnteriorEthmoid, PosteriorEthmoid, Sphenoidotomy, Done }

        /// <summary>(voice key, message, severity) for the monitor / voice / log.</summary>
        public event Action<string, string, Severity> Hint;

        public bool Available { get; private set; }
        public Step Current { get; private set; } = Step.FindOstia;
        public bool RightSide { get; private set; }
        public int Warnings { get; private set; }
        public string Progress { get; private set; } = "";

        VirtualPatient Patient => provider != null ? provider.patient : null;

        class Chunk { public GameObject go; public Vector3[] mm; public Bounds bounds; public int cell; }   // vertices in the side's work frame, mm
        class Group { public readonly List<Chunk> chunks = new List<Chunk>(); public int Removed => chunks.FindAll(c => !c.go.activeSelf).Count; }

        // [side 0 = left (operated frame), 1 = right][group name]
        readonly Dictionary<string, Group>[] _groups = { new Dictionary<string, Group>(), new Dictionary<string, Group>() };
        readonly bool[,] _found = new bool[2, NasalAnatomy.OstiumTargets.Length];
        readonly Dictionary<string, float> _said = new Dictionary<string, float>();
        readonly Dictionary<string, GameObject>[] _sinus = { new Dictionary<string, GameObject>(), new Dictionary<string, GameObject>() };
        readonly HashSet<int>[] _openCells = { new HashSet<int>(), new HashSet<int>() };
        bool _searched, _incised;
        float _nextShave, _seenSince = -1;
        int _seenTarget = -1;


        bool Ready()
        {
            if (Available) return true;
            if (_searched || Patient == null || Patient.AnatomyRoot == null) return false;
            _searched = true;
            foreach (Transform t in Patient.AnatomyRoot)
            {
                foreach (var sn in new[] { "MaxillarySinus", "SphenoidSinus", "FrontalSinus" })
                {
                    if (t.name == sn) _sinus[0][sn] = t.gameObject;
                    else if (t.name == "Right_" + sn) _sinus[1][sn] = t.gameObject;
                }
                // Fess_L_Bulla_2, Fess_R_Cells_512347
                var parts = t.name.Split('_');
                if (parts.Length != 4 || parts[0] != "Fess") continue;
                int side = parts[1] == "R" ? 1 : 0;
                if (!_groups[side].TryGetValue(parts[2], out var g)) _groups[side][parts[2]] = g = new Group();
                var mesh = t.GetComponent<MeshFilter>().sharedMesh;
                var v = mesh.vertices;
                var mm = new Vector3[v.Length];
                var b = new Bounds();
                for (int i = 0; i < v.Length; i++)
                {
                    var w = NasalAnatomy.ToWork(v[i] * 1000f);
                    if (side == 1) w.x = -w.x;
                    mm[i] = w;
                    if (i == 0) b = new Bounds(w, Vector3.zero); else b.Encapsulate(w);
                }
                int.TryParse(parts[3], out int cell);
                g.chunks.Add(new Chunk { go = t.gameObject, mm = mm, bounds = b, cell = parts[2] == "Cells" ? cell : -1 });
            }
            Available = _groups[0].Count > 0;
            if (Available) NasalAnatomy.IsOpened = IsOpened;
            ShowSinuses();
            Debug.Log(Available ? $"[SeptoMentor] FESS: {_groups[0].Count} removable groups per side." : "[SeptoMentor] FESS off: no sinus pieces in the anatomy.");
            UpdateProgress();
            return Available;
        }

        /// <summary>Restores every lamella and forgets the found ostia (restart / entering FESS).</summary>
        public void ResetAll()
        {
            if (!Ready()) return;
            foreach (var side in _groups) foreach (var g in side.Values) foreach (var c in g.chunks) c.go.SetActive(true);
            foreach (var o in _openCells) o.Clear();
            Array.Clear(_found, 0, _found.Length);
            ShowSinuses();
            _incised = false;
            Current = Step.FindOstia;
            Warnings = 0;
            UpdateProgress();
        }

        /// <summary>Skips to the next step (A button / "sonraki adım").</summary>
        public void NextStep()
        {
            if (!Ready()) return;
            Current = Current == Step.Done ? Step.FindOstia : Current + 1;
            Brief();
            UpdateProgress();
        }

        public void Brief()
        {
            switch (Current)
            {
                case Step.FindOstia: Say("fess_find", "Find the ostia: maxillary (behind the uncinate), ethmoid bulla, sphenoid (medial to the superior turbinate), frontal (frontal recess). Probe them or hold the scope on them.", Severity.Info, 0f, true); break;
                case Step.Uncinectomy: Say("fess_unc", "Uncinectomy: incise the uncinate with the sickle knife, then take it with the backbiter or Blakesley.", Severity.Info, 0f, true); break;
                case Step.Antrostomy: Say("fess_antro", "Maxillary antrostomy: widen the natural ostium backward and down - not forward (nasolacrimal duct).", Severity.Info, 0f, true); break;
                case Step.AnteriorEthmoid: Say("fess_ant_eth", "Anterior ethmoidectomy: open the bulla, then the anterior ethmoid cells one by one with the Blakesley or the J curette; the up-biting Blakesley takes the agger nasi to clear the frontal recess.", Severity.Info, 0f, true); break;
                case Step.PosteriorEthmoid: Say("fess_post_eth", "Posterior ethmoidectomy: through the basal lamella, inferomedially; stay below the skull base, medial to the orbit.", Severity.Info, 0f, true); break;
                case Step.Sphenoidotomy: Say("fess_sphen", "Sphenoidotomy: widen the sphenoid ostium medially and down - never laterally (carotid, optic nerve).", Severity.Info, 0f, true); break;
                case Step.Done: Say("fess_done", "Sinus surgery complete. Check haemostasis and the middle turbinate position.", Severity.Info, 0f, true); break;
            }
        }

        public float ProgressFraction
        {
            get
            {
                if (!Available) return 0;
                int s = RightSide ? 1 : 0;
                switch (Current)
                {
                    case Step.FindOstia: return FoundCount(s) / (float)NasalAnatomy.OstiumTargets.Length;
                    case Step.Uncinectomy: return Fraction(s, "Uncinate");
                    case Step.Antrostomy: return Fraction(s, "Antrostomy");
                    case Step.AnteriorEthmoid: return Mathf.Clamp01((Fraction(s, "Bulla") + Fraction(s, "AggerNasi") + CellFraction(s, true) / AntCells) / 3f);
                    case Step.PosteriorEthmoid: return Mathf.Clamp01((Fraction(s, "BasalLamella") + CellFraction(s, false) / PostCells) / 2f);
                    case Step.Sphenoidotomy: return Fraction(s, "Sphenoid");
                }
                return 1;
            }
        }

        /// <summary>Keys of the ostia not yet found on the side in use.</summary>
        public List<string> Remaining()
        {
            var l = new List<string>();
            int s = RightSide ? 1 : 0;
            for (int i = 0; i < NasalAnatomy.OstiumTargets.Length; i++) if (!_found[s, i]) l.Add(NasalAnatomy.OstiumTargets[i].key);
            return l;
        }

        int FoundCount(int s) { int n = 0; for (int i = 0; i < NasalAnatomy.OstiumTargets.Length; i++) if (_found[s, i]) n++; return n; }

        float Fraction(int s, string g) => _groups[s].TryGetValue(g, out var gr) && gr.chunks.Count > 0 ? gr.Removed / (float)gr.chunks.Count : 0f;

        /// <summary>Share of opened ethmoid cells in front of (anterior) or behind the basal lamella.</summary>
        float CellFraction(int s, bool anterior)
        {
            if (!_groups[s].TryGetValue("Cells", out var g)) return 0f;
            int n = 0, open = 0;
            foreach (var c in g.chunks)
            {
                if (c.bounds.center.z < -17f != anterior) continue;
                n++;
                if (!c.go.activeSelf) open++;
            }
            return n > 0 ? open / (float)n : 0f;
        }

        // Cells to open for a step (not every outlying cell: the ones that matter, roughly).
        const float AntCells = 0.35f, PostCells = 0.35f;

        int OpenCells(int s, bool anterior)
        {
            if (!_groups[s].TryGetValue("Cells", out var g)) return 0;
            int open = 0;
            foreach (var c in g.chunks) if (c.bounds.center.z < -17f == anterior && !c.go.activeSelf) open++;
            return open;
        }

        /// <summary>Sinus linings appear once the sinus has been opened (until then the scope sees the nose only).</summary>
        void ShowSinuses()
        {
            for (int s = 0; s < 2; s++)
            {
                if (_sinus[s].TryGetValue("MaxillarySinus", out var m)) m.SetActive(Fraction(s, "Antrostomy") > 0f);
                if (_sinus[s].TryGetValue("SphenoidSinus", out var sp)) sp.SetActive(Fraction(s, "Sphenoid") > 0f);
                if (_sinus[s].TryGetValue("FrontalSinus", out var fr)) fr.SetActive(Fraction(s, "AggerNasi") > 0f);
            }
        }

        /// <summary>Whether the point (side work frame, mm) lies in a space the resident has opened.</summary>
        bool IsOpened(Vector3 q, bool right)
        {
            int s = right ? 1 : 0;
            foreach (var g in _groups[s].Values)
                foreach (var c in g.chunks)
                    if (!c.go.activeSelf && c.bounds.SqrDistance(q) < 2.25f) return true;
            if (q.x < -14f && q.x > -26.5f && q.y > 0.5f && q.y < 21.5f && q.z > -51.5f && q.z < -2.5f && _openCells[s].Contains(NasalAnatomy.CellId(q))) return true;
            if (Fraction(s, "Antrostomy") > 0f && Inside(q, new Vector3(-34.5f, -8.5f, -40f), new Vector3(11.5f, 13.5f, 17f))) return true;
            if (Fraction(s, "Sphenoid") > 0f && Inside(q, new Vector3(-7f, 17f, 13f), new Vector3(6f, 6f, 9f))) return true;
            return false;
        }

        static bool Inside(Vector3 q, Vector3 c, Vector3 r)
        {
            var d = new Vector3((q.x - c.x) / r.x, (q.y - c.y) / r.y, (q.z - c.z) / r.z);
            return d.sqrMagnitude < 1f;
        }

        void UpdateProgress()
        {
            int s = RightSide ? 1 : 0;
            string side = RightSide ? "R" : "L";
            Progress = $"FESS {side}: {StepName(Current)}  |  ostia {FoundCount(s)}/{NasalAnatomy.OstiumTargets.Length}" +
                       $"  unc {Pct(s, "Uncinate")}  antro {Pct(s, "Antrostomy")}  bulla {Pct(s, "Bulla")}  agger {Pct(s, "AggerNasi")}" +
                       $"  cells {OpenCells(s, true)}+{OpenCells(s, false)}  lamella {Pct(s, "BasalLamella")}  sphen {Pct(s, "Sphenoid")}";
        }

        string Pct(int s, string g) => Mathf.RoundToInt(100f * Fraction(s, g)) + "%";

        public static string StepName(Step s)
        {
            switch (s)
            {
                case Step.FindOstia: return "find the ostia";
                case Step.Uncinectomy: return "uncinectomy";
                case Step.Antrostomy: return "maxillary antrostomy";
                case Step.AnteriorEthmoid: return "anterior ethmoidectomy";
                case Step.PosteriorEthmoid: return "posterior ethmoidectomy";
                case Step.Sphenoidotomy: return "sphenoidotomy";
            }
            return "done";
        }

        // ------------------------------------------------------------------ per frame

        void Update()
        {
            if (!Ready() || provider == null || provider.EndoCamera == null) return;
            ScopeSees();
        }

        /// <summary>An ostium counts as found when the endoscope looks straight at it from close by with nothing in between.</summary>
        void ScopeSees()
        {
            var cam = provider.EndoCamera.transform;
            var root = Patient.transform;
            var eye = NasalAnatomy.ToWork(root.InverseTransformPoint(cam.position) * 1000f);
            var fwd = NasalAnatomy.ToWork(root.InverseTransformDirection(cam.forward));
            bool right = eye.x > 0f;
            int s = right ? 1 : 0;
            if (right) { eye.x = -eye.x; fwd.x = -fwd.x; }
            int best = -1;
            for (int i = 0; i < NasalAnatomy.OstiumTargets.Length; i++)
            {
                if (_found[s, i]) continue;
                var tgt = NasalAnatomy.OstiumTargets[i];
                var d = tgt.at - eye;
                if (d.magnitude > 16f || Vector3.Angle(fwd, d) > 18f) continue;
                var tw = TargetWorld(tgt.at, right);
                if (Physics.Linecast(cam.position, tw, out var hit) && hit.distance < Vector3.Distance(cam.position, tw) - 0.0015f) continue;
                best = i;
                break;
            }
            if (best != _seenTarget) { _seenTarget = best; _seenSince = Time.time; }
            if (best >= 0 && Time.time - _seenSince > 0.6f) Found(s, best, "scope");
        }

        Vector3 TargetWorld(Vector3 workMm, bool right)
        {
            var w = workMm;
            if (right) w.x = -w.x;
            return Patient.transform.TransformPoint(NasalAnatomy.FromWork(w) * 0.001f);
        }

        void Found(int s, int i, string how)
        {
            if (_found[s, i]) return;
            _found[s, i] = true;
            var t = NasalAnatomy.OstiumTargets[i];
            Say("found_" + t.key, $"{char.ToUpper(t.name[0]) + t.name.Substring(1)} found ({(s == 1 ? "right" : "left")}, {how}).", Severity.Info, 0f, true);
            Haptics.Pattern(XRNode.RightHand, 2, 0.4f, 0.03f, 0.05f);
            if (Current == Step.FindOstia && FoundCount(s) == NasalAnatomy.OstiumTargets.Length) Advance(Step.Uncinectomy);
            UpdateProgress();
        }

        void Advance(Step next)
        {
            if (Current >= next) return;
            Current = next;
            Haptics.Both(0.5f, 0.1f);
            Brief();
            UpdateProgress();
        }

        /// <summary>The instrument tip (patient local space, m) this frame, from HandInstruments.</summary>
        public void Tick(Tool tool, Vector3 tipLocal, Quaternion rotLocal, bool trigger, bool triggerDown, XRNode hand)
        {
            if (!Ready()) return;
            var mm = NasalAnatomy.ToWork(tipLocal * 1000f);
            float depth = NasalAnatomy.Depth(mm, out var wall, out bool other);
            if (mm.z < -74f) RightSide = other;   // decided in the vestibule, kept deeper in
            int s = RightSide ? 1 : 0;
            var q = mm;
            if (RightSide) q.x = -q.x;
            bool inside = q.z > -80f && q.z < 30f && q.x > -48f && q.x < 8f && q.y > -31f && q.y < 56f;
            if (!inside) return;

            // A removed lamella is air now (the depth field does not know).
            var removed = RemovedChunkAt(s, q, 1.0f);
            if (removed) depth = Mathf.Min(depth, -0.5f);

            if (tool == Tool.BallProbe)
                for (int i = 0; i < NasalAnatomy.OstiumTargets.Length; i++)
                    if ((q - NasalAnatomy.OstiumTargets[i].at).magnitude < NasalAnatomy.OstiumTargets[i].radius) Found(s, i, "probe");

            // Bites and the shaver remove what is at the tip; the J curette breaks a cell wall when pulled (trigger) or pressed in.
            bool cutting = (InstrumentModels.Bites(tool) || tool == Tool.JCurette) && triggerDown || tool == Tool.Microdebrider && trigger ||
                           tool == Tool.JCurette && depth > 0.7f && Time.time >= _nextShave;
            if (tool == Tool.SickleKnife && depth > 0.2f && Near(s, "Uncinate", q, 1.2f) != null && !_incised)
            {
                _incised = true;
                Haptics.Pulse(hand, 0.5f, 0.02f);
                Say("unc_incised", "Uncinate incised. Now take it with the backbiter or the Blakesley.", Severity.Info, 0f, true);
                if (Current < Step.Uncinectomy) Current = Step.Uncinectomy;
                UpdateProgress();
            }
            if (cutting) Cut(tool, s, q, depth, wall, hand);
            Danger(tool, q, depth, wall, cutting);

            float rumble = depth > contactTolerance ? 0.12f + 0.4f * Mathf.Clamp01((depth - contactTolerance) / 3f) : 0f;
            if (tool == Tool.Microdebrider && trigger) rumble = Mathf.Max(rumble, 0.12f);   // the motor
            if (rumble > 0) Haptics.Rumble(hand, rumble);
        }

        void Cut(Tool tool, int s, Vector3 q, float depth, Wall wall, XRNode hand)
        {
            if ((tool == Tool.Microdebrider || tool == Tool.JCurette) && Time.time < _nextShave) return;
            _nextShave = Time.time + (tool == Tool.JCurette ? 0.4f : 0.25f);
            Chunk best = null; string bestGroup = null; float bestD = tool == Tool.Microdebrider ? 1.6f : 2.2f;
            foreach (var kv in _groups[s])
            {
                if (kv.Key == "Uncinate" && !_incised && tool != Tool.Backbiter && tool != Tool.Microdebrider) continue;   // needs the incision first
                if (tool == Tool.JCurette && (kv.Key == "Antrostomy" || kv.Key == "Sphenoid")) continue;                  // the curette takes thin cell walls only
                foreach (var c in kv.Value.chunks)
                {
                    if (!c.go.activeSelf || c.bounds.SqrDistance(q) > bestD * bestD) continue;
                    float d = MinDistance(c, q);
                    if (d < bestD) { bestD = d; best = c; bestGroup = kv.Key; }
                }
            }
            if (best == null)
            {
                if (InstrumentModels.Bites(tool)) Haptics.Pulse(hand, depth > 0.5f ? 0.5f : 0.15f, 0.02f);
                if (InstrumentModels.Bites(tool) && wall == Wall.MidTurb && depth > 0.3f)
                    Say("mt_bite", "That is the middle turbinate - preserve it; bite lateral to it.", Severity.Caution, 20f);
                return;
            }
            best.go.SetActive(false);
            // Eggshell crack of a thin lamella, then the crunch.
            Haptics.Pulse(hand, 0.9f, 0.05f);
            Haptics.Pattern(hand, 3, 0.45f, 0.012f, 0.02f);
            if (bestGroup == "Uncinate") _incised = true;
            if (best.cell >= 0)
            {
                _openCells[s].Add(best.cell);
                if (_openCells[s].Count == 1) Say("cell_open", "Ethmoid cell opened: its mucosa-lined inside and the walls of the next cells are in view.", Severity.Info, 0f, true);
            }
            ShowSinuses();
            CheckSteps(s);
            UpdateProgress();
        }

        void CheckSteps(int s)
        {
            if (Current <= Step.Uncinectomy && Fraction(s, "Uncinate") >= 0.75f)
            { Say("done_unc", "Uncinectomy done: the infundibulum and the maxillary ostium are open to view.", Severity.Info, 0f, true); Advance(Step.Antrostomy); }
            if (Current == Step.Antrostomy && Fraction(s, "Antrostomy") >= 0.66f)
            { Say("done_antro", "Antrostomy done: look into the maxillary sinus with the angled scope.", Severity.Info, 0f, true); Advance(Step.AnteriorEthmoid); }
            if (Current == Step.AnteriorEthmoid && Fraction(s, "Bulla") >= 0.75f && Fraction(s, "AggerNasi") >= 0.5f && CellFraction(s, true) >= AntCells)
            { Say("done_ant_eth", "Anterior ethmoid open, frontal recess clear.", Severity.Info, 0f, true); Advance(Step.PosteriorEthmoid); }
            if (Current == Step.PosteriorEthmoid && Fraction(s, "BasalLamella") >= 0.5f && CellFraction(s, false) >= PostCells)
            { Say("done_post_eth", "Posterior ethmoid cells open.", Severity.Info, 0f, true); Advance(Step.Sphenoidotomy); }
            if (Current == Step.Sphenoidotomy && Fraction(s, "Sphenoid") >= 0.5f)
                Advance(Step.Done);
        }

        static float MinDistance(Chunk c, Vector3 q)
        {
            float best = float.MaxValue;
            var v = c.mm;
            for (int i = 0; i < v.Length; i += 2) { float d = (v[i] - q).sqrMagnitude; if (d < best) best = d; }
            return Mathf.Sqrt(best);
        }

        Chunk Near(int s, string group, Vector3 q, float r)
        {
            if (!_groups[s].TryGetValue(group, out var g)) return null;
            foreach (var c in g.chunks) if (c.go.activeSelf && c.bounds.SqrDistance(q) < r * r && MinDistance(c, q) < r) return c;
            return null;
        }

        bool RemovedChunkAt(int s, Vector3 q, float r)
        {
            foreach (var g in _groups[s].Values)
                foreach (var c in g.chunks)
                    if (!c.go.activeSelf && c.bounds.SqrDistance(q) < r * r) return true;
            return false;
        }

        /// <summary>Danger zones: warnings when an instrument presses or cuts into them.</summary>
        void Danger(Tool tool, Vector3 q, float depth, Wall wall, bool cutting)
        {
            bool ethmoidZone = q.y > -2f && q.y < 26f && q.z > -56f && q.z < -2f;
            bool pressing = depth > 0.6f;
            if (ethmoidZone && q.x < -24.5f && (pressing || cutting))
                Warn("orbit", "Lamina papyracea! The orbit is just lateral - stay medial (orbital fat, medial rectus).");
            else if (q.y > 22.5f && q.z > -50f && q.z < 0f && (pressing || cutting))
                Warn("skull_base", "Skull base! Keep below the ethmoid roof - CSF leak risk.");
            else if (cutting && (q - new Vector3(-14f, 22f, -42f)).magnitude < 3.5f)
                Warn("ethmoid_artery", "Anterior ethmoidal artery at the skull base behind the frontal recess - do not bite here.");
            else if (cutting && q.z > 4f && q.y > 10f && q.x < -11.5f)
                Warn("sphenoid_lateral", "Lateral sphenoid wall: carotid artery and optic nerve! Open the sphenoid medially and inferiorly only.");
            else if (cutting && q.z < -46f && q.y < 4f && q.x < -18f)
                Warn("nld", "Nasolacrimal duct in front: do not extend the antrostomy forward.");
            else if (pressing && wall == Wall.MidTurb && depth > 1.5f)
                Say("mt_press", "Gentle on the middle turbinate: it fractures and lateralises.", Severity.Caution, 25f);
        }

        void Warn(string key, string msg)
        {
            if (_said.TryGetValue(key, out float t) && Time.time - t < 4f) return;
            Warnings++;
            Haptics.Pattern(XRNode.RightHand, 3, 1f, 0.06f, 0.06f);
            Say(key, msg, Severity.Warning, 4f);
        }

        void Say(string key, string message, Severity sev, float cooldown, bool force = false)
        {
            if (!force && _said.TryGetValue(key, out float t) && Time.time - t < cooldown) return;
            _said[key] = Time.time;
            Hint?.Invoke(key, message, sev);
        }
    }
}
