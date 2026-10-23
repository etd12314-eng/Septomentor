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
        [Tooltip("Lit opaque shader for the instrument-mount model (ORSurface).")]
        public Shader solidShader;
        public Shader markerShader;
        public float sliceSize = 15f;          // cm
        public float sliceFovMm = 80f;

        Label3D _banner, _status, _stepTitle, _stepBody, _readout, _mentor, _ai, _progress;
        readonly Material[] _slices = new Material[3];
        float _mentorUntil;
        float _total, _top, _w;
        Transform _guide, _guideModel, _frame;
        Label3D _frameHint;

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
            _total = total; _top = top; _w = w;

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
            // Gentle swing so the mount model reads as 3D.
            if (_guideModel != null && _guide.gameObject.activeSelf)
                _guideModel.localRotation = Quaternion.Euler(0, 35f * Mathf.Sin(Time.time * 0.6f), 0);
        }

        // ---------------------------------------------------------------- instrument mount guide

        /// <summary>
        /// Model of the right Touch controller with a pointer instrument fixed to the bottom end of its handle
        /// (where Meta puts the stylus tip on its own controllers), with mounting notes. Shown during calibration.
        /// </summary>
        public void ShowMountGuide(bool on)
        {
            if (!on && _guide == null) return;
            if (_guide == null) BuildGuide();
            if (_guide.gameObject.activeSelf != on) _guide.gameObject.SetActive(on);
            if (on && _progress != null) _progress.Set("", Color.white);
        }

        void BuildGuide()
        {
            _guide = new GameObject("MountGuide").transform;
            _guide.SetParent(transform, false);
            _guide.localPosition = new Vector3(-_total * 0.5f - 18f, 2f, -4f);
            _guideModel = new GameObject("Model").transform;
            _guideModel.SetParent(_guide, false);
            _guideModel.localScale = Vector3.one * 0.9f;

            var body = new Color(0.86f, 0.87f, 0.89f);
            Part(PrimitiveType.Capsule, "Handle", new Vector3(0, 0, 0), new Vector3(3.4f, 5.6f, 3.4f), body);
            Part(PrimitiveType.Sphere, "Head", new Vector3(0, 6.2f, -0.6f), new Vector3(6.4f, 3.2f, 5.6f), body);
            Part(PrimitiveType.Cylinder, "Stick", new Vector3(-1.0f, 7.9f, -0.6f), new Vector3(1.3f, 0.35f, 1.3f), new Color(0.15f, 0.15f, 0.17f));
            Part(PrimitiveType.Sphere, "ButtonA", new Vector3(1.4f, 7.7f, -1.4f), new Vector3(1f, 0.5f, 1f), new Color(0.2f, 0.2f, 0.22f));
            Part(PrimitiveType.Sphere, "ButtonB", new Vector3(1.6f, 7.7f, 0.4f), new Vector3(1f, 0.5f, 1f), new Color(0.2f, 0.2f, 0.22f));
            Part(PrimitiveType.Cube, "Trigger", new Vector3(0, 4.0f, -2.3f), new Vector3(1.3f, 2.2f, 1.1f), new Color(0.3f, 0.3f, 0.33f));
            // Clamp on the handle end + the instrument along the handle axis, tip 8 cm past the handle.
            var clamp = new Color(0.2f, 0.55f, 1f);
            Part(PrimitiveType.Cylinder, "Clamp", new Vector3(0, -4.6f, 0), new Vector3(3.9f, 0.9f, 3.9f), clamp, 0.3f);
            Part(PrimitiveType.Cylinder, "Strap", new Vector3(0, -1.8f, 0), new Vector3(3.7f, 0.35f, 3.7f), clamp, 0.3f);
            Part(PrimitiveType.Cylinder, "Instrument", new Vector3(0, -9.2f, 0), new Vector3(0.45f, 4.2f, 0.45f), new Color(0.75f, 0.77f, 0.8f), 0f, 0.8f);
            Part(PrimitiveType.Sphere, "Tip", new Vector3(0, -13.4f, 0), Vector3.one * 1.1f, new Color(1f, 0.92f, 0.15f), 0.7f);

            var title = new Label3D(_guide, "GuideTitle", 11);
            title.go.transform.localPosition = new Vector3(0, 12.5f, -0.2f);
            title.SetBox(30, 3);
            title.Set("WHERE TO MOUNT THE INSTRUMENT", new Color(0.45f, 0.75f, 1f));
            var tip = new Label3D(_guide, "GuideTip", 8, TMPro.TextAlignmentOptions.Left);
            tip.go.transform.localPosition = new Vector3(9.5f, -12f, -0.2f);
            tip.SetBox(14, 3);
            tip.Set("< tip: 5-10 cm past\n   the handle end", new Color(1f, 0.92f, 0.3f));
            var cl = new Label3D(_guide, "GuideClamp", 8, TMPro.TextAlignmentOptions.Left);
            cl.go.transform.localPosition = new Vector3(9.5f, -3.5f, -0.2f);
            cl.SetBox(14, 3);
            cl.Set("< clamp / 2 tight\n   straps on the handle", new Color(0.45f, 0.75f, 1f));
            var notes = new Label3D(_guide, "GuideNotes", 8.5f, TMPro.TextAlignmentOptions.TopLeft);
            notes.go.transform.localPosition = new Vector3(0, -24f, -0.2f);
            notes.SetBox(32, 14);
            notes.Set("- RIGHT controller, BOTTOM END of the handle, in line with the handle (hold it like a pen).\n" +
                      "- Straight, rigid, blunt instrument (probe, Freer, stylus).\n" +
                      "- It must not wobble at all: clamp, zip ties or 2 tight straps.\n" +
                      "- Keep the top face and the front of the controller uncovered (tracking LEDs).\n" +
                      "- Another rigid position also works - calibration measures where the tip is.\n" +
                      "- Re-calibrate every time the instrument is re-attached.", new Color(0.85f, 0.9f, 1f));
            _guide.gameObject.SetActive(false);
        }

        void Part(PrimitiveType type, string name, Vector3 pos, Vector3 scale, Color c, float emission = 0f, float metal = 0f)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(_guideModel, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            var m = new Material(solidShader != null ? solidShader : Shader.Find("SeptoMentor/ORSurface"));
            m.SetColor("_Color", c); m.SetFloat("_Emission", emission); m.SetFloat("_Metal", metal); m.SetFloat("_Gloss", 0.4f);
            go.GetComponent<Renderer>().sharedMaterial = m;
        }

        // ---------------------------------------------------------------- move mode frame

        /// <summary>Outline around the panel and a hint line while it is being moved.</summary>
        public void SetMoving(bool on, string hint = "")
        {
            if (!on && _frame == null) return;
            if (_frame == null) BuildFrame();
            if (_frame.gameObject.activeSelf != on) _frame.gameObject.SetActive(on);
            if (on) _frameHint.Set(hint, new Color(0.3f, 0.95f, 1f));
        }

        void BuildFrame()
        {
            _frame = new GameObject("MoveFrame").transform;
            _frame.SetParent(transform, false);
            float x0 = -_total * 0.5f - 33f, x1 = _total * 0.5f + 33f, y0 = -_w * 0.5f - 13f, y1 = _top + 15.5f;
            var mat = new Material(markerShader != null ? markerShader : Shader.Find("SeptoMentor/NavMarker")) { color = new Color(0.3f, 0.95f, 1f, 0.9f) };
            void Bar(Vector3 c, Vector3 size)
            {
                var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Destroy(q.GetComponent<Collider>());
                q.transform.SetParent(_frame, false);
                q.transform.localPosition = c;
                q.transform.localScale = size;
                q.GetComponent<Renderer>().sharedMaterial = mat;
            }
            float t = 0.5f;
            Bar(new Vector3(0, y1, 0), new Vector3(x1 - x0, t, 1));
            Bar(new Vector3(0, y0, 0), new Vector3(x1 - x0, t, 1));
            Bar(new Vector3(x0, (y0 + y1) * 0.5f, 0), new Vector3(t, y1 - y0, 1));
            Bar(new Vector3(x1, (y0 + y1) * 0.5f, 0), new Vector3(t, y1 - y0, 1));
            _frameHint = new Label3D(_frame, "MoveHint", 12);
            _frameHint.go.transform.localPosition = new Vector3(0, y0 - 2.5f, -0.2f);
            _frameHint.SetBox(x1 - x0, 4);
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
