using System;
using System.Collections.Generic;
using UnityEngine;

namespace SeptoMentor
{
    /// <summary>
    /// Real-size septoplasty instruments, built as smooth procedural meshes (lathed shafts and handles, flat
    /// profiled blades with a bevelled edge, swept tubes, finger rings). Frame: the working tip at the origin,
    /// +Z the direction it points, the shaft and handle running back along -Z, +Y up (blade back / ring side).
    /// The same model is used in the operating room (in the resident's hand) and, inside the anatomy, in front of
    /// the virtual endoscope. Meshes are built once and shared.
    /// </summary>
    public static class InstrumentModels
    {
        public enum Finish { Steel, Dark, Handle }

        public class Model
        {
            public Transform root;
            public Transform jawUpper, jawLower;   // through-cutting forceps only
            public float length;
            /// <summary>Tip to where the hand holds it, m (the controller sits there).</summary>
            public float grip;

            /// <summary>0 = closed, 1 = open (the upper jaw swings 28 deg, the lower one is nearly fixed).</summary>
            public void SetJaw(float open)
            {
                if (jawUpper == null) return;
                float a = Mathf.Clamp01(open);
                jawUpper.localRotation = Quaternion.Euler(-28f * a, 0, 0);
                jawLower.localRotation = Quaternion.Euler(4f * a, 0, 0);
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

        static readonly Dictionary<string, Mesh> Cache = new Dictionary<string, Mesh>();

        /// <summary>Builds the instrument under <paramref name="parent"/>; <paramref name="onPiece"/> styles each piece.</summary>
        public static Model Build(Structure s, Transform parent, Action<GameObject, Finish> onPiece)
        {
            var m = new Model { root = new GameObject(s.ToString()).transform };
            m.root.SetParent(parent, false);
            var r = m.root;

            switch (s)
            {
                case Structure.Scalpel: Scalpel(m, onPiece); break;
                case Structure.FreerElevator: Freer(m, onPiece); break;
                case Structure.ScissorsForceps: Forceps(m, onPiece); break;
                case Structure.Suction: Suction(m, onPiece); break;
            }
            return m;
        }

        // ------------------------------------------------------------------ instruments (all sizes in mm, converted)

        const float MM = 0.001f;

        /// <summary>#15 blade on a #3 Bard-Parker handle (blade and handle share the YZ plane).</summary>
        static void Scalpel(Model m, Action<GameObject, Finish> on)
        {
            m.length = 0.145f; m.grip = 0.075f;
            // Blade side view (z, y): straight back, small convex belly ending in the point; edge bevelled to a few microns.
            var blade = Get("Blade15", () =>
            {
                var o = new List<Vector2>(); var th = new List<float>();
                void P(float z, float y, float t) { o.Add(new Vector2(z, y)); th.Add(t); }
                P(0f, 0.2f, 0.06f);                                     // point
                P(-4f, 0.75f, 0.4f); P(-10f, 1.15f, 0.4f); P(-16f, 1.3f, 0.4f); P(-21f, 1.3f, 0.4f);   // back
                P(-21f, -1.5f, 0.4f); P(-17f, -1.9f, 0.4f);             // tang, heel
                P(-15.2f, -3.0f, 0.12f); P(-12.5f, -3.55f, 0.05f); P(-9f, -3.7f, 0.05f);              // belly (cutting edge)
                P(-6f, -3.45f, 0.05f); P(-3.6f, -2.75f, 0.05f); P(-1.6f, -1.65f, 0.05f); P(-0.4f, -0.6f, 0.05f);
                return Plate(o, th, 0.4f, PlaneYZ);
            });
            Add("Blade", blade, m.root, Vector3.zero, Quaternion.identity, Finish.Steel, on);
            // Handle: narrow blade fitting, then the flat 9 mm bar with a ribbed thumb rest, rounded end.
            var handle = Get("BardParker3", () =>
            {
                var o = new List<Vector2>(); var th = new List<float>();
                void P(float z, float y) { o.Add(new Vector2(z, y)); th.Add(2.6f); }
                P(-18f, 1.4f); P(-30f, 1.6f); P(-36f, 3.6f); P(-46f, 4.6f); P(-140f, 4.6f); P(-143f, 3.8f); P(-145f, 2f);
                P(-145.5f, 0f); P(-145f, -2f); P(-143f, -3.8f); P(-140f, -4.6f); P(-46f, -4.6f); P(-36f, -3.6f); P(-30f, -1.6f);
                P(-18f, -1.4f);
                return Plate(o, th, 2.6f, PlaneYZ);
            });
            Add("Handle", handle, m.root, new Vector3(0, -0.0002f, 0), Quaternion.identity, Finish.Handle, on);
            var rib = Get("ScalpelRib", () => Box(new Vector3(3.0f, 7.4f, 0.8f)));
            for (int i = 0; i < 9; i++)
                Add("Rib", rib, m.root, new Vector3(0, -0.0002f, (-52f - 3.2f * i) * MM), Quaternion.identity, Finish.Steel, on);
        }

        /// <summary>Double-ended Freer: sharp spatula in front, blunt one at the back, octagonal knurled middle.</summary>
        static void Freer(Model m, Action<GameObject, Finish> on)
        {
            m.length = 0.19f; m.grip = 0.095f;
            var spat = Get("FreerSpatula", () => Spatula(4.2f, 10f, 0.5f, 0.12f));
            // Front spatula angled up 8 deg, as the instrument is used flat on the septum.
            Add("Spatula", spat, m.root, Vector3.zero, Quaternion.Euler(-8, 0, 0), Finish.Steel, on);
            var shaft = Get("FreerShaft", () => Lathe(new[] { -9.5f, -14f, -40f, -58f, -60f }, new[] { 0.75f, 1.05f, 1.6f, 2.2f, 2.6f }, 16));
            Add("Shaft", shaft, m.root, new Vector3(0, -0.0014f, 0), Quaternion.identity, Finish.Steel, on);
            var mid = Get("FreerHandle", () =>
            {
                var z = new List<float>(); var rr = new List<float>();
                z.Add(-60f); rr.Add(2.6f);
                for (int i = 0; i <= 14; i++) { z.Add(-62f - 5f * i); rr.Add(i % 2 == 0 ? 3.3f : 3.0f); }   // knurled bands
                z.Add(-134f); rr.Add(2.6f);
                return Lathe(z.ToArray(), rr.ToArray(), 8);
            });
            Add("Handle", mid, m.root, new Vector3(0, -0.0014f, 0), Quaternion.identity, Finish.Handle, on);
            var back = Get("FreerShaftBack", () => Lathe(new[] { -134f, -136f, -160f, -178f }, new[] { 2.6f, 2.2f, 1.5f, 1.0f }, 16));
            Add("ShaftBack", back, m.root, new Vector3(0, -0.0014f, 0), Quaternion.identity, Finish.Steel, on);
            var blunt = Get("FreerBlunt", () => Spatula(4.8f, 11f, 0.7f, 0.35f));
            Add("Blunt", blunt, m.root, new Vector3(0, -0.0014f, -0.1875f), Quaternion.Euler(8, 180, 0), Finish.Steel, on);
        }

        /// <summary>Straight through-cutting (Weil-Blakesley type) forceps: fenestrated cups, shaft, ring handle below.</summary>
        static void Forceps(Model m, Action<GameObject, Finish> on)
        {
            m.length = 0.2f; m.grip = 0.15f;
            const float hinge = -9.5f;
            var cup = Get("ForcepsCup", () => Cup(9f, 3.3f, 1.25f));
            var window = Get("ForcepsWindow", () => Lathe(new[] { 0f, -0.6f, -4.2f, -4.8f }, new[] { 0f, 0.7f, 0.7f, 0f }, 10, 1f, 0.35f));
            m.jawUpper = new GameObject("JawUpper").transform;
            m.jawLower = new GameObject("JawLower").transform;
            foreach (var j in new[] { m.jawUpper, m.jawLower })
            {
                bool up = j == m.jawUpper;
                j.SetParent(m.root, false);
                j.localPosition = new Vector3(0, 0, hinge * MM);
                var rot = up ? Quaternion.identity : Quaternion.Euler(0, 0, 180);
                Add("Cup", cup, j, new Vector3(0, 0, -hinge * MM), rot, Finish.Steel, on);
                Add("Window", window, j, new Vector3(0, (up ? 1.12f : -1.12f) * MM, (-hinge - 2.6f) * MM), Quaternion.identity, Finish.Dark, on);
            }
            var hingeBox = Get("ForcepsHinge", () => Lathe(new[] { -8f, -9f, -12f, -14f }, new[] { 1.4f, 1.9f, 1.9f, 1.6f }, 16, 1.1f, 0.9f));
            Add("Hinge", hingeBox, m.root, Vector3.zero, Quaternion.identity, Finish.Steel, on);
            var shaft = Get("ForcepsShaft", () => Lathe(new[] { -14f, -60f, -112f, -116f }, new[] { 1.6f, 1.75f, 2.1f, 2.6f }, 18));
            Add("Shaft", shaft, m.root, Vector3.zero, Quaternion.identity, Finish.Steel, on);
            // Handle: the fixed shank drops below the line of sight (scope stays clear), rings at its end.
            var fixedShank = Get("ForcepsShankFixed", () => Sweep(new[] {
                new Vector3(0, 0, -116), new Vector3(0, -3, -124), new Vector3(0, -14, -138), new Vector3(0, -30, -152), new Vector3(0, -40, -160) },
                new[] { 2.4f, 2.4f, 2.2f, 2.1f, 2.0f }, 12));
            Add("ShankFixed", fixedShank, m.root, Vector3.zero, Quaternion.identity, Finish.Steel, on);
            var movingShank = Get("ForcepsShankMoving", () => Sweep(new[] {
                new Vector3(0, 1.5f, -116), new Vector3(0, 1.5f, -128), new Vector3(0, -2, -146), new Vector3(0, -8, -164), new Vector3(0, -10, -170) },
                new[] { 1.9f, 1.9f, 1.9f, 1.8f, 1.8f }, 12));
            Add("ShankMoving", movingShank, m.root, Vector3.zero, Quaternion.identity, Finish.Steel, on);
            var ring = Get("ForcepsRing", () => Torus(9.5f, 1.9f, 28, 10));
            Add("FingerRing", ring, m.root, new Vector3(0, -48f * MM, -167f * MM), Quaternion.Euler(0, 0, 0), Finish.Steel, on);
            Add("ThumbRing", ring, m.root, new Vector3(0, -12f * MM, -180f * MM), Quaternion.identity, Finish.Steel, on);
            var spur = Get("ForcepsSpur", () => Sweep(new[] { new Vector3(0, -57, -165), new Vector3(0, -66, -160), new Vector3(0, -72, -150) }, new[] { 1.8f, 1.6f, 1.2f }, 10));
            Add("Spur", spur, m.root, Vector3.zero, Quaternion.identity, Finish.Steel, on);
            var screw = Get("ForcepsScrew", () => Lathe(new[] { -1.4f, -1.4f, 1.4f, 1.4f }, new[] { 0f, 2.2f, 2.2f, 0f }, 16));
            Add("Screw", screw, m.root, new Vector3(0, 0, -120f * MM), Quaternion.Euler(0, 90, 0), Finish.Steel, on);
            m.SetJaw(1f);
        }

        /// <summary>Frazier 10 Fr: straight cannula, bend, finger-valve handle (thumb port), hose barb.</summary>
        static void Suction(Model m, Action<GameObject, Finish> on)
        {
            m.length = 0.2f; m.grip = 0.14f;
            var path = new List<Vector3>();
            path.Add(Vector3.zero);
            path.Add(new Vector3(0, 0, -92));
            for (int i = 1; i <= 8; i++)   // 40 deg bend down, radius 18 mm
            {
                float a = 40f * Mathf.Deg2Rad * i / 8f;
                path.Add(new Vector3(0, -18f * (1 - Mathf.Cos(a)), -92 - 18f * Mathf.Sin(a)));
            }
            var radii = new float[path.Count];
            for (int i = 0; i < radii.Length; i++) radii[i] = 1.65f;
            var tube = Get("FrazierTube", () => Sweep(path.ToArray(), radii, 14));
            Add("Cannula", tube, m.root, Vector3.zero, Quaternion.identity, Finish.Steel, on);
            var lumen = Get("FrazierLumen", () => Lathe(new[] { 0.05f, 0f, -3f }, new[] { 0f, 1.2f, 1.2f }, 14));
            Add("Lumen", lumen, m.root, Vector3.zero, Quaternion.identity, Finish.Dark, on);
            // Handle along the bent direction: tapered grip with a teardrop thumb port on top, barbs at the end.
            var end = path[path.Count - 1];
            var dir = Quaternion.Euler(-40, 0, 0);   // -Z bent down by 40 deg
            var grip = Get("FrazierGrip", () => Lathe(new[] { 0f, -4f, -10f, -60f, -66f, -70f, -73f, -76f, -79f, -82f },
                                                     new[] { 1.65f, 3.2f, 4.4f, 4.6f, 3.4f, 2.6f, 3.2f, 2.6f, 3.2f, 2.4f }, 18, 1f, 0.85f));
            Add("Grip", grip, m.root, end * MM, dir, Finish.Handle, on);
            var port = Get("FrazierPort", () => Lathe(new[] { 0f, -1.5f, -7f, -8f }, new[] { 0f, 1.6f, 0.9f, 0f }, 12, 1f, 0.4f));
            Add("ThumbPort", port, m.root, end * MM + dir * new Vector3(0, 3.95f, -16f) * MM, dir, Finish.Dark, on);
        }

        // ------------------------------------------------------------------ shapes (mm)

        static Mesh Get(string key, Func<Mesh> make)
        {
            if (Cache.TryGetValue(key, out var mesh) && mesh != null) return mesh;
            mesh = make();
            mesh.name = key;
            Cache[key] = mesh;
            return mesh;
        }

        static void Add(string name, Mesh mesh, Transform parent, Vector3 pos, Quaternion rot, Finish f, Action<GameObject, Finish> on)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = rot;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var rend = go.AddComponent<MeshRenderer>();
            rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            rend.receiveShadows = false;
            on?.Invoke(go, f);
        }

