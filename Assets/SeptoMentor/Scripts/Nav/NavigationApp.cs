using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

namespace SeptoMentor.Nav
{
    /// <summary>
    /// XR (passthrough) navigation for septoplasty: load the patient's CT, register it to the
    /// patient's face with a tracked pointer (controller tip), then follow the surgeon through
    /// the operative phases with CT slices at the tip, rule-based warnings and AI feedback.
    ///
    /// RESEARCH PROTOTYPE. Quest controller tracking and landmark registration typically give
    /// several millimetres of error; this is not a certified navigation system and must not be
    /// the basis of a surgical decision. Clinical use needs ethics / regulatory approval.
    ///
    /// Controls: right trigger = capture / hold to collect, A = next / confirm, B = back / redo,
    /// X = overlay mode, Y = ask AI, left trigger = CT window, right stick click = move panel here,
    /// hold left grip + sticks = nudge registration, hold both grips 2 s = main menu.
    /// Hands (controllers put down): right index fingertip = pointer (no calibration, but only ~5-10 mm
    /// accurate), left pinch = trigger, left palm toward the face = poke menu with the other buttons.
    /// </summary>
    public class NavigationApp : MonoBehaviour, IMentorContext
    {
        enum Step { SelectCase, Loading, Calibrate, PlanLandmarks, Register, Surface, Verify, Navigate, Debrief }

        public Shader ghostShader, markerShader;
        public NavDashboard dashboard;
        public AIMentorClient ai;
        public SpeechIO speech;
        public SessionLogger logger;
        public Transform head;
        [Tooltip("Tracking-space parent of the camera (XR origin).")]
        public Transform trackingOrigin;

        [Header("Rules (teaching defaults, not validated limits)")]
        public float lStrutMm = 10f;
        public float skullBaseCautionMm = 15f, skullBaseWarningMm = 10f;
        public float maxAcceptableFreMm = 4f;

        // ---- state ----
        Step _step = Step.SelectCase;
        readonly NavInput _in = new NavInput();
        List<string> _cases;
        int _caseIdx;
        volatile string _progressText = "";
        Task<Loaded> _loadTask;

        CaseData _case;
        Texture3D _ctTex, _labelTex;
        Registration.PointGrid _skinGrid;
        Transform _patientRoot, _skin, _bone, _labelsRoot, _ltrut, _landmarkRoot;
        MeshCollider _skinCollider;
        int _overlayMode = 1;   // 0 targets only, 1 + bone, 2 + skin, 3 off
        int _window;             // 0 bone, 1 soft tissue

        Vector3 _tipOffset = new Vector3(0f, -0.02f, 0.06f);   // grip pose -> tip, refined by pivot calibration
        bool _tipCalibrated;
        Transform _tipMarker, _shaft;
        Vector3 _tipWorld, _tipModel;
        bool _tipValid, _handPointer;
        HandMenu _handMenu;

        readonly List<Quaternion> _pivotRot = new List<Quaternion>();
        readonly List<Vector3> _pivotPos = new List<Vector3>();
        string _calibMsg = "";

        List<string> _regNames;
        int _regIdx;
        readonly List<Vector3> _regModel = new List<Vector3>(), _regWorld = new List<Vector3>();
        readonly List<string> _planMissing = new List<string>();
        int _planIdx;
        readonly List<Vector3> _surfacePts = new List<Vector3>();
        Registration.Rigid _modelToWorld = Registration.Rigid.Identity;
        bool _registered, _trackingSuspect;
        float _fre = float.NaN, _surfaceRms = float.NaN;

        SurgicalPhase _phase = SurgicalPhase.Setup;
        readonly Dictionary<SurgicalPhase, float> _phaseTime = new Dictionary<SurgicalPhase, float>();
        readonly Dictionary<SurgicalPhase, int> _phaseWarnings = new Dictionary<SurgicalPhase, int>();
        readonly Dictionary<string, int> _streak = new Dictionary<string, int>();
        readonly Dictionary<string, float> _lastFired = new Dictionary<string, float>();
        readonly HashSet<string> _seen = new HashSet<string>();
        float _gripsSince = -1;
        string _nearby = "", _summary = "";
        float _lStrutDistMm = float.PositiveInfinity, _skullClearMm = float.PositiveInfinity;

        static readonly List<XRInputSubsystem> Subsystems = new List<XRInputSubsystem>();

        class Loaded
        {
            public CaseData data;
            public SurfaceNets skin, bone;
            public List<KeyValuePair<byte, SurfaceNets>> labels = new List<KeyValuePair<byte, SurfaceNets>>();
            public List<CaseLoader.ObjPart> objs;
            public Registration.PointGrid skinGrid;
            public string error;
        }

        // ================================================================ lifecycle

        void Start()
        {
            if (trackingOrigin == null) trackingOrigin = new GameObject("TrackingOrigin").transform;
            _cases = CaseLoader.ListCases();
            if (PlayerPrefs.HasKey("nav_tip_x"))
            {
                _tipOffset = new Vector3(PlayerPrefs.GetFloat("nav_tip_x"), PlayerPrefs.GetFloat("nav_tip_y"), PlayerPrefs.GetFloat("nav_tip_z"));
                _tipCalibrated = true;
            }
            _patientRoot = new GameObject("PatientRoot").transform;
            BuildPointer();
            _handMenu = new GameObject("HandMenu").AddComponent<HandMenu>();
            _handMenu.head = head; _handMenu.shader = markerShader;
            NavInput.AddMenuButtons(_handMenu);
            _in.menu = _handMenu;

            SubsystemManager.GetSubsystems(Subsystems);
            foreach (var s in Subsystems) s.trackingOriginUpdated += OnTrackingOriginUpdated;

            ai.Answer += (kind, text) =>
            {
                dashboard.ShowAI(text);
                logger.Log("ai", PhaseName, kind, null, text);
                speech.Speak(text);
            };
            speech.QuestionHeard += q =>
            {
                logger.Log("question", PhaseName, null, null, q);
                dashboard.ShowAI("Thinking about: \"" + q + "\"");
                ai.Ask(q);
            };
            dashboard.SetWindow(2000, 300);
            Invoke(nameof(PlaceDashboard), 0.5f);
            logger.Log("nav", null, null, null, "XR navigation started; cases: " + string.Join(", ", _cases));
        }

