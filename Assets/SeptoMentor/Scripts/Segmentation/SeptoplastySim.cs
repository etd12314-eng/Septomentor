using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using Wall = SeptoMentor.NasalAnatomy.Wall;

namespace SeptoMentor
{
    /// <summary>
    /// Tissue response of the procedural septum to the instrument in the resident's right hand
    /// (HandInstruments calls Step every frame with the tip pose in the patient's local space):
    ///   Incision  - scalpel drawn along the Killian line through the mucoperichondrium (strip cut segment by segment);
    ///   Elevation - Freer in the subperichondrial plane, spreading from the incision (flap lifted off the cartilage);
    ///   Resection - through-cutting forceps bites (trigger) remove the deviated cartilage chunk by chunk, L-strut guarded;
    ///   Closure   - Freer / suction lays the flap back.
    /// Each completed step advances the mentor's phase. Bleeding at cuts (cleared with the suction, trigger),
    /// mucosal tears on rough handling, and controller vibration that follows what the tip touches.
    /// Teaching model with plausible, not validated, thresholds. Needs the procedural anatomy (NasalAnatomy).
    /// </summary>
    public class SeptoplastySim : MonoBehaviour
    {
        public SyntheticSegmentationProvider provider;
        public MentorEngine mentor;
        [Tooltip("Lateral lift of a fully elevated flap, mm.")]
        public float flapLiftMm = 4f;

        /// <summary>(message, severity) for the monitor / speech / log.</summary>
        public event Action<string, Severity> Hint;

        public bool Available { get; private set; }
        public int Tears { get; private set; }
        public int StrutViolations { get; private set; }
        /// <summary>Short progress text for the monitor, e.g. "Incision 5/8".</summary>
        public string Progress { get; private set; } = "";

        const int Seg = NasalAnatomy.IncisionSegments, CS = NasalAnatomy.ChunksS, CP = NasalAnatomy.ChunksP;
        const int ES = Seg, EP = 10;   // elevation grid over the flap (S x P); its S columns match the incision segments
        const float S0 = NasalAnatomy.FlapS0, S1 = NasalAnatomy.FlapS1, P0 = NasalAnatomy.FlapP0, P1 = NasalAnatomy.FlapP1;

        VirtualPatient Patient => provider != null ? provider.patient : null;
        SurgicalPhase Phase => mentor != null ? mentor.Phase : SurgicalPhase.Setup;

        readonly bool[] _cut = new bool[Seg];
        readonly float[] _cutProgress = new float[Seg];
        readonly float[,] _elev = new float[ES, EP];
        readonly bool[,] _removed = new bool[CS, CP];
        // Parts of the flap area that face the airway (the rest lies under the nasal roof / dorsum and cannot be reached).
        readonly bool[] _segOpen = new bool[Seg];
        readonly bool[,] _cellOpen = new bool[ES, EP];
        int _segOpenCount, _cellOpenCount;
        bool _repositioned, _searched;
        float _startTime = -1;

        GameObject[] _strips;
        GameObject[,] _chunks;
        Mesh _flapMesh;
        MeshCollider _flapCollider;
        Vector3[] _flapBase, _flapVerts;
        bool _flapDirty;
        float _nextFlapUpdate;

        class Blood { public Transform t; public float born, size; public Vector3 at; public AnatomyPart part; }
        readonly List<Blood> _blood = new List<Blood>();
        readonly List<GameObject> _tears = new List<GameObject>();
        readonly Dictionary<string, float> _said = new Dictionary<string, float>();

        Vector3 _lastMm;
        bool _hadTip;
        float _travel;   // mm since the last "texture" click

        void Start()
        {
            if (mentor != null) mentor.autoAdvancePhase = false;   // phases now follow what the resident actually does
        }

