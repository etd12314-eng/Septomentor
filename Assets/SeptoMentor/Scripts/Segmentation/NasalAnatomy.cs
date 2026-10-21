using System;
using System.Collections.Generic;
using SeptoMentor.Nav;
using UnityEngine;

namespace SeptoMentor
{
    /// <summary>
    /// Procedural right nasal cavity for endoscopic septoplasty training (synthetic, NOT patient data):
    /// vestibule with vibrissae, internal valve, septum with a C-shaped deviation to the right and a
    /// basal spur, inferior and middle turbinates, nasal floor, narrowing roof, choana and nasopharynx
    /// with Eustachian tori. Built as a signed-distance field (mm) and meshed with SurfaceNets.
    ///
    /// The septal flap area is made of separate pieces that SeptoplastySim changes as the resident operates:
    /// Killian-type incision strip (cut segment by segment), mucoperichondrial flap (elevated off the
    /// bluish-white cartilage at runtime), resection window cartilage (bitten out chunk by chunk, keeping a
    /// ~10 mm L-strut) with the contralateral mucoperichondrium behind it.
    ///
    /// Frame: the CT phantom's (L, S, P), in metres in the VirtualPatient's local space
    /// (+X patient-left, +Y superior, +Z posterior), so the nostril sits exactly where the OR head's is.
    /// Generated in the editor (SceneBuilder) and stored as mesh assets.
    /// </summary>
    public static class NasalAnatomy
    {
        public class Piece
        {
            public string name;
            public Structure structure;
            public Color tint;
            public int phaseMask;
            public readonly List<Vector3> verts = new List<Vector3>();
            public readonly List<int> tris = new List<int>();
            internal readonly Dictionary<int, int> remap = new Dictionary<int, int>();   // shared SurfaceNets vertices
        }

        public const int AllPhases = 0x1F;
        public static int Mask(params SurgicalPhase[] phases) { int m = 0; foreach (var p in phases) m |= 1 << (int)p; return m; }

        /// <summary>Autopilot path (m): from the right nostril up and back through the common meatus (>= 2 mm clearance).</summary>
        public static readonly Vector3 PathStart = new Vector3(-7.5f, -25f, -86f) * 0.001f, PathEnd = new Vector3(-8.5f, -2f, -30f) * 0.001f;

        // Flap area (S, P mm) on the right septal face, the incision strip in front of it, and the resection window.
        public const float FlapP0 = -71f, FlapP1 = -28f, FlapS0 = -21f, FlapS1 = 17f, IncisionP0 = -72.2f;
        public const float WinP0 = -62f, WinP1 = -36f, WinS0 = -18f, WinS1 = 6f;
        public const float CartilageDepth = 0.9f, ContralateralDepth = 2.6f;
        /// <summary>The incision strip is cut segment by segment (along S); the window is resected chunk by chunk.</summary>
        public const int IncisionSegments = 8, ChunksS = 3, ChunksP = 4;

        static readonly Color Mucosa = new Color(0.90f, 0.55f, 0.56f), Turbinate = new Color(0.82f, 0.42f, 0.45f),
                              Floor = new Color(0.88f, 0.52f, 0.52f), Pharynx = new Color(0.78f, 0.38f, 0.40f),
                              Skin = new Color(0.86f, 0.66f, 0.58f), Cartilage = new Color(0.82f, 0.80f, 0.86f),
                              Hair = new Color(0.12f, 0.08f, 0.06f), Contra = new Color(0.86f, 0.50f, 0.52f);

        // ---------------------------------------------------------------- field (mm, L/S/P)

        static float SMin(float a, float b, float k)
        {
            float h = Mathf.Clamp01(0.5f + 0.5f * (b - a) / k);
            return Mathf.Lerp(b, a, h) - k * h * (1 - h);
        }

        static float SMax(float a, float b, float k) => -SMin(-a, -b, k);

