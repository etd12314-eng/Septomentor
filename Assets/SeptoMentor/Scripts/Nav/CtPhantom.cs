using System.Collections.Generic;
using UnityEngine;

namespace SeptoMentor.Nav
{
    /// <summary>
    /// Synthetic head CT ("phantom") so the navigation mode works without patient data and can
    /// be rehearsed on a mannequin. Simplified, NOT anatomically accurate: skin and face, nose, nasal
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
        /// Approximate signed distance to the skin (negative inside), mm. The head is a loft of horizontal
        /// superellipses (forehead, face, jaw, neck: <see cref="Head"/>), with the face relief (brows, orbits and
        /// closed lids, cheeks, lips, chin) and creases displaced onto it, and ears. The nose is built like a
        /// sculptor's: a wedge-shaped dorsum with a rounded ridge, the lower nose standing on a tilted nasal base,
        /// a lobule with two tip-defining domes, the alar lobules swept around the nostrils, a columella, and
        /// teardrop nostrils that open into NasalAnatomy's vestibule.
        /// </summary>
        public static float SkinSdf(Vector3 p)
        {
            var m = new Vector3(Mathf.Abs(p.x), p.y, p.z);   // mirrored for paired features
            float d = Loft(p);
            float front = Mathf.Clamp01((LoftCentre - 20 - p.z) / 30);
            if (front > 0) d -= (Relief(m.x, p.y) + Creases(m.x, p.y)) * front;
            if (m.x < 32 && p.y > -44 && p.y < 45 && p.z < -55)
                d = SMin(d, Nose(p, m), Mathf.Clamp(3.5f + (p.y + 16) * 0.5f, 3.5f, 8f) + 1.5f * Mathf.Clamp01((-26 - p.y) / 3));
            if (m.x > 50 && p.y > -25 && p.y < 62 && p.z > -15 && p.z < 45)
            {
                float ear = EllR(m, new Vector3(71, 21, 16), new Vector3(5, 29, 16), 0, -12);                     // auricle, tilted back
                ear = SMin(ear, EllR(m, new Vector3(73.5f, 22, 16), new Vector3(3.2f, 30, 17.5f), 0, -12), 2);   // helix
                ear = SSub(ear, EllR(m, new Vector3(77, 23, 15), new Vector3(3.5f, 19, 10.5f), 0, -12), 2);      // scapha, fossa
                ear = SSub(ear, Ell(m, new Vector3(75.5f, 14, 12), new Vector3(4.5f, 9, 7)), 2.5f);              // concha
                ear = SMin(ear, Ell(m, new Vector3(71.5f, -4, 13), new Vector3(4, 7, 7)), 3);                     // lobule
                ear = SMin(ear, Ell(m, new Vector3(73, 14, 4), new Vector3(4, 5, 3.5f)), 1.5f);                   // tragus
                d = SMin(d, ear, 3);
            }
            if (m.x < 16 && p.y > -36 && p.y < -14 && p.z < -75) d = SSub(d, Nostril(m), 0.8f);
            return d;
        }

        // Head loft: per height S, a horizontal superellipse |L/A|^n + |(P - c)/R|^n = 1 with c = LoftCentre,
        // R = c - front (anterior) or back - c (posterior). Columns: S, front P (midline, before relief), back P, A, n.
        const float LoftCentre = 10f;
        static readonly float[,] Head =
        {
            { -160, -42, 62, 50, 2.0f }, { -100, -44, 62, 48, 2.0f }, { -88, -50, 64, 46, 2.0f }, { -82, -63, 65, 44, 2.1f },
            { -76, -75, 66, 44, 2.2f }, { -70, -79, 66, 46, 2.2f }, { -58, -75, 66, 50, 2.3f }, { -45, -79, 68, 55, 2.4f },
            { -32, -80, 72, 59, 2.4f }, { -20, -78, 78, 62, 2.4f }, { -5, -77, 85, 65, 2.4f }, { 10, -78, 92, 68, 2.3f },
            { 22, -80, 100, 70, 2.2f }, { 30, -83, 106, 72, 2.2f }, { 42, -88, 110, 74, 2.1f }, { 55, -87, 110, 75, 2.0f },
            { 75, -82, 106, 72, 2.0f }, { 95, -72, 97, 66, 2.0f }, { 110, -55, 80, 55, 2.0f }, { 120, -28, 52, 36, 2.0f },
            { 125, 0, 14, 8, 2.0f }, { 127, 8, 10, 1, 2.0f },
        };
        static float[][] _headSlopes;