        bool Ready()
        {
            if (Available) return true;
            if (_searched || Patient == null || Patient.AnatomyRoot == null) return false;
            _searched = true;
            var root = Patient.AnatomyRoot;
            _strips = new GameObject[Seg];
            for (int k = 0; k < Seg; k++) _strips[k] = Find(root, "Flap_IncisionStrip_" + k);
            _chunks = new GameObject[CS, CP];
            for (int i = 0; i < CS; i++) for (int j = 0; j < CP; j++) _chunks[i, j] = Find(root, $"Cartilage_Chunk_{i}_{j}");
            var flap = Find(root, "Flap");
            if (flap == null || _strips[0] == null || _chunks[0, 0] == null)
            {
                Debug.Log("[SeptoMentor] Septoplasty simulation off: the anatomy has no procedural septal pieces.");
                return false;
            }
            var mf = flap.GetComponent<MeshFilter>();
            _flapMesh = Instantiate(mf.sharedMesh);
            _flapMesh.name = "Flap (live)";
            _flapMesh.MarkDynamic();
            mf.sharedMesh = _flapMesh;
            _flapCollider = flap.GetComponent<MeshCollider>();
            _flapBase = _flapMesh.vertices;
            _flapVerts = (Vector3[])_flapBase.Clone();

            float incP = (NasalAnatomy.IncisionP0 + P0) * 0.5f, segW = (S1 - S0) / Seg;
            for (int k = 0; k < Seg; k++)
            {
                _segOpen[k] = Exposed(S0 + (k + 0.2f) * segW, incP) || Exposed(S0 + (k + 0.5f) * segW, incP) || Exposed(S0 + (k + 0.8f) * segW, incP);
                if (_segOpen[k]) _segOpenCount++;
            }
            for (int i = 0; i < ES; i++)
            for (int j = 0; j < EP; j++)
            {
                var c = CellCentre(i, j);
                _cellOpen[i, j] = Exposed(c.x, c.y);
                if (_cellOpen[i, j]) _cellOpenCount++;
            }
            Debug.Log($"[SeptoMentor] Septoplasty simulation: {_segOpenCount}/{Seg} incision segments, {_cellOpenCount}/{ES * EP} flap cells face the airway.");
            Available = true;
            Canonical(Phase);
            return true;
        }

        /// <summary>Septal mucosa at (S, P) faces the air (not buried under the roof or floor).</summary>
        static bool Exposed(float s, float p) => NasalAnatomy.Depth(new Vector3(NasalAnatomy.SeptumFace(s, p) - 0.4f, s, p), out _) < 0f;

        int CutCount() { int n = 0; for (int k = 0; k < Seg; k++) if (_cut[k] && _segOpen[k]) n++; return n; }

        static GameObject Find(Transform root, string name)
        {
            var t = root.Find(name);
            return t != null ? t.gameObject : null;
        }

        // ------------------------------------------------------------------ phase sync

        /// <summary>Called on every phase change. A manual jump (A button) sets the septum to that phase's starting state.</summary>
        public void OnPhase(SurgicalPhase p, bool manual)
        {
            if (!Ready()) return;
            if (manual) Canonical(p);
            UpdateProgress();
        }

        void Canonical(SurgicalPhase p)
        {
            bool cut = p >= SurgicalPhase.FlapElevation;
            for (int k = 0; k < Seg; k++) { _cut[k] = cut; _cutProgress[k] = cut ? 1 : 0; }
            float e = p == SurgicalPhase.Resection || p == SurgicalPhase.Closure ? 1f : 0f;
            for (int i = 0; i < ES; i++) for (int j = 0; j < EP; j++) _elev[i, j] = e;
            for (int i = 0; i < CS; i++) for (int j = 0; j < CP; j++) _removed[i, j] = p == SurgicalPhase.Closure;
            _repositioned = false;
            if (p == SurgicalPhase.Setup)
            {
                for (int k = _blood.Count - 1; k >= 0; k--) RemoveBlood(k);
                foreach (var t in _tears)
                {
                    var part = t.GetComponent<AnatomyPart>();
                    provider.RemoveLabelPart(part);
                    Patient.Parts.Remove(part);
                    Destroy(t);
                }
                _tears.Clear();
                Tears = StrutViolations = 0;
                _startTime = -1;
            }
            ApplyVisuals();
            _flapDirty = true;
        }