        void OnDestroy()
        {
            foreach (var s in Subsystems) s.trackingOriginUpdated -= OnTrackingOriginUpdated;
        }

        void OnTrackingOriginUpdated(XRInputSubsystem _)
        {
            if (!_registered) return;
            _trackingSuspect = true;
            Emit("tracking_reset", Severity.Warning,
                "Headset tracking was re-centred: the CT overlay may have moved. Re-verify (touch the nose tip) before relying on it.", force: true);
        }

        void Update()
        {
            _in.Update();
            UpdateTip();
            MenuGesture();

            if (_in.StickClick) PlaceDashboard();
            if (_in.X) CycleOverlay();
            if (_in.LeftTriggerDown) CycleWindow();
            if (_in.Y) Ask();
            if (_registered && _in.LeftGrip) Nudge();

            switch (_step)
            {
                case Step.SelectCase: SelectCase(); break;
                case Step.Loading: Loading(); break;
                case Step.Calibrate: Calibrate(); break;
                case Step.PlanLandmarks: PlanLandmarks(); break;
                case Step.Register: Register(); break;
                case Step.Surface: Surface(); break;
                case Step.Verify: Verify(); break;
                case Step.Navigate: Navigate(); break;
                case Step.Debrief: Debrief(); break;
            }
            UpdateStatus();
        }

        // ================================================================ steps

        void SelectCase()
        {
            if (_in.StickStepY != 0) _caseIdx = (_caseIdx - _in.StickStepY + _cases.Count) % _cases.Count;
            var sb = new StringBuilder();
            for (int i = 0; i < _cases.Count; i++) sb.Append(i == _caseIdx ? "> " : "   ").Append(_cases[i]).Append('\n');
            sb.Append("Copy CT folders (DICOM or .nrrd) to Android/data/org.septomentor.app/files/Cases/<name>/");
            dashboard.SetStep("1. Select case  (right stick up/down, A = load)", sb.ToString());
            if (_in.A) StartLoad(_cases[_caseIdx]);
        }

        void StartLoad(string name)
        {
            _step = Step.Loading;
            string root = CaseLoader.CasesRoot;
            logger.Log("nav", null, null, null, "loading case " + name);
            _loadTask = Task.Run(() =>
            {
                var r = new Loaded();
                try
                {
                    r.data = CaseLoader.Load(name, root, p => _progressText = p);
                    var v = r.data.volume;
                    _progressText = "Building skin surface...";
                    r.skin = SurfaceNets.Extract((i, j, k) => v.At(i, j, k), -300, Vector3Int.zero,
                                                 new Vector3Int(v.nx - 1, v.ny - 1, v.nz - 1), 2, v.IndexToModel);
                    r.skinGrid = new Registration.PointGrid(r.skin.vertices, 0.004f);
                    CropAroundNose(r.data, out var min, out var max);
                    _progressText = "Building bone surface...";
                    r.bone = SurfaceNets.Extract((i, j, k) => v.At(i, j, k), 300, min, max, 1, v.IndexToModel);
                    if (r.data.labels != null)
                    {
                        foreach (var kv in r.data.labelNames)
                        {
                            byte id = kv.Key; var lab = r.data.labels;
                            _progressText = "Building " + kv.Value + "...";
                            r.labels.Add(new KeyValuePair<byte, SurfaceNets>(id,
                                SurfaceNets.Extract((i, j, k) => lab[v.Index(i, j, k)] == id ? 1f : 0f, 0.5f, min, max, 1, v.IndexToModel)));
                        }
                    }
                    r.objs = CaseLoader.ReadObjs(name, root);
                }
                catch (Exception e) { r.error = e.Message; }
                return r;
            });
        }

        static void CropAroundNose(CaseData d, out Vector3Int min, out Vector3Int max)
        {
            var v = d.volume;
            min = Vector3Int.zero; max = new Vector3Int(v.nx - 1, v.ny - 1, v.nz - 1);
            if (!d.TryGetLandmark("Pronasale", out var tip)) return;
            var toIndex = v.IndexToModel.inverse;
            var lo = new Vector3(tip.x - 0.045f, tip.y - 0.045f, tip.z - 0.005f);
            var hi = new Vector3(tip.x + 0.045f, tip.y + 0.075f, tip.z + 0.120f);
            var a = toIndex.MultiplyPoint3x4(lo); var b = toIndex.MultiplyPoint3x4(hi);
            min = new Vector3Int(Clamp(Mathf.Min(a.x, b.x), v.nx), Clamp(Mathf.Min(a.y, b.y), v.ny), Clamp(Mathf.Min(a.z, b.z), v.nz));
            max = new Vector3Int(Clamp(Mathf.Max(a.x, b.x), v.nx), Clamp(Mathf.Max(a.y, b.y), v.ny), Clamp(Mathf.Max(a.z, b.z), v.nz));
        }

        static int Clamp(float f, int n) => Mathf.Clamp(Mathf.RoundToInt(f), 0, n - 1);

        void Loading()
        {
            dashboard.SetStep("Loading " + _cases[_caseIdx], _progressText);
            if (_loadTask == null || !_loadTask.IsCompleted) return;
            var r = _loadTask.Result;
            _loadTask = null;
            if (r.error != null)
            {
                Emit("load_error", Severity.Warning, "Could not load case: " + r.error, force: true);
                _step = Step.SelectCase;
                return;
            }
            BuildCase(r);
            _step = Step.Calibrate;
            _calibMsg = _tipCalibrated ? "Saved calibration loaded." : "Not calibrated: using a default tip offset.";
        }

