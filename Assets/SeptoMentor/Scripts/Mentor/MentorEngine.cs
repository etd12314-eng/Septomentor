using System;
using System.Collections.Generic;
using UnityEngine;

namespace SeptoMentor
{
    public enum SurgicalPhase { Setup, Incision, FlapElevation, Resection, Closure }

    [Serializable]
    public struct MentorEvent
    {
        public string ruleId;
        public Severity severity;
        public string message;
        public SurgicalPhase phase;
        public double time;
    }

    /// <summary>
    /// Rule-based, instant mentor. Heuristics on the label map; thresholds are teaching
    /// defaults, not validated clinical limits. Phase: manual (A button) plus a forward-only
    /// instrument heuristic (OPEN ITEM 4: replace with a learned phase classifier).
    /// </summary>
    public class MentorEngine : MonoBehaviour
    {
        public SegmentationProvider provider;

        [Header("Phase heuristic")]
        public bool autoAdvancePhase = true;
        public float phaseEvidenceSeconds = 1.5f;

        [Header("Rule thresholds (teaching defaults)")]
        public float lStrutMm = 10f;
        public float lStrutMarginMm = 2f;
        [Tooltip("Used only when the source has no mm scale (uncalibrated).")]
        public float lStrutFallbackPixels = 28f;
        public float nearPixels = 8f;
        public float torqueDegPerSec = 90f;
        public int minTearPixels = 30;
        public int persistFrames = 6;
        public float cooldownSeconds = 20f;
        [Tooltip("Septoplasty rules (turbinate proximity, plane, L-strut, perpendicular plate). Off during FESS.")]
        public bool septoplastyRules = true;

        public SurgicalPhase Phase { get; private set; } = SurgicalPhase.Setup;
        public SegmentationFrame LastFrame { get; private set; }

        public event Action<SurgicalPhase, SurgicalPhase, bool> PhaseChanged;   // old, new, manual
        public event Action<MentorEvent> Mentor;

        readonly Dictionary<string, int> _streak = new Dictionary<string, int>();
        readonly Dictionary<string, double> _lastFired = new Dictionary<string, double>();
        readonly HashSet<string> _seenThisFrame = new HashSet<string>();
        Structure _evidenceInstrument;
        double _evidenceSince;

        void OnEnable() { if (provider != null) provider.FrameReady += OnFrame; }
        void OnDisable() { if (provider != null) provider.FrameReady -= OnFrame; }

        public void NextPhase()
        {
            if (Phase == SurgicalPhase.Closure) SetPhase(SurgicalPhase.Setup, true);
            else SetPhase(Phase + 1, true);
        }

        public void SetPhase(SurgicalPhase p, bool manual)
        {
            if (p == Phase) return;
            var old = Phase;
            Phase = p;
            PhaseChanged?.Invoke(old, p, manual);
            Emit("phase_" + p, Severity.Info, PhaseBrief(p), force: true);
        }

        static string PhaseBrief(SurgicalPhase p)
        {
            switch (p)
            {
                case SurgicalPhase.Setup: return "Setup: orient the scope, identify caudal septum, turbinates and nasal floor.";
                case SurgicalPhase.Incision: return "Incision: cut through mucoperichondrium down to cartilage, not into it.";
                case SurgicalPhase.FlapElevation: return "Flap elevation: stay subperichondrial - bluish-white cartilage under the flap.";
                case SurgicalPhase.Resection: return "Resection: excise the spur only; keep an L-strut of about 10 mm.";
                case SurgicalPhase.Closure: return "Closure: inspect both flaps for tears, reposition, then quilt or splint per attending.";
            }
            return p.ToString();
        }