        static Vector3 PlaneYZ(Vector2 o, float x) => new Vector3(x, o.y, o.x);
        static Vector3 PlaneXZ(Vector2 o, float y) => new Vector3(o.y, y, o.x);

        /// <summary>
        /// Flat plate from a convex outline (mm) with per-point thickness (thin = bevelled edge) and the given
        /// thickness at the centre; <paramref name="map"/> places (outline, offset across the plate) in 3D.
        /// </summary>
        static Mesh Plate(List<Vector2> outline, List<float> thick, float centreThick, Func<Vector2, float, Vector3> map)
        {
            var c = Vector2.zero;
            foreach (var p in outline) c += p;
            c /= outline.Count;
            int n = outline.Count;
            var v = new List<Vector3>(); var t = new List<int>();
            for (int side = -1; side <= 1; side += 2)
            {
                int b = v.Count;
                v.Add(map(c, side * centreThick * 0.5f) * MM);
                for (int i = 0; i < n; i++) v.Add(map(outline[i], side * thick[i] * 0.5f) * MM);
                for (int i = 0; i < n; i++)
                {
                    int a = b + 1 + i, d = b + 1 + (i + 1) % n;
                    if (side > 0) { t.Add(b); t.Add(a); t.Add(d); } else { t.Add(b); t.Add(d); t.Add(a); }
                }
            }
            // Rim (own vertices so it shades flat).
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n, b = v.Count;
                v.Add(map(outline[i], -thick[i] * 0.5f) * MM); v.Add(map(outline[j], -thick[j] * 0.5f) * MM);
                v.Add(map(outline[j], thick[j] * 0.5f) * MM); v.Add(map(outline[i], thick[i] * 0.5f) * MM);
                t.Add(b); t.Add(b + 2); t.Add(b + 1); t.Add(b); t.Add(b + 3); t.Add(b + 2);
            }
            var core = map(c, 0f) * MM;
            return Finish3(v, t, _ => core);
        }

