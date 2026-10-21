using SeptoMentor.Nav;
using UnityEngine;

namespace SeptoMentor
{
    /// <summary>
    /// The patient on the OR table, meshed from signed-distance fields (baked by SceneBuilder; OperatingRoom falls back
    /// to meshing the head at runtime). Both meshes are in OperatingRoom's head frame: x = patient's right,
    /// y = superior, z = anterior, metres; phantom (L, S, P) mm = (-x, y, -z) * 1000.
    ///   Head  - CtPhantom.SkinSdf (face, nose, nostrils into the endoscopic anatomy), vertex-coloured skin.
    ///   Drape - the body (shoulders, chest, tucked arms, pelvis, legs, feet) under a surgical drape that tents
    ///           over it and spreads across the table, ending below the clavicles.
    /// </summary>
    public static class PatientModel
    {
        public static Mesh BuildHead(float stepMm = 1.25f)
        {
            // Grid over (L, S, P) mm: L -80..80, S -150..125 (neck runs under the drape), P -110..135.
            int nx = Mathf.CeilToInt(160 / stepMm), ny = Mathf.CeilToInt(275 / stepMm), nz = Mathf.CeilToInt(245 / stepMm);
            float st = stepMm;
            var toHead = Matrix4x4.TRS(new Vector3(0.08f, -0.15f, 0.11f), Quaternion.identity, new Vector3(-st, st, -st) * 0.001f);
            var sn = SurfaceNets.Extract((i, j, k) => -CtPhantom.SkinSdf(new Vector3(-80 + st * i, -150 + st * j, -110 + st * k)), 0f,
                                         Vector3Int.zero, new Vector3Int(nx, ny, nz), 1, toHead).Smooth(3);
            var mesh = sn.ToMesh("PatientHead");
            var colors = new Color[sn.vertices.Count];
            for (int v = 0; v < colors.Length; v++) colors[v] = CtPhantom.SkinTint(ToPhantom(sn.vertices[v]));
            mesh.colors = colors;
            return mesh;
        }

        public static Mesh BuildDrape(float stepMm = 12f)
        {
            // Grid over (L, S, P) mm: L -420..420, S -1960..-100, P -260..170.
            int nx = Mathf.CeilToInt(840 / stepMm), ny = Mathf.CeilToInt(1860 / stepMm), nz = Mathf.CeilToInt(430 / stepMm);
            float st = stepMm;
            var toHead = Matrix4x4.TRS(new Vector3(0.42f, -1.96f, 0.26f), Quaternion.identity, new Vector3(-st, st, -st) * 0.001f);
            var sn = SurfaceNets.Extract((i, j, k) => -DrapeSdf(new Vector3(-420 + st * i, -1960 + st * j, -260 + st * k)), 0f,
                                         Vector3Int.zero, new Vector3Int(nx, ny, nz), 1, toHead).Smooth(3);
            return sn.ToMesh("PatientDrape");
        }

        static Vector3 ToPhantom(Vector3 head) => new Vector3(-head.x, head.y, -head.z) * 1000f;

        /// <summary>Supine adult body (phantom frame, mm; the table top is at P = +130) under a drape.</summary>
        public static float DrapeSdf(Vector3 p)
        {
            var m = new Vector3(Mathf.Abs(p.x), p.y, p.z);
            float b = Ell(p, new Vector3(0, -330, 15), new Vector3(185, 190, 105));                                  // chest
            b = SMin(b, Capsule(m, new Vector3(150, -205, 25), new Vector3(60, -170, 10), 55, 45), 60);           // shoulders
            b = SMin(b, Ell(p, new Vector3(0, -600, 30), new Vector3(165, 220, 92)), 70);                         // abdomen
            b = SMin(b, Ell(p, new Vector3(0, -850, 35), new Vector3(180, 130, 95)), 60);                         // pelvis
            b = SMin(b, Capsule(m, new Vector3(100, -930, 35), new Vector3(85, -1380, 45), 68, 52), 40);          // thighs
            b = SMin(b, Capsule(m, new Vector3(85, -1380, 45), new Vector3(78, -1760, 60), 52, 38), 30);          // shins
            b = SMin(b, Capsule(m, new Vector3(78, -1790, 40), new Vector3(80, -1810, -90), 38, 32), 25);         // feet, toes up
            b = SMin(b, Capsule(m, new Vector3(222, -240, 35), new Vector3(232, -520, 45), 44, 38), 40);          // upper arms, tucked
            b = SMin(b, Capsule(m, new Vector3(232, -520, 45), new Vector3(226, -780, 45), 38, 30), 30);          // forearms
            float d = b - 22f;                                                                                   // drape slack
            float sheet = Mathf.Max(Mathf.Max(m.x - 370f, Mathf.Abs(p.z - 128f) - 4f), Mathf.Max(-1950f - p.y, p.y + 150f));
            d = SMin(d, sheet, 70);
            d += 3f * Mathf.Sin(p.y / 37f + Mathf.Sin(m.x / 53f) * 2f) * Mathf.Clamp01(-(p.z - 100f) / 100f);   // folds
            return -SMin(-d, -(p.y + 140f), 20);                                                                 // ends below the clavicles
        }

        static float Ell(Vector3 p, Vector3 c, Vector3 r)
        {
            var q = p - c;
            float k0 = new Vector3(q.x / r.x, q.y / r.y, q.z / r.z).magnitude;
            float k1 = new Vector3(q.x / (r.x * r.x), q.y / (r.y * r.y), q.z / (r.z * r.z)).magnitude + 1e-9f;
            return k0 * (k0 - 1) / k1;
        }

        static float Capsule(Vector3 p, Vector3 a, Vector3 b, float ra, float rb)
        {
            var ab = b - a; float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude);
            return Vector3.Distance(p, a + ab * t) - Mathf.Lerp(ra, rb, t);
        }

        static float SMin(float a, float b, float k)
        {
            float h = Mathf.Clamp01(0.5f + 0.5f * (b - a) / k);
            return Mathf.Lerp(b, a, h) - k * h * (1 - h);
        }
    }
}