        static float Ell(float l, float s, float p, float cl, float cs, float cp, float rl, float rs, float rp)
        {
            float x = (l - cl) / rl, y = (s - cs) / rs, z = (p - cp) / rp;
            float k0 = Mathf.Sqrt(x * x + y * y + z * z);
            float k1 = Mathf.Sqrt(x * x / (rl * rl) + y * y / (rs * rs) + z * z / (rp * rp)) + 1e-9f;
            return k0 * (k0 - 1) / k1;
        }

        static float Capsule(Vector3 q, Vector3 a, Vector3 b, float ra, float rb)
        {
            var ab = b - a; float t = Mathf.Clamp01(Vector3.Dot(q - a, ab) / ab.sqrMagnitude);
            return Vector3.Distance(q, a + ab * t) - Mathf.Lerp(ra, rb, t);
        }

        static float Window(float x, float a, float b, float w) => 1f / (1f + Mathf.Exp(-(x - a) / w)) / (1f + Mathf.Exp((x - b) / w));

        /// <summary>Septal midline offset (mm, negative = toward the patient's right).</summary>
        public static float Deviation(float s, float p) =>
            -4.5f * Mathf.Exp(-((s + 6) * (s + 6) / 110f + (p + 45) * (p + 45) / 300f))   // C-shaped deviation
            - 3.2f * Mathf.Exp(-(s + 22) * (s + 22) / 5f) * Window(p, -58, -14, 3);          // basal spur (vomer / crest junction)

        /// <summary>Right mucosal face of the septum (L, mm).</summary>
        public static float SeptumFace(float s, float p) => Deviation(s, p) - 1.8f;

        static float PosteriorEdge(float s) => 8f - (s + 27f) * 0.25f;   // free edge of the vomer

        static float Roof(float p) => p < -45 ? Mathf.Min(22f, (p + 80f) * 22f / 35f) : p > -5 ? 22f - (p + 5f) * 0.9f : 22f;

        public enum Wall { Septum, Lateral, Floor, Roof, Vestibule, Pharynx, InfTurb, MidTurb, Torus, Count }

        static void Components(float l, float s, float p, float[] c)
        {
            c[(int)Wall.Septum] = l - SeptumFace(s, p);
            c[(int)Wall.Lateral] = (-15f + Mathf.Max(0, s - 2) * 0.38f) - l;
            float ant = Mathf.Max(0, -60 - p);
            c[(int)Wall.Floor] = -27f + 0.004f * ant * ant - s;
            c[(int)Wall.Roof] = s - Roof(p);
            c[(int)Wall.Vestibule] = Capsule(new Vector3(l, s, p), new Vector3(-7.4f, -28.6f, -104f), new Vector3(-8f, -19f, -68f), 3.9f, 5.5f);   // runs out of the grid: open nostril
            float np = SMax(SMax(Mathf.Abs(l) - 15f, p - 30f, 4), SMax(-25f - s, s - (14f - (p - 5f) * 0.6f), 4), 4);
            c[(int)Wall.Pharynx] = SMax(np, PosteriorEdge(s) - p - 2f, 2);
            c[(int)Wall.InfTurb] = SMin(Ell(l, s, p, -15.5f, -17.5f, -20f, 7, 7.5f, 33), Ell(l, s, p, -14.8f, -16f, -44f, 7, 7.8f, 12), 3);
            c[(int)Wall.MidTurb] = SMin(Ell(l, s, p, -10.8f, 4f, -15f, 3.3f, 6, 22), Ell(l, s, p, -13f, 11f, -15f, 2, 6, 20), 2);
            c[(int)Wall.Torus] = Ell(Mathf.Abs(l), s, p, 14, -8, 20, 4, 6, 6);
        }

        /// <summary>Signed distance to the airway wall, mm: negative in air, positive in tissue.</summary>
        static float Air(float l, float s, float p, float[] c)
        {
            Components(l, s, p, c);
            float cav = SMax(SMax(c[(int)Wall.Septum], c[(int)Wall.Lateral], 2), SMax(c[(int)Wall.Floor], c[(int)Wall.Roof], 2), 2);
            cav = SMax(cav, SMax(-74f - p, p - PosteriorEdge(s), 2), 2);
            float a = SMin(SMin(cav, c[(int)Wall.Vestibule], 3), c[(int)Wall.Pharynx], 3);
            a = SMax(a, -c[(int)Wall.InfTurb], 1.5f);
            a = SMax(a, -c[(int)Wall.MidTurb], 1.5f);
            return SMax(a, -c[(int)Wall.Torus], 2);
        }