        /// <summary>Freer spatula, flat in XZ (thin in Y): rounded tip of the given width, neck into the shaft.</summary>
        static Mesh Spatula(float width, float length, float thick, float edgeThick)
        {
            var o = new List<Vector2>(); var th = new List<float>();
            float hw = width * 0.5f;
            for (int i = 0; i <= 10; i++)   // rounded working edge (thin), from one side to the other
            {
                float a = Mathf.PI * i / 10f;
                o.Add(new Vector2(-hw + hw * Mathf.Sin(a) * 0.9f, -hw * Mathf.Cos(a))); th.Add(edgeThick);
            }
            o.Add(new Vector2(-length * 0.7f, hw)); th.Add(thick);
            o.Add(new Vector2(-length, 0.8f)); th.Add(thick * 1.6f);
            o.Add(new Vector2(-length, -0.8f)); th.Add(thick * 1.6f);
            o.Add(new Vector2(-length * 0.7f, -hw)); th.Add(thick);
            return Plate(o, th, thick, PlaneXZ);
        }

        /// <summary>Surface of revolution about Z: radius r[i] at z[i] (mm); sx / sy flatten the section.</summary>
        static Mesh Lathe(float[] z, float[] r, int seg, float sx = 1f, float sy = 1f)
        {
            var v = new List<Vector3>(); var t = new List<int>();
            for (int i = 0; i < z.Length; i++)
                for (int k = 0; k < seg; k++)
                {
                    float a = 2 * Mathf.PI * k / seg;
                    v.Add(new Vector3(Mathf.Cos(a) * r[i] * sx, Mathf.Sin(a) * r[i] * sy, z[i]) * MM);
                }
            for (int i = 0; i + 1 < z.Length; i++)
                for (int k = 0; k < seg; k++)
                {
                    int a = i * seg + k, b = i * seg + (k + 1) % seg, c = a + seg, d = b + seg;
                    // z decreases along the profile: wind so normals face outward.
                    t.Add(a); t.Add(b); t.Add(c); t.Add(b); t.Add(d); t.Add(c);
                }
            Cap(v, t, 0, seg, z[0], r[0]);
            Cap(v, t, (z.Length - 1) * seg, seg, z[z.Length - 1], r[r.Length - 1]);
            float lo = Mathf.Min(z[0], z[z.Length - 1]) * MM + 0.0002f, hi = Mathf.Max(z[0], z[z.Length - 1]) * MM - 0.0002f;
            return Finish3(v, t, p => new Vector3(0, 0, Mathf.Clamp(p.z, Mathf.Min(lo, hi), Mathf.Max(lo, hi))));
        }

