using UnityEngine;

namespace SeptoMentor.Nav
{
    /// <summary>
    /// World-locked panel beside the operative field: status/instructions, three CT slices
    /// through the pointer tip (axial / coronal / sagittal, radiological convention), live
    /// readouts, mentor and AI text. Built at runtime; 1 local unit = 1 cm.
    /// </summary>
    public class NavDashboard : MonoBehaviour
    {
        public Shader sliceShader;
        public float sliceSize = 15f;          // cm
        public float sliceFovMm = 80f;

        Label3D _banner, _status, _stepTitle, _stepBody, _readout, _mentor, _ai, _progress;
        readonly Material[] _slices = new Material[3];
        float _mentorUntil;

        static readonly int VolumeId = Shader.PropertyToID("_Volume"), LabelsId = Shader.PropertyToID("_Labels"),
            PanelId = Shader.PropertyToID("_PanelToUvw"), WindowId = Shader.PropertyToID("_Window"), LevelId = Shader.PropertyToID("_Level"),
            PackedId = Shader.PropertyToID("_Packed"), UseLabelsId = Shader.PropertyToID("_UseLabels"), CrossId = Shader.PropertyToID("_ShowCross");

        // Panel axes in model space (+X patient-left, +Y superior, +Z posterior): (screen right, screen up).
        static readonly Vector3[,] Axes =
        {
            { Vector3.right, Vector3.back },   // axial, viewed from the feet
            { Vector3.right, Vector3.up },     // coronal, viewed from the front
            { Vector3.forward, Vector3.up },   // sagittal, face to the left
        };
        static readonly string[] Names = { "Axial", "Coronal", "Sagittal" };

        void Awake()
        {
            transform.localScale = Vector3.one * 0.01f;
            float w = sliceSize, gap = 1.2f, total = 3 * w + 2 * gap;
            float top = w * 0.5f;

            _banner = Text("Banner", new Vector3(0, top + 13f, 0), 13, total + 10, 3);
            _status = Text("Status", new Vector3(0, top + 10f, 0), 10, total + 10, 3);
            _stepTitle = Text("StepTitle", new Vector3(0, top + 6.5f, 0), 15, total + 10, 3);
            _stepBody = Text("StepBody", new Vector3(0, top + 3f, 0), 10, total + 4, 5);

            for (int i = 0; i < 3; i++)
            {
                float x = -total * 0.5f + w * 0.5f + i * (w + gap);
                var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
                q.name = "Slice_" + Names[i];
                Destroy(q.GetComponent<Collider>());
                q.transform.SetParent(transform, false);
                q.transform.localPosition = new Vector3(x, 0, 0);
                q.transform.localScale = new Vector3(w, w, 1);
                _slices[i] = new Material(sliceShader != null ? sliceShader : Shader.Find("SeptoMentor/CtSlice"));
                q.GetComponent<Renderer>().sharedMaterial = _slices[i];
                var cap = Text("Caption_" + Names[i], new Vector3(x, -w * 0.5f - 1.2f, 0), 9, w, 2);
                cap.Set(Names[i] + (i == 0 ? "  (R | L)" : i == 1 ? "  (R | L)" : "  (A | P)"), new Color(0.7f, 0.75f, 0.85f));
            }

            _readout = Text("Readout", new Vector3(0, -w * 0.5f - 5f, 0), 11, total + 10, 4);
            _mentor = Text("Mentor", new Vector3(0, -w * 0.5f - 9.5f, 0), 13, total + 14, 5);
            _ai = Text("AI", new Vector3(total * 0.5f + 17f, 6f, 0), 10, 28, 30, TMPro.TextAlignmentOptions.TopLeft);
            _progress = Text("Progress", new Vector3(-total * 0.5f - 17f, 6f, 0), 10, 28, 30, TMPro.TextAlignmentOptions.TopLeft);
            _banner.Set("XR NAVIGATION - research prototype, not a certified navigation system. Verify on anatomy.", new Color(1f, 0.7f, 0.2f));
        }

        Label3D Text(string name, Vector3 pos, float size, float w, float h, TMPro.TextAlignmentOptions align = TMPro.TextAlignmentOptions.Center)
        {
            var l = new Label3D(transform, name, size, align);
            l.go.transform.localPosition = pos + new Vector3(0, 0, -0.2f);
            l.SetBox(w, h);
            return l;
        }

        void Update()
        {
            if (Time.time > _mentorUntil) _mentor.Set("", Color.white);
        }

        public void SetVolume(CtVolume v, Texture3D ct, Texture3D labels)
        {
            foreach (var m in _slices)
            {
                m.SetTexture(VolumeId, ct);
                if (labels != null) m.SetTexture(LabelsId, labels);
                m.SetFloat(UseLabelsId, labels != null ? 1 : 0);
                m.SetFloat(PackedId, v.PackedTexture ? 1 : 0);
            }
        }

        public void SetWindow(float window, float level)
        {
            foreach (var m in _slices) { m.SetFloat(WindowId, window); m.SetFloat(LevelId, level); }
        }

        /// <summary>Centres all three slices on a model-space point.</summary>
        public void SetSliceCentre(CtVolume v, Vector3 model, bool crosshair)
        {
            if (v == null) return;
            var toUvw = v.ModelToUvw;
            float fov = sliceFovMm * 0.001f;
            for (int i = 0; i < 3; i++)
            {
                var u = Axes[i, 0] * fov; var w = Axes[i, 1] * fov;
                var corner = model - 0.5f * u - 0.5f * w;
                var panel = new Matrix4x4(u, w, Vector4.zero, new Vector4(corner.x, corner.y, corner.z, 1));
                _slices[i].SetMatrix(PanelId, toUvw * panel);
                _slices[i].SetFloat(CrossId, crosshair ? 1 : 0);
            }
        }

        public void SetStatus(string text, Color c) => _status.Set(text, c);
        public void SetStep(string title, string body) { _stepTitle.Set(title, Color.white); _stepBody.Set(body, new Color(0.85f, 0.9f, 1f)); }
        public void SetReadout(string text) => _readout.Set(text, new Color(0.8f, 1f, 0.85f));
        public void SetProgress(string text) => _progress.Set(text, new Color(0.85f, 0.88f, 0.92f));
        public void ShowAI(string text) => _ai.Set("AI assistant:\n" + text, new Color(0.75f, 0.85f, 1f));

        public void ShowMentor(string text, Severity sev, float seconds = 7f)
        {
            Color c = sev == Severity.Warning ? new Color(1f, 0.3f, 0.3f)
                    : sev == Severity.Caution ? new Color(1f, 0.75f, 0.2f)
                    : sev == Severity.Tip ? new Color(0.5f, 1f, 0.6f) : Color.white;
            _mentor.Set(text, c);
            _mentorUntil = Time.time + seconds;
        }
    }
}
