using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace SeptoMentor
{
    public enum Severity { Info, Tip, Caution, Warning }

    /// <summary>
    /// Virtual monitor in front of the resident: endoscope video + colour class map +
    /// floating structure labels + honesty banners + mentor/AI text.
    /// All UI is built at runtime under this transform (1 local unit = 1 cm).
    /// </summary>
    public class OverlayView : MonoBehaviour
    {
        public SegmentationProvider provider;
        public Shader monitorShader;

        [Header("Layout (cm)")]
        public float monitorSize = 48f;

        [Header("Overlay")]
        [Range(0, 1)] public float fillOpacity = 0.30f;
        [Range(0, 1)] public float outline = 0.9f;
        public bool showLabels = true;
        public int minLabelPixels = 120;

        Material _mat;
        Label3D _banner, _validity, _footer, _phase, _mentor, _ai;
        readonly Dictionary<Structure, Label3D> _labels = new Dictionary<Structure, Label3D>();
        static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        static readonly int LabelTexId = Shader.PropertyToID("_LabelTex");
        static readonly int PaletteId = Shader.PropertyToID("_Palette");
        static readonly int OpacityId = Shader.PropertyToID("_Opacity");
        static readonly int OutlineId = Shader.PropertyToID("_Outline");
        float _mentorUntil;

        // Awake, not Start: SeptoMentorApp.Start writes to these labels and Start order is undefined.
        void Awake()
        {
            transform.localScale = Vector3.one * 0.01f;

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "MonitorQuad";
            Destroy(quad.GetComponent<Collider>());
            quad.transform.SetParent(transform, false);
            quad.transform.localScale = new Vector3(monitorSize, monitorSize, 1);
            _mat = new Material(monitorShader != null ? monitorShader : Shader.Find("SeptoMentor/MonitorComposite"));
            quad.GetComponent<Renderer>().sharedMaterial = _mat;

            var palette = new Vector4[StructureInfo.Count];
            for (int i = 0; i < palette.Length; i++) palette[i] = (Color)StructureInfo.OverlayColor((Structure)i);
            _mat.SetVectorArray(PaletteId, palette);

            float half = monitorSize * 0.5f;
            _banner = Text("Banner", new Vector3(0, half + 5.5f, 0), 22, monitorSize * 1.2f, 4);
            _validity = Text("Validity", new Vector3(0, half + 2f, 0), 12, monitorSize * 1.2f, 3);
            _footer = Text("Footer", new Vector3(0, -half - 2f, 0), 10, monitorSize * 1.4f, 3);
            _footer.Set("Training aid only - not a medical device. The attending surgeon decides.", new Color(1, 1, 1, 0.8f));
            _phase = Text("Phase", new Vector3(0, -half - 5.5f, 0), 14, monitorSize * 1.4f, 3);
            _mentor = Text("Mentor", new Vector3(0, -half - 10f, 0), 15, monitorSize * 1.6f, 6);
            _ai = Text("AIMentor", new Vector3(half + 18f, 6f, 0), 11, 30, 30, TextAlignmentOptions.TopLeft);

            foreach (Structure s in System.Enum.GetValues(typeof(Structure)))
            {
                if (s == Structure.Background) continue;
                var l = Text("Label_" + s, Vector3.zero, 10, 22, 3);
                l.SetActive(false);
                _labels[s] = l;
            }
        }

        Label3D Text(string name, Vector3 pos, float size, float w, float h, TextAlignmentOptions align = TextAlignmentOptions.Center)
        {
            var l = new Label3D(transform, name, size, align);
            l.go.transform.localPosition = pos + new Vector3(0, 0, -0.2f);
            l.SetBox(w, h);
            return l;
        }

        void Update()
        {
            if (provider == null || _mat == null) return;
            _mat.SetTexture(MainTexId, provider.VideoTexture);
            _mat.SetTexture(LabelTexId, provider.LabelTexture);
            _mat.SetFloat(OpacityId, fillOpacity);
            _mat.SetFloat(OutlineId, outline);

            if (provider.IsSimulation)
                _banner.Set("SIMULATION - synthetic video and labels", new Color(1f, 0.85f, 0.1f));
            else
                _banner.Set("LIVE ENDOSCOPE", new Color(0.6f, 1f, 0.6f));
            _validity.Set(provider.LabelsValidated
                ? "Model labels validated (see model card)"
                : "UNVALIDATED LABELS - not confirmed anatomy", new Color(1f, 0.55f, 0.2f));

            if (Time.time > _mentorUntil) _mentor.Set("", Color.white);
            UpdateLabels(provider.Latest);
        }

        void UpdateLabels(SegmentationFrame f)
        {
            foreach (var kv in _labels)
            {
                var s = kv.Key; var l = kv.Value;
                bool on = showLabels && f != null && f.stats[(int)s].pixelCount >= minLabelPixels && f.stats[(int)s].anchor.x >= 0;
                l.SetActive(on);
                if (!on) continue;
                var a = f.stats[(int)s].anchor;
                l.go.transform.localPosition = new Vector3((a.x - 0.5f) * monitorSize, (a.y - 0.5f) * monitorSize + 1f, -0.4f);
                string name = StructureInfo.DisplayName(s);
                if (!provider.LabelsValidated) name += " ?";
                l.Set(name, StructureInfo.OverlayColor(s));
            }
        }

        public void SetPhase(string text) => _phase.Set(text, new Color(0.8f, 0.9f, 1f));

        public void ShowMentor(string text, Severity sev, float seconds = 6f)
        {
            Color c = sev == Severity.Warning ? new Color(1f, 0.3f, 0.3f)
                    : sev == Severity.Caution ? new Color(1f, 0.75f, 0.2f)
                    : sev == Severity.Tip ? new Color(0.5f, 1f, 0.6f) : Color.white;
            _mentor.Set(text, c);
            _mentorUntil = Time.time + seconds;
        }

        public void ShowAI(string text) => _ai.Set("AI mentor (training aid):\n" + text, new Color(0.75f, 0.85f, 1f));

        /// <summary>Side panel text when there is no AI answer to show (e.g. the controls card).</summary>
        public void ShowSidePanel(string text) => _ai.Set(text, new Color(0.85f, 0.88f, 0.92f));

        public void ToggleLabels() => showLabels = !showLabels;
    }
}
