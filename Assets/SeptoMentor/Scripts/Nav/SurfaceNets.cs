using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SeptoMentor.Nav
{
    /// <summary>
    /// Naive surface nets iso-surface extraction (thread-safe: produces plain arrays; call
    /// ToMesh on the main thread). One vertex per surface cell at the mean of its edge
    /// crossings; one quad per sign-changing grid edge, wound so faces point "outside"
    /// (toward lower field values) in model space.
    /// </summary>
    public class SurfaceNets
    {
        public readonly List<Vector3> vertices = new List<Vector3>();
        public readonly List<int> triangles = new List<int>();

        /// <param name="field">Value at a voxel index; inside where value >= iso.</param>
        /// <param name="min">Inclusive voxel-index crop start.</param>
        /// <param name="max">Inclusive voxel-index crop end.</param>
        public static SurfaceNets Extract(Func<int, int, int, float> field, float iso, Vector3Int min, Vector3Int max,
                                          int stride, Matrix4x4 indexToModel)
        {
            var sn = new SurfaceNets();
            int gx = (max.x - min.x) / stride + 1, gy = (max.y - min.y) / stride + 1, gz = (max.z - min.z) / stride + 1;
            if (gx < 2 || gy < 2 || gz < 2) return sn;
            var val = new float[gx * gy * gz];
            for (int z = 0; z < gz; z++)
            for (int y = 0; y < gy; y++)
            for (int x = 0; x < gx; x++)
                val[x + gx * (y + gy * z)] = field(min.x + x * stride, min.y + y * stride, min.z + z * stride) - iso;

            int cx = gx - 1, cy = gy - 1, cz = gz - 1;
            var cell = new int[cx * cy * cz];
            var corner = new float[8];
            for (int z = 0; z < cz; z++)
            for (int y = 0; y < cy; y++)
            for (int x = 0; x < cx; x++)
            {
                int mask = 0;
                for (int c = 0; c < 8; c++)
                {
                    corner[c] = val[(x + (c & 1)) + gx * ((y + ((c >> 1) & 1)) + gy * (z + ((c >> 2) & 1)))];
                    if (corner[c] >= 0) mask |= 1 << c;
                }
                int ci = x + cx * (y + cy * z);
                if (mask == 0 || mask == 255) { cell[ci] = -1; continue; }

                var sum = Vector3.zero; int n = 0;
                for (int c = 0; c < 8; c++)
                for (int axis = 0; axis < 3; axis++)
                {
                    int c2 = c | (1 << axis);
                    if (c2 == c) continue;
                    float a = corner[c], b = corner[c2];
                    if ((a >= 0) == (b >= 0)) continue;
                    float t = a / (a - b);
                    var pa = new Vector3(c & 1, (c >> 1) & 1, (c >> 2) & 1);
                    var pb = new Vector3(c2 & 1, (c2 >> 1) & 1, (c2 >> 2) & 1);
                    sum += Vector3.Lerp(pa, pb, t); n++;
                }
                var local = new Vector3(x, y, z) + sum / n;
                var idx = (Vector3)min + local * stride;
                cell[ci] = sn.vertices.Count;
                sn.vertices.Add(indexToModel.MultiplyPoint3x4(idx));
            }

            // Quads: for every grid edge with a sign change, join the 4 cells around it.
            var q = new int[4];
            for (int z = 0; z < gz; z++)
            for (int y = 0; y < gy; y++)
            for (int x = 0; x < gx; x++)
            {
                float v0 = val[x + gx * (y + gy * z)];
                for (int axis = 0; axis < 3; axis++)
                {
                    int x1 = x + (axis == 0 ? 1 : 0), y1 = y + (axis == 1 ? 1 : 0), z1 = z + (axis == 2 ? 1 : 0);
                    if (x1 >= gx || y1 >= gy || z1 >= gz) continue;
                    float v1 = val[x1 + gx * (y1 + gy * z1)];
                    if ((v0 >= 0) == (v1 >= 0)) continue;
                    // The two other axes span the 4 cells sharing this edge.
                    int u = (axis + 1) % 3, w = (axis + 2) % 3;
                    bool ok = true;
                    for (int k = 0; k < 4 && ok; k++)
                    {
                        var c = new Vector3Int(x, y, z);
                        c[u] -= (k == 1 || k == 2) ? 1 : 0;
                        c[w] -= (k >= 2) ? 1 : 0;
                        if (c.x < 0 || c.y < 0 || c.z < 0 || c.x >= cx || c.y >= cy || c.z >= cz) { ok = false; break; }
                        q[k] = cell[c.x + cx * (c.y + cy * c.z)];
                        if (q[k] < 0) ok = false;
                    }
                    if (!ok) continue;
                    var outIdx = Vector3.zero; outIdx[axis] = v0 >= 0 ? 1 : -1;   // toward the outside end of the edge
                    var outward = indexToModel.MultiplyVector(outIdx);
                    sn.AddTri(q[0], q[1], q[2], outward);
                    sn.AddTri(q[0], q[2], q[3], outward);
                }
            }
            return sn;
        }

        void AddTri(int a, int b, int c, Vector3 outward)
        {
            var n = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
            if (Vector3.Dot(n, outward) < 0) { int t = b; b = c; c = t; }
            triangles.Add(a); triangles.Add(b); triangles.Add(c);
        }

        /// <summary>Taubin smoothing (shrink-free): removes voxel stair-steps while keeping the shape's size.</summary>
        public SurfaceNets Smooth(int iterations = 4, float lambda = 0.5f, float mu = -0.53f)
        {
            int n = vertices.Count;
            if (n == 0) return this;
            var nb = new List<int>[n];
            for (int i = 0; i < n; i++) nb[i] = new List<int>(6);
            for (int t = 0; t < triangles.Count; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = triangles[t + e], b = triangles[t + (e + 1) % 3];
                    if (!nb[a].Contains(b)) nb[a].Add(b);
                    if (!nb[b].Contains(a)) nb[b].Add(a);
                }
            var tmp = new Vector3[n];
            for (int it = 0; it < iterations * 2; it++)
            {
                float f = (it & 1) == 0 ? lambda : mu;
                for (int i = 0; i < n; i++)
                {
                    if (nb[i].Count == 0) { tmp[i] = vertices[i]; continue; }
                    var avg = Vector3.zero;
                    foreach (int j in nb[i]) avg += vertices[j];
                    avg /= nb[i].Count;
                    tmp[i] = vertices[i] + (avg - vertices[i]) * f;
                }
                for (int i = 0; i < n; i++) vertices[i] = tmp[i];
            }
            return this;
        }

        public Mesh ToMesh(string name)
        {
            var m = new Mesh { name = name, indexFormat = vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            m.SetVertices(vertices);
            m.SetTriangles(triangles, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }
}
