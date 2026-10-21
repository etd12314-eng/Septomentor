using System;
using UnityEngine;

namespace SeptoMentor
{
    /// <summary>
    /// Real-size septoplasty instruments from primitives. Frame: the working tip at the origin, +Z the
    /// direction it points, the shaft and handle running back along -Z (about 20 cm in all).
    /// The same model is used in the operating room (in the resident's hand) and, inside the anatomy,
    /// in front of the virtual endoscope.
    /// </summary>
    public static class InstrumentModels
    {
        public enum Finish { Steel, Dark, Handle }

        public class Model
        {
            public Transform root;
            public Transform jawUpper, jawLower;   // through-cutting forceps only
            public float length;

            /// <summary>0 = closed, 1 = open (25 deg per jaw).</summary>
            public void SetJaw(float open)
            {
                if (jawUpper == null) return;
                float a = 25f * Mathf.Clamp01(open);
                jawUpper.localRotation = Quaternion.Euler(-a, 0, 0);
                jawLower.localRotation = Quaternion.Euler(a, 0, 0);
            }
        }

        public static string DisplayName(Structure s)
        {
            switch (s)
            {
                case Structure.Scalpel: return "Scalpel (#15)";
                case Structure.FreerElevator: return "Freer elevator";
                case Structure.ScissorsForceps: return "Through-cutting forceps";
                case Structure.Suction: return "Frazier suction";
            }
            return StructureInfo.DisplayName(s);
        }