        static float Loft(Vector3 p)
        {
            if (_headSlopes == null)
            {
                var s = new float[4][];
                for (int c = 1; c <= 4; c++) s[c - 1] = PchipSlopes(Head, c);
                _headSlopes = s;
            }
            float f = Pchip(Head, 1, _headSlopes[0], p.y), b = Pchip(Head, 2, _headSlopes[1], p.y);
            float a = Pchip(Head, 3, _headSlopes[2], p.y), n = Pchip(Head, 4, _headSlopes[3], p.y);
            float r = p.z < LoftCentre ? LoftCentre - f : b - LoftCentre;
            float x = Mathf.Abs(p.x) / a + 1e-6f, z = Mathf.Abs(p.z - LoftCentre) / r + 1e-6f;
            float rho = Mathf.Pow(Mathf.Pow(x, n) + Mathf.Pow(z, n), 1 / n) + 1e-9f;
            float k = Mathf.Pow(rho, 1 - n);
            float gx = k * Mathf.Pow(x, n - 1) / a, gz = k * Mathf.Pow(z, n - 1) / r;
            return Mathf.Max((rho - 1) / Mathf.Sqrt(gx * gx + gz * gz + 1e-12f), p.y - 127);
        }

        /// <summary>Monotone cubic (Fritsch-Carlson) slopes for column <paramref name="c"/> of a table keyed by column 0.</summary>
        static float[] PchipSlopes(float[,] t, int c)
        {
            int n = t.GetLength(0);
            var m = new float[n];
            float Dl(int i) => (t[i + 1, c] - t[i, c]) / (t[i + 1, 0] - t[i, 0]);
            m[0] = Dl(0); m[n - 1] = Dl(n - 2);
            for (int i = 1; i < n - 1; i++)
            {
                float d0 = Dl(i - 1), d1 = Dl(i);
                if (d0 * d1 <= 0) { m[i] = 0; continue; }
                float h0 = t[i, 0] - t[i - 1, 0], h1 = t[i + 1, 0] - t[i, 0];
                float w1 = 2 * h1 + h0, w2 = h1 + 2 * h0;
                m[i] = (w1 + w2) / (w1 / d0 + w2 / d1);
            }
            return m;
        }

        static float Pchip(float[,] t, int c, float[] m, float s)
        {
            int n = t.GetLength(0);
            s = Mathf.Clamp(s, t[0, 0], t[n - 1, 0]);
            int i = 0;
            while (i < n - 2 && s > t[i + 1, 0]) i++;
            float h = t[i + 1, 0] - t[i, 0], u = (s - t[i, 0]) / h, u2 = u * u, u3 = u2 * u;
            return (2 * u3 - 3 * u2 + 1) * t[i, c] + (u3 - 2 * u2 + u) * h * m[i] + (-2 * u3 + 3 * u2) * t[i + 1, c] + (u3 - u2) * h * m[i + 1];
        }

        static float G(float l, float s, float cl, float cs, float sl, float ss) => Mathf.Exp(-Sq((l - cl) / sl) - Sq((s - cs) / ss));