        void BuildCase(Loaded r)
        {
            _case = r.data;
            _skinGrid = r.skinGrid;
            var v = _case.volume;
            _ctTex = v.ToTexture();
            if (_case.labels != null)
            {
                _labelTex = new Texture3D(v.nx, v.ny, v.nz, TextureFormat.R8, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                _labelTex.SetPixelData(_case.labels, 0);
                _labelTex.Apply(false, true);
            }
            dashboard.SetVolume(v, _ctTex, _labelTex);

            _skin = MeshObject("Skin", r.skin.ToMesh("Skin"), new Color(1f, 0.85f, 0.75f, 0.18f));
            _skinCollider = _skin.gameObject.AddComponent<MeshCollider>();
            _skinCollider.sharedMesh = _skin.GetComponent<MeshFilter>().sharedMesh;
            _bone = MeshObject("Bone", r.bone.ToMesh("Bone"), new Color(1f, 0.95f, 0.8f, 0.22f));
            _labelsRoot = new GameObject("Targets").transform;
            _labelsRoot.SetParent(_patientRoot, false);
            foreach (var kv in r.labels)
            {
                var c = kv.Key == CtPhantom.LabelCartilage ? new Color(0.31f, 0.67f, 1f, 0.5f)
                      : kv.Key == CtPhantom.LabelSeptumBone ? new Color(1f, 0.67f, 0f, 0.45f) : new Color(0.24f, 0.86f, 0.47f, 0.3f);
                MeshObject(_case.labelNames[kv.Key], kv.Value.ToMesh(_case.labelNames[kv.Key]), c).SetParent(_labelsRoot, false);
            }
            foreach (var p in r.objs ?? new List<CaseLoader.ObjPart>())
            {
                var m = new Mesh { name = p.name, indexFormat = p.verts.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
                m.SetVertices(p.verts); m.SetTriangles(p.tris, 0); m.RecalculateNormals();
                MeshObject(p.name, m, new Color(0.6f, 0.8f, 1f, 0.35f)).SetParent(_labelsRoot, false);
            }
            BuildLStrut();
            BuildLandmarkMarkers();

            _regNames = new List<string>();
            foreach (var n in CaseLoader.RegistrationLandmarks) if (_case.TryGetLandmark(n, out _)) _regNames.Add(n);
            _planMissing.Clear();
            foreach (var n in CaseLoader.RegistrationLandmarks) if (!_case.TryGetLandmark(n, out _)) _planMissing.Add(n);

            PlacePreview();
            ApplyOverlayMode();
            if (!string.IsNullOrEmpty(_case.warning)) Emit("case_warning", Severity.Caution, _case.warning, force: true);
            logger.Log("nav", null, null, null, $"case {_case.name}: {v.description}; landmarks {_case.landmarks.Count}; skin {r.skin.vertices.Count} v, bone {r.bone.vertices.Count} v");
        }

        void Calibrate()
        {
            dashboard.SetStep("2. Pointer tip calibration",
                "Rest the controller tip (or a stylus fixed to it) in a small divot that cannot move. " +
                "HOLD the trigger and slowly pivot the controller around the tip for ~5 s, then release.\n" +
                _calibMsg + "   A = continue" + (_tipCalibrated ? "" : " with default") + "   B = back to cases");
            if (_handPointer)
            {
                dashboard.SetStep("2. Pointer: index fingertip (hand tracking)",
                    "Your right index fingertip is the pointer - no calibration needed. Hand tracking is only accurate to ~5-10 mm: " +
                    "for registration and verification prefer the calibrated controller.\nNext = continue   Back = back to cases");
                if (_in.A) _step = _planMissing.Count > 0 ? Step.PlanLandmarks : Step.Register;
                if (_in.B) { ClearCase(); _step = Step.SelectCase; }
                return;
            }
            if (_in.TriggerHeld && NavInput.RightPose(out var p, out var q))
            {
                _pivotPos.Add(trackingOrigin.TransformPoint(p)); _pivotRot.Add(trackingOrigin.rotation * q);
                _calibMsg = $"Collecting... {_pivotPos.Count} samples";
            }
            else if (_pivotPos.Count > 0)
            {
                float spread = 0;
                foreach (var r in _pivotRot) spread = Mathf.Max(spread, Quaternion.Angle(_pivotRot[0], r));
                if (_pivotPos.Count < 60 || spread < 25f)
                    _calibMsg = $"Not enough motion ({spread:0} deg, {_pivotPos.Count} samples). Pivot wider (> 25 deg) and longer.";
                else
                {
                    float rms = Registration.Pivot(_pivotRot, _pivotPos, out var tip, out _) * 1000f;
                    if (rms < 2.5f && tip.magnitude < 0.4f)
                    {
                        _tipOffset = tip; _tipCalibrated = true;
                        PlayerPrefs.SetFloat("nav_tip_x", tip.x); PlayerPrefs.SetFloat("nav_tip_y", tip.y); PlayerPrefs.SetFloat("nav_tip_z", tip.z);
                        PlayerPrefs.Save();
                        _calibMsg = $"Calibrated: tip {tip.magnitude * 1000f:0} mm from the grip, RMS {rms:0.0} mm.";
                    }
                    else _calibMsg = $"Calibration rejected (RMS {rms:0.0} mm). Keep the tip still in the divot and try again.";
                    logger.Log("nav", null, "pivot", null, _calibMsg);
                }
                _pivotPos.Clear(); _pivotRot.Clear();
            }
            if (_in.B) { ClearCase(); _step = Step.SelectCase; }
            if (_in.A) _step = _planMissing.Count > 0 ? Step.PlanLandmarks : Step.Register;
        }

        void PlanLandmarks()
        {
            if (_planIdx >= _planMissing.Count) { _step = Step.Register; return; }
            string name = _planMissing[_planIdx];
            dashboard.SetStep("3. Plan landmarks on the CT",
                $"This CT has no '{name}' landmark. Point the controller ray at it on the floating model and pull the trigger.\n" +
                "A = skip this landmark   B = undo previous");
            bool hit = Physics.Raycast(_tipWorld - RayDir() * 0.05f, RayDir(), out var h, 1.5f) && h.collider == _skinCollider;
            _shaftTarget = hit ? h.point : (Vector3?)null;
            if (_in.TriggerDown && hit)
            {
                var model = _patientRoot.InverseTransformPoint(h.point);
                _case.landmarks.Add(new Landmark(name, model));
                logger.Log("nav", null, "plan", null, $"{name} planned at {CtVolume.ModelToLps(model)} LPS mm");
                _planIdx++;
                RebuildLandmarkLists();
            }
            if (_in.A) _planIdx++;
            if (_in.B && _planIdx > 0)
            {
                _planIdx--;
                _case.landmarks.RemoveAll(l => l.name == _planMissing[_planIdx]);
                RebuildLandmarkLists();
            }
        }

        Vector3? _shaftTarget;

        void RebuildLandmarkLists()
        {
            _regNames.Clear();
            foreach (var n in CaseLoader.RegistrationLandmarks) if (_case.TryGetLandmark(n, out _)) _regNames.Add(n);
            BuildLandmarkMarkers();
        }

        void Register()
        {
            _shaftTarget = null;
            if (_regNames.Count < 3)
            {
                dashboard.SetStep("Registration needs 3 landmarks", "Only " + _regNames.Count + " landmarks are defined. B = plan landmarks.");
                if (_in.B) { _planIdx = 0; _step = Step.PlanLandmarks; }
                return;
            }
            if (_regIdx >= _regNames.Count)
            {
                FinishPairedRegistration();
                return;
            }
            string name = _regNames[_regIdx];
            HighlightLandmark(name);
            dashboard.SetStep($"4. Registration: touch {name}  ({_regIdx + 1}/{_regNames.Count})",
                $"Touch the patient's {Describe(name)} with the pointer tip and pull the trigger.\n" +
                "The highlighted point on the model shows where. A = skip   B = undo previous" +
                (_regWorld.Count >= 3 ? "   (3+ captured: A on the last one finishes)" : ""));
            if (_in.TriggerDown && _tipValid)
            {
                _case.TryGetLandmark(name, out var m);
                _regModel.Add(m); _regWorld.Add(_tipWorld);
                logger.Log("nav", null, "landmark", null, name + " captured" + (_handPointer ? " (hand pointer)" : ""));
                _regIdx++;
            }
            else if (_in.A) _regIdx++;
            else if (_in.B && _regIdx > 0)
            {
                _regIdx--;
                if (_regWorld.Count > 0) { _regWorld.RemoveAt(_regWorld.Count - 1); _regModel.RemoveAt(_regModel.Count - 1); }
            }
        }

        static string Describe(string n)
        {
            switch (n)
            {
                case "Nasion": return "nasion (deepest point between the eyes, at the nasal root)";
                case "Pronasale": return "nose tip (most anterior point)";
                case "Subnasale": return "subnasale (where the columella meets the upper lip)";
                case "Endocanthion R": return "RIGHT inner eye corner (patient's right)";
                case "Endocanthion L": return "LEFT inner eye corner (patient's left)";
            }
            return n;
        }

        void FinishPairedRegistration()
        {
            if (_regWorld.Count < 3)
            {
                Emit("reg_few", Severity.Caution, "At least 3 landmarks are needed. Starting the landmark round again.", force: true);
                _regIdx = 0; _regWorld.Clear(); _regModel.Clear();
                return;
            }
            _fre = Registration.Fit(_regModel, _regWorld, out _modelToWorld) * 1000f;
            ApplyRegistration();
            HighlightLandmark(null);
            logger.Log("nav", null, "registration", null, $"paired-point FRE {_fre:0.0} mm with {_regWorld.Count} landmarks");
            Emit("reg_done", _fre > maxAcceptableFreMm ? Severity.Caution : Severity.Info,
                $"Landmark registration error {_fre:0.0} mm" + (_fre > maxAcceptableFreMm ? " - high: re-touch the landmarks more carefully (B in the next step)." : "."), force: true);
            _surfacePts.Clear();
            _step = Step.Surface;
        }

        void ApplyRegistration()
        {
            _registered = true;
            _trackingSuspect = false;
            _patientRoot.SetPositionAndRotation(_modelToWorld.translation, _modelToWorld.rotation);
            if (_overlayMode == 2) _overlayMode = 1;
            ApplyOverlayMode();
        }

        void Surface()
        {
            dashboard.SetStep("5. Surface refinement (optional)",
                $"HOLD the trigger and stroke the tip over the nasal dorsum, forehead and cheeks.  Points: {_surfacePts.Count} (aim for 150+)\n" +
                "A = refine and continue   B = skip refinement" + (_fre > maxAcceptableFreMm ? "   (FRE high - consider redoing: B then B)" : ""));
            if (_in.TriggerHeld && _tipValid && (_surfacePts.Count == 0 || (_surfacePts[_surfacePts.Count - 1] - _tipWorld).sqrMagnitude > 0.002f * 0.002f))
                _surfacePts.Add(_tipWorld);
            if (_in.A)
            {
                if (_surfacePts.Count >= 40)
                {
                    var before = _modelToWorld;
                    var refined = _modelToWorld;
                    float rms = Registration.Icp(_skinGrid, _surfacePts, ref refined) * 1000f;
                    float moved = Vector3.Distance(refined.translation, before.translation) * 1000f;
                    float turned = Quaternion.Angle(refined.rotation, before.rotation);
                    if (!float.IsInfinity(rms) && moved < 15f && turned < 15f)
                    {
                        _modelToWorld = refined; _surfaceRms = rms;
                        ApplyRegistration();
                        Emit("icp", Severity.Info, $"Surface refinement: {rms:0.0} mm RMS to the CT skin (moved {moved:0.0} mm, {turned:0.0} deg).", force: true);
                    }
                    else Emit("icp_rejected", Severity.Caution, $"Surface refinement rejected (moved {moved:0} mm / {turned:0} deg). Keeping the landmark registration.", force: true);
                    logger.Log("nav", null, "icp", null, $"rms {rms:0.0} mm, moved {moved:0.0} mm, {turned:0.0} deg, points {_surfacePts.Count}");
                }
                _step = Step.Verify;
            }
            else if (_in.B) _step = Step.Verify;
        }

        void Verify()
        {
            float d = TipToSkinMm();
            dashboard.SetStep("6. Verify accuracy",
                "Touch the nose tip, the nasion and a cheek. The tip-to-CT-skin distance should read close to 0 mm when touching skin.\n" +
                $"Tip to CT skin: {(float.IsInfinity(d) ? "--" : d.ToString("0.0") + " mm")}     A = accept   B = redo registration");
            if (_in.A)
            {
                _trackingSuspect = false;
                logger.Log("nav", null, "verify", null, $"accepted; FRE {_fre:0.0} mm, surface {_surfaceRms:0.0} mm, last check {d:0.0} mm");
                if (_phaseTime.Count == 0) StartPhase(SurgicalPhase.Setup, true);
                _step = Step.Navigate;
            }
            if (_in.B)
            {
                _regIdx = 0; _regWorld.Clear(); _regModel.Clear(); _registered = false;
                PlacePreview();
                _step = Step.Register;
            }
        }

        float TipToSkinMm()
        {
            if (!_tipValid || _skinGrid == null) return float.PositiveInfinity;
            var m = _patientRoot.InverseTransformPoint(_tipWorld);
            return _skinGrid.Nearest(m, 0.03f, out var n) ? Vector3.Distance(m, n) * 1000f : float.PositiveInfinity;
        }

        // ================================================================ navigation

        void Navigate()
        {
            _phaseTime[_phase] = _phaseTime.TryGetValue(_phase, out var t) ? t + Time.deltaTime : Time.deltaTime;
            dashboard.SetStep($"Phase: {PhaseTitle(_phase)}   (A next, B previous)", Checklist(_phase));
            if (_in.A) { if (_phase == SurgicalPhase.Closure) { _step = Step.Debrief; SendDebrief(); } else StartPhase(_phase + 1, true); }
            else if (_in.B && _phase > SurgicalPhase.Setup) StartPhase(_phase - 1, true);

            _seen.Clear();
            if (_tipValid) EvaluateRules();
            var keys = new List<string>(_streak.Keys);
            foreach (var k in keys) if (!_seen.Contains(k)) _streak[k] = 0;
        }

        void StartPhase(SurgicalPhase p, bool manual)
        {
            var old = _phase;
            _phase = p;
            if (!_phaseTime.ContainsKey(p)) _phaseTime[p] = 0;
            logger.Log("phase", p.ToString(), null, null, (manual ? "manual " : "auto ") + old + " -> " + p);
            Emit("phase_" + p, Severity.Info, PhaseTitle(p) + ": " + Checklist(p).Split('\n')[0], force: true);
            ai.Coach("phase_change", new MentorEvent { ruleId = "phase_" + p, severity = Severity.Info, message = "Entered " + PhaseTitle(p), phase = p });
            UpdateProgress();
        }

        static string PhaseTitle(SurgicalPhase p)
        {
            switch (p)
            {
                case SurgicalPhase.Setup: return "Setup & infiltration";
                case SurgicalPhase.Incision: return "Incision";
                case SurgicalPhase.FlapElevation: return "Flap elevation";
                case SurgicalPhase.Resection: return "Cartilage / bone resection";
                case SurgicalPhase.Closure: return "Reposition & closure";
            }
            return p.ToString();
        }

        static string Checklist(SurgicalPhase p)
        {
            switch (p)
            {
                case SurgicalPhase.Setup:
                    return "Confirm side and deviation on the CT slices; touch the caudal septum and the deviation with the pointer.\n" +
                           "Infiltrate both sides subperichondrially; check the slice view for spurs and the skull-base height.";
                case SurgicalPhase.Incision:
                    return "Hemitransfixion or Killian incision through mucoperichondrium down to cartilage, not into it.\n" +
                           "Pointer at the caudal border shows the start of the caudal L-strut.";
                case SurgicalPhase.FlapElevation:
                    return "Stay subperichondrial (bluish-white cartilage). Elevate over the deviation, then subperiosteal over bone.\n" +
                           "Avoid opposing tears at the same level.";
                case SurgicalPhase.Resection:
                    return $"Remove only the deviated part. Keep >= {10:0} mm dorsal and caudal L-strut (orange band).\n" +
                           "At the perpendicular plate cut sharply, never twist - watch the skull-base clearance.";
                case SurgicalPhase.Closure:
                    return "Inspect both flaps for tears, reposition, quilting suture or splints per attending.\n" +
                           "A = finish and show the debrief.";
            }
            return "";
        }

        void EvaluateRules()
        {
            var m = _tipModel;
            _lStrutDistMm = float.PositiveInfinity; _skullClearMm = float.PositiveInfinity;
            bool haveStrut = _case.TryGetLandmark("SeptumASA", out var asa) & _case.TryGetLandmark("SeptumDorsal", out var dor) & _case.TryGetLandmark("SeptumANS", out var ans);
            bool nearSeptum = haveStrut && Mathf.Abs(m.x - asa.x) < 0.008f;
            if (haveStrut)
            {
                float dd = DistToSegment(m, asa, dor), dc = DistToSegment(m, asa, ans);
                _lStrutDistMm = Mathf.Min(dd, dc) * 1000f;
                bool inside = nearSeptum && InsideL(m, asa, dor, ans);
                if (_phase == SurgicalPhase.Resection)
                    Check("l_strut", inside && _lStrutDistMm < lStrutMm, Severity.Warning,
                        $"Tip is {_lStrutDistMm:0} mm from the {(dd < dc ? "dorsal" : "caudal")} septal border - inside the {lStrutMm:0} mm L-strut. Do not resect here.");
                if (_phase == SurgicalPhase.Incision)
                    Check("caudal_border", nearSeptum && dc < 0.004f, Severity.Tip,
                        "At the caudal septal border: incision site for hemitransfixion.", cooldown: 30f);
            }
            if (_case.TryGetLandmark("SkullBase", out var sb) && _case.TryGetLandmark("Pronasale", out var tipNose) && m.z > tipNose.z + 0.02f
                && Mathf.Abs(m.x - sb.x) < 0.02f)
            {
                _skullClearMm = (sb.y - m.y) * 1000f;
                Check("skull_base_warn", _skullClearMm < skullBaseWarningMm, Severity.Warning,
                    $"Skull base about {_skullClearMm:0} mm above the tip. Cut, don't twist or rock.");
                Check("skull_base_caution", _skullClearMm >= skullBaseWarningMm && _skullClearMm < skullBaseCautionMm, Severity.Caution,
                    $"Approaching the skull base: about {_skullClearMm:0} mm above the tip.", cooldown: 20f);
            }
            if (_trackingSuspect) Check("tracking_suspect", true, Severity.Caution, "Tracking was re-centred: re-verify registration (B on the verify step).", cooldown: 30f);

            _nearby = NearbyStructures(m);
            _summary = NavSummary();
        }

        /// <summary>Tip inside the septal L (between its borders), using the parallelogram they span.</summary>
        static bool InsideL(Vector3 m, Vector3 asa, Vector3 dor, Vector3 ans)
        {
            var u = new Vector2(dor.y - asa.y, dor.z - asa.z); var v = new Vector2(ans.y - asa.y, ans.z - asa.z);
            var p = new Vector2(m.y - asa.y, m.z - asa.z);
            float det = u.x * v.y - u.y * v.x;
            if (Mathf.Abs(det) < 1e-9f) return true;
            float a = (p.x * v.y - p.y * v.x) / det, b = (u.x * p.y - u.y * p.x) / det;
            return a > -0.1f && b > -0.1f && a < 1.2f && b < 1.2f;
        }

        static float DistToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            var ab = b - a; float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(1e-12f, ab.sqrMagnitude));
            return Vector3.Distance(p, a + ab * t);
        }

