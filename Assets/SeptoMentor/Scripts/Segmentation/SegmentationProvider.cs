using System;
using UnityEngine;

namespace SeptoMentor
{
    /// <summary>
    /// Source of (video, per-pixel labels). Implementations: SyntheticSegmentationProvider
    /// (virtual endoscope, exact labels) and SegmentationRunner (ONNX, SEPTO_ONNX).
    /// </summary>
    public abstract class SegmentationProvider : MonoBehaviour
    {
        public const int LabelSize = 256;

        /// <summary>Endoscope-style colour video shown to the resident.</summary>
        public abstract Texture VideoTexture { get; }

        /// <summary>R8 label map, point-filtered, values = (byte)Structure.</summary>
        public Texture2D LabelTexture { get; private set; }

        /// <summary>True when frames are simulated (drives the SIMULATION banner).</summary>
        public abstract bool IsSimulation { get; }

        /// <summary>
        /// True only when labels come from a model/mapping validated by experts.
        /// Defaults to false everywhere in this repo.
        /// </summary>
        public virtual bool LabelsValidated => false;

        public SegmentationFrame Latest { get; private set; }

        public event Action<SegmentationFrame> FrameReady;

        protected virtual void Awake()
        {
            LabelTexture = new Texture2D(LabelSize, LabelSize, TextureFormat.R8, false, true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                name = "SeptoLabelMap",
            };
        }

        /// <summary>Called by implementations once a label buffer is complete (main thread).</summary>
        protected void Publish(SegmentationFrame frame)
        {
            frame.timestamp = Time.timeAsDouble;
            frame.ComputeStats();
            LabelTexture.SetPixelData(frame.labels, 0);
            LabelTexture.Apply(false, false);
            Latest = frame;
            FrameReady?.Invoke(frame);
        }

        /// <summary>Encode the current video frame as JPEG (only used when frame upload is explicitly enabled).</summary>
        public byte[] CaptureJpeg(int quality = 70, int maxSize = 512)
        {
            var src = VideoTexture;
            if (src == null) return null;
            int w = Mathf.Min(maxSize, src.width), h = Mathf.Min(maxSize, src.height);
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(src, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            var jpg = tex.EncodeToJPG(quality);
            Destroy(tex);
            return jpg;
        }
    }
}