        static readonly float[] Scratch = new float[(int)Wall.Count];

        /// <summary>
        /// Tissue depth at a point (mm, L/S/P): positive inside tissue, negative in air, plus the wall that is closest.
        /// Main thread only (shared scratch buffer).
        /// </summary>
        public static float Depth(Vector3 mm, out Wall wall)
        {
            float d = Air(mm.x, mm.y, mm.z, Scratch);
            int best = 0;
            for (int i = 1; i < (int)Wall.Count; i++) if (Mathf.Abs(Scratch[i]) < Mathf.Abs(Scratch[best])) best = i;
            wall = (Wall)best;
            return d;
        }

        // ---------------------------------------------------------------- build

        public static List<Piece> Build(float stepMm = 0.5f, Action<string> progress = null)
        {
            var pieces = new Dictionary<string, Piece>();
            Piece Get(string name, Structure s, Color tint, int mask = AllPhases)
            {
                if (!pieces.TryGetValue(name, out var pc)) pieces[name] = pc = new Piece { name = name, structure = s, tint = tint, phaseMask = mask };
                return pc;
            }

            progress?.Invoke("airway surface");
            var min = new Vector3(-19f, -31f, -97f);
            var n = new Vector3Int(Mathf.CeilToInt(35f / stepMm), Mathf.CeilToInt(57f / stepMm), Mathf.CeilToInt(130f / stepMm));
            var toModel = Matrix4x4.TRS(min * 0.001f, Quaternion.identity, Vector3.one * stepMm * 0.001f);
            var buf = new float[(int)Wall.Count];
            var sn = SurfaceNets.Extract((i, j, k) => Air(min.x + i * stepMm, min.y + j * stepMm, min.z + k * stepMm, buf), 0f,
                                         Vector3Int.zero, n, 1, toModel).Smooth(2);

            progress?.Invoke("classifying");
            for (int t = 0; t < sn.triangles.Count; t += 3)
            {
                int a = sn.triangles[t], b = sn.triangles[t + 1], c = sn.triangles[t + 2];
                var m = (sn.vertices[a] + sn.vertices[b] + sn.vertices[c]) * (1000f / 3f);
                Components(m.x, m.y, m.z, buf);
                int best = 0;
                for (int i = 1; i < (int)Wall.Count; i++) if (Mathf.Abs(buf[i]) < Mathf.Abs(buf[best])) best = i;
                Piece pc;
                switch ((Wall)best)
                {
                    case Wall.Septum:
                        // The flap area is rebuilt below as phase pieces; the cut is a little smaller so they overlap.
                        if (m.z > IncisionP0 + 0.3f && m.z < FlapP1 - 0.3f && m.y > FlapS0 + 0.3f && m.y < FlapS1 - 0.3f) continue;
                        pc = m.z < -72f ? Get("Septum_Caudal", Structure.CaudalSeptum, Mucosa)
                           : m.y < -22.5f ? Get("Septum_MaxillaryCrest", Structure.MaxillaryCrest, Mucosa)
                           : m.z > FlapP1 ? (m.y > -8f ? Get("Septum_PerpendicularPlate", Structure.PerpendicularPlate, Mucosa)
                                                       : Get("Septum_Vomer", Structure.Vomer, Mucosa))
                           : Get("Septum_Cartilaginous", Structure.SeptalCartilage, Mucosa);
                        break;
                    case Wall.InfTurb: pc = Get("InferiorTurbinate", Structure.InferiorTurbinate, Turbinate); break;
                    case Wall.MidTurb: pc = Get("MiddleTurbinate", Structure.MiddleTurbinate, Turbinate * 1.05f); break;
                    case Wall.Floor: pc = Get("NasalFloor", Structure.NasalFloor, Floor); break;
                    case Wall.Vestibule: pc = Get("Vestibule", Structure.Background, Skin); break;
                    case Wall.Pharynx: case Wall.Torus: pc = Get("Nasopharynx", Structure.SinonasalBone, Pharynx); break;
                    default: pc = Get("LateralWall", Structure.SinonasalBone, Mucosa * 0.95f); break;
                }
                foreach (int vi in new[] { a, b, c })
                {
                    if (!pc.remap.TryGetValue(vi, out int li)) { li = pc.verts.Count; pc.verts.Add(sn.vertices[vi]); pc.remap[vi] = li; }
                    pc.tris.Add(li);
                }
            }

            progress?.Invoke("septal flap pieces");
            var full = new Rect(FlapS0, FlapP0, FlapS1 - FlapS0, FlapP1 - FlapP0);   // x = S, y = P
            var win = new Rect(WinS0, WinP0, WinS1 - WinS0, WinP1 - WinP0);
            // Pieces without a phase mask below are switched by SeptoplastySim as the resident operates.
            float segW = (FlapS1 - FlapS0) / IncisionSegments;
            for (int k = 0; k < IncisionSegments; k++)
                Sheet(Get("Flap_IncisionStrip_" + k, Structure.MucoperichondrialFlap, Mucosa),
                      new Rect(FlapS0 + k * segW, IncisionP0, segW, FlapP0 - IncisionP0), 0f, null, null, stepMm);
            Sheet(Get("Incision_Bed", Structure.SeptalCartilage, new Color(0.62f, 0.22f, 0.22f)),
                  new Rect(FlapS0, IncisionP0 - 0.3f, FlapS1 - FlapS0, FlapP0 - IncisionP0 + 0.6f), 1.1f, null, null, stepMm);
            Sheet(Get("Flap", Structure.MucoperichondrialFlap, Mucosa), full, 0f, null, null, stepMm);   // elevated at runtime
            Sheet(Get("Cartilage", Structure.SeptalCartilage, Cartilage), full, CartilageDepth, null, win, stepMm);
            float cw = win.width / ChunksS, ch = win.height / ChunksP;
            for (int i = 0; i < ChunksS; i++)
            for (int j = 0; j < ChunksP; j++)
                Sheet(Get($"Cartilage_Chunk_{i}_{j}", Structure.SeptalCartilage, Cartilage),
                      new Rect(win.x + i * cw, win.y + j * ch, cw, ch), CartilageDepth, null, null, stepMm);
            Sheet(Get("Contralateral_Mucoperichondrium", Structure.MucoperichondrialFlap, Contra),
                  new Rect(win.x - 0.5f, win.y - 0.5f, win.width + 1f, win.height + 1f), ContralateralDepth, null, null, stepMm);
            WindowRim(Get("Cartilage_CutEdge", Structure.SeptalCartilage, Cartilage * 0.92f), win, stepMm);

            progress?.Invoke("vibrissae");
            Vibrissae(Get("Vibrissae", Structure.Background, Hair));

            var list = new List<Piece>();
            foreach (var p in pieces.Values) if (p.tris.Count > 0) list.Add(p);
            return list;
        }