        string NearbyStructures(Vector3 m)
        {
            var v = _case.volume;
            float hu = v.SampleModel(m);
            string tissue = hu < -500 ? "air" : hu < 100 ? "soft tissue" : hu < 400 ? "cartilage/soft" : "bone";
            if (_case.labels == null) return tissue;
            var names = new HashSet<string>();
            var toIndex = v.IndexToModel.inverse;
            for (int z = -1; z <= 1; z++) for (int y = -1; y <= 1; y++) for (int x = -1; x <= 1; x++)
            {
                var q = toIndex.MultiplyPoint3x4(m + new Vector3(x, y, z) * 0.003f);
                int i = Mathf.RoundToInt(q.x), j = Mathf.RoundToInt(q.y), k = Mathf.RoundToInt(q.z);
                if (i < 0 || j < 0 || k < 0 || i >= v.nx || j >= v.ny || k >= v.nz) continue;
                byte id = _case.labels[v.Index(i, j, k)];
                if (id != 0 && _case.labelNames.TryGetValue(id, out var n)) names.Add(n);
            }
            return names.Count == 0 ? tissue : tissue + "; near " + string.Join(", ", names);
        }

        string NavSummary()
        {
            var sb = new StringBuilder();
            sb.Append($"registration_error_mm={(float.IsNaN(_surfaceRms) ? _fre : _surfaceRms):0.0}");
            if (_trackingSuspect) sb.Append("; tracking_reset_unverified");
            if (_handPointer) sb.Append("; pointer=hand_tracking_index_finger (extra 5-10 mm uncertainty)");
            sb.Append($"; tip={_nearby}");
            if (_case.TryGetLandmark("SeptumASA", out var asa)) sb.Append($"; tip_side={(_tipModel.x < asa.x ? "patient right" : "patient left")} of septum by {Mathf.Abs(_tipModel.x - asa.x) * 1000f:0} mm");
            if (!float.IsInfinity(_lStrutDistMm)) sb.Append($"; L_strut_border_mm={_lStrutDistMm:0}");
            if (!float.IsInfinity(_skullClearMm)) sb.Append($"; skull_base_clearance_mm={_skullClearMm:0}");
            sb.Append($"; phase_time_s={PhaseSeconds(_phase):0}");
            return sb.ToString();
        }

