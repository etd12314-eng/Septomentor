using System;
using UnityEngine;

namespace SeptoMentor
{
    /// <summary>Per-class statistics computed once per frame on the CPU label map.</summary>
    [Serializable]
    public struct ClassStats
    {
        public int pixelCount;
        public Vector2 centroid;      // normalised [0,1], origin bottom-left
        public Vector2 anchor;        // an in-class pixel nearest the centroid (label position)
    }

    /// <summary>
    /// One segmented frame. Labels are row-major, bottom row first (matches
    /// Unity texture readback), one byte per pixel = (byte)Structure.
    /// </summary>
    public sealed class SegmentationFrame
    {
        public readonly int width;
        public readonly int height;
        public readonly byte[] labels;
        public readonly ClassStats[] stats = new ClassStats[StructureInfo.Count];

        public double timestamp;
        public int frameIndex;

        /// <summary>Instrument tip in normalised image coords, if known.</summary>
        public Vector2? instrumentTip;
        public Structure activeInstrument = Structure.Background;

        /// <summary>Approximate scale at the tip (mm per label pixel); null = uncalibrated.</summary>
        public float? mmPerPixel;

        /// <summary>Instrument angular speed (deg/s) if the source knows it (synthetic / tracked).</summary>
        public float? instrumentAngularSpeed;

        public SegmentationFrame(int w, int h)
        {
            width = w; height = h;
            labels = new byte[w * h];
        }

        public Structure At(int x, int y) => (Structure)labels[y * width + x];

        public bool Visible(Structure s, int minPixels = 40) => stats[(int)s].pixelCount >= minPixels;

        public float Fraction(Structure s) => stats[(int)s].pixelCount / (float)(width * height);

        public void ComputeStats()
        {
            int n = StructureInfo.Count;
            var sx = new long[n]; var sy = new long[n]; var c = new int[n];
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    int k = labels[row + x];
                    if (k >= n) continue;
                    c[k]++; sx[k] += x; sy[k] += y;
                }
            }
            for (int k = 0; k < n; k++)
            {
                stats[k].pixelCount = c[k];
                if (c[k] == 0) { stats[k].centroid = stats[k].anchor = new Vector2(-1, -1); continue; }
                var cen = new Vector2(sx[k] / (float)c[k], sy[k] / (float)c[k]);
                stats[k].centroid = new Vector2(cen.x / width, cen.y / height);
                stats[k].anchor = NearestPixelOfClass((byte)k, cen);
            }

            // Fallback tip heuristic for sources that don't know the tip:
            // the instrument pixel nearest the image centre (scope axis).
            if (!instrumentTip.HasValue)
            {
                Structure best = Structure.Background; int bestCount = 0;
                foreach (Structure s in new[] { Structure.Scalpel, Structure.FreerElevator, Structure.ScissorsForceps, Structure.Suction })
                    if (stats[(int)s].pixelCount > bestCount) { best = s; bestCount = stats[(int)s].pixelCount; }
                if (bestCount >= 40)
                {
                    activeInstrument = best;
                    instrumentTip = NearestPixelOfClass((byte)best, new Vector2(width * 0.5f, height * 0.5f));
                }
            }
        }

        Vector2 NearestPixelOfClass(byte k, Vector2 p)
        {
            // Coarse stride search keeps this O(N/16); exactness is not needed for label placement.
            float best = float.MaxValue; int bx = -1, by = -1;
            for (int y = 0; y < height; y += 2)
                for (int x = 0; x < width; x += 2)
                {
                    if (labels[y * width + x] != k) continue;
                    float d = (x - p.x) * (x - p.x) + (y - p.y) * (y - p.y);
                    if (d < best) { best = d; bx = x; by = y; }
                }
            return bx < 0 ? new Vector2(-1, -1) : new Vector2(bx / (float)width, by / (float)height);
        }

        /// <summary>Distance in label pixels from a normalised point to the nearest pixel of a class (search radius capped).</summary>
        public float PixelDistanceTo(Vector2 normPoint, Structure s, int maxRadius = 64)
        {
            int px = Mathf.RoundToInt(normPoint.x * width), py = Mathf.RoundToInt(normPoint.y * height);
            float best = float.MaxValue;
            int x0 = Mathf.Max(0, px - maxRadius), x1 = Mathf.Min(width - 1, px + maxRadius);
            int y0 = Mathf.Max(0, py - maxRadius), y1 = Mathf.Min(height - 1, py + maxRadius);
            byte k = (byte)s;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    if (labels[y * width + x] != k) continue;
                    float d = (x - px) * (x - px) + (y - py) * (y - py);
                    if (d < best) best = d;
                }
            return best == float.MaxValue ? float.PositiveInfinity : Mathf.Sqrt(best);
        }
    }
}
