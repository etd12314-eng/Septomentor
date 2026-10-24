using System;
using System.Collections.Generic;
using SeptoMentor.Nav;
using UnityEngine;

namespace SeptoMentor
{
    /// <summary>
    /// Procedural right nasal cavity for endoscopic septoplasty training (synthetic, NOT patient data):
    /// vestibule with vibrissae, internal valve, an almost straight septum with an isolated septal spur
    /// (chondro-vomerine ridge) pointing into the operated cavity, inferior and middle turbinates, nasal floor, narrowing roof, choana and nasopharynx
    /// with Eustachian tori. Built as a signed-distance field (mm) and meshed with SurfaceNets.
    ///
    /// The septal flap area is made of separate pieces that SeptoplastySim changes as the resident operates:
    /// Killian-type incision strip (cut segment by segment), mucoperichondrial flap (elevated off the
    /// bluish-white cartilage at runtime), resection window cartilage (bitten out chunk by chunk, keeping a
    /// ~10 mm L-strut) with the contralateral mucoperichondrium behind it.
    ///
    /// The operated cavity is the patient's LEFT side (left-nostril approach). The RIGHT cavity is modelled too, as the
    /// mirror image of the same walls (the spur does not reach into it), so the endoscope can examine both sides;
    /// it has no surgical pieces. The field is written for the right side
    /// ("work frame") and mirrored at the end: L_patient = Mirror * L_work. Everything public that takes or returns
    /// millimetres (Depth, SeptumFace, the flap constants) is in the work frame.
    /// Frame of the meshes: the CT phantom's (L, S, P), in metres in the VirtualPatient's local space
    /// (+X patient-left, +Y superior, +Z posterior), so the nostril sits exactly where the OR head's is.
    /// Surface detail: superior turbinate, uncinate process and agger nasi, septal swell body, Hasner's valve,
    /// Eustachian orifices, adenoid tissue, corrugated turbinates and mucosal micro-relief; per-vertex colour
    /// (Little's area, congested turbinates, paler upper walls, mucus, pale thin lining in sinuses and cells), ambient
    /// occlusion in its alpha, vessel / wetness weights in uv and wall thinness (translucent folds) in uv2.
    /// Sinuses (FESS): pyramidal maxillary sinus sharing the lateral nasal wall, fan-shaped frontal sinus over an hourglass
    /// frontal recess, box-shaped sphenoid sinus with the sella, carotid and optic prominences, ethmoid labyrinth under a
    /// sloping skull base; oval ostia through thin bone.
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
            public readonly List<Color> colors = new List<Color>();
            public readonly List<Vector2> uvs = new List<Vector2>();   // x = vessel density, y = wetness (1 = default)
            public readonly List<Vector2> uvs2 = new List<Vector2>();  // x = thinness (0 thick .. 1 a fold the light shines through)
            public Look look;
            internal readonly Dictionary<int, int> remap = new Dictionary<int, int>();   // shared SurfaceNets vertices
        }

        public enum Look { Mucosa, Turbinate, Skin, Cartilage, Plain, Sinus }

        /// <summary>Patient side of the cavity: -1 = left (the field is written for the right side).</summary>
        public const float Mirror = -1f;
        public static Vector3 ToWork(Vector3 v) => new Vector3(Mirror * v.x, v.y, v.z);
        public static Vector3 FromWork(Vector3 v) => new Vector3(Mirror * v.x, v.y, v.z);

        public const int AllPhases = 0x1F;
        public static int Mask(params SurgicalPhase[] phases) { int m = 0; foreach (var p in phases) m |= 1 << (int)p; return m; }

        /// <summary>Autopilot path (m, patient frame): from the left nostril up and back through the common meatus (>= 2 mm clearance).</summary>
        public static readonly Vector3 PathStart = FromWork(new Vector3(-7.5f, -25f, -86f)) * 0.001f, PathEnd = FromWork(new Vector3(-5.5f, -6f, -30f)) * 0.001f;

        // Flap area (S, P mm) on the septal face, the incision strip in front of it (~24 mm Killian incision about
        // 1 cm behind the caudal septal edge), and the excision window over the spur (~9 x 22 mm).
        public const float FlapP0 = -66f, FlapP1 = -30f, FlapS0 = -24.5f, FlapS1 = -0.5f, IncisionP0 = -67.2f;
        public const float WinP0 = -56f, WinP1 = -34f, WinS0 = -21.5f, WinS1 = -12.5f;
        /// <summary>Bites above / in front of these cut into the 10 mm L-strut (dorsum at S ~22, caudal end at P ~-76).</summary>
        public const float DorsalStrutS = 12f, CaudalStrutP = -65f;
        public const float CartilageDepth = 0.9f, ContralateralDepth = 2.6f;
        /// <summary>The incision strip is cut segment by segment (along S); the window is resected chunk by chunk.</summary>
        public const int IncisionSegments = 6, ChunksS = 2, ChunksP = 4;

        static readonly Color Mucosa = new Color(0.90f, 0.55f, 0.56f), Turbinate = new Color(0.82f, 0.42f, 0.45f),
                              Floor = new Color(0.88f, 0.52f, 0.52f), Pharynx = new Color(0.78f, 0.38f, 0.40f),
                              Skin = new Color(0.86f, 0.66f, 0.58f), Cartilage = new Color(0.82f, 0.80f, 0.86f),
                              Hair = new Color(0.12f, 0.08f, 0.06f), Contra = new Color(0.86f, 0.50f, 0.52f),
                              SinusLining = new Color(0.93f, 0.66f, 0.62f);   // thin, paler sinus mucosa

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

        /// <summary>Septal midline offset (mm, negative = toward the operated cavity): only a slight bow.</summary>
        public static float Deviation(float s, float p) =>
            -0.8f * Mathf.Exp(-((s + 6) * (s + 6) / 110f + (p + 45) * (p + 45) / 300f));

        /// <summary>
        /// Isolated septal spur, mm toward the operated cavity: a sharp horizontal ridge at the chondro-vomerine
        /// junction, rising slightly backward, spindle-shaped along P, its crest ~2 mm short of the inferior turbinate.
        /// </summary>
        public static float Spur(float s, float p)
        {
            float ds = (s - (-17f + 0.1f * (p + 45f))) / 2.6f, dp = (p + 45f) / 9f;
            return 4f * Mathf.Exp(-ds * ds - dp * dp);
        }

        /// <summary>Mucosal face of the septum on the operated side (L, mm, work frame), spur included.</summary>
        public static float SeptumFace(float s, float p) => Deviation(s, p) - 1.8f - SwellBody(s, p) - Spur(s, p);

        /// <summary>Septal swell body (erectile mucosa opposite the middle turbinate), mm toward the cavity.</summary>
        static float SwellBody(float s, float p) => 1.5f * Mathf.Exp(-((s - 7) * (s - 7) / 28f + (p + 47) * (p + 47) / 70f));

        // Value noise (deterministic, thread-safe) for surface relief and colour variation.
        static float Hash(int i, int j, int k)
        {
            unchecked
            {
                int h = (i * 73856093) ^ (j * 19349663) ^ (k * 83492791);
                h = (h ^ (h >> 13)) * 1274126177;
                return ((h ^ (h >> 16)) & 0xffff) / 65535f;
            }
        }

        public static float Noise(float x, float y, float z)
        {
            int i = Mathf.FloorToInt(x), j = Mathf.FloorToInt(y), k = Mathf.FloorToInt(z);
            float fx = x - i, fy = y - j, fz = z - k;
            fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy); fz = fz * fz * (3 - 2 * fz);
            float a = Mathf.Lerp(Mathf.Lerp(Hash(i, j, k), Hash(i + 1, j, k), fx), Mathf.Lerp(Hash(i, j + 1, k), Hash(i + 1, j + 1, k), fx), fy);
            float b = Mathf.Lerp(Mathf.Lerp(Hash(i, j, k + 1), Hash(i + 1, j, k + 1), fx), Mathf.Lerp(Hash(i, j + 1, k + 1), Hash(i + 1, j + 1, k + 1), fx), fy);
            return Mathf.Lerp(a, b, fz);
        }

        static float PosteriorEdge(float s) => 8f - (s + 27f) * 0.25f;   // free edge of the vomer

        static float Roof(float p) => p < -45 ? Mathf.Min(22f, (p + 80f) * 22f / 35f) : p > -5 ? 22f - (p + 5f) * 0.9f : 22f;

        public enum Wall
        {
            Septum, Lateral, Floor, Roof, Vestibule, Pharynx, InfTurb, MidTurb, Torus, SupTurb, Uncinate, Adenoid,
            Agger, Bulla, BasalLam,                                          // FESS tissue (bony lamellae with mucosa)
            MaxSinus, BullaCell, AggerCell, EthCells, Sphenoid, Frontal, Ostia,  // FESS air spaces and their openings
            Count,
        }

        // ---------------------------------------------------------------- paranasal sinuses (FESS), work frame mm

        /// <summary>Uncinate process free edge, from its attachment under the agger nasi down and back to the inferior turbinate.</summary>
        static readonly Vector3[] UncinateEdge =
        {
            new Vector3(-16.4f, 14.5f, -50.5f), new Vector3(-16.6f, 10f, -48.5f), new Vector3(-16.8f, 5f, -46f),
            new Vector3(-17.0f, 0.5f, -43f), new Vector3(-17.2f, -3.5f, -39.5f), new Vector3(-17.4f, -6.5f, -35.5f),
        };
        // Frontal recess: from the middle meatus behind the agger nasi up and forward to the frontal ostium (narrowest), then
        // flaring into the funnel of the sinus floor - an hourglass.
        static readonly Vector3[] FrontalRecess =
        {
            new Vector3(-14.2f, 14f, -44f), new Vector3(-14.2f, 19f, -49f), new Vector3(-13.4f, 23.4f, -54.5f),
            new Vector3(-12.6f, 26.5f, -59.5f),
        };
        static readonly float[] FrontalRecessR = { 2.6f, 2.4f, 1.6f, 3f };
        const float RecessFlat = 1.35f;
        static readonly Vector3[] FrontalRecessScaled = Array.ConvertAll(FrontalRecess, v => new Vector3(v.x * RecessFlat, v.y, v.z));
        // Web from the free edge to its attachment on the lateral wall, as a plate in a frame turned about S
        // (L' = 0.66 L - 0.75 P, P' = -0.75 L - 0.66 P), so the plate is thin along L'.
        const float WebNl = 0.66f, WebNp = -0.75f, WebTl = -0.75f, WebTp = -0.66f;
        static readonly Vector3[] UncEdgeScaled, UncWebScaled;
        const float UncEdgeHalf = 1.4f, UncEdgeThick = 0.5f, UncWebHalf = 2.3f, UncWebThick = 0.5f;

        static NasalAnatomy()
        {
            var edge = CatmullRom(UncinateEdge, 4);
            UncEdgeScaled = new Vector3[edge.Length];
            for (int i = 0; i < edge.Length; i++) UncEdgeScaled[i] = new Vector3(edge[i].x * UncEdgeHalf / UncEdgeThick, edge[i].y, edge[i].z);
            var mid = new Vector3[UncinateEdge.Length];
            for (int i = 0; i < mid.Length; i++)
            {
                var q = UncinateEdge[i] + new Vector3(-1.7f, 0, -1.5f);
                mid[i] = new Vector3(WebNl * q.x + WebNp * q.z, q.y, WebTl * q.x + WebTp * q.z);
            }
            var web = CatmullRom(mid, 4);
            UncWebScaled = new Vector3[web.Length];
            for (int i = 0; i < web.Length; i++) UncWebScaled[i] = new Vector3(web[i].x * UncWebHalf / UncWebThick, web[i].y, web[i].z);
        }

        static Vector3[] CatmullRom(Vector3[] k, int per)
        {
            var o = new List<Vector3>();
            for (int i = 0; i + 1 < k.Length; i++)
            {
                Vector3 p0 = i > 0 ? k[i - 1] : 2 * k[0] - k[1], p1 = k[i], p2 = k[i + 1], p3 = i + 2 < k.Length ? k[i + 2] : 2 * k[i + 1] - k[i];
                for (int j = 0; j < per; j++)
                {
                    float t = j / (float)per;
                    o.Add(0.5f * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t * t + (-p0 + 3 * p1 - 3 * p2 + p3) * t * t * t));
                }
            }
            o.Add(k[k.Length - 1]);
            return o.ToArray();
        }

        /// <summary>Capsule chain through pre-scaled points (L already multiplied by the plate's half / thick): a thin curved plate.</summary>
        static float FlatChain(float l, float s, float p, Vector3[] scaled, float half, float thick)
        {
            float f = half / thick;
            var q = new Vector3(l * f, s, p);
            float d = float.MaxValue;
            for (int i = 0; i + 1 < scaled.Length; i++)
            {
                float c = Capsule(q, scaled[i], scaled[i + 1], half, half);
                d = d == float.MaxValue ? c : SMin(d, c, 2f);   // wide blend: no ridges where the segments meet
            }
            return d / f;
        }

        static float Uncinate(float l, float s, float p)
        {
            if (l < -24f || l > -12f || s < -11f || s > 19f || p < -58f || p > -28f) return 20f;
            float edge = FlatChain(l, s, p, UncEdgeScaled, UncEdgeHalf, UncEdgeThick);
            float web = FlatChain(WebNl * l + WebNp * p, s, WebTl * l + WebTp * p, UncWebScaled, UncWebHalf, UncWebThick);
            return SMin(edge, web, 0.8f);
        }

        /// <summary>Sphenoethmoidal recess: part of the nose, medial to the superior turbinate, in front of the sphenoid ostium.</summary>
        static float Recess(float l, float s, float p) => Ell(l, s, p, -5f, 15f, -1f, 3f, 5.5f, 3.8f);

        // ---- ethmoid labyrinth: a honeycomb of air cells with thin bony septa (Voronoi cells of a jittered seed grid)
        const float CellSpacing = 4.8f, CellJitter = 0.35f, SeptumHalf = 0.4f;
        static readonly Vector3 CellOrigin = new Vector3(-26f, 0f, -52f);

        static float Hash01(int i, int j, int k, int salt)
        {
            float x = Mathf.Sin(i * 127.1f + j * 311.7f + k * 74.7f + salt * 19.19f) * 43758.5453f;
            return x - Mathf.Floor(x);
        }

        static Vector3 CellSeed(int i, int j, int k) =>
            CellOrigin + CellSpacing * (new Vector3(i, j, k) + Vector3.one * 0.5f) +
            CellSpacing * CellJitter * (new Vector3(Hash01(i, j, k, 1), Hash01(i, j, k, 2), Hash01(i, j, k, 3)) * 2f - Vector3.one);

        /// <summary>Distances to the nearest two cell seeds and the nearest cell's id.</summary>
        static void NearestCells(Vector3 q, out float d1, out float d2, out int id)
        {
            var g = (q - CellOrigin) / CellSpacing;
            int gi = Mathf.FloorToInt(g.x), gj = Mathf.FloorToInt(g.y), gk = Mathf.FloorToInt(g.z);
            d1 = d2 = 1e9f; id = 0;
            for (int i = gi - 1; i <= gi + 1; i++)
            for (int j = gj - 1; j <= gj + 1; j++)
            for (int k = gk - 1; k <= gk + 1; k++)
            {
                float d = (q - CellSeed(i, j, k)).magnitude;
                if (d < d1) { d2 = d1; d1 = d; id = (i + 50) * 10000 + (j + 50) * 100 + (k + 50); }
                else if (d < d2) d2 = d;
            }
        }

        /// <summary>The id of the ethmoid cell nearest to <paramref name="q"/> (side work frame, mm).</summary>
        public static int CellId(Vector3 q) { NearestCells(q, out _, out _, out int id); return id; }

        static float Lat0(float s, float p)
        {
            float meatus = Window(s, -8.5f, 14f, 1.8f) * Window(p, -56f, -14f, 3f);
            return -15f - 4.5f * meatus + Mathf.Max(0, s - 15f) * 0.45f * (1f - meatus);
        }

        /// <summary>
        /// Ethmoid cells (negative inside a cell): the labyrinth lateral to the middle meatus and above it, between the
        /// lamina papyracea (L -25.5) and the skull base, kept clear of the bulla, agger nasi, basal lamella (except as a
        /// wall), the openings and the other sinuses. c must already hold those walls.
        /// </summary>
        static float EthCells(float l, float s, float p, float[] c)
        {
            if (l < -29f || l > -13f || s < 0.5f || s > 27f || p < -51.5f || p > -2.5f) return 50f;
            // Lamina papyracea and fovea ethmoidalis with a slightly scalloped bony surface.
            float bump = 0.6f * (Noise(s / 4f, p / 4f, 3.3f) - 0.5f);
            float r = Mathf.Max(Mathf.Max(l - (Lat0(s, p) - 1.1f), LaminaPapyracea(p) + 0.5f + bump - l),
                                Mathf.Max(Mathf.Max(s - (SkullBase(p) - 0.5f + bump), 1f - s), Mathf.Max(-51f - p, p + 3f)));
            r = Mathf.Max(r, Mathf.Max(0.9f - c[(int)Wall.Bulla], 0.9f - c[(int)Wall.Agger]));
            r = Mathf.Max(r, Mathf.Max(1.1f - Mathf.Abs(p + 17f), 0.9f - c[(int)Wall.Ostia]));
            r = Mathf.Max(r, Mathf.Max(0.9f - c[(int)Wall.MaxSinus], Mathf.Max(0.9f - c[(int)Wall.Frontal], 0.9f - c[(int)Wall.Sphenoid])));
            if (r > 3f) return r;
            NearestCells(new Vector3(l, s, p), out float d1, out float d2, out _);
            return Mathf.Max(r, SeptumHalf - (d2 - d1) * 0.5f);
        }

        /// <summary>
        /// The openings of the sinuses: short oval channels through thin bone (not drilled holes), each widening into its sinus.
        /// Maxillary: high on the medial antral wall, at the floor of the ethmoid infundibulum behind the uncinate process,
        /// oval along P, with a small posterior fontanelle (accessory) ostium below the bulla. Bulla: the retrobullar opening.
        /// Sphenoid: a vertical oval on the anterior wall, medial to the superior turbinate, above the sphenoethmoidal recess.
        /// Frontal: the hourglass frontal recess behind the agger nasi, narrowest at the ostium, flaring into the sinus floor.
        /// </summary>
        static float Ostia(float l, float s, float p)
        {
            var q = new Vector3(l, s, p);
            float o = SMin(Ell(l, s, p, -20.5f, -2.6f, -38.8f, 2.4f, 1.5f, 2.9f), Ell(l, s, p, -22.6f, -3.6f, -39f, 1.6f, 2.6f, 3.6f), 1f);  // maxillary natural ostium
            o = Mathf.Min(o, Ell(l, s, p, -20.4f, -6.4f, -31f, 2.2f, 0.9f, 1.4f));                                     // accessory (fontanelle) ostium
            o = Mathf.Min(o, Capsule(q, new Vector3(-17.4f, 10.6f, -25.6f), new Vector3(-19.4f, 9.2f, -27.6f), 1f, 1f));  // bulla ostium
            o = Mathf.Min(o, SMin(Recess(l, s, p), Ell(l, s, p, -5f, 18f, 3.4f, 1.5f, 2.4f, 2f), 1f));                  // sphenoid ostium
            if (s > 10f && p < -38f && l < -8f)
            {
                // Evaluated with L stretched so the recess is a slit (narrow mediolaterally, wide front to back).
                var qs = new Vector3(l * RecessFlat, s, p);
                float fr = float.MaxValue;
                for (int i = 0; i + 1 < FrontalRecess.Length; i++)
                {
                    float c = Capsule(qs, FrontalRecessScaled[i], FrontalRecessScaled[i + 1], FrontalRecessR[i], FrontalRecessR[i + 1]);
                    fr = fr == float.MaxValue ? c : SMin(fr, c, 1.2f);
                }
                o = Mathf.Min(o, fr / RecessFlat);   // frontal recess up to the frontal ostium
            }
            return o;
        }

        /// <summary>Ostia the resident has to find (operated side, work frame mm), with the radius that counts as "there".</summary>
        public static readonly (string key, string name, Vector3 at, float radius)[] OstiumTargets =
        {
            ("maxillary", "maxillary sinus ostium", new Vector3(-20.2f, -2.8f, -38.8f), 3.2f),
            ("ethmoid", "ethmoid bulla ostium", new Vector3(-17.6f, 10.4f, -25.8f), 3f),
            ("sphenoid", "sphenoid sinus ostium", new Vector3(-5.2f, 17.6f, 2f), 3f),
            ("frontal", "frontal recess / ostium", new Vector3(-13.4f, 23.4f, -54.5f), 3.5f),
        };

        /// <summary>Lateral limit of the ethmoid labyrinth (lamina papyracea, the orbit beyond), L mm: wider behind.</summary>
        public static float LaminaPapyracea(float p) => -25f - 0.045f * (p + 50f);

        /// <summary>Ethmoid roof (fovea ethmoidalis) above the labyrinth, S mm: falls backward, above the cribriform plate (S 22).</summary>
        public static float SkullBase(float p) => 25.5f - 0.06f * (p + 50f);

        /// <summary>
        /// Maxillary sinus (mm, negative inside): a pyramid on its side. Its medial wall is the lateral nasal wall itself (2 mm of
        /// bone and mucosa, stepping out with the middle meatus); the roof is the orbital floor with the infraorbital canal
        /// ridge; the anterior (facial) wall dips in at the canine fossa; the floor bowls below the nasal floor over the
        /// molar roots; the apex runs into the zygomatic recess; the posterior wall bulges forward (tuberosity).
        /// </summary>
        public static float MaxillarySinus(float l, float s, float p)
        {
            if (l > -12f || l < -54f || s < -40f || s > 13f || p < -72f || p > -14f) return 8f;
            float medial = l - (Lat0(s, p) - 2.2f);
            float lateral = -44f + 0.03f * (s + 7f) * (s + 7f) - l;
            float floor = -29f + 0.006f * (p + 44f) * (p + 44f) + 0.01f * (l + 31f) * (l + 31f) - s;
            float roofS = 2.2f + 0.05f * (-20f - l) - 1.5f * Mathf.Exp(-(l + 31f) * (l + 31f) / 5f) * Window(p, -60f, -28f, 2f);
            float roof = s - roofS;
            float front = -61f + 0.02f * (l + 31f) * (l + 31f) + 4f * Mathf.Exp(-((l + 34f) * (l + 34f) / 50f + (s + 12f) * (s + 12f) / 45f)) - p;
            float back = p - (-25f - 0.03f * (l + 29f) * (l + 29f) - 0.05f * (s + 10f) * (s + 10f) / 4f);
            float d = SMax(SMax(medial, lateral, 4f), SMax(floor, roof, 4f), 3f);
            d = SMax(d, SMax(front, back, 4f), 3f);
            return d - 0.5f * (Noise(l / 5f, s / 5f, p / 5f) - 0.5f);
        }

        /// <summary>
        /// Frontal sinus (mm, negative inside): a fan in the frontal bone - narrow at its floor, where the ostium sits
        /// posteromedially, widening upward and laterally over the orbit; thin intersinus septum at the midline, anterior table
        /// under the forehead, posterior table toward the anterior cranial fossa, scalloped dome.
        /// </summary>
        public static float FrontalSinus(float l, float s, float p)
        {
            if (l > 4f || l < -42f || s < 17f || p < -92f || p > -48f) return 8f;
            float medial = l + 1f;
            float lateral = Mathf.Max(-30f, -15f - 1f * (s - 26f)) - l;
            float floor = 25.4f + 0.035f * (l + 12f) * (l + 12f) - s;
            float top = s - (48f - 0.012f * (l + 11f) * (l + 11f) - 2.2f * Mathf.Abs(Mathf.Sin(l * 0.32f)));
            float front = Mathf.Max(-79f, -71.5f - 0.5f * (s - 26f)) - p;
            float back = p - (-58.5f - 0.2f * (s - 26f));
            float d = SMax(SMax(medial, lateral, 3f), SMax(floor, top, 4f), 3f);
            return SMax(d, SMax(front, back, 3f), 2.5f) - 0.6f * (Noise(l / 4f, s / 4f, p / 4f) - 0.5f);
        }

        /// <summary>
        /// Sphenoid sinus (mm, negative inside): a rounded box in the sphenoid body behind the sphenoethmoidal recess, its floor
        /// following the nasopharyngeal roof down and back; the intersinus septum at the midline (with the other side), the
        /// sella turcica bulging into the posterosuperior roof, the carotid (posterior) and optic (superior) prominences on the
        /// lateral wall with the opticocarotid recess between them.
        /// </summary>
        public static float SphenoidSinus(float l, float s, float p)
        {
            if (l > 3f || l < -24f || s < 0f || s > 38f || p < -4f || p > 32f) return 8f;
            float medial = l + 0.8f, lateral = -16f - l;
            float floor = Mathf.Max(8f, 15.8f - 0.55f * (p - 5f)) - s, roof = s - 29f;
            float front = 4.4f - p, back = p - 24f;
            float d = SMax(SMax(medial, lateral, 3f), SMax(floor, roof, 3f), 3f);
            d = SMax(d, SMax(front, back, 3f), 3f);
            var q = new Vector3(l, s, p);
            float sella = Ell(l, s, p, 0f, 32.5f, 18.5f, 9f, 6.5f, 6.5f);
            float carotid = Capsule(q, new Vector3(-16.6f, 9f, 21f), new Vector3(-16.6f, 25f, 14f), 2.8f, 2.6f);
            float optic = Capsule(q, new Vector3(-17.2f, 25.5f, 5f), new Vector3(-15f, 26.5f, 20f), 2.2f, 2f);
            d = SMax(d, -sella, 2f);
            d = SMax(d, -SMin(carotid, optic, 1f), 1.5f);
            return d - 0.4f * (Noise(l / 4f, s / 4f, p / 4f) - 0.5f);
        }

        /// <summary>
        /// Middle turbinate (mm, negative inside): a curved plate of bone and mucosa - its medial face convex toward the
        /// septum, the lower free edge thickened and curling laterally over the middle meatus, a rounded head in front and a
        /// tapering tail - hanging from the vertical lamella that runs up to the cribriform plate.
        /// </summary>
        static float MiddleTurbinate(float l, float s, float p)
        {
            // Coronal section: a capsule along a curved centreline, its lower end rounded (not cut flat).
            float sBot = -4.5f + 0.0045f * (p + 28f) * (p + 28f);
            float head = Mathf.Exp(-(p + 41f) * (p + 41f) / 40f);
            float HalfW(float t) => 1.5f + 0.5f * Mathf.Exp(-(t - 2.5f) * (t - 2.5f) / 8f) + 0.9f * head;
            float Centre(float t) { float c = Mathf.Max(0f, 5.5f - t); return -9.6f - 0.14f * c * c + 0.4f * head; }
            float tc = Mathf.Max(s - sBot, HalfW(0f));
            float plate = Mathf.Sqrt((l - Centre(tc)) * (l - Centre(tc)) + (s - sBot - tc) * (s - sBot - tc)) - HalfW(tc);
            float front = -43.5f - 0.25f * (s + 3f) - p;
            float back = p - (-6f - 0.3f * (s - sBot));
            float d = SMax(plate, SMax(SMax(front, back, 3f), s - 9f, 2f), 3f);
            float lam = SMax(Mathf.Abs(l + 9.6f) - 1.2f, SMax(Mathf.Max(2f - s, s - 24f), Mathf.Max(-40f - p, p), 1.5f), 1.5f);
            return SMin(d, lam, 1.5f);
        }

        /// <summary>
        /// Wall distances of one cavity. side = +1: the operated cavity; -1: the other one, evaluated mirrored
        /// (pass -L) so the same walls apply, its septal face on the concave side of the deviation.
        /// </summary>
        static void Components(float l, float s, float p, float[] c, float side = 1f)
        {
            float face = side * Deviation(s, p) - 1.8f;
            c[(int)Wall.Septum] = l - (face - SwellBody(s, p) - (side > 0 ? Spur(s, p) : 0f));
            // Lateral wall: inferior meatus at -15; the middle meatus recessed laterally (uncinate, bulla and the sinus
            // openings stand in it); narrowing toward the olfactory cleft above. In front it converges on the septum into
            // the internal nasal valve (limen nasi): a slit narrow at the top, wider along the floor.
            float lat0 = Lat0(s, p);
            float valveW = Mathf.Clamp(9f - 0.3f * (s + 26f), 1f, 12f) * (1f - Mathf.Clamp01((-77f - p) / 6f));
            float toValve = Mathf.Pow(Mathf.Clamp01((-56f - p) / 20f), 1.5f);
            c[(int)Wall.Lateral] = Mathf.Lerp(lat0, face - valveW, toValve) - l;
            float ant = Mathf.Max(0, -60 - p);
            c[(int)Wall.Floor] = -27f + 0.004f * ant * ant - s;
            c[(int)Wall.Roof] = s - Roof(p);
            c[(int)Wall.Vestibule] = Vestibule(l, s, p);
            float np = SMax(SMax(Mathf.Abs(l) - 15f, p - 30f, 4), SMax(-25f - s, s - (14f - (p - 5f) * 0.6f), 4), 4);
            c[(int)Wall.Pharynx] = SMax(np, PosteriorEdge(s) - p - 2f, 2);
            float rough = Noise(l / 2.2f, s / 2.2f, p / 3.5f) - 0.5f;   // corrugated turbinate mucosa
            // Inferior turbinate: its lower edge hangs free over the inferior meatus (carved out under its lateral half).
            float it = SMin(Ell(l, s, p, -15.5f, -17.5f, -20f, 7, 7.5f, 33), Ell(l, s, p, -14.8f, -16f, -44f, 7, 7.8f, 12), 3);
            it = SMax(it, -Ell(l, s, p, -15.6f, -23.6f, -33f, 2.8f, 3.4f, 24f), 1.2f);
            c[(int)Wall.InfTurb] = it - 0.7f * rough;
            c[(int)Wall.MidTurb] = MiddleTurbinate(l, s, p) - 0.4f * rough;
            // Basal lamella: the middle turbinate's attachment turning laterally to the lamina papyracea (anterior / posterior ethmoid).
            c[(int)Wall.BasalLam] = SMax(Mathf.Abs(p + 17f) - 0.7f, SMax(Mathf.Max(-l - 24f, l + 10f), Mathf.Max(1.5f - s, s - 22f), 1.2f), 1.2f);
            c[(int)Wall.Torus] = Ell(Mathf.Abs(l), s, p, 14, -8, 20, 4, 6, 6);
            c[(int)Wall.SupTurb] = Ell(l, s, p, -11.5f, 15.5f, -4f, 2.0f, 3.0f, 9f) - 0.3f * rough;
            c[(int)Wall.Uncinate] = Uncinate(l, s, p);
            c[(int)Wall.Agger] = Ell(l, s, p, -18f, 15f, -52.5f, 3.4f, 4.2f, 4.4f);
            c[(int)Wall.Bulla] = Ell(l, s, p, -19.6f, 6f, -30.5f, 4.4f, 5.6f, 6f);
            c[(int)Wall.Adenoid] = Ell(l, s, p, 0, 10, 24, 13, 6, 7) - 1.4f * (Noise(l / 2f, s / 2f, p / 2f) - 0.5f);
            // Air spaces: the sinuses, ethmoid cells and the openings that join them to the nose.
            c[(int)Wall.MaxSinus] = MaxillarySinus(l, s, p);
            c[(int)Wall.BullaCell] = Ell(l, s, p, -20.3f, 6f, -30.5f, 3.2f, 4.4f, 4.7f);
            c[(int)Wall.AggerCell] = Ell(l, s, p, -18.6f, 15.2f, -52.5f, 2.1f, 2.9f, 3f);
            c[(int)Wall.Sphenoid] = SphenoidSinus(l, s, p);
            c[(int)Wall.Frontal] = FrontalSinus(l, s, p);
            c[(int)Wall.Ostia] = Ostia(l, s, p);
            c[(int)Wall.EthCells] = EthCells(l, s, p, c);
        }

        /// <summary>
        /// Nasal vestibule: the naris exactly as cut into the face (CtPhantom.Nostril, so the endoscope enters through
        /// the nostril it sees) and the skin-lined chamber rising from it up and back under the ala to the valve.
        /// </summary>
        static float Vestibule(float l, float s, float p)
        {
            float naris = CtPhantom.Naris(new Vector3(-l, s, p));
            return SMin(naris, Capsule(new Vector3(l, s, p), VestA, VestB, 3.3f, 4.0f), 2.5f);
        }

        static readonly Vector3 VestA = new Vector3(-7.8f, -26.8f, -86f), VestB = new Vector3(-7.4f, -23.5f, -76f);

        /// <summary>
        /// Signed distance to the airway wall of both cavities, mm: negative in air, positive in tissue.
        /// <paramref name="c"/> gets the wall distances of the nearer cavity (<paramref name="other"/>: the right one, mirrored).
        /// </summary>
        /// <param name="mode">0: everything; 1: the nasal cavity alone; 2: the nasal cavity with the sinus openings.</param>
        static float Air(float l, float s, float p, float[] c, float[] c2, out bool other, int mode = 0)
        {
            // Each side's walls lie on its own side of the septum (the shared nasopharynx is symmetric).
            float a = l < 4f ? Cavity(l, s, p, c, 1f, mode) : 1e3f, b = l > -4f ? Cavity(-l, s, p, c2, -1f, mode) : 1e3f;
            other = b < a;
            if (other) Array.Copy(c2, c, c.Length);
            return Mathf.Min(a, b) - 0.2f * (Noise(l / 1.4f, s / 1.4f, p / 1.4f) - 0.5f);   // mucosal micro-relief
        }

        static readonly (Wall wall, float k)[] TissueWalls =
        {
            (Wall.InfTurb, 1.5f), (Wall.MidTurb, 1.2f), (Wall.SupTurb, 1f), (Wall.Uncinate, 0.6f), (Wall.Agger, 1f),
            (Wall.Bulla, 1.2f), (Wall.BasalLam, 0.8f), (Wall.Torus, 2f), (Wall.Adenoid, 2f),
        };
        static readonly Wall[] AirCells = { Wall.BullaCell, Wall.AggerCell, Wall.EthCells, Wall.MaxSinus, Wall.Sphenoid, Wall.Frontal };

        /// <param name="mode">0: everything; 1: the nasal cavity alone (closed sinuses, cells and their narrow openings
        /// count as bone; the sphenoethmoidal recess is nose); 2: the nasal cavity with the openings.</param>
        static float Cavity(float l, float s, float p, float[] c, float side, int mode = 0)
        {
            Components(l, s, p, c, side);
            float cav = SMax(SMax(c[(int)Wall.Septum], c[(int)Wall.Lateral], 2), SMax(c[(int)Wall.Floor], c[(int)Wall.Roof], 2), 2);
            cav = SMax(cav, SMax(-88f - p, p - PosteriorEdge(s), 2), 2);   // the front is closed by the valve (Lateral)
            float a = SMin(SMin(cav, c[(int)Wall.Vestibule], 4), c[(int)Wall.Pharynx], 3);
            a = SMin(a, Ell(l, s, p, -15.6f, -24.5f, -50f, 1.0f, 1.4f, 2.4f), 0.6f);                 // Hasner's valve
            a = SMin(a, Ell(Mathf.Abs(l), s, p, 12.2f, -9.5f, 15f, 1.5f, 2.6f, 1.8f), 0.8f);         // Eustachian orifices
            foreach (var t in TissueWalls) a = SMax(a, -c[(int)t.wall], t.k);
            if (mode == 1) return SMin(a, Recess(l, s, p), 0.6f);
            a = SMin(a, c[(int)Wall.Ostia], 0.6f);
            if (mode == 2) return a;
            foreach (var w in AirCells) a = Mathf.Min(a, c[(int)w]);   // closed cells inside bone (thin walls kept)
            return a;
        }

        static readonly float[] Scratch = new float[(int)Wall.Count], Scratch2 = new float[(int)Wall.Count];

        /// <summary>
        /// Tissue depth at a point (mm, L/S/P work frame): positive inside tissue, negative in air, plus the wall that is closest
        /// (of the nearer cavity). Main thread only (shared scratch buffers).
        /// </summary>
        public static float Depth(Vector3 mm, out Wall wall) => Depth(mm, out wall, out _);

        public static float Depth(Vector3 mm, out Wall wall, out bool otherSide)
        {
            float d = Air(mm.x, mm.y, mm.z, Scratch, Scratch2, out otherSide);
            int best = 0;
            for (int i = 1; i < (int)Wall.Count; i++) if (Mathf.Abs(Scratch[i]) < Mathf.Abs(Scratch[best])) best = i;
            wall = (Wall)best;
            return d;
        }

        /// <summary>Set by FessSim: whether a point (side work frame mm, right side?) lies in a sinus or cell already opened.</summary>
        public static Func<Vector3, bool, bool> IsOpened;

        /// <summary>
        /// Depth for the endoscope (mm, work frame): the nasal cavity only - the scope cannot pass into a sinus or a cell
        /// until it has been opened; <paramref name="opened"/> says where (side work frame) the resident has opened it.
        /// </summary>
        public static float ScopeDepth(Vector3 mm) => OpenedDepth(mm, 1);

        /// <summary>Depth for an instrument: like the scope's, but the narrow openings (ostia, frontal recess) are passable.</summary>
        public static float InstrumentDepth(Vector3 mm) => OpenedDepth(mm, 2);

        static float OpenedDepth(Vector3 mm, int mode)
        {
            float d = Air(mm.x, mm.y, mm.z, Scratch, Scratch2, out bool other, mode);
            if (d <= 0f || IsOpened == null) return d;
            var q = new Vector3(other ? -mm.x : mm.x, mm.y, mm.z);
            return IsOpened(q, other) ? Mathf.Min(d, Air(mm.x, mm.y, mm.z, Scratch, Scratch2, out _)) : d;
        }

        /// <summary>Moves <paramref name="mm"/> out of the tissue until it is <paramref name="clearance"/> mm inside the air.</summary>
        public static Vector3 PushOut(Vector3 mm, Func<Vector3, float> depth, float clearance)
        {
            for (int it = 0; it < 3; it++)
            {
                float d = depth(mm);
                if (d <= -clearance) break;
                const float h = 0.25f;
                var g = new Vector3(depth(mm + Vector3.right * h) - depth(mm - Vector3.right * h),
                                    depth(mm + Vector3.up * h) - depth(mm - Vector3.up * h),
                                    depth(mm + Vector3.forward * h) - depth(mm - Vector3.forward * h));
                if (g.sqrMagnitude < 1e-8f) break;
                mm -= g.normalized * (d + clearance);
            }
            return mm;
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

            progress?.Invoke("airway and sinus surface");
            // Both nasal cavities with their maxillary sinuses (L +-48), up to the frontal sinuses (S 56).
            var min = new Vector3(-48f, -31f, -97f);
            var n = new Vector3Int(Mathf.CeilToInt(96f / stepMm), Mathf.CeilToInt(87f / stepMm), Mathf.CeilToInt(130f / stepMm));
            var toModel = Matrix4x4.TRS(min * 0.001f, Quaternion.identity, Vector3.one * stepMm * 0.001f);
            int gx = n.x + 1, gy = n.y + 1, gz = n.z + 1;
            var grid = new float[gx * gy * gz];
            System.Threading.Tasks.Parallel.For(0, gz, k =>
            {
                var b1 = new float[(int)Wall.Count]; var b2 = new float[(int)Wall.Count];
                for (int j = 0; j < gy; j++)
                for (int i = 0; i < gx; i++)
                    grid[i + gx * (j + gy * k)] = Air(min.x + i * stepMm, min.y + j * stepMm, min.z + k * stepMm, b1, b2, out _);
            });
            var sn = SurfaceNets.Extract((i, j, k) => grid[i + gx * (j + gy * k)], 0f, Vector3Int.zero, n, 1, toModel).Smooth(2);
            grid = null;
            var buf = new float[(int)Wall.Count];

            progress?.Invoke("classifying");
            for (int t = 0; t < sn.triangles.Count; t += 3)
            {
                int a = sn.triangles[t], b = sn.triangles[t + 1], c = sn.triangles[t + 2];
                var m = (sn.vertices[a] + sn.vertices[b] + sn.vertices[c]) * (1000f / 3f);
                // The nares run out through the nasal base: keep only what lies inside the (uncut) face; the endoscope
                // sees the outer skin around them (PatientModel.EndoscopeHead, which leaves the nostrils open).
                if (m.z < -70f && CtPhantom.SkinSdf(FromWork(m), false) > 0.3f) continue;
                bool other = m.x > Deviation(m.y, m.z);
                Components(other ? -m.x : m.x, m.y, m.z, buf, other ? -1f : 1f);
                int best = 0;
                for (int i = 1; i < (int)Wall.Count; i++) if (Mathf.Abs(buf[i]) < Mathf.Abs(buf[best])) best = i;
                Piece pc;
                var q = new Vector3(other ? -m.x : m.x, m.y, m.z);   // this side's work frame
                string fess = FessGroup((Wall)best, q);
                if (fess == null && (Wall)best == Wall.Lateral && q.y > 1.5f && q.y < 26f && q.z > -51f && q.z < -3f)
                {
                    // The meatal wall in front of an ethmoid cell opens with it.
                    var inner = q + new Vector3(-1.6f, 0, 0);
                    Components(inner.x, inner.y, inner.z, buf, other ? -1f : 1f);
                    if (buf[(int)Wall.EthCells] < 0f) fess = "Cells_" + CellId(inner);
                }
                if (fess != null)
                    pc = Get("Fess_" + (other ? "R_" : "L_") + fess, Structure.SinonasalBone, Mucosa * 0.97f);
                else if (SinusPiece((Wall)best) is string sinus)
                    pc = Get((other ? "Right_" : "") + sinus, Structure.SinonasalBone, SinusLining);
                else if (other && m.z < 5f)
                {
                    // The right cavity: whole walls, no surgical pieces (the nasopharynx behind is shared).
                    switch ((Wall)best)
                    {
                        case Wall.Septum: pc = Get("Right_Septum", Structure.SeptalCartilage, Contra); break;
                        case Wall.InfTurb: pc = Get("Right_InferiorTurbinate", Structure.InferiorTurbinate, Turbinate); break;
                        case Wall.MidTurb: case Wall.SupTurb: pc = Get("Right_MiddleTurbinate", Structure.MiddleTurbinate, Turbinate * 1.05f); break;
                        case Wall.Floor: pc = Get("Right_NasalFloor", Structure.NasalFloor, Floor); break;
                        case Wall.Vestibule: pc = Get("Right_Vestibule", Structure.Background, Skin); break;
                        default: pc = Get("Right_LateralWall", Structure.SinonasalBone, Mucosa * 0.95f); break;
                    }
                }
                else
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
                    case Wall.SupTurb: pc = Get("SuperiorTurbinate", Structure.MiddleTurbinate, Turbinate * 1.05f); break;

                    case Wall.Adenoid: pc = Get("Adenoid", Structure.SinonasalBone, new Color(0.84f, 0.52f, 0.53f)); break;
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
                  new Rect(win.x - 0.5f, win.y - 0.5f, win.width + 1f, win.height + 1f), ContralateralDepth, Spur, null, stepMm);   // the flat far side
            WindowRim(Get("Cartilage_CutEdge", Structure.SeptalCartilage, Cartilage * 0.92f), win, stepMm);

            progress?.Invoke("vibrissae");
            Vibrissae(Get("Vibrissae", Structure.Background, Hair), 1f);
            Vibrissae(Get("Right_Vibrissae", Structure.Background, Hair), -1f);

            progress?.Invoke("surface colour, mirroring to the left side");
            var list = new List<Piece>();
            foreach (var p in pieces.Values) if (p.tris.Count > 0) { Finish(p); list.Add(p); }
            return list;
        }

        /// <summary>
        /// FESS: the bony lamellae the resident removes, cut into chunks a bite takes one at a time (both surfaces of each
        /// thin wall, so a removed chunk leaves a window). Null for walls that stay. <paramref name="q"/>: side work frame, mm.
        /// </summary>
        public static string FessGroup(Wall w, Vector3 q)
        {
            switch (w)
            {
                case Wall.Uncinate: return "Uncinate_" + (q.y > 9f ? 0 : q.y > 3f ? 1 : q.y > -2f ? 2 : 3);
                case Wall.Agger: case Wall.AggerCell: return "AggerNasi_" + (q.z < -52.5f ? 0 : 1);
                case Wall.Bulla: case Wall.BullaCell: return "Bulla_" + ((q.y > 6f ? 0 : 2) + (q.z > -30.5f ? 1 : 0));
                case Wall.BasalLam: return "BasalLamella_" + (q.y > 10f ? 0 : 1);
                case Wall.EthCells: return "Cells_" + CellId(q);
            }
            if (w == Wall.Ostia && (q - new Vector3(-18.4f, 9.9f, -26.6f)).sqrMagnitude < 9f) return "Bulla_1";
            if ((w == Wall.Lateral || w == Wall.MaxSinus || w == Wall.Ostia) && (q - AntrostomyCentre).sqrMagnitude < 6.5f * 6.5f)
                return "Antrostomy_" + (q.z < -41f ? 0 : q.z < -37f ? 1 : 2);
            if ((w == Wall.Sphenoid || w == Wall.Ostia || w == Wall.Pharynx || w == Wall.Roof || w == Wall.Lateral || w == Wall.SupTurb) &&
                (q - SphenoidotomyCentre).sqrMagnitude < 25f)
                return "Sphenoid_" + (q.y > 17.5f ? 0 : 1);
            return null;
        }

        public static readonly Vector3 AntrostomyCentre = new Vector3(-21f, -3.6f, -39.5f), SphenoidotomyCentre = new Vector3(-5.3f, 17.5f, 3.2f);

        static string SinusPiece(Wall w)
        {
            switch (w)
            {
                case Wall.MaxSinus: return "MaxillarySinus";
                case Wall.Sphenoid: return "SphenoidSinus";
                case Wall.Frontal: return "FrontalSinus";
                case Wall.Ostia: return "SinusOstia";
            }
            return null;
        }

        /// <summary>
        /// Per-vertex look (computed in the work frame), then mirrored to the patient's left side. Colour alpha carries ambient
        /// occlusion from the field (meatuses, ostia, cell floors and the depths of recesses darker, as the endoscope sees them);
        /// mucosa that lines a sinus or an ethmoid cell gets the thin, pale sinus look.
        /// </summary>
        static void Finish(Piece pc)
        {
            pc.look = pc.name.Contains("Vibrissae") ? Look.Plain
                    : pc.name.Contains("Sinus") ? Look.Sinus
                    : pc.name.Contains("Vestibule") ? Look.Skin
                    : pc.name.Contains("Turbinate") ? Look.Turbinate
                    : pc.name.StartsWith("Cartilage") || pc.name == "Incision_Bed" ? Look.Cartilage
                    : Look.Mucosa;
            bool right = pc.name.StartsWith("Right_") || pc.name.StartsWith("Fess_R_");
            bool occlusion = pc.look != Look.Plain && pc.look != Look.Cartilage && !pc.name.StartsWith("Contralateral");
            int n = pc.verts.Count;
            var colors = new Color[n]; var uvs = new Vector2[n]; var uvs2 = new Vector2[n];
            var lining = new Color(SinusLining.r / Mucosa.r, SinusLining.g / Mucosa.g, SinusLining.b / Mucosa.b);
            System.Threading.Tasks.Parallel.For(0, n, () => new[] { new float[(int)Wall.Count], new float[(int)Wall.Count] }, (i, _, buf) =>
            {
                var at = pc.verts[i] * 1000f;   // both cavities, work frame
                var w = at;
                if (right) w.x = -w.x;   // shade the right cavity like its mirror image
                var look = pc.look;
                float ao = 1f, thin = 0f;
                if (occlusion)
                {
                    var nrm = AirNormal(at, buf);
                    ao = Occlusion(at, nrm, buf);
                    thin = Thinness(at, nrm, buf);
                    if (look == Look.Mucosa && LinesSinus(at + nrm * 1.2f, buf)) look = Look.Sinus;
                }
                Shade(look, w, out var c, out var uv);
                if (look == Look.Sinus && pc.look == Look.Mucosa) c *= lining;
                c.a = ao;
                colors[i] = c; uvs[i] = uv; uvs2[i] = new Vector2(thin, 0f);
                return buf;
            }, _ => { });
            for (int i = 0; i < n; i++) pc.verts[i] = FromWork(pc.verts[i]);
            pc.colors.AddRange(colors);
            pc.uvs.AddRange(uvs);
            pc.uvs2.AddRange(uvs2);
            if (Mirror < 0)
                for (int t = 0; t < pc.tris.Count; t += 3) { int b = pc.tris[t + 1]; pc.tris[t + 1] = pc.tris[t + 2]; pc.tris[t + 2] = b; }
        }

        static float AirAt(Vector3 q, float[][] buf, int mode = 0) => Air(q.x, q.y, q.z, buf[0], buf[1], out _, mode);

        /// <summary>Unit normal into the air (down the field's gradient).</summary>
        static Vector3 AirNormal(Vector3 q, float[][] buf)
        {
            const float h = 0.4f;
            var g = new Vector3(AirAt(q + Vector3.right * h, buf) - AirAt(q - Vector3.right * h, buf),
                                AirAt(q + Vector3.up * h, buf) - AirAt(q - Vector3.up * h, buf),
                                AirAt(q + Vector3.forward * h, buf) - AirAt(q - Vector3.forward * h, buf));
            return g.sqrMagnitude > 1e-10f ? -g.normalized : Vector3.zero;
        }

        static readonly float[] AoStep = { 0.7f, 1.6f, 3.2f, 5.5f }, AoWeight = { 0.3f, 0.3f, 0.25f, 0.15f };

        /// <summary>Ambient occlusion 0..1: how much tissue crowds the air in front of the surface (field samples along the normal).</summary>
        static float Occlusion(Vector3 q, Vector3 n, float[][] buf)
        {
            if (n == Vector3.zero) return 1f;
            float occ = 0f;
            for (int i = 0; i < AoStep.Length; i++)
            {
                float h = AoStep[i], f = AirAt(q + n * h, buf);
                occ += AoWeight[i] * Mathf.Clamp01((h + f) / h);   // in open air f = -h
            }
            return Mathf.Clamp01(1f - occ);
        }

        static readonly float[] ThinStep = { 0.7f, 1.4f, 2.2f, 3.2f };

        /// <summary>How thin the wall is under the surface, 0..1: air reached again close behind it (uncinate, cell walls, fold edges).</summary>
        static float Thinness(Vector3 q, Vector3 n, float[][] buf)
        {
            if (n == Vector3.zero) return 0f;
            foreach (var h in ThinStep)
                if (AirAt(q - n * h, buf) < 0f) return 1f - h / 3.8f;
            return 0f;
        }

        /// <summary>Whether the air just off the surface belongs to a sinus or an ethmoid cell, not the nose.</summary>
        static bool LinesSinus(Vector3 q, float[][] buf) => AirAt(q, buf) < -0.2f && AirAt(q, buf, 1) > 0.3f;

        /// <summary>
        /// Colour multiplier (applied to the part's tint) and (vessel density, wetness): Little's area redder and
        /// more vascular, upper walls paler, congested mottled turbinates, patches of glistening mucus.
        /// </summary>
        static void Shade(Look look, Vector3 mm, out Color c, out Vector2 uv)
        {
            float l = mm.x, s = mm.y, p = mm.z;
            c = Color.white;
            float vessels = 1f, wet = 1f;
            float mucus = Mathf.Clamp01((Noise(l / 3f, s / 3f, p / 5f) - 0.72f) * 6f);
            switch (look)
            {
                case Look.Mucosa:
                    float little = Mathf.Exp(-((s + 16) * (s + 16) / 60f + (p + 66) * (p + 66) / 40f));   // Kiesselbach's plexus
                    c = Color.Lerp(Color.white, new Color(0.9f, 0.62f, 0.66f), little);
                    vessels = 1f + 1.6f * little;
                    c *= Color.Lerp(Color.white, new Color(1f, 1.07f, 1.06f), Mathf.Clamp01((s - 5f) / 10f));   // thinner, paler mucosa up high
                    c *= 0.96f + 0.08f * Noise(l / 4f, s / 4f, p / 4f);
                    break;
                case Look.Turbinate:
                    float m = Noise(l / 1.2f, s / 1.2f, p / 1.2f);
                    c = new Color(0.97f, 0.92f, 1.04f) * (0.92f + 0.16f * m);   // congested, slightly bluish
                    vessels = 0.9f;
                    break;
                case Look.Cartilage:
                    c *= 0.97f + 0.06f * Noise(l, s, p);
                    vessels = 0.25f; wet = 1.2f; mucus = 0;
                    break;
                case Look.Skin: vessels = 0; wet = 0.5f; mucus = 0; break;
                case Look.Sinus:
                    // Thin, translucent lining: the bone shows through yellowish, fine vessels stand out, very wet.
                    c = Color.Lerp(Color.white, new Color(1.04f, 1.0f, 0.86f), 0.4f * Noise(l / 3f, s / 3f, p / 3f));
                    vessels = 1.5f; wet = 1.3f;
                    break;
                default: mucus = 0; break;
            }
            c = Color.Lerp(c, new Color(1.1f, 1.2f, 1.15f), 0.5f * mucus);
            c.a = 1;
            uv = new Vector2(vessels, wet + 1.5f * mucus);
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
                var a1 = new Vector3(SeptumFace(a.x, a.y) + ContralateralDepth + Spur(a.x, a.y), a.x, a.y) * 0.001f;
                var b1 = new Vector3(SeptumFace(b.x, b.y) + ContralateralDepth + Spur(b.x, b.y), b.x, b.y) * 0.001f;
                AddTri(pc, a0, b0, a1);
                AddTri(pc, b0, b1, a1);
            }
        }

        /// <summary>Short dark hairs on the vestibular skin, pointing toward the nostril opening (side -1: the other nostril).</summary>
        static void Vibrissae(Piece pc, float side)
        {
            var rnd = new System.Random(7);
            Vector3 a = VestA, b = VestB;
            var axis = (b - a).normalized;
            var u = Vector3.Cross(axis, Vector3.right).normalized; var v = Vector3.Cross(axis, u);
            for (int h = 0; h < 70; h++)
            {
                float t = 0.05f + 0.45f * (float)rnd.NextDouble(), ang = (float)(rnd.NextDouble() * Math.PI * 2);
                float r = Mathf.Lerp(3.3f, 4.0f, t) * 0.95f;
                var radial = u * Mathf.Cos(ang) + v * Mathf.Sin(ang);
                var root = a + (b - a) * t + radial * r;
                var dir = (-radial * 0.8f - axis * 0.6f).normalized;
                float len = 1.8f + 2.2f * (float)rnd.NextDouble(), w = 0.07f;
                var tip = root + dir * len + radial * 0.3f;
                var s1 = Vector3.Cross(dir, radial).normalized * w; var s2 = radial * w;
                Vector3 r0 = (root + s1) * 0.001f, r1 = (root - s1 + s2) * 0.001f, r2 = (root - s1 - s2) * 0.001f, tp = tip * 0.001f;
                if (side < 0)
                {
                    r0.x = -r0.x; r1.x = -r1.x; r2.x = -r2.x; tp.x = -tp.x;
                    AddTri(pc, r1, r0, tp); AddTri(pc, r2, r1, tp); AddTri(pc, r0, r2, tp);
                }
                else { AddTri(pc, r0, r1, tp); AddTri(pc, r1, r2, tp); AddTri(pc, r2, r0, tp); }
            }
        }
    }
}