        void ApplyVisuals()
        {
            for (int k = 0; k < Seg; k++) if (_strips[k] != null) _strips[k].SetActive(!_cut[k] || _repositioned);
            for (int i = 0; i < CS; i++) for (int j = 0; j < CP; j++) if (_chunks[i, j] != null) _chunks[i, j].SetActive(!_removed[i, j]);
        }

        void UpdateProgress()
        {
            switch (Phase)
            {
                case SurgicalPhase.Setup: Progress = "pick up the scalpel"; break;
                case SurgicalPhase.Incision: Progress = $"incision {CutCount()}/{_segOpenCount}"; break;
                case SurgicalPhase.FlapElevation: Progress = $"flap elevated {Mathf.RoundToInt(100f * ElevatedFraction())}%"; break;
                case SurgicalPhase.Resection: Progress = $"cartilage removed {CountRemoved()}/{CS * CP}"; break;
                case SurgicalPhase.Closure: Progress = _repositioned ? "done" : $"flap laid back {Mathf.RoundToInt(100f * (1f - MeanElevation()))}%"; break;
            }
        }

        int CountRemoved() { int n = 0; foreach (var b in _removed) if (b) n++; return n; }

        float ElevatedFraction()
        {
            int n = 0;
            for (int i = 0; i < ES; i++) for (int j = 0; j < EP; j++) if (_cellOpen[i, j] && _elev[i, j] >= 0.6f) n++;
            return n / (float)Mathf.Max(1, _cellOpenCount);
        }

        float MeanElevation()
        {
            float s = 0;
            for (int i = 0; i < ES; i++) for (int j = 0; j < EP; j++) if (_cellOpen[i, j]) s += _elev[i, j];
            return s / Mathf.Max(1, _cellOpenCount);
        }

        void Advance(SurgicalPhase next, string message)
        {
            Say("done_" + next, message, Severity.Info, 0f);
            Haptics.Both(0.6f, 0.12f);
            Haptics.Both(0.6f, 0.12f, 0.22f);
            mentor?.SetPhase(next, false);
            UpdateProgress();
        }

        // ------------------------------------------------------------------ per frame

        /// <summary>
        /// The instrument tip (patient local space, m) this frame. <paramref name="trigger"/> 0..1; a bite / suction uses it.
        /// Vibrates <paramref name="hand"/> according to what the tip touches.
        /// </summary>
        public void Step(Structure tool, Vector3 tipLocal, bool trigger, bool triggerDown, XRNode hand)
        {
            if (!Ready()) return;
            var mm = tipLocal * 1000f;
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            var delta = _hadTip ? mm - _lastMm : Vector3.zero;
            if (delta.magnitude > 10f) delta = Vector3.zero;   // teleport / regrab
            float speed = delta.magnitude / dt;
            _lastMm = mm; _hadTip = true;

            if (tool == Structure.Suction && trigger) Suction(mm, hand);

            // Only inside the modelled nasal cavity (behind the nostril).
            bool inside = mm.z > -96f && mm.z < 30f && mm.x > -19f && mm.x < 16f && mm.y > -31f && mm.y < 26f;
            if (!inside) { _travel = 0; return; }
            if (_startTime < 0) _startTime = Time.time;

            float depth = NasalAnatomy.Depth(mm, out var wall);
            float s = mm.y, p = mm.z;
            float face = NasalAnatomy.SeptumFace(s, p);
            float pen0 = mm.x - face;   // below the original mucosal surface
            bool flapArea = wall == Wall.Septum && s > S0 && s < S1 && p > NasalAnatomy.IncisionP0 - 1.5f && p < P1;
            float rumble = 0;
            _travel += delta.magnitude;

            if (flapArea && Available)
            {
                float e = Elevation(s, p);
                float lift = flapLiftMm * e * Taper(s, p);
                bool pocket = e > 0.3f && pen0 > -lift && pen0 < NasalAnatomy.CartilageDepth + 0.15f;
                switch (tool)
                {
                    case Structure.Scalpel: rumble = Scalpel(mm, s, p, pen0, delta, hand); break;
                    case Structure.FreerElevator:
                        rumble = Phase == SurgicalPhase.Closure ? LayBack(s, p, pen0 + lift, hand) : Elevate(s, p, pen0, speed, pocket, hand);
                        break;
                    case Structure.ScissorsForceps:
                        rumble = pocket || pen0 > 0 ? 0.15f + 0.3f * Mathf.Clamp01(pen0 / 2f) : 0f;
                        if (triggerDown) Bite(mm, s, p, pen0, e, hand);
                        break;
                    case Structure.Suction:
                        rumble = Phase == SurgicalPhase.Closure ? LayBack(s, p, pen0 + lift, hand) : Contact(pocket ? pen0 - NasalAnatomy.CartilageDepth : pen0, 0.12f);
                        break;
                }
                if (pocket && tool != Structure.ScissorsForceps) rumble = Mathf.Max(rumble, 0.08f);   // the flap brushing the shaft
            }
            else
            {
                rumble = Contact(depth, wall == Wall.Septum || wall == Wall.Lateral || wall == Wall.Floor ? 0.2f : 0.12f);
                if (depth > 0.4f) OffTarget(tool, wall, mm, depth, triggerDown, hand);
                else if (triggerDown && tool == Structure.ScissorsForceps) Haptics.Pulse(hand, 0.2f, 0.02f);   // jaws closing on air
            }

            if (rumble > 0) Haptics.Rumble(hand, rumble);
            if (_flapDirty && Time.time >= _nextFlapUpdate) UpdateFlapMesh();
            GrowBlood();
        }