        void Check(string id, bool cond, Severity sev, string msg, float? cooldown = null)
        {
            if (!cond) return;
            _seen.Add(id);
            _streak.TryGetValue(id, out int n);
            _streak[id] = ++n;
            if (n < 6) return;   // ~0.1 s of persistence at 72 Hz
            Emit(id, sev, msg, cooldown: cooldown);
        }

        void Emit(string id, Severity sev, string msg, bool force = false, float? cooldown = null)
        {
            float now = Time.time;
            if (!force && _lastFired.TryGetValue(id, out var last) && now - last < (cooldown ?? 8f)) return;
            _lastFired[id] = now;
            var e = new MentorEvent { ruleId = id, severity = sev, message = msg, phase = _phase, time = now };
            dashboard.ShowMentor(msg, sev);
            logger.LogMentor(e);
            ai.RememberEvent(e);
            if (sev >= Severity.Caution) speech.Speak(msg);
            if (sev == Severity.Warning)
            {
                NavInput.HapticRight(0.8f, 0.25f);
                _phaseWarnings[_phase] = (_phaseWarnings.TryGetValue(_phase, out var w) ? w : 0) + 1;
                if (_step == Step.Navigate) ai.Coach("warning", e);
                UpdateProgress();
            }
        }

        float PhaseSeconds(SurgicalPhase p) => _phaseTime.TryGetValue(p, out var t) ? t : 0f;
        int Warnings(SurgicalPhase p) => _phaseWarnings.TryGetValue(p, out var w) ? w : 0;

