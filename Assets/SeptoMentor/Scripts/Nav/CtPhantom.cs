using System.Collections.Generic;
using UnityEngine;

namespace SeptoMentor.Nav
{
    /// <summary>
    /// Synthetic head CT ("phantom") so the navigation mode works without patient data and can
    /// be rehearsed on a mannequin. Simplified, NOT anatomically accurate: skin, nose, nasal
    /// cavity with a septum deviated to the patient's right, turbinates, maxillary sinuses,
    /// palate, skull base and cranium. Also returns a label volume (septal cartilage / bone /
    /// turbinates) and the case landmarks, all in the same LPS frame.
    /// Phantom coordinates are written as (L, S, P) mm, i.e. model space * 1000.
    /// </summary>
    public static class CtPhantom
    {
        public const byte LabelCartilage = 1, LabelSeptumBone = 2, LabelTurbinate = 3;

        const float Step = 1.25f;
        static readonly Vector3 Min = new Vector3(-80, -95, -110), Max = new Vector3(80, 125, 135);   // (L, S, P)

        // L-strut corners (L, S, P): anterior septal angle, dorsal end of the cartilage, caudal-inferior end.
        static readonly Vector3 Asa = new Vector3(0, -5, -80), Dorsal = new Vector3(0, 22, -45), Ans = new Vector3(0, -27, -78);

        public static CaseData Generate()
        {
            int nx = Mathf.RoundToInt((Max.x - Min.x) / Step), ny = Mathf.RoundToInt((Max.z - Min.z) / Step), nz = Mathf.RoundToInt((Max.y - Min.y) / Step);
            // Voxel axes in LPS: i -> L, j -> P, k -> S.
            var vol = new CtVolume(nx, ny, nz)
            {
                spacing = new Vector3(Step, Step, Step),
                origin = new Vector3(Min.x, Min.z, Min.y),
                description = "Synthetic head phantom (not patient data)",
            };
            var labels = new byte[vol.hu.Length];
            for (int k = 0; k < nz; k++)
            for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                var p = new Vector3(Min.x + i * Step, Min.y + k * Step, Min.z + j * Step);   // (L, S, P)
                int n = vol.Index(i, j, k);
                // Partial-volume blend over ~1 voxel at the skin so the extracted surface is smooth, not stepped.
                float skin = SkinSdf(p);
                vol.hu[n] = (short)(skin > 1f ? -1000 : Mathf.Lerp(Hu(p, skin, out labels[n]), -1000, Mathf.Clamp01((skin + 1f) * 0.5f)));
            }

            var data = new CaseData { name = "Phantom (synthetic)", volume = vol, labels = labels, isPhantom = true };
            data.labelNames[LabelCartilage] = "Septal cartilage";
            data.labelNames[LabelSeptumBone] = "Bony septum";
            data.labelNames[LabelTurbinate] = "Turbinates";

            // Skin landmarks: first skin hit of an anterior->posterior ray at (L, S).
            AddSkin(data, vol, "Nasion", 0, 27);
            data.landmarks.Add(new Landmark("Pronasale", NoseTip(vol)));
            AddSkin(data, vol, "Subnasale", 0, -31);
            AddSkin(data, vol, "Endocanthion R", -17, 31);
            AddSkin(data, vol, "Endocanthion L", 17, 31);
            data.landmarks.Add(new Landmark("SeptumASA", LspToModel(Asa)));
            data.landmarks.Add(new Landmark("SeptumDorsal", LspToModel(Dorsal)));
            data.landmarks.Add(new Landmark("SeptumANS", LspToModel(Ans)));
            data.landmarks.Add(new Landmark("SkullBase", LspToModel(new Vector3(0, 22, -25))));
            return data;
        }

        static Vector3 LspToModel(Vector3 lsp) => lsp * 0.001f;   // (L, S, P) mm == model mm

        static void AddSkin(CaseData d, CtVolume v, string name, float l, float s)
        {
            for (float pp = Min.z; pp < 0; pp += 0.5f)
            {
                var m = LspToModel(new Vector3(l, s, pp));
                if (v.SampleModel(m) > -300) { d.landmarks.Add(new Landmark(name, m)); return; }
            }
        }

