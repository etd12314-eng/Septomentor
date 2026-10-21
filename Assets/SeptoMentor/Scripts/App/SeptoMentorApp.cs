using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace SeptoMentor
{
    /// <summary>Glue: input -> provider/mentor, mentor -> overlay/speech/AI/log.</summary>
    public class SeptoMentorApp : MonoBehaviour
    {
        public SegmentationProvider provider;
        public MentorEngine mentor;
        public OverlayView overlay;
        public AIMentorClient ai;
        public SpeechIO speech;
        public SessionLogger logger;
        public ControllerInput input;
        [Tooltip("Optional: operating room. When set, recenter moves the user to the surgeon's spot instead of moving the monitor.")]
        public OperatingRoom room;
        public HandheldScope scope;
        public HandInstruments hands;
        public SeptoplastySim sim;
        public VoiceCoach voice;
        public VoiceCommands commands;
        public Transform head;
        [Tooltip("Parent of monitor + hologram; moved by Recenter.")]
        public Transform userRig;
        public float rigDistance = 0.9f, rigHeightOffset = -0.15f;

        [Tooltip("Fallback question when no speech recognizer is wired.")]
        public string defaultQuestion = "What should I focus on right now, and what is the main risk at this step?";

        SyntheticSegmentationProvider Synthetic => provider as SyntheticSegmentationProvider;

        void Start()
        {
            input.NextPhase += () => { mentor.NextPhase(); logger.Log("input", mentor.Phase.ToString(), null, null, "A: next phase"); };
            input.CycleInstrument += OnCycleInstrument;
            input.Recenter += DoRecenter;
            input.AskAI += OnAsk;
            input.ToggleLabels += () => overlay.ToggleLabels();
            input.ToggleScope += () => SetScope(scope == null || !scope.handheld);
            if (scope != null) scope.Hint += (key, msg, sev) => OnHint("scope", key, msg, sev);
            if (hands != null) hands.Hint += (key, msg, sev) => OnHint("instrument", key, msg, sev);
            if (sim != null) sim.Hint += (key, msg, sev) => OnHint("tissue", key, msg, sev);
            if (commands != null) commands.Command += OnVoiceCommand;
            input.SimulateTear += () =>
            {
                if (Synthetic != null && Synthetic.SimulateTear()) logger.Log("input", mentor.Phase.ToString(), null, null, "simulated mucosal tear");
            };

            mentor.PhaseChanged += (oldP, newP, manual) =>
            {
                overlay.SetPhase(PhaseLine(newP, manual));
                if (Synthetic != null && Synthetic.patient != null) Synthetic.patient.SetPhase(newP);
                if (sim != null) sim.OnPhase(newP, manual);
                _manualPhase = manual;
                logger.Log("phase", newP.ToString(), null, null, (manual ? "manual " : "auto ") + oldP + " -> " + newP);
                ai.Coach("phase_change", new MentorEvent { ruleId = "phase_" + newP, severity = Severity.Info, message = "Entered " + newP, phase = newP });
            };
            mentor.Mentor += e =>
            {
                overlay.ShowMentor(e.message, e.severity);
                logger.LogMentor(e);
                ai.RememberEvent(e);
                // Phase briefs are spoken when the resident changed phase; a completed step already announced the next one.
                if (!e.ruleId.StartsWith("phase_") || _manualPhase) Speak(e.ruleId, e.message, e.severity);
                if (e.severity == Severity.Warning) ai.Coach("warning", e);
            };
            ai.Answer += (kind, text) =>
            {
                overlay.ShowAI(text);
                logger.Log("ai", mentor.Phase.ToString(), kind, null, text);
                speech.Speak(text);
            };
            speech.QuestionHeard += q =>
            {
                logger.Log("question", mentor.Phase.ToString(), null, null, q);
                overlay.ShowAI("Thinking about: \"" + q + "\"");
                ai.Ask(q);
            };

            overlay.SetPhase(PhaseLine(mentor.Phase, true));
            overlay.ShowSidePanel(ControlsCard());
            Invoke(nameof(DoRecenter), 0.5f);   // after the first HMD pose arrives
        }

        bool _manualPhase = true;
        string _voiceStatus;
        string _progress;

        string PhaseLine(SurgicalPhase p, bool manual) =>
            $"Phase: {p}{(manual ? "" : " (auto)")}" + (sim != null && sim.Available && sim.Progress.Length > 0 ? "  -  " + sim.Progress : "") +
            $"   [A next  B instrument  X recenter{(ai.Configured ? "  Y ask" : "")}]";

        void OnHint(string source, string key, string msg, Severity sev)
        {
            overlay.ShowMentor(msg, sev, sev >= Severity.Caution ? 5f : 4f);
            logger.Log(source, mentor.Phase.ToString(), key, sev.ToString(), msg);
            Speak(key, msg, sev);
        }

        /// <summary>Turkish voice clip for the key when there is one; otherwise English TTS for cautions and warnings.</summary>
        void Speak(string key, string msg, Severity sev)
        {
            if (voice != null) voice.Say(key, msg, sev);
            else if (sev >= Severity.Caution) speech.Speak(msg);
        }

        void SetScope(bool handheld)
        {
            if (scope == null) return;
            scope.handheld = handheld;
            overlay.ShowMentor(handheld ? "Hand-held scope: the left controller is the endoscope." : "Scope on autopilot / thumbsticks.", Severity.Info, 3f);
            voice?.Say(handheld ? "scope_hand" : "scope_auto", null);
            logger.Log("input", mentor.Phase.ToString(), null, null, "scope mode " + (handheld ? "hand-held" : "auto"));
        }

        void OnVoiceCommand(VoiceCommand cmd, string heard)
        {
            logger.Log("voice", mentor.Phase.ToString(), cmd.ToString(), null, heard);
            overlay.ShowMentor($"Voice: \"{Display(heard)}\"", Severity.Info, 2f);
            switch (cmd)
            {
                case VoiceCommand.Next: mentor.NextPhase(); break;
                case VoiceCommand.Previous: if (mentor.Phase > SurgicalPhase.Setup) mentor.SetPhase(mentor.Phase - 1, true); break;
                case VoiceCommand.Restart: voice?.Say("restart", null); mentor.SetPhase(SurgicalPhase.Setup, true); break;
                case VoiceCommand.Scalpel: hands?.Request(Structure.Scalpel); break;
                case VoiceCommand.Elevator: hands?.Request(Structure.FreerElevator); break;
                case VoiceCommand.Forceps: hands?.Request(Structure.ScissorsForceps); break;
                case VoiceCommand.Suction: hands?.Request(Structure.Suction); break;
                case VoiceCommand.Release: hands?.Request(Structure.Background); voice?.Say("tool_none", null); break;
                case VoiceCommand.LabelsOn: overlay.SetLabels(true); voice?.Say("labels_on", null); break;
                case VoiceCommand.LabelsOff: overlay.SetLabels(false); voice?.Say("labels_off", null); break;
                case VoiceCommand.Recenter: DoRecenter(); voice?.Say("recenter", null); break;
                case VoiceCommand.Status:
                    int pct = sim != null ? Mathf.Clamp(Mathf.RoundToInt(sim.ProgressFraction * 10f) * 10, 0, 100) : 0;
                    voice?.SayAll("name_" + mentor.Phase, "pct_" + pct);
                    break;
                case VoiceCommand.Hint: voice?.SayAll("phase_" + mentor.Phase); break;
                case VoiceCommand.Repeat: voice?.Repeat(); break;
                case VoiceCommand.Mute: voice?.Say("voice_off", null); if (voice != null) voice.muted = true; break;
                case VoiceCommand.Unmute: if (voice != null) voice.muted = false; voice?.Say("voice_on", null); break;
                case VoiceCommand.ScopeHand: SetScope(true); break;
                case VoiceCommand.ScopeAuto: SetScope(false); break;
                case VoiceCommand.Ask: if (ai.Configured) OnAsk(); else voice?.Say("no_ai", null); break;
                case VoiceCommand.Help: voice?.SayAll("help"); break;
            }
        }

        string ControlsCard() =>
            "Controls\n" +
            "A  next phase\n" +
            "B  change instrument\n" +
            "X  recenter view\n" +
            (ai.Configured ? "Y  ask the AI mentor\n" : "Y  (AI mentor off in this build)\n") +
            (scope != null ? "LEFT controller = endoscope: into the LEFT nostril of the patient\n" +
                             "Left stick click  hand-held scope / autopilot\n" : "") +
            (hands != null ? "RIGHT hand = instruments (Mayo stand on your right)\n" +
                             "  Grip over an instrument: take it   Grip again: hand it back\n" +
                             "  Trigger: forceps bite / suction\n" +
                             "  Scalpel: incision line   Freer: lift the flap   Forceps: deviated cartilage\n" : "") +
            "Right stick  look around (autopilot)\n" +
            "Left stick  advance / withdraw scope (autopilot)\n" +
            "Right stick click  labels on/off\n" +
            (commands != null ? Display("VOICE (Turkish): sonraki adım / önceki adım / neşter ver / kaldırıcı ver / makas ver / emme ver /\n" +
                                        "  aleti bırak / etiket aç-kapat / merkeze al / durum nedir / ne yapmalıyım / tekrar / sesi kapat-aç / yardım") : "");

        /// <summary>The monitor font has no dotless i, s-cedilla or g-breve: show them as plain letters.</summary>
        static string Display(string t) =>
            t.Replace('ı', 'i').Replace('ş', 's').Replace('ğ', 'g').Replace('İ', 'I').Replace('Ş', 'S').Replace('Ğ', 'G');

        void Update()
        {
            var syn = Synthetic;
            if (syn == null) return;
            syn.manualLook = input.Look;
            syn.manualAdvance = input.Advance;
            if (syn.Instrument != null) syn.Instrument.rollInput = input.Twist;
            if (commands != null && commands.Status != _voiceStatus)
            {
                _voiceStatus = commands.Status;
                overlay.ShowMentor(Display(_voiceStatus), Severity.Info, 4f);
            }
            var line = PhaseLine(mentor.Phase, _manualPhase);
            if (line != _progress) { _progress = line; overlay.SetPhase(line); }
        }

        void OnCycleInstrument()
        {
            var syn = Synthetic;
            if (syn == null || syn.Instrument == null) return;
            var s = hands != null && hands.Active ? hands.Cycle() : syn.Instrument.Next();
            string name = s == Structure.Background ? "none" : InstrumentModels.DisplayName(s);
            if (s == Structure.Background || hands == null || !hands.Active) overlay.ShowMentor("Instrument: " + name, Severity.Info, 2f);
            logger.Log("input", mentor.Phase.ToString(), null, null, "B: instrument " + name);
        }

        void OnAsk()
        {
            logger.Log("input", mentor.Phase.ToString(), null, null, "Y: ask AI");
            if (!ai.Configured) { overlay.ShowMentor("AI mentor is off in this build (no proxy endpoint).", Severity.Info, 3f); return; }
            if (speech.StartListening()) { overlay.ShowAI("Listening..."); return; }
            speech.OnTranscription(defaultQuestion);   // no recognizer wired: canned question
        }

        static readonly List<XRInputSubsystem> Subsystems = new List<XRInputSubsystem>();

        void DoRecenter()
        {
            SubsystemManager.GetSubsystems(Subsystems);
            if (room != null) { room.PlaceUser(head); return; }
            foreach (var s in Subsystems) s.TryRecenter();
            if (head == null || userRig == null) return;
            var fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            userRig.position = head.position + fwd * rigDistance + Vector3.up * rigHeightOffset;
            userRig.rotation = Quaternion.LookRotation(fwd, Vector3.up);
        }
    }
}
