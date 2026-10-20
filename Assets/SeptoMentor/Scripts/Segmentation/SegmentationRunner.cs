// Real-video path. Compiled only when the SEPTO_ONNX scripting define is set
// (Player Settings > Scripting Define Symbols) AND com.unity.ai.inference is installed.
// API written against Unity Inference Engine 2.2 (namespace Unity.InferenceEngine). NOT COMPILED/TESTED.
#if SEPTO_ONNX
using System;
using Unity.InferenceEngine;
using UnityEngine;

namespace SeptoMentor
{
    /// <summary>
    /// ONNX segmentation: input [1,3,256,256] RGB, output logits [1,16,256,256].
    /// ArgMax runs on the GPU inside the compiled graph; only 256x256 int class ids are read back (async).
    /// </summary>
    public class SegmentationRunner : SegmentationProvider
    {
        public ModelAsset modelAsset;
        public BackendType backend = BackendType.GPUCompute;

        [Header("Video source")]
        [Tooltip("Any texture (VideoPlayer RenderTexture, capture card...). Used if no webcam name is given.")]
        public Texture source;
        [Tooltip("Optional UVC/WebCamTexture device name. Quest 3 support for external UVC endoscopes is NOT verified.")]
        public string webcamDevice = "";

        [Header("Preprocessing (must match training)")]
        public Vector3 mean = new Vector3(0.485f, 0.456f, 0.406f);
        public Vector3 std = new Vector3(0.229f, 0.224f, 0.225f);
        [Tooltip("Flip rows if overlay appears upside down (tensor row order is a guess).")]
        public bool flipRows = true;

        [Header("Validation status")]
        [Tooltip("Set true ONLY for a model validated on expert-annotated, ethics-approved data.")]
        public bool modelValidated = false;

        public float maxFps = 15f;

        public override Texture VideoTexture => source;
        public override bool IsSimulation => false;
        public override bool LabelsValidated => modelValidated;

        Worker _worker;
        Tensor<float> _input;
        WebCamTexture _cam;
        SegmentationFrame[] _pool;
        int _poolIdx, _frameIdx;
        bool _busy;
        float _next;

        protected override void Awake()
        {
            base.Awake();
            _pool = new[] { new SegmentationFrame(LabelSize, LabelSize), new SegmentationFrame(LabelSize, LabelSize) };
        }

        void Start()
        {
            if (!string.IsNullOrEmpty(webcamDevice))
            {
                _cam = new WebCamTexture(webcamDevice, 1280, 720, 30);
                _cam.Play();
                source = _cam;
            }

            var model = ModelLoader.Load(modelAsset);
            var graph = new FunctionalGraph();
            var inputs = graph.AddInputs(model);
            var m = Functional.Constant(new TensorShape(1, 3, 1, 1), new[] { mean.x, mean.y, mean.z });
            var s = Functional.Constant(new TensorShape(1, 3, 1, 1), new[] { std.x, std.y, std.z });
            var logits = Functional.Forward(model, new[] { (inputs[0] - m) / s })[0];
            var classes = Functional.ArgMax(logits, 1, false);   // [1,256,256] int
            _worker = new Worker(graph.Compile(classes), backend);
            _input = new Tensor<float>(new TensorShape(1, 3, LabelSize, LabelSize));
        }

        async void Update()
        {
            if (_busy || source == null || _worker == null || Time.time < _next) return;
            _next = Time.time + 1f / maxFps;
            _busy = true;
            try
            {
                TextureConverter.ToTensor(source, _input, new TextureTransform());
                _worker.Schedule(_input);
                var output = _worker.PeekOutput() as Tensor<int>;
                using var cpu = await output.ReadbackAndCloneAsync();
                if (this == null) return;
                var ids = cpu.DownloadToArray();

                var frame = _pool[_poolIdx];
                _poolIdx = (_poolIdx + 1) % _pool.Length;
                frame.frameIndex = ++_frameIdx;
                frame.instrumentTip = null;       // ComputeStats() applies the tip heuristic
                frame.mmPerPixel = null;          // uncalibrated (OPEN ITEM 4)
                frame.instrumentAngularSpeed = null;
                frame.activeInstrument = Structure.Background;
                for (int y = 0; y < LabelSize; y++)
                {
                    int srcRow = flipRows ? (LabelSize - 1 - y) : y;
                    for (int x = 0; x < LabelSize; x++)
                        frame.labels[y * LabelSize + x] = (byte)Mathf.Clamp(ids[srcRow * LabelSize + x], 0, StructureInfo.Count - 1);
                }
                Publish(frame);
            }
            catch (Exception e) { Debug.LogException(e); }
            finally { _busy = false; }
        }

        void OnDestroy()
        {
            _worker?.Dispose();
            _input?.Dispose();
            if (_cam != null) _cam.Stop();
        }
    }
}
#endif