        static void Cap(List<Vector3> v, List<int> t, int ring, int seg, float z, float r)
        {
            if (r < 1e-4f) return;
            int c = v.Count;
            v.Add(new Vector3(0, 0, z) * MM);
            for (int k = 0; k < seg; k++) { t.Add(c); t.Add(ring + k); t.Add(ring + (k + 1) % seg); }
        }

        /// <summary>Tube along a polyline (mm) with rotation-minimising frames, closed at both ends.</summary>
        static Mesh Sweep(Vector3[] path, float[] radius, int seg)
        {
            var v = new List<Vector3>(); var t = new List<int>();
            Vector3 prevN = Vector3.zero;
            for (int i = 0; i < path.Length; i++)
            {
                var tan = (path[Mathf.Min(i + 1, path.Length - 1)] - path[Mathf.Max(i - 1, 0)]).normalized;
                var n = i == 0 ? Vector3.Cross(tan, Mathf.Abs(tan.x) < 0.9f ? Vector3.right : Vector3.up).normalized
                               : Vector3.ProjectOnPlane(prevN, tan).normalized;
                prevN = n;
                var bn = Vector3.Cross(tan, n);
                for (int k = 0; k < seg; k++)
                {
                    float a = 2 * Mathf.PI * k / seg;
                    v.Add((path[i] + (n * Mathf.Cos(a) + bn * Mathf.Sin(a)) * radius[i]) * MM);
                }
            }
            for (int i = 0; i + 1 < path.Length; i++)
                for (int k = 0; k < seg; k++)
                {
                    int a = i * seg + k, b = i * seg + (k + 1) % seg, c = a + seg, d = b + seg;
                    t.Add(a); t.Add(c); t.Add(b); t.Add(b); t.Add(c); t.Add(d);
                }
            Cap(v, t, 0, seg, path[0]);
            Cap(v, t, (path.Length - 1) * seg, seg, path[path.Length - 1]);
            return Finish3(v, t, p => Nearest(path, p / MM) * MM);
        }