        static void AddTri(Piece pc, Vector3 a, Vector3 b, Vector3 c)
        {
            int i = pc.verts.Count;
            pc.verts.Add(a); pc.verts.Add(b); pc.verts.Add(c);
            pc.tris.Add(i); pc.tris.Add(i + 1); pc.tris.Add(i + 2);
        }

        /// <summary>
        /// Height field on the septum: L = face + depth (+ displacement) over rect (x = S, y = P), facing the
        /// right cavity (-L). Cells inside <paramref name="hole"/> are skipped.
        /// </summary>
        static void Sheet(Piece pc, Rect r, float depth, Func<float, float, float> disp, Rect? hole, float step)
        {
            int ns = Mathf.Max(1, Mathf.CeilToInt(r.width / step)), np = Mathf.Max(1, Mathf.CeilToInt(r.height / step));
            int baseIdx = pc.verts.Count;
            for (int j = 0; j <= np; j++)
            for (int i = 0; i <= ns; i++)
            {
                float s = r.x + r.width * i / ns, p = r.y + r.height * j / np;
                float l = SeptumFace(s, p) + depth + (disp != null ? disp(s, p) : 0f);
                pc.verts.Add(new Vector3(l, s, p) * 0.001f);
            }
            for (int j = 0; j < np; j++)
            for (int i = 0; i < ns; i++)
            {
                if (hole.HasValue)
                {
                    float s = r.x + r.width * (i + 0.5f) / ns, p = r.y + r.height * (j + 0.5f) / np;
                    if (hole.Value.Contains(new Vector2(s, p))) continue;
                }
                int v00 = baseIdx + j * (ns + 1) + i, v10 = v00 + 1, v01 = v00 + ns + 1, v11 = v01 + 1;
                // (P, S) order so the face normal points to -L (into the right cavity).
                pc.tris.Add(v00); pc.tris.Add(v01); pc.tris.Add(v10);
                pc.tris.Add(v10); pc.tris.Add(v01); pc.tris.Add(v11);
            }
        }