        static Vector3 NoseTip(CtVolume v)
        {
            var best = Vector3.zero; float bestP = float.MaxValue;
            for (float s = -30; s <= 10; s += 0.5f)
                for (float pp = Min.z; pp < bestP && pp < 0; pp += 0.5f)
                    if (v.SampleModel(LspToModel(new Vector3(0, s, pp))) > -300) { bestP = pp; best = new Vector3(0, s, pp); break; }
            return LspToModel(best);
        }

        static float In(Vector3 p, Vector3 c, Vector3 r)
        {
            var d = new Vector3((p.x - c.x) / r.x, (p.y - c.y) / r.y, (p.z - c.z) / r.z);
            return d.sqrMagnitude;   // < 1 inside
        }

        /// <summary>CT value (HU) at a phantom point (L, S, P mm), with the same partial-volume skin blend as Generate.</summary>
        public static float SampleHu(Vector3 p)
        {
            float skin = SkinSdf(p);
            return skin > 1f ? -1000f : Mathf.Lerp(Hu(p, skin, out _), -1000f, Mathf.Clamp01((skin + 1f) * 0.5f));
        }

        /// <summary>
        /// Approximate signed distance to the skin (negative inside), mm: an adult head from smoothly blended
        /// primitives - skull, cheeks, jaw, closed eyes with lid lines, ears, a nose with dorsum, tip domes,
        /// alar wings, columella and teardrop nostrils that open into NasalAnatomy's vestibule, and lips.
        /// </summary>
        public static float SkinSdf(Vector3 p)
        {
            var m = new Vector3(Mathf.Abs(p.x), p.y, p.z);   // mirrored for paired features
            // Skull and facial masses.
            float d = Ell(p, new Vector3(0, 36, 8), new Vector3(72, 88, 98));                                   // cranium
            d = SMin(d, Ell(p, new Vector3(0, -4, -26), new Vector3(60, 66, 58)), 14);                          // midface
            d = SSub(d, Ell(m, new Vector3(66, 30, -40), new Vector3(10, 18, 20)), 10);                         // temples
            d = SMin(d, Capsule(m, new Vector3(46, -50, -6), new Vector3(16, -74, -52), 8, 7), 14);             // mandible
            d = SMin(d, Ell(p, new Vector3(0, -73, -58), new Vector3(18, 11, 13)), 10);                         // chin
            d = SMin(d, Ell(p, new Vector3(0, -66, -30), new Vector3(36, 14, 32)), 14);                         // submental
            d = SSub(d, Capsule(p, new Vector3(-12, -62, -74), new Vector3(12, -62, -74), 3, 3), 5);            // mentolabial sulcus
            d = SMin(d, Ell(m, new Vector3(44, 8, -52), new Vector3(15, 10, 17)), 14);                          // zygoma
            d = SMin(d, Ell(m, new Vector3(30, -12, -64), new Vector3(16, 14, 12)), 12);                        // cheeks
            d = SMin(d, Ell(m, new Vector3(24, 41, -74), new Vector3(19, 5.5f, 9)), 8);                         // brow ridge
            // Eyes: socket, closed lids with the lid line and the upper lid crease.
            d = SSub(d, Ell(m, new Vector3(31, 29, -84), new Vector3(15, 10, 14)), 5);
            d = SMin(d, Ell(m, new Vector3(31, 28, -66), new Vector3(12.5f, 9.5f, 11.5f)), 2.5f);
            d = SSub(d, Capsule(m, new Vector3(21, 27.2f, -77.3f), new Vector3(41, 27.6f, -72), 0.35f, 0.35f), 0.6f);
            d = SSub(d, Capsule(m, new Vector3(21, 33.5f, -75), new Vector3(41, 34, -70), 0.6f, 0.6f), 1.5f);
            float neck = (Mathf.Sqrt(Sq(p.x / 50) + Sq((p.z - 20) / 52)) - 1) * 50;
            d = SMin(d, Mathf.Max(neck, p.y + 15), 16);
            float ear = SSub(Ell(m, new Vector3(72, 22, 14), new Vector3(7, 29, 17)), Ell(m, new Vector3(78, 18, 14), new Vector3(4, 13, 8)), 3);   // helix + concha
            d = SMin(d, ear, 3);

            // Nose: dorsum, lobule with tip-defining domes, alar wings around the nostrils, columella.
            float nose = Capsule(p, new Vector3(0, 30, -80), new Vector3(0, -6, -93), 5f, 6.5f);
            nose = SMin(nose, Ell(p, new Vector3(0, -14, -91), new Vector3(8, 7.5f, 7)), 4);
            nose = SMin(nose, Ell(m, new Vector3(3.4f, -12.5f, -95), new Vector3(4.3f, 5, 4.5f)), 3);
            nose = SMin(nose, EllR(m, new Vector3(11.5f, -23, -85), new Vector3(4.6f, 5, 10), 28, 0), 5);
            nose = SMin(nose, Capsule(p, new Vector3(0, -21, -92), new Vector3(0, -29.5f, -82), 3f, 3.3f), 2.5f);
            d = SMin(d, nose, 5);
            d = SSub(d, Capsule(m, new Vector3(20.5f, -27, -75.5f), new Vector3(26, -50, -68), 0.6f, 0.5f), 5);  // nasolabial folds

            // Lips: upper lip with Cupid's bow, lower lip, mouth line, philtrum.
            d = SMin(d, EllR(p, new Vector3(0, -39, -74), new Vector3(17, 5.5f, 7), 0, -15), 4);
            d = SMin(d, Ell(m, new Vector3(4, -42.5f, -77.5f), new Vector3(5, 2.6f, 3.2f)), 2);
            d = SMin(d, EllR(p, new Vector3(0, -50, -71.5f), new Vector3(15, 5.5f, 7.5f), 0, 10), 4);
            d = SSub(d, EllR(p, new Vector3(0, -45.2f, -84), new Vector3(20, 0.7f, 12), 0, 0), 1.2f);
            d = SSub(d, Capsule(p, new Vector3(0, -31.5f, -81.5f), new Vector3(0, -38, -81.5f), 1.4f, 1.6f), 1.5f);

            // Nostrils: teardrop openings on the nasal base, continuing up into the vestibule (as NasalAnatomy's).
            float nostril = EllR(m, new Vector3(7.6f, -28.6f, -88), new Vector3(3f, 3.2f, 6.3f), -22, 0);
            nostril = SMin(nostril, Capsule(m, new Vector3(7.6f, -27.5f, -90), new Vector3(8, -19, -68), 3.2f, 4.6f), 2);
            return SSub(d, nostril, 0.8f);
        }