        static void Cap(List<Vector3> v, List<int> t, int ring, int seg, Vector3 centre)
        {
            int c = v.Count;
            v.Add(centre * MM);
            for (int k = 0; k < seg; k++) { t.Add(c); t.Add(ring + k); t.Add(ring + (k + 1) % seg); }
        }

        /// <summary>Ring in the YZ plane (axis X): R ring radius, r wire radius (mm).</summary>
        static Mesh Torus(float R, float r, int segU, int segV)
        {
            var v = new List<Vector3>(); var t = new List<int>();
            for (int i = 0; i < segU; i++)
            {
                float u = 2 * Mathf.PI * i / segU;
                var c = new Vector3(0, Mathf.Cos(u), Mathf.Sin(u));
                for (int k = 0; k < segV; k++)
                {
                    float w = 2 * Mathf.PI * k / segV;
                    v.Add((c * (R + r * Mathf.Cos(w)) + Vector3.right * r * Mathf.Sin(w)) * MM);
                }
            }
            for (int i = 0; i < segU; i++)
                for (int k = 0; k < segV; k++)
                {
                    int a = i * segV + k, b = i * segV + (k + 1) % segV, c = ((i + 1) % segU) * segV + k, d = ((i + 1) % segU) * segV + (k + 1) % segV;
                    t.Add(a); t.Add(b); t.Add(c); t.Add(b); t.Add(d); t.Add(c);
                }
            return Finish3(v, t, p =>
            {
                var q = new Vector3(0, p.y, p.z);
                return q.sqrMagnitude > 1e-12f ? q.normalized * R * MM : Vector3.zero;
            });
        }