        /// <summary>Cut cartilage edge around the resection window (from the cartilage face to the far mucoperichondrium).</summary>
        static void WindowRim(Piece pc, Rect w, float step)
        {
            var ring = new List<Vector2>();
            int ns = Mathf.CeilToInt(w.width / step), np = Mathf.CeilToInt(w.height / step);
            for (int i = 0; i < ns; i++) ring.Add(new Vector2(w.xMin + w.width * i / ns, w.yMin));
            for (int j = 0; j < np; j++) ring.Add(new Vector2(w.xMax, w.yMin + w.height * j / np));
            for (int i = ns; i > 0; i--) ring.Add(new Vector2(w.xMin + w.width * i / ns, w.yMax));
            for (int j = np; j > 0; j--) ring.Add(new Vector2(w.xMin, w.yMin + w.height * j / np));
            for (int k = 0; k < ring.Count; k++)
            {
                var a = ring[k]; var b = ring[(k + 1) % ring.Count];
                var a0 = new Vector3(SeptumFace(a.x, a.y) + CartilageDepth, a.x, a.y) * 0.001f;
                var b0 = new Vector3(SeptumFace(b.x, b.y) + CartilageDepth, b.x, b.y) * 0.001f;
                var a1 = new Vector3(SeptumFace(a.x, a.y) + ContralateralDepth, a.x, a.y) * 0.001f;
                var b1 = new Vector3(SeptumFace(b.x, b.y) + ContralateralDepth, b.x, b.y) * 0.001f;
                AddTri(pc, a0, b0, a1);
                AddTri(pc, b0, b1, a1);
            }
        }

        /// <summary>Short dark hairs on the vestibular skin, pointing toward the nostril opening.</summary>
        static void Vibrissae(Piece pc)
        {
            var rnd = new System.Random(7);
            Vector3 a = new Vector3(-7.5f, -27f, -92f), b = new Vector3(-8f, -19f, -68f);
            var axis = (b - a).normalized;
            var u = Vector3.Cross(axis, Vector3.right).normalized; var v = Vector3.Cross(axis, u);
            for (int h = 0; h < 70; h++)
            {
                float t = 0.12f + 0.3f * (float)rnd.NextDouble(), ang = (float)(rnd.NextDouble() * Math.PI * 2);
                float r = Mathf.Lerp(4.2f, 5.5f, t) * 0.92f;
                var radial = u * Mathf.Cos(ang) + v * Mathf.Sin(ang);
                var root = a + (b - a) * t + radial * r;
                var dir = (-radial * 0.8f - axis * 0.6f).normalized;
                float len = 2.5f + 3f * (float)rnd.NextDouble(), w = 0.07f;
                var tip = root + dir * len + radial * 0.3f;
                var s1 = Vector3.Cross(dir, radial).normalized * w; var s2 = radial * w;
                Vector3 r0 = (root + s1) * 0.001f, r1 = (root - s1 + s2) * 0.001f, r2 = (root - s1 - s2) * 0.001f, tp = tip * 0.001f;
                AddTri(pc, r0, r1, tp); AddTri(pc, r1, r2, tp); AddTri(pc, r2, r0, tp);
            }
        }
    }
}