        /// <summary>Builds the instrument under <paramref name="parent"/>; <paramref name="onPiece"/> styles each primitive.</summary>
        public static Model Build(Structure s, Transform parent, Action<GameObject, Finish> onPiece)
        {
            var m = new Model { root = new GameObject(s.ToString()).transform, length = 0.2f };
            m.root.SetParent(parent, false);
            var r = m.root;

            switch (s)
            {
                case Structure.Scalpel:
                    // #15 blade: straight back, convex cutting belly below, point at the origin.
                    Piece(PrimitiveType.Cube, r, new Vector3(0, 0.0011f, -0.0045f), new Vector3(0.0004f, 0.0022f, 0.009f), Quaternion.Euler(-8, 0, 0), Finish.Steel, onPiece);
                    Piece(PrimitiveType.Sphere, r, new Vector3(0, -0.0002f, -0.0042f), new Vector3(0.00035f, 0.0026f, 0.0085f), Quaternion.identity, Finish.Steel, onPiece);
                    Rod(r, new Vector3(0, 0.001f, -0.009f), new Vector3(0, 0.001f, -0.11f), 0.0012f, Finish.Steel, onPiece);
                    Piece(PrimitiveType.Cube, r, new Vector3(0, 0.001f, -0.155f), new Vector3(0.009f, 0.004f, 0.09f), Quaternion.identity, Finish.Handle, onPiece);
                    break;

                case Structure.FreerElevator:
                    // Thin rounded spatula, slightly angled, on a slim shaft with a knurled handle.
                    var blade = new GameObject("Blade").transform;
                    blade.SetParent(r, false);
                    blade.localRotation = Quaternion.Euler(10, 0, 0);
                    Piece(PrimitiveType.Cylinder, blade, new Vector3(0, 0, -0.0018f), new Vector3(0.0038f, 0.00025f, 0.0038f), Quaternion.identity, Finish.Steel, onPiece);
                    Piece(PrimitiveType.Cube, blade, new Vector3(0, 0, -0.0055f), new Vector3(0.0038f, 0.0005f, 0.0075f), Quaternion.identity, Finish.Steel, onPiece);
                    Rod(r, new Vector3(0, -0.0016f, -0.0095f), new Vector3(0, -0.0016f, -0.105f), 0.0013f, Finish.Steel, onPiece);
                    Rod(r, new Vector3(0, -0.0016f, -0.105f), new Vector3(0, -0.0016f, -0.2f), 0.0045f, Finish.Handle, onPiece);
                    break;

                case Structure.ScissorsForceps:
                    // Straight through-cutting (Blakesley-type) forceps: two jaws hinged 9 mm behind the tip.
                    m.jawUpper = new GameObject("JawUpper").transform;
                    m.jawLower = new GameObject("JawLower").transform;
                    foreach (var j in new[] { m.jawUpper, m.jawLower })
                    {
                        j.SetParent(r, false);
                        j.localPosition = new Vector3(0, 0, -0.009f);
                        float y = j == m.jawUpper ? 0.0006f : -0.0006f;
                        Piece(PrimitiveType.Cube, j, new Vector3(0, y, 0.0045f), new Vector3(0.0026f, 0.0012f, 0.009f), Quaternion.identity, Finish.Steel, onPiece);
                        Piece(PrimitiveType.Sphere, j, new Vector3(0, y, 0.0088f), new Vector3(0.0026f, 0.0012f, 0.0026f), Quaternion.identity, Finish.Steel, onPiece);
                    }
                    Rod(r, new Vector3(0, 0, -0.009f), new Vector3(0, 0, -0.12f), 0.0019f, Finish.Steel, onPiece);
                    // Scissor handle with finger rings.
                    Rod(r, new Vector3(0, 0.002f, -0.12f), new Vector3(0, 0.03f, -0.175f), 0.0028f, Finish.Steel, onPiece);
                    Rod(r, new Vector3(0, -0.002f, -0.12f), new Vector3(0, -0.026f, -0.18f), 0.0028f, Finish.Steel, onPiece);
                    Piece(PrimitiveType.Cylinder, r, new Vector3(0, 0.036f, -0.188f), new Vector3(0.022f, 0.0015f, 0.022f), Quaternion.Euler(0, 0, 90), Finish.Steel, onPiece);
                    Piece(PrimitiveType.Cylinder, r, new Vector3(0, -0.031f, -0.192f), new Vector3(0.022f, 0.0015f, 0.022f), Quaternion.Euler(0, 0, 90), Finish.Steel, onPiece);
                    m.SetJaw(1f);
                    break;

                case Structure.Suction:
                    // Frazier 10 Fr: straight tube with a dark lumen, bent into a handle with a thumb port.
                    Rod(r, new Vector3(0, 0, 0), new Vector3(0, 0, -0.095f), 0.0016f, Finish.Steel, onPiece);
                    Piece(PrimitiveType.Cylinder, r, new Vector3(0, 0, 0.00005f), new Vector3(0.0022f, 0.0002f, 0.0022f), Quaternion.Euler(90, 0, 0), Finish.Dark, onPiece);
                    Rod(r, new Vector3(0, 0, -0.095f), new Vector3(0, -0.035f, -0.18f), 0.0042f, Finish.Handle, onPiece);
                    Piece(PrimitiveType.Sphere, r, new Vector3(0, -0.012f, -0.122f), new Vector3(0.006f, 0.005f, 0.006f), Quaternion.identity, Finish.Dark, onPiece);
                    break;
            }
            return m;
        }

        static void Rod(Transform parent, Vector3 a, Vector3 b, float radius, Finish f, Action<GameObject, Finish> onPiece)
        {
            var d = b - a;
            Piece(PrimitiveType.Cylinder, parent, (a + b) * 0.5f, new Vector3(radius * 2, d.magnitude * 0.5f, radius * 2),
                  Quaternion.FromToRotation(Vector3.up, d), f, onPiece);
        }

        static void Piece(PrimitiveType t, Transform parent, Vector3 pos, Vector3 scale, Quaternion rot, Finish f, Action<GameObject, Finish> onPiece)
        {
            var go = GameObject.CreatePrimitive(t);
            UnityEngine.Object.Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = rot;
            go.transform.localScale = scale;
            var rend = go.GetComponent<Renderer>();
            rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            rend.receiveShadows = false;
            onPiece?.Invoke(go, f);
        }
    }
}