        void UpdateProgress()
        {
            var sb = new StringBuilder("Operative steps\n");
            foreach (SurgicalPhase p in Enum.GetValues(typeof(SurgicalPhase)))
            {
                bool done = p < _phase || _step == Step.Debrief, cur = p == _phase && _step != Step.Debrief;
                sb.Append(done ? "[x] " : cur ? "[>] " : "[ ] ").Append(PhaseTitle(p));
                if (_phaseTime.TryGetValue(p, out var t)) sb.Append($"  {t / 60f:0.0} min");
                if (_phaseWarnings.TryGetValue(p, out var w) && w > 0) sb.Append($"  ({w} warn)");
                sb.Append('\n');
            }
            dashboard.SetProgress(sb.ToString());
        }

        void SendDebrief()
        {
            UpdateProgress();
            var sb = new StringBuilder("Debrief: ");
            foreach (var kv in _phaseTime) sb.Append($"{kv.Key} {kv.Value / 60f:0.0} min ({Warnings(kv.Key)} warnings); ");
            logger.Log("debrief", null, null, null, sb.ToString());
            ai.Coach("debrief", new MentorEvent { ruleId = "debrief", severity = Severity.Info, message = sb.ToString(), phase = _phase });
        }

        void Debrief()
        {
            dashboard.SetStep("Case finished", "The step log is saved in Android/data/org.septomentor.app/files/sessions/.\nB = back to closure   hold both grips = main menu");
            if (_in.B) { _step = Step.Navigate; UpdateProgress(); }
        }

        // ================================================================ pointer, overlay, helpers