        /// <summary>Forceps cup: half of an elongated rounded shell, open side down (toward the other jaw).</summary>
        static Mesh Cup(float length, float width, float height)
        {
            int seg = 10, rings = 14;
            var v = new List<Vector3>(); var t = new List<int>();
            for (int i = 0; i <= rings; i++)
            {
                float u = i / (float)rings;                  // 0 tip .. 1 hinge
                float z = -u * length;
                float prof = Mathf.Sqrt(Mathf.Clamp01(1 - (1 - Mathf.Min(1, u * 2.2f)) * (1 - Mathf.Min(1, u * 2.2f))));   // rounded tip
                for (int k = 0; k <= seg; k++)
                {
                    float a = Mathf.PI * k / seg;            // 0..pi: the upper half
                    v.Add(new Vector3(Mathf.Cos(a) * width * 0.5f * prof, Mathf.Sin(a) * height * prof + 0.05f, z) * MM);
                }
            }
            for (int i = 0; i < rings; i++)
                for (int k = 0; k < seg; k++)
                {
                    int a = i * (seg + 1) + k, b = a + 1, c = a + seg + 1, d = c + 1;
                    t.Add(a); t.Add(c); t.Add(b); t.Add(b); t.Add(c); t.Add(d);
                }
            // Flat cutting face underneath.
            int baseIdx = v.Count;
            for (int i = 0; i <= rings; i++)
            {
                v.Add(v[i * (seg + 1)]); v.Add(v[i * (seg + 1) + seg]);
            }
            for (int i = 0; i < rings; i++)
            {
                int a = baseIdx + 2 * i, b = a + 1, c = a + 2, d = a + 3;
                t.Add(a); t.Add(b); t.Add(c); t.Add(b); t.Add(d); t.Add(c);
            }
            return Finish3(v, t, p => new Vector3(0, 0.4f * height * MM, Mathf.Clamp(p.z, -length * MM + 0.0003f, -0.0003f)));
        }

        static Mesh Box(Vector3 size)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var src = go.GetComponent<MeshFilter>().sharedMesh;
            UnityEngine.Object.Destroy(go);
            var v = src.vertices;
            for (int i = 0; i < v.Length; i++) v[i] = Vector3.Scale(v[i], size) * MM;
            var m = new Mesh { vertices = v, triangles = src.triangles, normals = src.normals };
            m.RecalculateBounds();
            return m;
        }

        /// <summary>
        /// Builds the mesh with every triangle facing away from <paramref name="core"/>(its centre) - the axis for
        /// lathed parts, the path for tubes, the ring circle for tori: all the shapes here are star-shaped about it.
        /// </summary>
        static Mesh Finish3(List<Vector3> v, List<int> t, Func<Vector3, Vector3> core)
        {
            for (int i = 0; i < t.Count; i += 3)
            {
                Vector3 a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]];
                var mid = (a + b + c) / 3f;
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), mid - core(mid)) < 0) { int k = t[i + 1]; t[i + 1] = t[i + 2]; t[i + 2] = k; }
            }
            var m = new Mesh();
            m.SetVertices(v);
            m.SetTriangles(t, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        static Vector3 Nearest(Vector3[] path, Vector3 p)
        {
            Vector3 best = path[0]; float bd = float.MaxValue;
            for (int i = 0; i + 1 < path.Length; i++)
            {
                var ab = path[i + 1] - path[i];
                float u = Mathf.Clamp01(Vector3.Dot(p - path[i], ab) / Mathf.Max(ab.sqrMagnitude, 1e-9f));
                var q = path[i] + ab * u;
                if (i == 0 && u <= 0f) q += ab.normalized * 0.3f;                          // caps: just inside the end
                if (i == path.Length - 2 && u >= 1f) q -= ab.normalized * 0.3f;
                float d = (q - p).sqrMagnitude;
                if (d < bd) { bd = d; best = q; }
            }
            return best;
        }
    }
}