        /// <summary>
        /// Skin colour multiplier at a skin point (L, S, P mm): lip vermilion, rosy nose tip and cheeks, eyebrows,
        /// lash line, darker reddish nostril and vestibule lining.
        /// </summary>
        public static Color SkinTint(Vector3 p)
        {
            var m = new Vector3(Mathf.Abs(p.x), p.y, p.z);
            var c = Color.white;
            float lip = Mathf.Min(EllR(p, new Vector3(0, -39, -74), new Vector3(17, 5.5f, 7), 0, -15),
                                  EllR(p, new Vector3(0, -50, -71.5f), new Vector3(15, 5.5f, 7.5f), 0, 10));
            lip = Mathf.Min(lip, Ell(m, new Vector3(4, -42.5f, -77.5f), new Vector3(5, 2.6f, 3.2f)));
            c = Color.Lerp(c, new Color(0.86f, 0.6f, 0.64f), Mathf.Clamp01(1f - (lip + 0.5f) / 2f) * Mathf.Clamp01((-p.z - 70f) / 4f));
            float rosy = Mathf.Exp(-(Sq(p.x) + Sq(p.y + 14) + Sq(p.z + 95)) / 150f) + 0.6f * Mathf.Exp(-(Sq(m.x - 30) + Sq(p.y + 12) + Sq(p.z + 72)) / 400f);
            c *= Color.Lerp(Color.white, new Color(1f, 0.92f, 0.91f), Mathf.Clamp01(rosy));
            float brow = Mathf.Exp(-(Sq(p.y - 43.5f + 0.004f * Sq(m.x - 26)) / 6f + Sq(m.x - 26) / 170f)) * Mathf.Clamp01((-p.z - 62f) / 6f);
            float hairs = 0.55f + 0.45f * NasalAnatomy.Noise(m.x * 1.4f, p.y * 0.35f, p.z * 0.35f);
            c = Color.Lerp(c, new Color(0.33f, 0.26f, 0.23f), Mathf.Clamp01(brow * 1.3f) * hairs);
            float lash = Capsule(m, new Vector3(21, 27.2f, -77.3f), new Vector3(41, 27.6f, -72), 0.35f, 0.35f);
            c = Color.Lerp(c, new Color(0.3f, 0.24f, 0.22f), 0.8f * Mathf.Clamp01(1f - lash / 1.2f));
            float nostril = Mathf.Min(EllR(m, new Vector3(7.6f, -28.6f, -88), new Vector3(3f, 3.2f, 6.3f), -22, 0),
                                      Capsule(m, new Vector3(7.6f, -27.5f, -90), new Vector3(8, -19, -68), 3.2f, 4.6f));
            float deep = Mathf.Clamp01((p.y + 27f) / 6f);
            c = Color.Lerp(c, new Color(0.7f, 0.46f, 0.46f) * (1f - 0.45f * deep), Mathf.Clamp01(1f - nostril / 1.5f));
            float mouth = EllR(p, new Vector3(0, -45.2f, -84), new Vector3(20, 0.7f, 12), 0, 0);
            c = Color.Lerp(c, new Color(0.45f, 0.3f, 0.3f), Mathf.Clamp01(1f - mouth / 1.2f));
            c.a = 1;
            return c;
        }