        void OnFrame(SegmentationFrame f)
        {
            LastFrame = f;
            _seenThisFrame.Clear();
            if (autoAdvancePhase) PhaseHeuristic(f);

            var tip = f.instrumentTip;
            var inst = f.activeInstrument;
            bool hasTip = tip.HasValue && inst != Structure.Background;

            // 1) Mucosal tear (any phase).
            Check("mucosal_tear", f.stats[(int)Structure.MucosalTear].pixelCount >= minTearPixels, Severity.Warning,
                "Mucosal tear visible. Note its site; avoid a tear in the opposite flap at the same level (perforation risk).");

            if (!septoplastyRules) { ResetStreaks(); return; }

            // 2) Turbinate protection.
            if (hasTip)
            {
                foreach (var t in new[] { Structure.InferiorTurbinate, Structure.MiddleTurbinate })
                    Check("turbinate_" + t, f.PixelDistanceTo(tip.Value, t, (int)nearPixels + 2) <= nearPixels, Severity.Caution,
                        $"Instrument close to the {StructureInfo.DisplayName(t).ToLowerInvariant()}: protect its mucosa.");
            }

            // 3) Subperichondrial plane (flap elevation with Freer).
            if (Phase == SurgicalPhase.FlapElevation && hasTip && inst == Structure.FreerElevator)
            {
                bool cartilageAtTip = f.PixelDistanceTo(tip.Value, Structure.SeptalCartilage, 14) <= 12;
                bool flapVisible = f.Visible(Structure.MucoperichondrialFlap);
                Check("plane_ok", cartilageAtTip && flapVisible, Severity.Tip,
                    "Good plane: bluish-white cartilage under the elevated flap (subperichondrial).", cooldownOverride: 30f);
                Check("plane_lost", flapVisible && !f.Visible(Structure.SeptalCartilage, 60), Severity.Caution,
                    "No bare cartilage visible: you may be superficial to the perichondrium. Re-find the subperichondrial plane.");
            }

            // 4) L-strut (resection with cutting instrument near caudal septum).
            if (Phase == SurgicalPhase.Resection && hasTip && StructureInfo.IsCuttingInstrument(inst))
            {
                float px = f.PixelDistanceTo(tip.Value, Structure.CaudalSeptum, 96);
                if (!float.IsInfinity(px))
                {
                    if (f.mmPerPixel.HasValue)
                    {
                        float mm = px * f.mmPerPixel.Value;
                        Check("l_strut", mm < lStrutMm + lStrutMarginMm, Severity.Warning,
                            $"Cut ~{mm:0} mm from the caudal septum. Keep an L-strut of about {lStrutMm:0} mm (caudal and dorsal).");
                    }
                    else
                    {
                        Check("l_strut", px < lStrutFallbackPixels, Severity.Warning,
                            $"Cutting near the caudal septum (scale uncalibrated). Keep an L-strut of about {lStrutMm:0} mm.");
                    }
                }
            }

            // 5) Torque at the perpendicular plate.
            if (Phase == SurgicalPhase.Resection && hasTip && f.PixelDistanceTo(tip.Value, Structure.PerpendicularPlate, (int)nearPixels + 4) <= nearPixels + 2)
            {
                if (f.instrumentAngularSpeed.HasValue)
                    Check("ppe_torque", f.instrumentAngularSpeed.Value > torqueDegPerSec, Severity.Warning,
                        "Twisting at the perpendicular plate can transmit force to the skull base. Cut bone sharply; do not twist or rock.");
                else
                    Check("ppe_near", true, Severity.Caution,
                        "At the perpendicular plate: cut, don't twist (skull-base risk).", cooldownOverride: 30f);
            }

            ResetStreaks();
        }

        /// <summary>Resets the streaks of rules that were not true this frame.</summary>
        void ResetStreaks()
        {
            var keys = new List<string>(_streak.Keys);
            foreach (var k in keys) if (!_seenThisFrame.Contains(k)) _streak[k] = 0;
        }

        void PhaseHeuristic(SegmentationFrame f)
        {
            var inst = f.activeInstrument;
            if (!f.Visible(inst, 60)) inst = Structure.Background;
            if (inst != _evidenceInstrument) { _evidenceInstrument = inst; _evidenceSince = f.timestamp; return; }
            if (f.timestamp - _evidenceSince < phaseEvidenceSeconds) return;

            SurgicalPhase? target = null;
            if (inst == Structure.Scalpel && Phase == SurgicalPhase.Setup) target = SurgicalPhase.Incision;
            else if (inst == Structure.FreerElevator && Phase == SurgicalPhase.Incision) target = SurgicalPhase.FlapElevation;
            else if (inst == Structure.ScissorsForceps && Phase == SurgicalPhase.FlapElevation) target = SurgicalPhase.Resection;
            if (target.HasValue) SetPhase(target.Value, false);
        }

        void Check(string id, bool condition, Severity sev, string message, float? cooldownOverride = null)
        {
            if (!condition) return;
            _seenThisFrame.Add(id);
            _streak.TryGetValue(id, out int n);
            _streak[id] = ++n;
            if (n < persistFrames) return;
            Emit(id, sev, message, cooldownOverride: cooldownOverride);
        }

        void Emit(string id, Severity sev, string message, bool force = false, float? cooldownOverride = null)
        {
            double now = Time.timeAsDouble;
            if (!force && _lastFired.TryGetValue(id, out double last) && now - last < (cooldownOverride ?? cooldownSeconds)) return;
            _lastFired[id] = now;
            Mentor?.Invoke(new MentorEvent { ruleId = id, severity = sev, message = message, phase = Phase, time = now });
        }

        /// <summary>Compact scene summary for the AI mentor (no pixels).</summary>
        public List<string> VisibleStructures(int minPixels = 120)
        {
            var list = new List<string>();
            var f = LastFrame;
            if (f == null) return list;
            for (int i = 1; i < StructureInfo.Count; i++)
                if (f.stats[i].pixelCount >= minPixels) list.Add(StructureInfo.DisplayName((Structure)i));
            return list;
        }
    }
}