        /// <summary>Plain contact: harder press, stronger buzz; a light click every 2 mm while sliding (surface texture).</summary>
        float Contact(float depth, float baseAmp)
        {
            if (depth <= 0) return 0;
            if (_travel > 2f) { _travel = 0; Haptics.Pulse(XRNode.RightHand, 0.35f, 0.012f); }
            return baseAmp + 0.55f * Mathf.Clamp01(depth / 2.5f);
        }

        // ------------------------------------------------------------------ incision

        float Scalpel(Vector3 mm, float s, float p, float pen0, Vector3 delta, XRNode hand)
        {
            if (pen0 <= 0.1f) return 0;
            bool onLine = p > NasalAnatomy.IncisionP0 - 2.5f && p < P0 + 2.5f;
            bool open = Phase <= SurgicalPhase.Incision;
            if (onLine && open)
            {
                if (Phase == SurgicalPhase.Setup) mentor?.SetPhase(SurgicalPhase.Incision, false);
                if (pen0 > 2.4f)
                {
                    Say("inc_deep", "Too deep: the blade is through the cartilage - the opposite flap is at risk. Cut the mucoperichondrium only.", Severity.Caution, 5f);
                    return 0.8f;
                }
                int k = Mathf.Clamp(Mathf.FloorToInt((s - S0) / (S1 - S0) * Seg), 0, Seg - 1);
                // Only drawing the blade along the line (S) cuts; a push without a stroke does not.
                float along = Mathf.Abs(delta.y);
                if (!_cut[k] && along > 0.01f)
                {
                    _cutProgress[k] += along / ((S1 - S0) / Seg) * 1.4f;
                    if (_travel > 0.8f) { _travel = 0; Haptics.Pulse(hand, 0.5f, 0.012f); }   // the blade catching tissue
                    if (_cutProgress[k] >= 1f)
                    {
                        _cut[k] = true;
                        ApplyVisuals();
                        AddBlood(new Vector3(NasalAnatomy.SeptumFace(s, p) - 0.1f, s, (NasalAnatomy.IncisionP0 + P0) * 0.5f), 0.8f);
                        if (CutCount() == _segOpenCount)
                            Advance(SurgicalPhase.FlapElevation, "Incision complete, down to cartilage. Take the Freer elevator (grip) and lift the flap in the subperichondrial plane.");
                        UpdateProgress();
                    }
                }
                return 0.3f + 0.3f * Mathf.Clamp01(pen0 / 2f);
            }
            if (Elevation(s, p) < 0.3f && pen0 > 0.7f && p > P0 + 2.5f)
            {
                Tear(mm, "The scalpel cut into the flap away from the incision line: mucosal tear.");
                return 0.9f;
            }
            return Contact(pen0, 0.2f);
        }

