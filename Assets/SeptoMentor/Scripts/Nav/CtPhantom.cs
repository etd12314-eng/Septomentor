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
                vol.hu[n] = (short)Hu(p, out labels[n]);
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

        static float Ell(Vector3 p, Vector3 c, Vector3 r)
        {
            var d = new Vector3((p.x - c.x) / r.x, (p.y - c.y) / r.y, (p.z - c.z) / r.z);
            return d.sqrMagnitude;   // < 1 inside
        }

        /// <summary>Approximate signed distance to the skin (negative inside), in mm.</summary>
        static float SkinSdf(Vector3 p)
        {
            float head = (Mathf.Sqrt(Ell(p, new Vector3(0, 15, 30), new Vector3(75, 105, 100))) - 1f) * 75f;
            // Nose: a pyramid bounded by the dorsum (nasion -> tip), the columella (tip -> subnasale),
            // two sloping sides, and capped inside the face; plus the alae.
            float s = p.y, pp = p.z;
            float dorsum = (s - NoseTipS) * 0.581f - (pp - NoseTipP) * 0.814f;      // > 0 in front of the dorsum
            float under = -(s - NoseTipS) * 0.906f - (pp - NoseTipP) * 0.425f;      // > 0 below the columella
            float side = (Mathf.Abs(p.x) - 4f + 0.45f * dorsum) * 0.911f;           // widens away from the dorsum
            float nose = Mathf.Max(Mathf.Max(dorsum, under), Mathf.Max(side, pp + 50f));
            float alaR = (Mathf.Sqrt(Ell(p, new Vector3(-11, -20, -80), new Vector3(7, 6, 9))) - 1f) * 6f;
            float alaL = (Mathf.Sqrt(Ell(p, new Vector3(11, -20, -80), new Vector3(7, 6, 9))) - 1f) * 6f;
            return Mathf.Min(head, Mathf.Min(nose, Mathf.Min(alaR, alaL)));
        }

        const float NoseTipS = -12f, NoseTipP = -98f;

        static float SeptumDeviation(float s, float pp) =>
            -5f * Mathf.Exp(-((s + 8) * (s + 8) / 128f + (pp + 45) * (pp + 45) / 288f))      // C-shaped deviation to the right
            - 3f * Mathf.Exp(-((s + 24) * (s + 24) / 8f + (pp + 30) * (pp + 30) / 200f));   // basal spur

        static float CavityTop(float pp) => pp >= -45 ? 22f : Asa.y + (pp - Asa.z) * (Dorsal.y - Asa.y) / (Dorsal.z - Asa.z);

        static int Hu(Vector3 p, out byte label)
        {
            label = 0;
            float skin = SkinSdf(p);
            if (skin > 0) return -1000;
            float l = p.x, s = p.y, pp = p.z;
            float al = Mathf.Abs(l);

            // Air spaces.
            bool inNasalBox = al < 15 && s > -27 && s < CavityTop(pp) && pp > -84 && pp < 8 && skin < -2.5f;
            bool nostril = DistSeg(new Vector3(al, s, pp), new Vector3(7, -26, -86), new Vector3(7, -16, -74)) < 4f;
            bool pharynx = al < 14 && s > -27 && s < 10 && pp >= 8 && pp < 32;
            bool maxSinus = Ell(new Vector3(al, s, pp), new Vector3(32, -10, -35), new Vector3(12, 14, 16)) < 1;

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
            float inf = Ell(new Vector3(al, s, pp), new Vector3(13, -18, -25), new Vector3(4.5f, 7, 32));
            float mid = Ell(new Vector3(al, s, pp), new Vector3(11, 3, -15), new Vector3(3.5f, 5, 22));
            if (inf < 1 || mid < 1)
            {
                label = LabelTurbinate;
                bool core = Ell(new Vector3(al, s, pp), inf < 1 ? new Vector3(14, -18, -25) : new Vector3(12, 3, -15),
                                inf < 1 ? new Vector3(1.5f, 4, 28) : new Vector3(1.2f, 3, 18)) < 1;
                return core ? 700 : 45;
            }

            if (inNasalBox || nostril || pharynx) return -1000;
            if (maxSinus) return -1000;

            // Bone.
            bool lateralWall = al >= 15 && al < 17 && s > -29 && s < 23 && pp > -62 && pp < 8;
            bool palate = s > -32 && s <= -27 && al < 18 && pp > -64 && pp < 8;
            bool skullBase = s >= 22 && s < 25 && al < 13 && pp > -56 && pp < 8;
            bool sinusWall = Ell(new Vector3(al, s, pp), new Vector3(32, -10, -35), new Vector3(14, 16, 18)) < 1;
            bool cranium = skin < -5 && skin > -11 && s > 24;
            bool nasalBones = skin < -2.5f && skin > -5.5f && pp < -62 && s > 12 && al < 10;
            if (lateralWall || palate || skullBase || sinusWall || cranium || nasalBones) return 1200;

            // Orbits: globe + fat.
            float orb = Ell(new Vector3(al, s, pp), new Vector3(32, 30, -48), new Vector3(15, 15, 17));
            if (orb < 1) return Ell(new Vector3(al, s, pp), new Vector3(32, 30, -52), new Vector3(12, 12, 12)) < 1 ? 15 : -90;

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
