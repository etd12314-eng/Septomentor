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
        public FessSim fess;
        public VoiceCoach voice;
        public VoiceCommands commands;
        public Transform head;
        [Tooltip("Parent of monitor + hologram; moved by Recenter.")]
        public Transform userRig;
        public float rigDistance = 0.9f, rigHeightOffset = -0.15f;

        [Tooltip("Fallback question when no speech recognizer is wired.")]
        public string defaultQuestion = "What should I focus on right now, and what is the main risk at this step?";

        SyntheticSegmentationProvider Synthetic => provider as SyntheticSegmentationProvider;

        public Procedure Procedure { get; private set; } = Procedure.Septoplasty;
        bool Fess => Procedure == Procedure.Fess;

        void Start()
        {
            input.NextPhase += () =>
            {
                if (Fess) fess?.NextStep(); else mentor.NextPhase();
                logger.Log("input", mentor.Phase.ToString(), null, null, "A: next " + (Fess ? "FESS step" : "phase"));
            };
            input.CycleInstrument += OnCycleInstrument;
            input.Recenter += DoRecenter;
            input.AskAI += OnAsk;
            input.ToggleLabels += () => overlay.ToggleLabels();
            input.ToggleScope += () => SetScope(scope == null || !scope.handheld);
            if (scope != null) scope.Hint += (key, msg, sev) => OnHint("scope", key, msg, sev);
            if (hands != null) hands.Hint += (key, msg, sev) => OnHint("instrument", key, msg, sev);
            if (sim != null) sim.Hint += (key, msg, sev) => OnHint("tissue", key, msg, sev);
            if (fess != null) fess.Hint += (key, msg, sev) => OnHint("sinus", key, msg, sev);
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
                if (!e.ruleId.StartsWith("phase_")) Speak(e.ruleId, e.message, e.severity);
                else if (_manualPhase && !Fess) Speak(e.ruleId, e.message, e.severity, true);
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
            SetProcedure(ModeSwitch.StartProcedure, false);
            Invoke(nameof(DoRecenter), 0.5f);   // after the first HMD pose arrives
        }

        bool _manualPhase = true;
        string _voiceStatus;
        string _progress;

        string PhaseLine(SurgicalPhase p, bool manual) =>
            (Fess && fess != null ? fess.Progress
                : $"Phase: {p}{(manual ? "" : " (auto)")}" + (sim != null && sim.Available && sim.Progress.Length > 0 ? "  -  " + sim.Progress : "")) +
            $"   [scope {(Synthetic != null ? Synthetic.viewAngle : 0):0} deg   A next  B instrument  X recenter{(ai.Configured ? "  Y ask" : "")}]";

        /// <summary>Septoplasty or FESS: tray, mentor rules, steps, controls card.</summary>
        public void SetProcedure(Procedure p, bool announce = true)
        {
            Procedure = p;
            hands?.SetProcedure(p);
            mentor.septoplastyRules = p == Procedure.Septoplasty;
            if (scope != null) scope.collide = p == Procedure.Fess;
            var syn = Synthetic;
            if (syn != null && syn.Instrument != null)
            {
                var tray = p == Procedure.Fess ? HandInstruments.FessTray : HandInstruments.SeptoplastyTray;
                var cycle = new Tool[tray.Length + 1];
                System.Array.Copy(tray, 0, cycle, 1, tray.Length);
                syn.Instrument.cycle = cycle;
                syn.Instrument.Set(Tool.None);
            }
            if (p == Procedure.Fess)
            {
                if (mentor.Phase != SurgicalPhase.Setup) mentor.SetPhase(SurgicalPhase.Setup, false);
                fess?.ResetAll();
            }
            overlay.ShowSidePanel(ControlsCard());
            overlay.SetPhase(PhaseLine(mentor.Phase, true));
            logger.Log("procedure", mentor.Phase.ToString(), null, null, p.ToString());
            if (!announce) return;
            voice?.Reply(p == Procedure.Fess ? "proc_fess" : "proc_septo");
            if (p == Procedure.Fess) fess?.Brief();
        }

        void SetScopeAngle(float deg)
        {
            if (Synthetic != null) Synthetic.viewAngle = deg;
            overlay.ShowMentor($"Endoscope {deg:0} degrees", Severity.Info, 2f);
            voice?.Reply("scope_" + Mathf.RoundToInt(deg));
        }

        void OnHint(string source, string key, string msg, Severity sev)
        {
            overlay.ShowMentor(msg, sev, sev >= Severity.Caution ? 5f : 4f);
            logger.Log(source, mentor.Phase.ToString(), key, sev.ToString(), msg);
            Speak(key, msg, sev);
        }

        /// <summary>Turkish voice clip for the key when there is one; otherwise English TTS for cautions and warnings.</summary>
        void Speak(string key, string msg, Severity sev, bool asked = false)
        {
            if (voice != null) voice.Say(key, msg, sev, asked);
            else if (sev >= Severity.Caution) speech.Speak(msg);
        }

        void SetScope(bool handheld)
        {
            if (scope == null) return;
            scope.handheld = handheld;
            overlay.ShowMentor(handheld ? "Hand-held scope: the left controller is the endoscope." : "Scope on autopilot / thumbsticks.", Severity.Info, 3f);
            voice?.Reply(handheld ? "scope_hand" : "scope_auto");
            logger.Log("input", mentor.Phase.ToString(), null, null, "scope mode " + (handheld ? "hand-held" : "auto"));
        }

        void OnVoiceCommand(VoiceCommand cmd, string heard)
        {
            logger.Log("voice", mentor.Phase.ToString(), cmd.ToString(), null, heard);
            overlay.ShowMentor($"Voice: \"{Display(heard)}\"", Severity.Info, 2f);
            switch (cmd)
            {
                case VoiceCommand.Next: if (Fess) fess?.NextStep(); else mentor.NextPhase(); break;
                case VoiceCommand.Previous: if (!Fess && mentor.Phase > SurgicalPhase.Setup) mentor.SetPhase(mentor.Phase - 1, true); break;
                case VoiceCommand.Restart:
                    voice?.Reply("restart");
                    mentor.SetPhase(SurgicalPhase.Setup, true);
                    if (Fess) { fess?.ResetAll(); fess?.Brief(); }
                    break;
                case VoiceCommand.Scalpel: hands?.Request(Fess ? Tool.SickleKnife : Tool.Scalpel); break;
                case VoiceCommand.Elevator: hands?.Request(Tool.Freer); break;
                case VoiceCommand.Forceps: hands?.Request(Fess ? Tool.Blakesley : Tool.ThroughCut); break;
                case VoiceCommand.Suction: hands?.Request(Fess ? Tool.CurvedSuction : Tool.Suction); break;
                case VoiceCommand.Sickle: hands?.Request(Tool.SickleKnife); break;
                case VoiceCommand.Probe: hands?.Request(Tool.BallProbe); break;
                case VoiceCommand.BlakesleyStraight: hands?.Request(Tool.Blakesley); break;
                case VoiceCommand.BlakesleyUp: hands?.Request(Tool.BlakesleyUp); break;
                case VoiceCommand.Backbiter: hands?.Request(Tool.Backbiter); break;
                case VoiceCommand.Shaver: hands?.Request(Tool.Microdebrider); break;
                case VoiceCommand.CurvedSuction: hands?.Request(Tool.CurvedSuction); break;
                case VoiceCommand.Curette: hands?.Request(Tool.JCurette); break;
                case VoiceCommand.Release: hands?.Request(Tool.None); voice?.Reply("tool_none"); break;
                case VoiceCommand.LabelsOn: overlay.SetLabels(true); voice?.Reply("labels_on"); break;
                case VoiceCommand.LabelsOff: overlay.SetLabels(false); voice?.Reply("labels_off"); break;
                case VoiceCommand.Recenter: DoRecenter(); voice?.Reply("recenter"); break;
                case VoiceCommand.Status:
                    float frac = Fess ? (fess != null ? fess.ProgressFraction : 0) : (sim != null ? sim.ProgressFraction : 0);
                    int pct = Mathf.Clamp(Mathf.RoundToInt(frac * 10f) * 10, 0, 100);
                    if (Fess) voice?.SayAll("fess_step_" + fess.Current, "pct_" + pct);
                    else voice?.SayAll("name_" + mentor.Phase, "pct_" + pct);
                    break;
                case VoiceCommand.Hint: if (Fess) fess?.Brief(); else voice?.SayAll("phase_" + mentor.Phase); break;
                case VoiceCommand.Targets: if (Fess) SayTargets(); break;
                case VoiceCommand.Repeat: voice?.Repeat(); break;
                case VoiceCommand.Mute: voice?.Reply("voice_off"); if (voice != null) voice.muted = true; break;
                case VoiceCommand.Unmute: if (voice != null) voice.muted = false; voice?.Reply("voice_on"); break;
                case VoiceCommand.Quiet: SetVerbosity(VoiceCoach.Verbosity.Quiet); break;
                case VoiceCommand.NormalTalk: SetVerbosity(VoiceCoach.Verbosity.Normal); break;
                case VoiceCommand.Verbose: SetVerbosity(VoiceCoach.Verbosity.Full); break;
                case VoiceCommand.ScopeHand: SetScope(true); break;
                case VoiceCommand.ScopeAuto: SetScope(false); break;
                case VoiceCommand.Scope0: SetScopeAngle(0); break;
                case VoiceCommand.Scope30: SetScopeAngle(30); break;
                case VoiceCommand.Scope70: SetScopeAngle(70); break;
                case VoiceCommand.Septoplasty: SetProcedure(Procedure.Septoplasty); break;
                case VoiceCommand.Fess: SetProcedure(Procedure.Fess); break;
                case VoiceCommand.Ask: if (ai.Configured) OnAsk(); else voice?.Reply("no_ai"); break;
                case VoiceCommand.Help: voice?.SayAll(Fess ? "help_fess" : "help"); break;
            }
        }

        void SetVerbosity(VoiceCoach.Verbosity v)
        {
            if (voice == null) return;
            voice.verbosity = v;
            voice.Reply("talk_" + v.ToString().ToLowerInvariant());
            overlay.ShowMentor("Voice: " + (v == VoiceCoach.Verbosity.Quiet ? "warnings only" : v == VoiceCoach.Verbosity.Normal ? "normal" : "everything"), Severity.Info, 2f);
        }

        /// <summary>Speaks the ostia still to find on the side in use.</summary>
        void SayTargets()
        {
            if (fess == null) return;
            var keys = new List<string>();
            foreach (var t in fess.Remaining()) keys.Add("left_" + t);
            if (keys.Count == 0) keys.Add("left_none");
            voice?.SayAll(keys.ToArray());
        }

        string ControlsCard() =>
            "Controls\n" +
            "A  next phase\n" +
            "B  change instrument\n" +
            "X  recenter view\n" +
            (ai.Configured ? "Y  ask the AI mentor\n" : "Y  (AI mentor off in this build)\n") +
            (Fess ? "FESS: find the ostia, uncinectomy, antrostomy, ethmoidectomy, sphenoidotomy\n" : "SEPTOPLASTY: incision, flap, spur excision, closure\n") +
            (scope != null ? "LEFT controller = endoscope: into either nostril" + (Fess ? "" : " (septoplasty on the LEFT)") + "\n" +
                             "Left stick click  hand-held scope / autopilot\n" : "") +
            (hands != null ? "RIGHT hand = instruments (Mayo stand on your right)\n" +
                             "  Grip over an instrument: take it   Grip again: hand it back\n" +
                             "  Trigger: bite / suction / shaver\n" +
                             (Fess ? "  Probe: find ostia   Sickle: incise uncinate   Backbiter / Blakesley / J curette / shaver: remove\n"
                                   : "  Scalpel: incision line   Freer: lift the flap   Forceps: excise the spur\n") : "") +
            "Right stick  look around (autopilot)\n" +
            "Left stick  advance / withdraw scope (autopilot)\n" +
            "Right stick click  labels on/off\n" +
            (commands != null ? Display("VOICE (Turkish): sonraki adım / baştan başla / " +
                                        (Fess ? "eğri bıçak, sonda, düz pens, yukarı pens, kaşık, ters pens, tıraş, eğri emme ver /\n  hedefler / "
                                              : "neşter, kaldırıcı, makas, emme ver /\n  ") +
                                        "aleti bırak / sıfır-otuz-yetmiş derece / birinci-ikinci ameliyat /\n" +
                                        "  az-normal-çok konuş / durum nedir / ne yapmalıyım / tekrar / sesi kapat-aç / yardım") : "");

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
            // Where the instrument is when the endoscope does not show it: an arrow at the edge of the image.
            var vi = syn.Instrument;
            if (vi != null && vi.Tip != null && vi.CurrentTool != Tool.None && syn.EndoCamera != null)
                overlay.SetPointer(syn.EndoCamera.WorldToViewportPoint(vi.Tip.position));
            else overlay.SetPointer(null);
            var line = PhaseLine(mentor.Phase, _manualPhase);
            if (line != _progress) { _progress = line; overlay.SetPhase(line); }
        }

        void OnCycleInstrument()
        {
            var syn = Synthetic;
            if (syn == null || syn.Instrument == null) return;
            var s = hands != null && hands.Active ? hands.Cycle() : syn.Instrument.Next();
            string name = InstrumentModels.DisplayName(s);
            if (s == Tool.None || hands == null || !hands.Active) overlay.ShowMentor("Instrument: " + name, Severity.Info, 2f);
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