        /// <summary>Face relief (mm outward) at (|L|, S).</summary>
        static float Relief(float a, float s)
        {
            float r = 3.5f * G(a, s, 23, 42, 12, 4.5f) + 1.5f * G(a, s, 0, 41, 8, 5) + 2 * G(a, s, 25, 70, 18, 15);   // brow, glabella, frontal eminence
            r += -7 * G(a, s, 30, 29, 14, 10) + 9.5f * G(a, s, 31, 28, 11, 8) - 3 * G(a, s, 19, 29, 4, 5);              // orbit, closed lid over the globe, medial canthus
            r += 3 * G(a, s, 30, 0, 15, 13) + 2.5f * G(a, s, 46, 12, 10, 8) - 4 * G(a, s, 63, 32, 8, 14);               // cheek, zygoma, temple
            r += 1.5f * G(a, s, 27, -30, 8, 10);                                                                        // cheek over the fold
            r += 5.2f * G(a, s, 0, -41, 14, 3.3f) + 0.9f * G(a, s, 4, -39.5f, 3, 1.5f);                                 // upper lip, Cupid's bow
            r += 0.7f * G(a, s, 4.5f, -36, 2.4f, 4) - 0.4f * G(a, s, 0, -36, 2, 4);                                     // philtral columns, groove
            r += 5 * G(a, s, 0, -50, 13, 3.6f) - 2.5f * G(a, s, 0, -59, 14, 3) + 3.5f * G(a, s, 0, -71, 14, 7);         // lower lip, mentolabial sulcus, chin
            return r;
        }

        static float LidLine(float a) => 27.6f + 0.006f * Sq(a - 31);
        static float MouthLine(float a) => -45.2f - 0.0035f * a * a;

        static float Creases(float a, float s)
        {
            float c = -0.6f * Mathf.Exp(-Sq((s - LidLine(a)) / 0.5f)) * Mathf.Exp(-Sq(Sq((a - 31) / 8.5f)));              // closed lid line
            c += -0.35f * Mathf.Exp(-Sq((s - 34.5f - 0.008f * Sq(a - 31)) / 0.9f)) * Mathf.Exp(-Sq(Sq((a - 31) / 9)));     // upper lid crease
            c += -1.7f * Mathf.Exp(-Sq((s - MouthLine(a)) / 0.75f)) * Mathf.Exp(-Mathf.Pow(a / 23, 6));                     // mouth line
            float lx = 17.5f + Mathf.Clamp01((s + 24) / -24f) * 9.5f;                                                      // nasolabial fold
            c += -0.5f * Mathf.Exp(-Sq((a - lx) / 2.6f)) * Mathf.Exp(-Sq(Sq((s + 34) / 10)));
            return c;
        }

        static float Nose(Vector3 p, Vector3 m)
        {
            float n = Dorsum(p);
            float lb = SMin(LowerNose(p), Ala(m), 1.2f);
            lb = SMin(lb, Ell(m, new Vector3(13.8f, -23, -77.5f), new Vector3(3.6f, 6, 4.5f)), 2);                    // alar base, set into the cheek
            n = SMin(n, lb, 5);
            n = SMin(n, Ell(p, new Vector3(0, -16, -95), new Vector3(8.6f, 8.6f, 8.5f)), 6);                           // lobule
            n = SMin(n, Ell(m, new Vector3(4, -14, -97.5f), new Vector3(4, 4.6f, 6)), 2.5f);                            // domes
            return SMin(n, Capsule(p, new Vector3(0, -24.8f, -95), new Vector3(0, -29, -82.5f), 3, 2.9f), 2.5f);        // columella
        }

        // Dorsal line radix -> supratip; the wedge's sidewalls open at 40 -> 33 degrees from the depth axis,
        // its ridge is rounded with 3.5 -> 8 mm.
        static readonly Vector3 DorsumA = new Vector3(0, 31, -79.5f), DorsumB = new Vector3(0, -13, -99.3f);