        // ------------------------------------------------------------------ flap elevation / closure

        float Elevate(float s, float p, float pen0, float speed, bool pocket, XRNode hand)
        {
            if (pen0 <= 0 && !pocket) return 0;
            bool reachable = Reachable(s, p);
            if (!reachable)
            {
                // Pushing on intact mucosa from the cavity side.
                if (pen0 > 1.6f) { Tear(new Vector3(NasalAnatomy.SeptumFace(s, p), s, p), "The elevator went through intact mucosa: flap tear. Enter through the incision and stay under the perichondrium."); return 1f; }
                if (CutCount() == 0) Say("elev_noinc", "Make the incision first (scalpel), then elevate through it.", Severity.Tip, 8f);
                return Contact(pen0, 0.15f);
            }
            if (pen0 > NasalAnatomy.CartilageDepth + 0.9f)
            {
                Say("elev_deep", "The elevator is digging into the cartilage. Keep the blade flat on its surface.", Severity.Caution, 5f);
                return 0.75f;
            }
            if (pen0 < 0.3f) return 0.1f;

            // In the subperichondrial plane: lift the cells around the blade (about 4 mm either side).
            bool changed = false;
            float rate = (1.4f + speed / 15f) * Time.deltaTime;
            for (int i = 0; i < ES; i++)
            for (int j = 0; j < EP; j++)
            {
                var c = CellCentre(i, j);
                float d = Vector2.Distance(c, new Vector2(s, p));
                if (d > 4.5f || _elev[i, j] >= 1f || !CellReachable(i, j)) continue;
                _elev[i, j] = Mathf.Min(1f, _elev[i, j] + rate * (1f - d / 4.5f));
                changed = true;
            }
            if (changed)
            {
                _flapDirty = true;
                if (Phase == SurgicalPhase.Incision && CutCount() == _segOpenCount) mentor?.SetPhase(SurgicalPhase.FlapElevation, false);
                if (Phase == SurgicalPhase.FlapElevation && ElevatedFraction() >= 0.8f)
                    Advance(SurgicalPhase.Resection, "Flap elevated over the deviated cartilage. Take the through-cutting forceps and bite out the deviated part (trigger) - keep the L-strut.");
                UpdateProgress();
            }
            // Scraping on cartilage: a crisp click every 1.5 mm of sweep.
            if (_travel > 1.5f) { _travel = 0; Haptics.Pulse(hand, 0.55f, 0.012f); }
            return 0.22f + 0.2f * Mathf.Clamp01((pen0 - 0.3f) / 1.2f);
        }

        float LayBack(float s, float p, float penFlap, XRNode hand)
        {
            if (penFlap < -0.3f || _repositioned) return 0;
            bool changed = false;
            float rate = 1.6f * Time.deltaTime;
            for (int i = 0; i < ES; i++)
            for (int j = 0; j < EP; j++)
            {
                float d = Vector2.Distance(CellCentre(i, j), new Vector2(s, p));
                if (d > 5f || _elev[i, j] <= 0f) continue;
                _elev[i, j] = Mathf.Max(0f, _elev[i, j] - rate * (1f - d / 5f));
                changed = true;
            }
            if (changed)
            {
                _flapDirty = true;
                if (MeanElevation() < 0.15f)
                {
                    for (int i = 0; i < ES; i++) for (int j = 0; j < EP; j++) _elev[i, j] = 0;
                    _repositioned = true;
                    ApplyVisuals();
                    float t = _startTime >= 0 ? Time.time - _startTime : 0;
                    Say("done_all", $"Flap laid back, incision closed. Septoplasty complete in {Mathf.FloorToInt(t / 60)}:{Mathf.FloorToInt(t % 60):00} - " +
                                    $"{Tears} mucosal tear(s), {StrutViolations} L-strut warning(s).", Severity.Info, 0f);
                    Haptics.Both(0.7f, 0.15f);
                    Haptics.Both(0.7f, 0.15f, 0.25f);
                    Haptics.Both(0.7f, 0.3f, 0.5f);
                }
                UpdateProgress();
            }
            return 0.15f + 0.25f * Mathf.Clamp01(penFlap / 2f);
        }

