using System.Collections.Generic;
using UnityEngine;

namespace SeptoMentor
{
    /// <summary>
    /// Crude primitive instrument that enters the endoscope view from the lower right,
    /// alongside the scope. Rendered into both video and label passes.
    /// Scripted stroke motion for now; OPEN ITEM 6 replaces this with controller-driven pose.
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

        /// <summary>Angular speed of the instrument in deg/s (simulated torque proxy).</summary>
        public float AngularSpeed { get; private set; }

        public readonly List<AnatomyPart> Parts = new List<AnatomyPart>();

        Material _mat;
        Transform _body;
        Quaternion _lastRot;
        float _roll;
        System.Action<AnatomyPart> _register, _unregister;

        public void Init(Material tissueMat, System.Action<AnatomyPart> register, System.Action<AnatomyPart> unregister)
        {
            _mat = tissueMat; _register = register; _unregister = unregister;
            // Shaft starts below-right of the lens and converges toward the optical axis.
            transform.localPosition = new Vector3(0.0035f, -0.0030f, 0.0f);
            transform.localRotation = Quaternion.Euler(-6f, -7f, 0);
        }

        public Structure Next()
        {
            int i = System.Array.IndexOf(Cycle, Current);
            Set(Cycle[(i + 1) % Cycle.Length]);
            return Current;
        }

        public void Set(Structure s)
        {
            if (_body != null)
            {
                foreach (var p in Parts) _unregister?.Invoke(p);
                Parts.Clear();
                Destroy(_body.gameObject);
                _body = null; Tip = null;
            }
            Current = s;
            if (s == Structure.Background) return;

            _body = new GameObject(s.ToString()).transform;
            _body.SetParent(transform, false);
            switch (s)
            {
                case Structure.Scalpel:
                    Piece(PrimitiveType.Cylinder, new Vector3(0, 0, 0.010f), new Vector3(0.0012f, 0.010f, 0.0012f), Quaternion.Euler(90, 0, 0));
                    Piece(PrimitiveType.Cube, new Vector3(0, 0.0004f, 0.0215f), new Vector3(0.0002f, 0.0018f, 0.003f), Quaternion.identity);
                    TipAt(0.023f);
                    break;
                case Structure.FreerElevator:
                    Piece(PrimitiveType.Cylinder, new Vector3(0, 0, 0.010f), new Vector3(0.0012f, 0.010f, 0.0012f), Quaternion.Euler(90, 0, 0));
                    Piece(PrimitiveType.Cube, new Vector3(0, 0, 0.0215f), new Vector3(0.0028f, 0.0003f, 0.004f), Quaternion.Euler(0, 0, 15));
                    TipAt(0.0235f);
                    break;
                case Structure.ScissorsForceps:
                    Piece(PrimitiveType.Cylinder, new Vector3(0, 0, 0.010f), new Vector3(0.0016f, 0.010f, 0.0016f), Quaternion.Euler(90, 0, 0));
                    Piece(PrimitiveType.Cube, new Vector3(0.0006f, 0, 0.022f), new Vector3(0.0005f, 0.0005f, 0.004f), Quaternion.Euler(0, 8, 0));
                    Piece(PrimitiveType.Cube, new Vector3(-0.0006f, 0, 0.022f), new Vector3(0.0005f, 0.0005f, 0.004f), Quaternion.Euler(0, -8, 0));
                    TipAt(0.024f);
                    break;
                case Structure.Suction:
                    Piece(PrimitiveType.Cylinder, new Vector3(0, 0, 0.0115f), new Vector3(0.0018f, 0.0115f, 0.0018f), Quaternion.Euler(90, 0, 0));
                    TipAt(0.023f);
                    break;
            }
            _lastRot = _body.localRotation;
        }

        void Piece(PrimitiveType t, Vector3 pos, Vector3 scale, Quaternion rot)
        {
            var go = GameObject.CreatePrimitive(t);
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(_body, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = rot;
            go.transform.localScale = scale;
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = _mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var p = go.AddComponent<AnatomyPart>();
            p.structure = Current;
            p.tissueColor = AnatomyPart.DefaultTint(Current);
            p.wetness = 1.4f;
            p.mappingConfirmed = true;   // virtual instruments are known by construction
            p.Apply();
            Parts.Add(p);
            _register?.Invoke(p);
        }

        void TipAt(float z)
        {
            Tip = new GameObject("Tip").transform;
            Tip.SetParent(_body, false);
            Tip.localPosition = new Vector3(0, 0, z);
        }

        void Update()
        {
            if (_body == null) { AngularSpeed = 0; return; }
            float stroke = Mathf.Sin(Time.time * Mathf.PI * 2 * strokeHz) * strokeAmplitude;
            _body.localPosition = new Vector3(0, 0, stroke);
            _roll += rollInput * 180f * Time.deltaTime;
            _body.localRotation = Quaternion.Euler(0, 0, _roll);
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            // Relative to the scope, so moving the scope is not counted as instrument torque.
            AngularSpeed = Quaternion.Angle(_lastRot, _body.localRotation) / dt;
            _lastRot = _body.localRotation;
        }
    }
}