        /// <summary>Ellipsoid turned by yaw (about S) then pitch (about L), degrees.</summary>
        static float EllR(Vector3 p, Vector3 c, Vector3 r, float yaw, float pitch)
        {
            var q = p - c;
            float cy = Mathf.Cos(yaw * Mathf.Deg2Rad), sy = Mathf.Sin(yaw * Mathf.Deg2Rad);
            float x = q.x * cy - q.z * sy, z = q.x * sy + q.z * cy;
            float cp = Mathf.Cos(pitch * Mathf.Deg2Rad), sp = Mathf.Sin(pitch * Mathf.Deg2Rad);
            return Ell(new Vector3(x, q.y * cp - z * sp, q.y * sp + z * cp), Vector3.zero, r);
        }

        static float Sq(float x) => x * x;

        /// <summary>Ellipsoid signed distance (bound-corrected approximation), mm.</summary>
        static float Ell(Vector3 p, Vector3 c, Vector3 r)
        {
            var q = p - c;
            float k0 = new Vector3(q.x / r.x, q.y / r.y, q.z / r.z).magnitude;
            float k1 = new Vector3(q.x / (r.x * r.x), q.y / (r.y * r.y), q.z / (r.z * r.z)).magnitude + 1e-9f;
            return k0 * (k0 - 1) / k1;
        }

        /// <summary>Capsule from a (radius ra) to b (radius rb).</summary>
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

        static float SSub(float a, float b, float k) => -SMin(-a, b, k);   // a minus b

        static float SeptumDeviation(float s, float pp) =>
            -5f * Mathf.Exp(-((s + 8) * (s + 8) / 128f + (pp + 45) * (pp + 45) / 288f))      // C-shaped deviation to the right
            - 3f * Mathf.Exp(-((s + 24) * (s + 24) / 8f + (pp + 30) * (pp + 30) / 200f));   // basal spur

        static float CavityTop(float pp) => pp >= -45 ? 22f : Asa.y + (pp - Asa.z) * (Dorsal.y - Asa.y) / (Dorsal.z - Asa.z);