        Vector2 CellCentre(int i, int j) => new Vector2(S0 + (i + 0.5f) * (S1 - S0) / ES, P0 + (j + 0.5f) * (P1 - P0) / EP);

        bool CellReachable(int i, int j)
        {
            if (_elev[i, j] >= 0.3f) return true;
            if (j == 0 && _cut[i]) return true;   // straight in through the incision
            return (i > 0 && _elev[i - 1, j] >= 0.6f) || (i < ES - 1 && _elev[i + 1, j] >= 0.6f) ||
                   (j > 0 && _elev[i, j - 1] >= 0.6f) || (j < EP - 1 && _elev[i, j + 1] >= 0.6f);
        }

        bool Reachable(float s, float p)
        {
            int i = Mathf.Clamp(Mathf.FloorToInt((s - S0) / (S1 - S0) * ES), 0, ES - 1);
            int j = Mathf.Clamp(Mathf.FloorToInt((p - P0) / (P1 - P0) * EP), 0, EP - 1);
            return CellReachable(i, j);
        }

        /// <summary>Elevation 0..1 at (S, P), bilinear between cell centres.</summary>
        float Elevation(float s, float p)
        {
            float x = Mathf.Clamp((s - S0) / (S1 - S0) * ES - 0.5f, 0, ES - 1), y = Mathf.Clamp((p - P0) / (P1 - P0) * EP - 0.5f, 0, EP - 1);
            int i = Mathf.Min((int)x, ES - 2), j = Mathf.Min((int)y, EP - 2);
            float fx = x - i, fy = y - j;
            return Mathf.Lerp(Mathf.Lerp(_elev[i, j], _elev[i + 1, j], fx), Mathf.Lerp(_elev[i, j + 1], _elev[i + 1, j + 1], fx), fy);
        }

        /// <summary>The flap stays attached along its upper, lower and back edges; the incision edge is free.</summary>
        static float Taper(float s, float p) =>
            Mathf.SmoothStep(0, 1, (s - S0) / 6f) * Mathf.SmoothStep(0, 1, (S1 - s) / 6f) * Mathf.SmoothStep(0, 1, (P1 - p) / 8f);

        void UpdateFlapMesh()
        {
            _flapDirty = false;
            _nextFlapUpdate = Time.time + 0.08f;
            for (int v = 0; v < _flapBase.Length; v++)
            {
                var b = _flapBase[v];
                float s = b.y * 1000f, p = b.z * 1000f;
                _flapVerts[v] = new Vector3(b.x - 0.001f * flapLiftMm * Elevation(s, p) * Taper(s, p), b.y, b.z);
            }
            _flapMesh.vertices = _flapVerts;
            _flapMesh.RecalculateNormals();
            _flapMesh.RecalculateBounds();
            // The collider stays at the intact surface; switch it off once lifted so the scope can enter the pocket.
            if (_flapCollider != null) _flapCollider.enabled = MeanElevation() < 0.05f;
        }

        // ------------------------------------------------------------------ resection