        void BuildPointer()
        {
            var mat = new Material(markerShader != null ? markerShader : Shader.Find("SeptoMentor/NavMarker"));
            mat.color = new Color(1f, 0.95f, 0.2f, 1f);
            _tipMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere).transform;
            _tipMarker.name = "PointerTip";
            Destroy(_tipMarker.GetComponent<Collider>());
            _tipMarker.localScale = Vector3.one * 0.004f;
            _tipMarker.GetComponent<Renderer>().sharedMaterial = mat;
            _shaft = GameObject.CreatePrimitive(PrimitiveType.Cylinder).transform;
            _shaft.name = "PointerShaft";
            Destroy(_shaft.GetComponent<Collider>());
            var sm = new Material(mat) { color = new Color(1f, 0.95f, 0.2f, 0.5f) };
            _shaft.GetComponent<Renderer>().sharedMaterial = sm;
        }

        Quaternion _ctrlRot = Quaternion.identity;
        Vector3 _ctrlPos;

        Vector3 RayDir() => (_tipWorld - _ctrlPos).sqrMagnitude > 1e-6f ? (_tipWorld - _ctrlPos).normalized : _ctrlRot * Vector3.forward;

        void UpdateTip()
        {
            bool wasHand = _handPointer;
            _handPointer = NavInput.RightIndex(out var tip, out var distal, out var tq);
            if (_handPointer != wasHand && _registered)
                Emit("pointer_change", Severity.Caution, _handPointer
                    ? "Pointer is now your right index fingertip (hand tracking, ~5-10 mm accuracy). Pick up the controller for precise pointing."
                    : "Pointer is the controller again.", force: true);
            Vector3 p = default; Quaternion q = Quaternion.identity;
            _tipValid = _handPointer || NavInput.RightPose(out p, out q);
            _tipMarker.gameObject.SetActive(_tipValid);
            _shaft.gameObject.SetActive(_tipValid);
            if (!_tipValid) return;
            if (_handPointer)
            {
                // Shaft runs along the finger; the tip joint sits at the fingertip.
                _ctrlPos = trackingOrigin.TransformPoint(distal);
                _ctrlRot = trackingOrigin.rotation * tq;
                _tipWorld = trackingOrigin.TransformPoint(tip);
            }
            else
            {
                _ctrlPos = trackingOrigin.TransformPoint(p);
                _ctrlRot = trackingOrigin.rotation * q;
                _tipWorld = _ctrlPos + _ctrlRot * _tipOffset;
            }
            _tipMarker.position = _tipWorld;
            var end = _shaftTarget ?? _tipWorld;
            var a = _shaftTarget.HasValue ? _tipWorld : _ctrlPos;
            var d = end - a;
            _shaft.position = a + d * 0.5f;
            _shaft.rotation = d.sqrMagnitude > 1e-8f ? Quaternion.FromToRotation(Vector3.up, d) : Quaternion.identity;
            _shaft.localScale = new Vector3(0.0012f, d.magnitude * 0.5f, 0.0012f);
            _tipModel = _patientRoot.InverseTransformPoint(_tipWorld);

            // Slices follow the tip once registered; before that they show the nose region.
            if (_case != null)
            {
                if (_registered) dashboard.SetSliceCentre(_case.volume, _tipModel, true);
                else if (_case.TryGetLandmark("Pronasale", out var n)) dashboard.SetSliceCentre(_case.volume, n + new Vector3(0, 0.005f, 0.03f), false);
            }
        }

