using System.Collections.Generic;
using UnityEngine;

namespace SeptoMentor
{
    /// <summary>
    /// The instrument as the endoscope sees it, rendered into both video and label passes.
    /// Hand-driven (HandInstruments): the instrument in the resident's right hand is mirrored into the
    /// anatomy every frame (SetExternalPose). Otherwise it hangs in front of the lens from the lower
    /// right with a scripted stroke (autopilot / no controller).
    /// </summary>
    public class VirtualInstrument : MonoBehaviour
    {
        public static readonly Structure[] Cycle =
            { Structure.Background, Structure.Scalpel, Structure.FreerElevator, Structure.ScissorsForceps, Structure.Suction };

        public Structure Current { get; private set; } = Structure.Background;
        public Transform Tip { get; private set; }

        /// <summary>Roll input in [-1,1] (e.g. thumbstick while grip held) to simulate twisting.</summary>
        public float rollInput;
        public float strokeAmplitude = 0.004f;   // m
        public float strokeHz = 0.6f;
        /// <summary>Forceps jaws: 0 closed, 1 open.</summary>
        public float jawOpen = 1f;

        /// <summary>Angular speed of the instrument in deg/s (simulated torque proxy).</summary>
        public float AngularSpeed { get; private set; }
        public bool External { get; private set; }

        public readonly List<AnatomyPart> Parts = new List<AnatomyPart>();

        Material _mat;
        InstrumentModels.Model _model;
        Transform _body, _scope;
        Quaternion _lastRot;
        float _roll;
        System.Action<AnatomyPart> _register, _unregister;

        static readonly Vector3 ScopePosition = new Vector3(0.0035f, -0.0030f, 0.0f);
        static readonly Quaternion ScopeRotation = Quaternion.Euler(-6f, -7f, 0);

        public void Init(Material tissueMat, System.Action<AnatomyPart> register, System.Action<AnatomyPart> unregister)
        {
            _mat = tissueMat; _register = register; _unregister = unregister;
            _scope = transform.parent;
            // Shaft starts below-right of the lens and converges toward the optical axis.
            transform.localPosition = ScopePosition;
            transform.localRotation = ScopeRotation;
        }

        /// <summary>Hand-driven: <paramref name="parent"/> is the patient; null hangs the instrument back in front of the lens.</summary>
        public void SetExternal(Transform parent)
        {
            bool ext = parent != null;
            if (ext == External) return;
            External = ext;
            transform.SetParent(ext ? parent : _scope, false);
            if (!ext) { transform.localPosition = ScopePosition; transform.localRotation = ScopeRotation; }
        }

        public void SetExternalPose(Vector3 localPosition, Quaternion localRotation)
        {
            transform.localPosition = localPosition;
            transform.localRotation = localRotation;
        }

        public Structure Next()
        {
            int i = System.Array.IndexOf(Cycle, Current);
            Set(Cycle[(i + 1) % Cycle.Length]);
            return Current;
        }

        public void Set(Structure s)
        {
            if (s == Current && (_body != null || s == Structure.Background)) return;
            if (_body != null)
            {
                foreach (var p in Parts) _unregister?.Invoke(p);
                Parts.Clear();
                Destroy(_body.gameObject);
                _body = null; Tip = null; _model = null;
            }
            Current = s;
            if (s == Structure.Background) return;

            _body = new GameObject("Body").transform;
            _body.SetParent(transform, false);
            _model = InstrumentModels.Build(s, _body, (go, finish) =>
            {
                var r = go.GetComponent<Renderer>();
                r.sharedMaterial = _mat;
                var p = go.AddComponent<AnatomyPart>();
                p.structure = Current;
                p.tissueColor = finish == InstrumentModels.Finish.Dark ? new Color(0.05f, 0.05f, 0.05f) : AnatomyPart.DefaultTint(Current);
                p.wetness = 1.4f;
                p.mappingConfirmed = true;   // virtual instruments are known by construction
                p.Apply();
                Parts.Add(p);
                _register?.Invoke(p);
            });
            Tip = _body;   // the model's tip is at its origin
            _lastRot = External ? transform.localRotation : Quaternion.identity;
        }

        void Update()
        {
            if (_body == null) { AngularSpeed = 0; return; }
            _model?.SetJaw(jawOpen);
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            if (External)
            {
                _body.localPosition = Vector3.zero;
                _body.localRotation = Quaternion.identity;
                AngularSpeed = Quaternion.Angle(_lastRot, transform.localRotation) / dt;
                _lastRot = transform.localRotation;
                return;
            }
            float stroke = Mathf.Sin(Time.time * Mathf.PI * 2 * strokeHz) * strokeAmplitude;
            _body.localPosition = new Vector3(0, 0, 0.023f + stroke);
            _roll += rollInput * 180f * Time.deltaTime;
            _body.localRotation = Quaternion.Euler(0, 0, _roll);
            // Relative to the scope, so moving the scope is not counted as instrument torque.
            AngularSpeed = Quaternion.Angle(_lastRot, _body.localRotation) / dt;
            _lastRot = _body.localRotation;
        }
    }
}
