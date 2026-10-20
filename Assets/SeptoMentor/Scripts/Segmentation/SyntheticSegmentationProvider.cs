using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SeptoMentor
{
    /// <summary>
    /// PLACEHOLDER provider: a virtual endoscope flies through the VirtualPatient mesh and
    /// renders (a) endoscope-style video and (b) exact per-pixel labels from the same pose.
    /// Labels are only as correct as the mesh-to-class mapping (OPEN ITEM 1).
    /// </summary>
    public class SyntheticSegmentationProvider : SegmentationProvider
    {
        [Header("Scene")]
        public VirtualPatient patient;
        public Shader labelShader;

        [Header("Endoscope")]
        public int videoSize = 512;
        [Tooltip("Vertical FOV, deg. 0-degree rigid scopes are roughly 70-90 deg diagonal.")]
        public float fieldOfView = 75f;
        public float renderFps = 30f;
        public float nearClip = 0.0005f, farClip = 0.2f;

        [Header("Fly-through")]
        public float pathCycleSeconds = 40f;
        public float swayMeters = 0.0008f;

        [Header("Manual (thumbstick)")]
        public Vector2 manualLook;      // x yaw, y pitch in [-1,1]
        public float manualAdvance;     // [-1,1]

        public VirtualInstrument Instrument { get; private set; }
        public Camera EndoCamera => _endoCam;

        /// <summary>Scope pose in the patient's local space (for the hologram marker).</summary>
        public Vector3 ScopeLocalPosition => patient.transform.InverseTransformPoint(_endoCam.transform.position);
        public Vector3 ScopeLocalForward => patient.transform.InverseTransformDirection(_endoCam.transform.forward);

        public override Texture VideoTexture => _videoRT;
        public override bool IsSimulation => true;
        public override bool LabelsValidated => false;   // synthetic labels are never validated anatomy

        RenderTexture _videoRT, _labelRT;
        Camera _endoCam, _labelCam;
        Transform _rig;
        Material _labelMat;
        readonly List<AnatomyPart> _parts = new List<AnatomyPart>();
        readonly List<GameObject> _tears = new List<GameObject>();
        readonly SegmentationFrame[] _pool = new SegmentationFrame[3];
        int _poolIdx, _frameIdx;
        bool _readbackPending;
        float _nextRender, _u, _manualUntil, _autoDir = 1f;

        protected override void Awake()
        {
            base.Awake();
            for (int i = 0; i < _pool.Length; i++) _pool[i] = new SegmentationFrame(LabelSize, LabelSize);
        }

        void Start()
        {
            patient.Build();
            _parts.AddRange(patient.Parts);
            _labelMat = new Material(labelShader != null ? labelShader : Shader.Find("SeptoMentor/LabelId")) { name = "LabelId" };

            _videoRT = new RenderTexture(videoSize, videoSize, 24, RenderTextureFormat.ARGB32) { name = "EndoVideo", antiAliasing = 2 };
            var labelFmt = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.R8) ? RenderTextureFormat.R8 : RenderTextureFormat.ARGB32;
            _labelRT = new RenderTexture(LabelSize, LabelSize, 24, labelFmt, RenderTextureReadWrite.Linear)
            { name = "EndoLabels", antiAliasing = 1, filterMode = FilterMode.Point };

            _rig = new GameObject("EndoscopeRig").transform;
            _rig.SetParent(patient.transform, false);
            _endoCam = MakeCamera("EndoCamera", _videoRT);
            _labelCam = MakeCamera("LabelCamera", _labelRT);
            _labelCam.allowMSAA = false;

            Instrument = new GameObject("VirtualInstrument").AddComponent<VirtualInstrument>();
            Instrument.transform.SetParent(_endoCam.transform, false);
            Instrument.Init(patient.TissueMaterial, p => _parts.Add(p), p => _parts.Remove(p));
        }

        Camera MakeCamera(string name, RenderTexture target)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_rig, false);
            var cam = go.AddComponent<Camera>();
            cam.enabled = false;                 // rendered manually
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;   // label 0 = Background
            cam.fieldOfView = fieldOfView;
            cam.nearClipPlane = nearClip;
            cam.farClipPlane = farClip;
            cam.allowHDR = false;
            cam.useOcclusionCulling = false;
            cam.targetTexture = target;
            return cam;
        }

        void Update()
        {
            if (_endoCam == null) return;
            MoveScope();
            if (Time.time < _nextRender) return;
            _nextRender = Time.time + 1f / Mathf.Max(1f, renderFps);

            _endoCam.Render();
            RenderLabels();
            if (!_readbackPending)
            {
                _readbackPending = true;
                var frame = _pool[_poolIdx];
                _poolIdx = (_poolIdx + 1) % _pool.Length;
                FillFrameMeta(frame);
                AsyncGPUReadback.Request(_labelRT, 0, TextureFormat.R8, req => OnReadback(req, frame));
            }
        }

        void MoveScope()
        {
            bool manual = manualLook.sqrMagnitude > 0.01f || Mathf.Abs(manualAdvance) > 0.1f;
            if (manual) _manualUntil = Time.time + 5f;

            float len = Mathf.Max(1e-4f, Vector3.Distance(patient.pathStart, patient.pathEnd));
            if (Time.time < _manualUntil)
                _u = Mathf.Clamp01(_u + manualAdvance * Time.deltaTime * 0.01f / len);
            else
            {
                // Resume the back-and-forth from wherever the resident left the scope (no jump).
                _u += _autoDir * Time.deltaTime * 2f / Mathf.Max(1f, pathCycleSeconds);
                if (_u >= 1f) { _u = 1f; _autoDir = -1f; }
                else if (_u <= 0f) { _u = 0f; _autoDir = 1f; }
            }

            var dir = (patient.pathEnd - patient.pathStart).normalized;
            var sway = new Vector3(Mathf.Sin(Time.time * 0.7f), Mathf.Sin(Time.time * 0.53f + 1f), 0) * swayMeters;
            _rig.localPosition = Vector3.Lerp(patient.pathStart, patient.pathEnd, _u) + sway;

            float yaw = patient.scopeYaw + manualLook.x * 35f;
            float pitch = -manualLook.y * 25f;
            _rig.localRotation = Quaternion.LookRotation(dir, Vector3.up) * Quaternion.Euler(pitch, yaw, 0);
        }

        void RenderLabels()
        {
            var originals = new Material[_parts.Count];
            for (int i = 0; i < _parts.Count; i++)
            {
                if (_parts[i] == null) continue;
                originals[i] = _parts[i].Rend.sharedMaterial;
                _parts[i].Rend.sharedMaterial = _labelMat;   // MPB already carries _ClassId
            }
            _labelCam.Render();
            for (int i = 0; i < _parts.Count; i++)
                if (_parts[i] != null) _parts[i].Rend.sharedMaterial = originals[i];
        }

        void FillFrameMeta(SegmentationFrame f)
        {
            f.frameIndex = ++_frameIdx;
            f.instrumentTip = null; f.mmPerPixel = null; f.instrumentAngularSpeed = null;
            f.activeInstrument = Instrument != null ? Instrument.Current : Structure.Background;
            if (Instrument == null || Instrument.Tip == null) return;

            var vp = _labelCam.WorldToViewportPoint(Instrument.Tip.position);
            if (vp.z > 0 && vp.x >= 0 && vp.x <= 1 && vp.y >= 0 && vp.y <= 1)
                f.instrumentTip = new Vector2(vp.x, vp.y);
            // Exact scale at the tip depth (known in simulation; a real scope needs calibration - OPEN ITEM 4).
            float heightM = 2f * vp.z * Mathf.Tan(fieldOfView * 0.5f * Mathf.Deg2Rad);
            f.mmPerPixel = heightM * 1000f / LabelSize;
            f.instrumentAngularSpeed = Instrument.AngularSpeed;
        }

        void OnReadback(AsyncGPUReadbackRequest req, SegmentationFrame frame)
        {
            _readbackPending = false;
            if (this == null || req.hasError) return;
            var data = req.GetData<byte>();
            if (data.Length != frame.labels.Length) return;
            data.CopyTo(frame.labels);
            Publish(frame);
        }

        /// <summary>Spawns a small simulated mucosal tear where the scope axis hits tissue.</summary>
        public bool SimulateTear()
        {
            if (_endoCam == null) return false;
            var cam = _endoCam.transform;
            if (!Physics.Raycast(cam.position, cam.forward, out var hit, 0.06f)) return false;
            var tear = GameObject.CreatePrimitive(PrimitiveType.Cube);
            tear.name = "SimulatedTear";
            Destroy(tear.GetComponent<Collider>());
            tear.transform.SetParent(patient.AnatomyRoot, false);
            tear.transform.position = hit.point + hit.normal * 0.0002f;
            tear.transform.rotation = Quaternion.LookRotation(hit.normal) * Quaternion.Euler(0, 0, Random.Range(0f, 180f));
            tear.transform.localScale = new Vector3(0.004f, 0.0008f, 0.0003f);
            _parts.Add(patient.AddPart(tear, Structure.MucosalTear, true));
            _tears.Add(tear);
            if (_tears.Count > 3) { var old = _tears[0]; _tears.RemoveAt(0); _parts.Remove(old.GetComponent<AnatomyPart>()); patient.Parts.Remove(old.GetComponent<AnatomyPart>()); Destroy(old); }
            return true;
        }

        void OnDestroy()
        {
            if (_videoRT != null) _videoRT.Release();
            if (_labelRT != null) _labelRT.Release();
        }
    }
}