        static float Dorsum(Vector3 p)
        {
            var ab = DorsumB - DorsumA; float len = ab.magnitude; var w = ab / len;
            var dep = new Vector3(0, -w.z, w.y); if (dep.z < 0) dep = -dep;   // into the face, in the sagittal plane
            var q = p - DorsumA;
            float t = Vector3.Dot(q, w) / len, tc = Mathf.Clamp01(t);
            float r = Mathf.Lerp(3.5f, 8f, tc), th = Mathf.Lerp(40f, 33f, tc) * Mathf.Deg2Rad;
            float u = Mathf.Abs(q.x), depth = Vector3.Dot(q, dep), v = depth - r;
            float dx = Mathf.Sin(th), dy = Mathf.Cos(th);
            float k = Mathf.Max(u * dx + v * dy, 0), ex = u - k * dx, ey = v - k * dy;
            float dist = Mathf.Sqrt(ex * ex + ey * ey);
            float d = (u * dy - v * dx < 0 ? -dist : dist) - r;
            d = Mathf.Max(Mathf.Max(d, depth - 20), -t * len);
            return -SMin(-d, -(t - 1) * len, 6);
        }

        /// <summary>Lower nose: rounded triangle in the basal view (tip -> alar bases) standing on the nasal base,
        /// which tilts up toward the tip.</summary>
        static float LowerNose(Vector3 p)
        {
            float u = Mathf.Clamp01((p.y + 29) / 21);
            float ay = -97.5f + 2.5f * u, bx = 11 - 5 * u, by = -77f, n = 2.2f;
            float x = Mathf.Abs(p.x) / bx + 1e-6f, z = Mathf.Max(by - p.z, 0) / (by - ay) + 1e-6f;
            float rho = Mathf.Pow(Mathf.Pow(x, n) + Mathf.Pow(z, n), 1 / n), k = Mathf.Pow(rho, 1 - n);
            float gx = k * Mathf.Pow(x, n - 1) / bx, gz = k * Mathf.Pow(z, n - 1) / (by - ay);
            float d2 = Mathf.Max((rho - 1) / Mathf.Sqrt(gx * gx + gz * gz), p.z - by - 6);
            float h = Mathf.Max(-29.6f - 0.16f * (p.z + 80) - p.y, p.y + 8);
            return Mathf.Min(Mathf.Max(d2, h), 0) + Mathf.Sqrt(Sq(Mathf.Max(d2, 0)) + Sq(Mathf.Max(h, 0)));
        }

        // Alar lobule and rim: a tube swept around the nostril from the soft triangle to the alar base (L, S, P, radius),
        // taller than wide (S scaled by AlaTall).
        static readonly Vector4[] AlaPath =
        {
            new Vector4(5.8f, -23.8f, -95.2f, 2.4f), new Vector4(9.4f, -24.4f, -93f, 2.7f), new Vector4(12.7f, -23.6f, -89.4f, 3.4f),
            new Vector4(14.3f, -23.2f, -84.4f, 3.7f), new Vector4(14f, -25f, -80f, 3.3f),
        };
        const float AlaTall = 1.35f;
        static Vector4[] _alaTube;

        static float Ala(Vector3 m)
        {
            if (_alaTube == null) _alaTube = CatmullRom(AlaPath, 5);
            var q = new Vector3(m.x, m.y / AlaTall, m.z);
            float d = 1e9f;
            for (int i = 0; i < _alaTube.Length - 1; i++)
            {
                Vector4 a = _alaTube[i], b = _alaTube[i + 1];
                float c = Capsule(q, new Vector3(a.x, a.y / AlaTall, a.z), new Vector3(b.x, b.y / AlaTall, b.z), a.w, b.w);
                d = i == 0 ? c : SMin(d, c, 0.8f);
            }
            return d;
        }