        static int Hu(Vector3 p, float skin, out byte label)
        {
            label = 0;
            float l = p.x, s = p.y, pp = p.z;
            float al = Mathf.Abs(l);

            // Air spaces.
            bool inNasalBox = al < 15 && s > -27 && s < CavityTop(pp) && pp > -84 && pp < 8 && skin < -2.5f;
            bool nostril = DistSeg(new Vector3(al, s, pp), new Vector3(7, -26, -86), new Vector3(7, -16, -74)) < 4f;
            bool pharynx = al < 14 && s > -27 && s < 10 && pp >= 8 && pp < 32;
            bool maxSinus = In(new Vector3(al, s, pp), new Vector3(32, -10, -35), new Vector3(12, 14, 16)) < 1;

            // Septum (inside the nasal box only).
            if (inNasalBox && pp > Ans.z - 2)
            {
                float dev = SeptumDeviation(s, pp);
                bool cartilage = pp < -40 && s > -25;
                float half = cartilage ? 1.3f : 1.0f;
                if (Mathf.Abs(l - dev) < half && pp > -80 + Mathf.Max(0, -27 - s))
                {
                    label = cartilage ? LabelCartilage : LabelSeptumBone;
                    return cartilage ? 160 : 1100;
                }
            }

            // Turbinates (soft tissue with bony core) project into the cavity.
            float inf = In(new Vector3(al, s, pp), new Vector3(13, -18, -25), new Vector3(4.5f, 7, 32));
            float mid = In(new Vector3(al, s, pp), new Vector3(11, 3, -15), new Vector3(3.5f, 5, 22));
            if (inf < 1 || mid < 1)
            {
                label = LabelTurbinate;
                bool core = In(new Vector3(al, s, pp), inf < 1 ? new Vector3(14, -18, -25) : new Vector3(12, 3, -15),
                                inf < 1 ? new Vector3(1.5f, 4, 28) : new Vector3(1.2f, 3, 18)) < 1;
                return core ? 700 : 45;
            }

            if (inNasalBox || nostril || pharynx) return -1000;
            if (maxSinus) return -1000;

            // Bone.
            bool lateralWall = al >= 15 && al < 17 && s > -29 && s < 23 && pp > -62 && pp < 8;
            bool palate = s > -32 && s <= -27 && al < 18 && pp > -64 && pp < 8;
            bool skullBase = s >= 22 && s < 25 && al < 13 && pp > -56 && pp < 8;
            bool sinusWall = In(new Vector3(al, s, pp), new Vector3(32, -10, -35), new Vector3(14, 16, 18)) < 1;
            bool cranium = skin < -5 && skin > -11 && s > 24;
            bool nasalBones = skin < -2.5f && skin > -5.5f && pp < -62 && s > 12 && al < 10;
            if (lateralWall || palate || skullBase || sinusWall || cranium || nasalBones) return 1200;

            // Orbits: globe + fat.
            float orb = In(new Vector3(al, s, pp), new Vector3(32, 30, -48), new Vector3(15, 15, 17));
            if (orb < 1) return In(new Vector3(al, s, pp), new Vector3(32, 30, -52), new Vector3(12, 12, 12)) < 1 ? 15 : -90;

            return s > 25 && skin < -11 ? 35 : 45;   // brain / soft tissue
        }

        static float DistSeg(Vector3 p, Vector3 a, Vector3 b)
        {
            var ab = b - a; float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude);
            return Vector3.Distance(p, a + ab * t);
        }
    }

    public struct Landmark
    {
        public string name;
        public Vector3 model;   // model space (m)
        public Landmark(string n, Vector3 m) { name = n; model = m; }
    }

    /// <summary>Everything loaded for one patient case.</summary>
    public class CaseData
    {
        public string name;
        public CtVolume volume;
        public byte[] labels;                                  // optional, same grid as volume
        public readonly Dictionary<byte, string> labelNames = new Dictionary<byte, string>();
        public readonly List<Landmark> landmarks = new List<Landmark>();
        public readonly List<KeyValuePair<string, Mesh>> extraMeshes = new List<KeyValuePair<string, Mesh>>();   // model space
        public bool isPhantom;
        public string warning = "";

        public bool TryGetLandmark(string name, out Vector3 model)
        {
            foreach (var l in landmarks) if (string.Equals(l.name, name, System.StringComparison.OrdinalIgnoreCase)) { model = l.model; return true; }
            model = default; return false;
        }
    }
}