        void Bite(Vector3 mm, float s, float p, float pen0, float e, XRNode hand)
        {
            if (pen0 < 0.2f) { Haptics.Pulse(hand, 0.2f, 0.02f); return; }
            if (e < 0.45f)
            {
                Tear(new Vector3(NasalAnatomy.SeptumFace(s, p), s, p), "The forceps bit mucosa that was not elevated: flap tear. Elevate the flap over the area first.");
                return;
            }
            const float tol = 1f;
            bool inWindow = s > NasalAnatomy.WinS0 - tol && s < NasalAnatomy.WinS1 + tol && p > NasalAnatomy.WinP0 - tol && p < NasalAnatomy.WinP1 + tol;
            if (inWindow)
            {
                int i = Mathf.Clamp(Mathf.FloorToInt((s - NasalAnatomy.WinS0) / (NasalAnatomy.WinS1 - NasalAnatomy.WinS0) * CS), 0, CS - 1);
                int j = Mathf.Clamp(Mathf.FloorToInt((p - NasalAnatomy.WinP0) / (NasalAnatomy.WinP1 - NasalAnatomy.WinP0) * CP), 0, CP - 1);
                if (_removed[i, j]) { Haptics.Pulse(hand, 0.25f, 0.03f); return; }
                if (Phase < SurgicalPhase.Resection) mentor?.SetPhase(SurgicalPhase.Resection, false);
                _removed[i, j] = true;
                ApplyVisuals();
                // Cartilage giving way: a hard bite, then a short crunch.
                Haptics.Pulse(hand, 1f, 0.09f);
                Haptics.Pattern(hand, 3, 0.5f, 0.015f, 0.025f);
                AddBlood(new Vector3(NasalAnatomy.SeptumFace(s, p) + 0.2f, s, p), 0.6f);
                if (pen0 > NasalAnatomy.ContralateralDepth + 0.6f)
                {
                    Tears++;
                    Say("contra", "The bite went through the opposite mucoperichondrium: risk of septal perforation. Keep the jaws on the cartilage.", Severity.Warning, 4f);
                }
                if (CountRemoved() == CS * CP)
                    Advance(SurgicalPhase.Closure, "Deviated cartilage removed, L-strut intact. Lay the flap back with the Freer or the suction.");
                UpdateProgress();
                return;
            }
            bool dorsal = s >= NasalAnatomy.WinS1 + tol, caudal = p <= NasalAnatomy.WinP0 - tol;
            if (dorsal || caudal)
            {
                StrutViolations++;
                Haptics.Pattern(hand, 3, 1f, 0.08f, 0.07f);
                Say("strut", $"L-strut! That is the {(dorsal ? "dorsal" : "caudal")} strut - keep at least 10 mm. Bite not taken.", Severity.Warning, 2f);
                return;
            }
            Haptics.Pulse(hand, 0.3f, 0.03f);
            Say("outside", "That cartilage is not deviated - no need to remove it.", Severity.Tip, 6f);
        }

        // ------------------------------------------------------------------ other walls

        void OffTarget(Structure tool, Wall wall, Vector3 mm, float depth, bool triggerDown, XRNode hand)
        {
            string name = WallName(wall, mm);
            bool soft = wall == Wall.InfTurb || wall == Wall.MidTurb || wall == Wall.Torus;
            if (tool == Structure.Scalpel && depth > 0.6f && soft) { Tear(mm, $"The scalpel cut the {name}: bleeding. Keep the blade on the septum."); return; }
            if (tool == Structure.ScissorsForceps && triggerDown) { Tear(mm, $"The forceps bit the {name}! Only bite cartilage under an elevated flap."); return; }
            if (wall == Wall.Septum && mm.z > P1 && tool == Structure.ScissorsForceps)
                Say("bone", "Bony septum (perpendicular plate / vomer): cut sharply, never twist or rock - skull-base risk.", Severity.Caution, 10f);
            if (depth > 2.2f) Say("force_" + wall, $"Too much pressure on the {name}.", Severity.Caution, 6f);
        }

        static string WallName(Wall w, Vector3 mm)
        {
            switch (w)
            {
                case Wall.InfTurb: return "inferior turbinate";
                case Wall.MidTurb: return "middle turbinate";
                case Wall.Floor: return "nasal floor";
                case Wall.Roof: return "roof of the nose";
                case Wall.Vestibule: return "nasal vestibule";
                case Wall.Pharynx: case Wall.Torus: return "nasopharynx";
                case Wall.Septum: return mm.z > P1 ? "bony septum" : "septum";
                default: return "lateral nasal wall";
            }
        }