        Transform MeshObject(string name, Mesh mesh, Color c)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_patientRoot, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mat = new Material(ghostShader != null ? ghostShader : Shader.Find("SeptoMentor/NavGhost"));
            mat.SetColor("_Color", c);
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go.transform;
        }

        void BuildLStrut()
        {
            if (!(_case.TryGetLandmark("SeptumASA", out var asa) & _case.TryGetLandmark("SeptumDorsal", out var dor) & _case.TryGetLandmark("SeptumANS", out var ans))) return;
            float w = lStrutMm * 0.001f;
            var inDorsal = Vector3.ProjectOnPlane(ans - asa, (dor - asa).normalized).normalized * w;   // toward the septal body
            var inCaudal = Vector3.ProjectOnPlane(dor - asa, (ans - asa).normalized).normalized * w;
            var verts = new List<Vector3> { asa, dor, dor + inDorsal, asa + inDorsal, asa, ans, ans + inCaudal, asa + inCaudal };
            var tris = new List<int> { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
            var m = new Mesh { name = "LStrut" };
            m.SetVertices(verts); m.SetTriangles(tris, 0); m.RecalculateNormals();
            _ltrut = MeshObject("LStrutZone", m, new Color(1f, 0.55f, 0.1f, 0.45f));
        }

        readonly Dictionary<string, Renderer> _markers = new Dictionary<string, Renderer>();

        void BuildLandmarkMarkers()
        {
            if (_landmarkRoot != null) Destroy(_landmarkRoot.gameObject);
            _markers.Clear();
            _landmarkRoot = new GameObject("Landmarks").transform;
            _landmarkRoot.SetParent(_patientRoot, false);
            foreach (var l in _case.landmarks)
            {
                var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                s.name = "LM_" + l.name;
                Destroy(s.GetComponent<Collider>());
                s.transform.SetParent(_landmarkRoot, false);
                s.transform.localPosition = l.model;
                s.transform.localScale = Vector3.one * 0.003f;
                var mat = new Material(markerShader != null ? markerShader : Shader.Find("SeptoMentor/NavMarker")) { color = new Color(0.3f, 0.9f, 1f, 0.8f) };
                var r = s.GetComponent<Renderer>(); r.sharedMaterial = mat;
                _markers[l.name] = r;
            }
        }

        void HighlightLandmark(string name)
        {
            foreach (var kv in _markers)
            {
                bool on = kv.Key == name;
                kv.Value.sharedMaterial.color = on ? new Color(1f, 0.3f, 0.9f, 1f) : new Color(0.3f, 0.9f, 1f, 0.8f);
                kv.Value.transform.localScale = Vector3.one * (on ? 0.006f : 0.003f);
            }
        }

        void CycleOverlay()
        {
            _overlayMode = (_overlayMode + 1) % 4;
            ApplyOverlayMode();
            string[] names = { "targets only", "targets + bone", "targets + bone + skin", "overlay off" };
            dashboard.ShowMentor("Overlay: " + names[_overlayMode], Severity.Info, 2f);
        }

        void ApplyOverlayMode()
        {
            if (_case == null) return;
            bool preview = !_registered;
            bool on = _overlayMode != 3;
            if (_skin != null) _skin.gameObject.SetActive(preview || _overlayMode == 2);
            if (_bone != null) _bone.gameObject.SetActive(on && (preview || _overlayMode >= 1));
            if (_labelsRoot != null) _labelsRoot.gameObject.SetActive(on);
            if (_ltrut != null) _ltrut.gameObject.SetActive(on);
            if (_landmarkRoot != null) _landmarkRoot.gameObject.SetActive(on);
        }

        void CycleWindow()
        {
            _window = (_window + 1) % 2;
            if (_window == 0) dashboard.SetWindow(2000, 300); else dashboard.SetWindow(400, 40);
            dashboard.ShowMentor(_window == 0 ? "CT window: bone" : "CT window: soft tissue", Severity.Info, 2f);
        }

        /// <summary>Hold left grip: left stick moves the overlay in the view plane, right stick Y up/down, right stick X yaw.</summary>
        void Nudge()
        {
            if (head == null) return;
            float dt = Time.deltaTime, speed = 0.005f;   // 5 mm/s
            var right = Vector3.ProjectOnPlane(head.right, Vector3.up).normalized;
            var fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            var move = (right * _in.LeftStick.x + fwd * _in.LeftStick.y + Vector3.up * _in.RightStick.y) * speed * dt;
            var rot = Quaternion.AngleAxis(_in.RightStick.x * 3f * dt, Vector3.up);
            if (move == Vector3.zero && Mathf.Abs(_in.RightStick.x) < 0.05f) return;
            var pivot = _patientRoot.position;
            _modelToWorld.rotation = rot * _modelToWorld.rotation;
            _modelToWorld.translation = rot * (_modelToWorld.translation - pivot) + pivot + move;
            _patientRoot.SetPositionAndRotation(_modelToWorld.translation, _modelToWorld.rotation);
            _nudged = true;
        }

        bool _nudged;

        void PlacePreview()
        {
            if (head == null) return;
            var fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            // Model anterior is -Z: facing the user means model +Z along the user's forward.
            _patientRoot.rotation = Quaternion.LookRotation(fwd, Vector3.up);
            var centre = _case != null && _case.TryGetLandmark("Pronasale", out var n) ? n : Vector3.zero;
            _patientRoot.position = head.position + fwd * 0.45f - Vector3.up * 0.12f - _patientRoot.rotation * centre;
            ApplyOverlayMode();
        }

        void PlaceDashboard()
        {
            if (head == null || dashboard == null) return;
            var fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            // Slightly left of the line of sight so the operative field stays clear.
            var left = Vector3.Cross(fwd, Vector3.up).normalized;
            var pos = head.position + fwd * 0.75f + left * 0.25f + Vector3.up * 0.05f;
            dashboard.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(pos - head.position, Vector3.up));
        }

        void MenuGesture()
        {
            if (!_in.BothGripsHeld) { _gripsSince = -1; return; }
            if (_gripsSince < 0) _gripsSince = Time.time;
            if (Time.time - _gripsSince > 2f) SceneManager.LoadScene(ModeSwitch.LauncherScene);
        }

        void ClearCase()
        {
            for (int i = _patientRoot.childCount - 1; i >= 0; i--) Destroy(_patientRoot.GetChild(i).gameObject);
            _case = null; _registered = false; _regIdx = 0; _regWorld.Clear(); _regModel.Clear(); _planIdx = 0;
            if (_ctTex != null) Destroy(_ctTex);
            if (_labelTex != null) Destroy(_labelTex);
            _ctTex = _labelTex = null;
        }

        void Ask()
        {
            logger.Log("input", PhaseName, null, null, "Y: ask AI");
            if (!ai.Configured) { dashboard.ShowMentor("AI assistant is off in this build (no proxy endpoint).", Severity.Info, 3f); return; }
            if (speech.StartListening()) { dashboard.ShowAI("Listening..."); return; }
            speech.OnTranscription("Given where my pointer is and the current step, what should I watch out for right now?");
        }

        void UpdateStatus()
        {
            string reg = !_registered ? "not registered"
                : $"registered: landmarks {_fre:0.0} mm" + (float.IsNaN(_surfaceRms) ? "" : $", surface {_surfaceRms:0.0} mm") + (_nudged ? ", nudged" : "");
            string cal = _handPointer ? "pointer: index finger (~5-10 mm)" : _tipCalibrated ? "tip calibrated" : "tip NOT calibrated";
            var c = _trackingSuspect ? new Color(1f, 0.35f, 0.3f) : _registered ? new Color(0.6f, 1f, 0.6f) : new Color(1f, 0.85f, 0.4f);
            string hands = _in.HandsActive ? "\nHands: left pinch = trigger, turn left palm to you = button menu (poke with right index)" : "";
            dashboard.SetStatus($"{(_case != null ? _case.name : "no case")}  |  {reg}{(_trackingSuspect ? "  |  TRACKING RESET - RE-VERIFY" : "")}  |  {cal}{hands}", c);

            if (_registered && _tipValid && _case != null)
            {
                var lps = CtVolume.ModelToLps(_tipModel);
                string r = $"Tip LPS ({lps.x:0}, {lps.y:0}, {lps.z:0}) mm   {_nearby}";
                if (!float.IsInfinity(_lStrutDistMm)) r += $"\nL-strut border {_lStrutDistMm:0} mm";
                if (!float.IsInfinity(_skullClearMm)) r += $"   skull base {_skullClearMm:0} mm above";
                dashboard.SetReadout(r);
                if (_step != Step.Navigate) _nearby = NearbyStructures(_tipModel);
            }
            else dashboard.SetReadout(_case == null ? "" : "Slices show the nose region until registration is done.");
        }

        // ================================================================ IMentorContext

        public string Mode => "xr_navigation";
        public string PhaseName => _step == Step.Navigate || _step == Step.Debrief ? _phase.ToString() : "Navigation setup: " + _step;
        public string Instrument => "navigation pointer";
        public string[] VisibleStructures => string.IsNullOrEmpty(_nearby) ? new string[0] : new[] { _nearby };
        public string Navigation => _registered ? _summary : "not registered";
        public bool IsSimulation => _case == null || _case.isPhantom;
        public bool LabelsValidated => false;
        public byte[] CaptureJpeg() => null;
    }
}