        static Vector4[] CatmullRom(Vector4[] k, int perSegment)
        {
            var o = new List<Vector4>();
            for (int i = 0; i < k.Length - 1; i++)
            {
                Vector4 a = k[Mathf.Max(i - 1, 0)], b = k[i], c = k[i + 1], d = k[Mathf.Min(i + 2, k.Length - 1)];
                for (int j = 0; j < perSegment; j++)
                {
                    float t = j / (float)perSegment, t2 = t * t, t3 = t2 * t;
                    o.Add(0.5f * (2 * b + (c - a) * t + (2 * a - 5 * b + 4 * c - d) * t2 + (3 * b - a - 3 * c + d) * t3));
                }
            }
            o.Add(k[k.Length - 1]);
            return o.ToArray();
        }

        /// <summary>Teardrop nostril on the nasal base (wide end posterolateral) and the vestibule above it.</summary>
        static float Nostril(Vector3 m)
        {
            float c = Capsule(m, new Vector3(9.2f, -28.4f, -84), new Vector3(6, -26.4f, -89.5f), 2.9f, 1.7f);
            return SMin(c, Capsule(m, new Vector3(7.6f, -27.4f, -87), new Vector3(7.3f, -22.8f, -83.5f), 2.9f, 2.7f), 2);
        }

        /// <summary>
        /// Skin colour multiplier at a skin point (L, S, P mm): lip vermilion, rosy nose and cheeks, eyebrows,
        /// lash line, darker reddish nostril and vestibule lining, mouth line.
        /// </summary>
        public static Color SkinTint(Vector3 p)
        {
            var m = new Vector3(Mathf.Abs(p.x), p.y, p.z);
            float a = m.x, s = p.y;
            var c = Color.white;
            float faceFront = Mathf.Clamp01((-p.z - 62f) / 6f);
            // Vermilion: from the mouth line up to the (Cupid's bow) border, and down over the lower lip.
            float mouth = MouthLine(a), border = -38.9f + 0.6f * Mathf.Exp(-Sq((a - 4) / 2.5f)) - 0.006f * a * a;
            float upper = Mathf.Clamp01((border - s) / 0.8f + 0.5f) * Mathf.Clamp01((s - mouth) / 0.8f + 0.5f);
            float lower = Mathf.Clamp01((mouth - s) / 0.8f + 0.5f) * Mathf.Clamp01((s - (-54f + 0.008f * a * a)) / 2f + 0.5f);
            float lipW = Mathf.Clamp01((24 - a) / 4f);
            c = Color.Lerp(c, new Color(0.86f, 0.6f, 0.64f), Mathf.Max(upper, lower) * lipW * faceFront);
            float rosy = Mathf.Exp(-(Sq(a / 9) + Sq((s + 15) / 9))) + 0.6f * Mathf.Exp(-(Sq((a - 30) / 20) + Sq((s + 12) / 20)));
            c *= Color.Lerp(Color.white, new Color(1f, 0.92f, 0.91f), Mathf.Clamp01(rosy) * faceFront);
            float brow = Mathf.Exp(-(Sq(s - 43.5f + 0.004f * Sq(a - 26)) / 6f + Sq(a - 26) / 170f)) * faceFront;
            float hairs = 0.55f + 0.45f * NasalAnatomy.Noise(a * 1.4f, s * 0.35f, p.z * 0.35f);
            c = Color.Lerp(c, new Color(0.33f, 0.26f, 0.23f), Mathf.Clamp01(brow * 1.3f) * hairs);
            float lash = Mathf.Exp(-Sq((s - LidLine(a)) / 0.6f)) * Mathf.Exp(-Sq(Sq((a - 31) / 9.5f)));
            c = Color.Lerp(c, new Color(0.3f, 0.24f, 0.22f), 0.8f * lash * faceFront);
            float nostril = Nostril(m), deep = Mathf.Clamp01((s + 27f) / 6f);
            c = Color.Lerp(c, new Color(0.7f, 0.46f, 0.46f) * (1f - 0.45f * deep), Mathf.Clamp01(1f - nostril / 1.5f));
            c = Color.Lerp(c, new Color(0.45f, 0.3f, 0.3f), Mathf.Exp(-Sq((s - mouth) / 0.6f)) * lipW * faceFront);
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
