using System;
using System.Collections.Generic;
using UnityEngine;

namespace SeptoMentor.Nav
{
    /// <summary>Rigid point registration (Horn's quaternion method), ICP, pivot calibration.</summary>
    public static class Registration
    {
        public struct Rigid
        {
            public Quaternion rotation;
            public Vector3 translation;
            public Vector3 Apply(Vector3 p) => rotation * p + translation;
            public Rigid Inverse() { var ri = Quaternion.Inverse(rotation); return new Rigid { rotation = ri, translation = -(ri * translation) }; }
            public static Rigid Identity => new Rigid { rotation = Quaternion.identity };
            public static Rigid operator *(Rigid a, Rigid b) => new Rigid { rotation = a.rotation * b.rotation, translation = a.rotation * b.translation + a.translation };
        }

        /// <summary>Best rigid transform with T(src[i]) ~= dst[i]. Returns RMS residual (same units).</summary>
        public static float Fit(IList<Vector3> src, IList<Vector3> dst, out Rigid t)
        {
            int n = src.Count;
            t = Rigid.Identity;
            if (n == 0 || n != dst.Count) return float.PositiveInfinity;
            Vector3 cs = Vector3.zero, cd = Vector3.zero;
            for (int i = 0; i < n; i++) { cs += src[i]; cd += dst[i]; }
            cs /= n; cd /= n;
            if (n < 3) { t.translation = cd - cs; return Rms(src, dst, t); }

            double sxx = 0, sxy = 0, sxz = 0, syx = 0, syy = 0, syz = 0, szx = 0, szy = 0, szz = 0;
            for (int i = 0; i < n; i++)
            {
                var a = src[i] - cs; var b = dst[i] - cd;
                sxx += a.x * b.x; sxy += a.x * b.y; sxz += a.x * b.z;
                syx += a.y * b.x; syy += a.y * b.y; syz += a.y * b.z;
                szx += a.z * b.x; szy += a.z * b.y; szz += a.z * b.z;
            }
            var N = new double[4, 4]
            {
                { sxx + syy + szz, syz - szy,        szx - sxz,        sxy - syx },
                { syz - szy,       sxx - syy - szz,  sxy + syx,        szx + sxz },
                { szx - sxz,       sxy + syx,       -sxx + syy - szz,  syz + szy },
                { sxy - syx,       szx + sxz,        syz + szy,       -sxx - syy + szz },
            };
            var e = MaxEigenvector(N);   // (w, x, y, z)
            var q = new Quaternion((float)e[1], (float)e[2], (float)e[3], (float)e[0]).normalized;

            // Guard against quaternion sign/handedness conventions: keep whichever fits better.
            var a1 = new Rigid { rotation = q, translation = cd - q * cs };
            var qc = Quaternion.Inverse(q);
            var a2 = new Rigid { rotation = qc, translation = cd - qc * cs };
            float r1 = Rms(src, dst, a1), r2 = Rms(src, dst, a2);
            t = r1 <= r2 ? a1 : a2;
            return Mathf.Min(r1, r2);
        }

        public static float Rms(IList<Vector3> src, IList<Vector3> dst, Rigid t)
        {
            double s = 0;
            for (int i = 0; i < src.Count; i++) s += (t.Apply(src[i]) - dst[i]).sqrMagnitude;
            return (float)Math.Sqrt(s / Math.Max(1, src.Count));
        }

        static double[] MaxEigenvector(double[,] a)
        {
            // Cyclic Jacobi on a symmetric 4x4.
            var m = (double[,])a.Clone();
            var v = new double[4, 4];
            for (int i = 0; i < 4; i++) v[i, i] = 1;
            for (int sweep = 0; sweep < 50; sweep++)
            {
                double off = 0;
                for (int p = 0; p < 4; p++) for (int q = p + 1; q < 4; q++) off += m[p, q] * m[p, q];
                if (off < 1e-18) break;
                for (int p = 0; p < 4; p++)
                for (int q = p + 1; q < 4; q++)
                {
                    if (Math.Abs(m[p, q]) < 1e-30) continue;
                    double theta = (m[q, q] - m[p, p]) / (2 * m[p, q]);
                    double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    if (theta == 0) t = 1;
                    double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                    for (int k = 0; k < 4; k++)
                    {
                        double mkp = m[k, p], mkq = m[k, q];
                        m[k, p] = c * mkp - s * mkq; m[k, q] = s * mkp + c * mkq;
                    }
                    for (int k = 0; k < 4; k++)
                    {
                        double mpk = m[p, k], mqk = m[q, k];
                        m[p, k] = c * mpk - s * mqk; m[q, k] = s * mpk + c * mqk;
                    }
                    for (int k = 0; k < 4; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = c * vkp - s * vkq; v[k, q] = s * vkp + c * vkq;
                    }
                }
            }
            int best = 0;
            for (int i = 1; i < 4; i++) if (m[i, i] > m[best, best]) best = i;
            return new[] { v[0, best], v[1, best], v[2, best], v[3, best] };
        }

        /// <summary>Uniform-grid nearest neighbour over a fixed point set.</summary>
        public class PointGrid
        {
            readonly Dictionary<Vector3Int, List<Vector3>> _cells = new Dictionary<Vector3Int, List<Vector3>>();
            readonly float _cell;