        // ------------------------------------------------------------------ bleeding, tears

        void Tear(Vector3 mm, string message)
        {
            if (_said.TryGetValue("tear", out float last) && Time.time - last < 3f) return;
            Tears++;
            Haptics.Pulse(XRNode.RightHand, 1f, 0.35f);
            Say("tear", message, Severity.Warning, 3f);
            var n = Normal(mm);
            var surf = mm;
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "MucosalTear";
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(Patient.AnatomyRoot, false);
            go.transform.localPosition = (surf + n * 0.15f) * 0.001f;
            go.transform.localRotation = Quaternion.LookRotation(n) * Quaternion.Euler(0, 0, UnityEngine.Random.Range(0f, 180f));
            go.transform.localScale = new Vector3(0.004f, 0.0008f, 0.0003f);
            provider.AddLabelPart(Patient.AddPart(go, Structure.MucosalTear, true));
            _tears.Add(go);
            AddBlood(surf + n * 0.1f, 1f);
        }

        /// <summary>Direction out of the tissue (toward the air), from the depth field's gradient.</summary>
        static Vector3 Normal(Vector3 mm)
        {
            const float h = 0.3f;
            float dx = NasalAnatomy.Depth(mm + Vector3.right * h, out _) - NasalAnatomy.Depth(mm - Vector3.right * h, out _);
            float dy = NasalAnatomy.Depth(mm + Vector3.up * h, out _) - NasalAnatomy.Depth(mm - Vector3.up * h, out _);
            float dz = NasalAnatomy.Depth(mm + Vector3.forward * h, out _) - NasalAnatomy.Depth(mm - Vector3.forward * h, out _);
            var g = new Vector3(dx, dy, dz);
            return g.sqrMagnitude > 1e-8f ? -g.normalized : Vector3.left;
        }

        void AddBlood(Vector3 mm, float size)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "Blood";
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(Patient.AnatomyRoot, false);
            var part = Patient.AddPart(go, Structure.Background, true);
            part.tissueColor = new Color(0.42f, 0.03f, 0.04f);
            part.wetness = 1.8f;
            part.Apply();
            provider.AddLabelPart(part);
            var b = new Blood { t = go.transform, born = Time.time, size = size, at = mm, part = part };
            _blood.Add(b);
            PlaceBlood(b, 0f);
            if (_blood.Count > 24) RemoveBlood(0);
        }

        /// <summary>Oozes for ~20 s: the drop widens and trickles down (inferiorly).</summary>
        static void PlaceBlood(Blood b, float age)
        {
            float g = Mathf.Clamp01(age / 20f);
            float w = (0.8f + 1.4f * g) * b.size, len = (1f + 5f * g) * b.size;
            b.t.localPosition = (b.at + new Vector3(-0.05f, -len * 0.4f, 0)) * 0.001f;
            b.t.localRotation = Quaternion.identity;
            b.t.localScale = new Vector3(0.35f, len, w) * 0.001f;
        }

        void GrowBlood()
        {
            foreach (var b in _blood) if (Time.time - b.born < 20f) PlaceBlood(b, Time.time - b.born);
        }

        void RemoveBlood(int k)
        {
            var b = _blood[k];
            provider.RemoveLabelPart(b.part);
            Patient.Parts.Remove(b.part);
            Destroy(b.t.gameObject);
            _blood.RemoveAt(k);
        }

        void Suction(Vector3 mm, XRNode hand)
        {
            Haptics.Rumble(hand, 0.1f);   // the suction running
            for (int k = _blood.Count - 1; k >= 0; k--)
            {
                if (Vector3.Distance(_blood[k].at, mm) > 5f) continue;
                Haptics.Pulse(hand, 0.35f, 0.04f);
                RemoveBlood(k);
            }
        }

        void Say(string key, string message, Severity sev, float cooldown)
        {
            if (_said.TryGetValue(key, out float last) && Time.time - last < cooldown) return;
            _said[key] = Time.time;
            Hint?.Invoke(message, sev);
        }
    }
}