            public PointGrid(IEnumerable<Vector3> pts, float cell)
            {
                _cell = cell;
                foreach (var p in pts)
                {
                    var k = Key(p);
                    if (!_cells.TryGetValue(k, out var l)) _cells[k] = l = new List<Vector3>();
                    l.Add(p);
                }
            }

            Vector3Int Key(Vector3 p) => new Vector3Int(Mathf.FloorToInt(p.x / _cell), Mathf.FloorToInt(p.y / _cell), Mathf.FloorToInt(p.z / _cell));

            public bool Nearest(Vector3 p, float maxDist, out Vector3 best)
            {
                best = default; float bestD = maxDist * maxDist; bool found = false;
                var k = Key(p); int r = Mathf.CeilToInt(maxDist / _cell);
                for (int z = -r; z <= r; z++)
                for (int y = -r; y <= r; y++)
                for (int x = -r; x <= r; x++)
                {
                    if (!_cells.TryGetValue(new Vector3Int(k.x + x, k.y + y, k.z + z), out var l)) continue;
                    foreach (var q in l)
                    {
                        float d = (q - p).sqrMagnitude;
                        if (d < bestD) { bestD = d; best = q; found = true; }
                    }
                }
                return found;
            }
        }

        /// <summary>
        /// Refines model->world <paramref name="modelToWorld"/> so the world surface points lie on
        /// the model surface. Returns the RMS point-to-surface distance (m) of the inliers.
        /// </summary>
        public static float Icp(PointGrid modelSurface, IList<Vector3> worldPts, ref Rigid modelToWorld,
                                float maxPairDist = 0.012f, int iterations = 30)
        {
            float rms = float.PositiveInfinity;
            var src = new List<Vector3>(); var dst = new List<Vector3>();
            for (int it = 0; it < iterations; it++)
            {
                var worldToModel = modelToWorld.Inverse();
                src.Clear(); dst.Clear();
                foreach (var w in worldPts)
                {
                    var m = worldToModel.Apply(w);
                    if (modelSurface.Nearest(m, maxPairDist, out var nearest)) { src.Add(m); dst.Add(nearest); }
                }
                if (src.Count < 6) break;
                float r = Fit(src, dst, out var step);   // in model space: moves measured points onto the surface
                // world = M2W(model'), model' = step(model)  =>  new M2W = M2W * step^-1
                modelToWorld = modelToWorld * step.Inverse();
                if (Mathf.Abs(rms - r) < 1e-5f) { rms = r; break; }
                rms = r;
            }
            return rms;
        }

        /// <summary>
        /// Pivot calibration: the tool tip rests in a fixed divot while the tool rotates.
        /// Solves R_i * tip + p_i = pivot for tip (tool frame) and pivot (world).
        /// Returns RMS residual (m).
        /// </summary>
        public static float Pivot(IList<Quaternion> rot, IList<Vector3> pos, out Vector3 tip, out Vector3 pivot)
        {
            var ata = new double[6, 6]; var atb = new double[6];
            for (int i = 0; i < rot.Count; i++)
            {
                var m = Matrix4x4.Rotate(rot[i]);
                for (int r = 0; r < 3; r++)
                {
                    var row = new double[] { m[r, 0], m[r, 1], m[r, 2], r == 0 ? -1 : 0, r == 1 ? -1 : 0, r == 2 ? -1 : 0 };
                    double b = -pos[i][r];
                    for (int a = 0; a < 6; a++) { atb[a] += row[a] * b; for (int c = 0; c < 6; c++) ata[a, c] += row[a] * row[c]; }
                }
            }
            var x = Solve(ata, atb);
            tip = new Vector3((float)x[0], (float)x[1], (float)x[2]);
            pivot = new Vector3((float)x[3], (float)x[4], (float)x[5]);
            double s = 0;
            for (int i = 0; i < rot.Count; i++) s += (rot[i] * tip + pos[i] - pivot).sqrMagnitude;
            return (float)Math.Sqrt(s / Math.Max(1, rot.Count));
        }

        static double[] Solve(double[,] a, double[] b)
        {
            int n = b.Length;
            var m = (double[,])a.Clone(); var x = (double[])b.Clone();
            for (int c = 0; c < n; c++)
            {
                int p = c;
                for (int r = c + 1; r < n; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[p, c])) p = r;
                if (Math.Abs(m[p, c]) < 1e-12) continue;
                for (int k = 0; k < n; k++) { var t = m[c, k]; m[c, k] = m[p, k]; m[p, k] = t; }
                { var t = x[c]; x[c] = x[p]; x[p] = t; }
                for (int r = 0; r < n; r++)
                {
                    if (r == c) continue;
                    double f = m[r, c] / m[c, c];
                    for (int k = c; k < n; k++) m[r, k] -= f * m[c, k];
                    x[r] -= f * x[c];
                }
            }
            for (int i = 0; i < n; i++) x[i] = Math.Abs(m[i, i]) < 1e-12 ? 0 : x[i] / m[i, i];
            return x;
        }
    }
}
