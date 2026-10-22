using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SeptoMentor.Nav
{
    /// <summary>
    /// DICOM CT series reader for what hospital CDs / PACS exports actually contain:
    ///   - transfer syntaxes: implicit / explicit VR little endian, JPEG Lossless (.57, .70), RLE Lossless;
    ///     anything else (JPEG 2000, JPEG-LS, lossy JPEG) must be re-saved uncompressed or exported as NRRD;
    ///   - several series in one folder (DICOMDIR CDs): scouts / localizers and non-CT series are ignored and the
    ///     CT series with the most slices is loaded (others are reported);
    ///   - gantry tilt (sheared stack), duplicate slices, missing or unevenly spaced slices (resampled to an even grid);
    ///   - large thin-slice series are box-downsampled while reading, so peak memory stays near the final volume.
    /// Classic single-frame images only (not enhanced multi-frame CT).
    /// </summary>
    public static class DicomLoader
    {
        class Slice
        {
            public string series, seriesDescription, transferSyntax, modality, imageType;
            public int rows, cols, bits = 16, bitsStored, signed, samples = 1, frames = 1;
            public double slope = 1, intercept;
            public Vector3 ipp; public bool hasIpp;
            public Vector3 rowDir = Vector3.right, colDir = Vector3.up;
            public Vector2 pixelSpacing = Vector2.one;
            public long pixelOffset = -1; public int pixelLength; public bool encapsulated;
            public string path;
            public float t;   // position along the slice normal
        }

        const string ImplicitLE = "1.2.840.10008.1.2", ExplicitLE = "1.2.840.10008.1.2.1";
        const int HeaderPrefix = 64 * 1024;

        static readonly HashSet<string> SkipExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".json", ".txt", ".nrrd", ".nhdr", ".raw", ".obj", ".mtl", ".stl", ".zip", ".jpg", ".jpeg", ".png", ".bmp", ".gif",
            ".pdf", ".htm", ".html", ".xml", ".exe", ".dll", ".inf", ".ini", ".ico", ".css", ".js", ".mp4", ".avi",
        };

        public static bool LooksLikeDicom(string path)
        {
            try
            {
                using (var f = File.OpenRead(path))
                {
                    if (f.Length < 132) return false;
                    var b = new byte[132]; f.Read(b, 0, 132);
                    return b[128] == 'D' && b[129] == 'I' && b[130] == 'C' && b[131] == 'M';
                }
            }
            catch { return false; }
        }

        public static CtVolume LoadFolder(string dir, Action<string> progress = null) => LoadFolder(dir, progress, out _);

        /// <param name="maxDim">Axes longer than this are halved (box filter) while reading, as CtVolume.Downsampled does.</param>
        public static CtVolume LoadFolder(string dir, Action<string> progress, out string warning, int maxDim = 256)
        {
            var notes = new List<string>();
            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Where(f => !SkipExtensions.Contains(Path.GetExtension(f)) && !Path.GetFileName(f).Equals("DICOMDIR", StringComparison.OrdinalIgnoreCase)
                            && !Path.GetFileName(f).StartsWith("."))
                .ToList();

            // ---- headers (in parallel: a CD can hold thousands of images) ----
            var headers = new Slice[files.Count];
            int read = 0;
            Parallel.For(0, files.Count, i =>
            {
                headers[i] = ReadHeader(files[i]);
                int n = Interlocked.Increment(ref read);
                if (n % 50 == 0) progress?.Invoke($"Reading DICOM headers {n}/{files.Count}");
            });
            var all = headers.Where(s => s != null && s.pixelOffset >= 0).ToList();
            var usable = all.Where(s => s.hasIpp && s.rows > 0 && s.cols > 0 && s.samples == 1 && s.frames <= 1).ToList();
            if (usable.Count == 0)
            {
                var odd = headers.FirstOrDefault(s => s != null && (s.transferSyntax == "1.2.840.10008.1.2.2" || s.transferSyntax == "1.2.840.10008.1.2.1.99"));
                if (odd != null)
                    throw new Exception($"DICOM {DicomCodecs.Describe(odd.transferSyntax)} encoding is not supported: re-save the series uncompressed " +
                                        "(3D Slicer, Horos, `gdcmconv --raw`) or export it as NRRD.");
                if (all.Any(s => s.frames > 1))
                    throw new Exception("Enhanced multi-frame CT is not supported: export the series as classic single-frame DICOM or as NRRD from 3D Slicer.");
                throw new Exception($"No readable DICOM CT images in '{Path.GetFileName(dir)}' ({files.Count} files checked).");
            }

            // ---- series selection: one geometry per group, scouts out, CT first, then most slices ----
            var groups = usable.GroupBy(s => $"{s.series}|{s.rows}x{s.cols}|{Round(s.rowDir)}{Round(s.colDir)}")
                               .Select(g => g.ToList())
                               .Where(g => g.Count >= 3 && !g.All(s => (s.imageType ?? "").ToUpperInvariant().Contains("LOCALIZER")))
                               .ToList();
            if (groups.Count == 0) throw new Exception("No CT series with at least 3 slices (only scouts or single images found).");
            bool IsCt(List<Slice> g) => string.Equals(g[0].modality, "CT", StringComparison.OrdinalIgnoreCase);
            var group = groups.OrderByDescending(g => IsCt(g) ? 1 : 0).ThenByDescending(g => g.Count).First();
            var first = group[0];
            string seriesName = string.IsNullOrEmpty(first.seriesDescription) ? "unnamed series" : first.seriesDescription;
            int ctSeries = groups.Count(IsCt);
            if (ctSeries > 1)
                notes.Add($"The folder holds {ctSeries} CT series; loaded the largest, '{seriesName}' ({group.Count} images). " +
                          "To use another one, put only that series in the case folder.");
            if (!IsCt(group)) notes.Add($"The series is {first.modality}, not CT: Hounsfield thresholds may not fit.");

            foreach (var ts in group.Select(s => s.transferSyntax).Distinct())
                if (ts != ImplicitLE && ts != ExplicitLE && !DicomCodecs.Supports(ts))
                    throw new Exception($"Compressed DICOM ({DicomCodecs.Describe(ts)}) is not supported: re-save the series uncompressed " +
                                        "(3D Slicer, Horos, `gdcmconv --raw`) or export it as NRRD.");
            if (group.Any(s => s.bits != 16)) throw new Exception($"Only 16-bit CT pixels are supported (this series has {first.bits}-bit).");

            // ---- geometry ----
            var normal = Vector3.Cross(first.rowDir, first.colDir).normalized;
            foreach (var s in group) s.t = Vector3.Dot(s.ipp, normal);
            group = group.OrderBy(s => s.t).ToList();
            var stack = new List<Slice> { group[0] };
            for (int i = 1; i < group.Count; i++)
                if (group[i].t - stack[stack.Count - 1].t > 0.01f) stack.Add(group[i]);
            if (stack.Count < group.Count)
                notes.Add($"{group.Count - stack.Count} duplicate slice positions ignored (overlapping acquisitions in one series).");
            if (stack.Count < 3) throw new Exception("Fewer than 3 distinct slice positions in the series.");

            var gaps = new List<float>();
            for (int i = 1; i < stack.Count; i++) gaps.Add(stack[i].t - stack[i - 1].t);
            var sorted = gaps.OrderBy(g => g).ToList();
            float dz = sorted[sorted.Count / 2];
            float span = stack[stack.Count - 1].t - stack[0].t;
            int nUniform = Mathf.RoundToInt(span / dz) + 1;
            int missing = gaps.Sum(g => Mathf.Max(0, Mathf.RoundToInt(g / dz) - 1));
            bool uneven = gaps.Any(g => Mathf.Abs(g / dz - Mathf.Round(g / dz)) > 0.1f);
            if (missing > 0) notes.Add($"{missing} missing slices were filled by interpolation: check the CT is complete.");
            else if (uneven) notes.Add("Uneven slice spacing: the stack was resampled to an even grid.");

            // Gantry tilt: slice origins drift in-plane as the stack advances; the k axis follows the drift (sheared grid).
            var drift = stack[stack.Count - 1].ipp - stack[0].ipp - normal * span;
            var kStep = normal + drift / span;   // LPS mm per mm along the normal
            float tilt = Vector3.Angle(normal, kStep);
            if (tilt > 0.5f) notes.Add($"Gantry tilt of {tilt:0.0} degrees corrected.");

            // ---- output grid (box-decimated by powers of two, as CtVolume.Downsampled) ----
            int fx = Factor(first.cols, maxDim), fy = Factor(first.rows, maxDim), fz = Factor(nUniform, maxDim);
            int ox = first.cols / fx, oy = first.rows / fy, oz = nUniform / fz, plane = ox * oy;
            var srcSpacing = new Vector3(first.pixelSpacing.y, first.pixelSpacing.x, dz * kStep.magnitude);   // PixelSpacing = (row, column) spacing
            var vol = new CtVolume(ox, oy, oz)
            {
                spacing = Vector3.Scale(srcSpacing, new Vector3(fx, fy, fz)),
                description = $"DICOM {first.modality} '{seriesName}' {first.cols}x{first.rows}x{stack.Count}, " +
                              $"{first.pixelSpacing.y:0.###}x{first.pixelSpacing.x:0.###}x{dz:0.###} mm" + (fx * fy * fz > 1 ? $" -> {ox}x{oy}x{oz}" : ""),
            };
            vol.axes = new Matrix4x4(first.rowDir, first.colDir, kStep.normalized, new Vector4(0, 0, 0, 1));
            vol.origin = stack[0].ipp + vol.axes.MultiplyVector(Vector3.Scale(srcSpacing, new Vector3(fx - 1, fy - 1, fz - 1) * 0.5f));

            // ---- pixels: decode + in-plane decimation in parallel, then resample / decimate along the stack ----
            var planes = new short[stack.Count][];
            string failure = null;
            int done = 0;
            Parallel.For(0, stack.Count, k =>
            {
                if (failure != null) return;
                try
                {
                    var s = stack[k];
                    if (s.rows != first.rows || s.cols != first.cols) throw new Exception("slice size differs");
                    planes[k] = Shrink(ReadHu(s), s.cols, fx, fy, ox, oy);
                }
                catch (Exception e) { failure = $"{Path.GetFileName(stack[k].path)}: {e.Message}"; }
                int n = Interlocked.Increment(ref done);
                if (n % 10 == 0) progress?.Invoke($"Loading slice {n}/{stack.Count}");
            });
            if (failure != null) throw new Exception("Cannot read pixel data of " + failure);

            var acc = new float[plane];
            int src = 0;
            for (int k = 0; k < oz; k++)
            {
                Array.Clear(acc, 0, plane);
                for (int c = 0; c < fz; c++)
                {
                    float t = stack[0].t + (k * fz + c) * dz;
                    while (src + 2 < stack.Count && stack[src + 1].t < t) src++;
                    float w = Mathf.Clamp01((t - stack[src].t) / Mathf.Max(1e-4f, stack[src + 1].t - stack[src].t));
                    short[] a = planes[src], b = planes[src + 1];
                    if (w < 1e-3f) for (int p = 0; p < plane; p++) acc[p] += a[p];
                    else if (w > 1 - 1e-3f) for (int p = 0; p < plane; p++) acc[p] += b[p];
                    else for (int p = 0; p < plane; p++) acc[p] += a[p] + (b[p] - a[p]) * w;
                }
                int o = plane * k;
                for (int p = 0; p < plane; p++) vol.hu[o + p] = (short)Mathf.RoundToInt(acc[p] / fz);
            }

            warning = string.Join(" ", notes);
            return vol;
        }

        static int Factor(int n, int maxDim) { int f = 1; while (n / f > maxDim) f *= 2; return f; }

        static string Round(Vector3 v) => $"{v.x:0.00},{v.y:0.00},{v.z:0.00};";

        /// <summary>Box filter fx * fy (truncating the remainder, as CtVolume.Downsampled).</summary>
        static short[] Shrink(short[] hu, int cols, int fx, int fy, int ox, int oy)
        {
            if (fx == 1 && fy == 1 && cols == ox) return hu;
            var o = new short[ox * oy];
            int n = fx * fy;
            for (int j = 0; j < oy; j++)
            for (int i = 0; i < ox; i++)
            {
                int sum = 0;
                for (int b = 0; b < fy; b++) for (int a = 0; a < fx; a++) sum += hu[(j * fy + b) * cols + i * fx + a];
                o[j * ox + i] = (short)(sum / n);
            }
            return o;
        }

        /// <summary>One slice in Hounsfield units, clamped to [-1024, 3071] (padding outside the scan field becomes air).</summary>
        static short[] ReadHu(Slice s)
        {
            int n = s.rows * s.cols;
            var d = File.ReadAllBytes(s.path);
            ushort[] raw;
            if (s.encapsulated)
                raw = DicomCodecs.Decode(s.transferSyntax, DicomCodecs.Fragments(d, (int)s.pixelOffset), s.rows, s.cols, s.bits);
            else
            {
                if (s.pixelOffset + 2L * n > d.Length) throw new Exception("pixel data is truncated");
                raw = new ushort[n];
                Buffer.BlockCopy(d, (int)s.pixelOffset, raw, 0, 2 * n);   // little endian, as the device
            }
            int stored = s.bitsStored > 0 && s.bitsStored <= 16 ? s.bitsStored : 16;
            int mask = stored == 16 ? 0xFFFF : (1 << stored) - 1, sign = 1 << (stored - 1);
            var hu = new short[n];
            for (int p = 0; p < n; p++)
            {
                int v = raw[p] & mask;
                if (s.signed == 1 && (v & sign) != 0) v -= mask + 1;
                double h = v * s.slope + s.intercept;
                hu[p] = (short)(h < -1024 ? -1024 : h > 3071 ? 3071 : Math.Round(h));
            }
            return hu;
        }

        // ---- header parsing -------------------------------------------------------------------

        static readonly HashSet<string> LongVr = new HashSet<string> { "OB", "OW", "OF", "SQ", "UT", "UN", "UC", "UR", "OD", "OL", "OV", "SV", "UV" };

        /// <summary>Parses the header from the first 64 kB, falling back to the whole file when it is longer.</summary>
        static Slice ReadHeader(string path)
        {
            try
            {
                byte[] d;
                long length;
                using (var f = File.OpenRead(path))
                {
                    length = f.Length;
                    if (length < 16) return null;
                    d = new byte[Math.Min(length, HeaderPrefix)];
                    int got = 0; while (got < d.Length) { int r = f.Read(d, got, d.Length - got); if (r <= 0) break; got += r; }
                }
                var s = Parse(d, path);
                if ((s == null || s.pixelOffset < 0) && length > d.Length) s = Parse(File.ReadAllBytes(path), path);
                return s;
            }
            catch { return null; }
        }

        static Slice Parse(byte[] d, string path)
        {
            int pos = 0;
            if (d.Length >= 132 && d[128] == 'D' && d[129] == 'I' && d[130] == 'C' && d[131] == 'M') pos = 132;
            else if (U16(d, 0) != 0x0002 && U16(d, 0) != 0x0008) return null;   // no preamble and not a bare data set
            var s = new Slice { path = path, transferSyntax = ImplicitLE };
            try
            {
                // Group 0002 is always explicit VR little endian.
                while (pos + 8 <= d.Length && U16(d, pos) == 0x0002)
                {
                    ReadElement(d, ref pos, true, out uint tag, out int len, out int valPos);
                    if (tag == 0x00020010) s.transferSyntax = Str(d, valPos, len);
                }
                if (s.transferSyntax == "1.2.840.10008.1.2.2" || s.transferSyntax == "1.2.840.10008.1.2.1.99") return s;   // big endian / deflated: reported later
                bool explicitVr = s.transferSyntax != ImplicitLE;
                while (pos + 8 <= d.Length)
                {
                    ReadElement(d, ref pos, explicitVr, out uint tag, out int len, out int valPos);
                    if (tag == 0x7FE00010)
                    {
                        s.pixelOffset = valPos; s.pixelLength = len; s.encapsulated = len < 0;
                        return s;
                    }
                    switch (tag)
                    {
                        case 0x00080008: s.imageType = Str(d, valPos, len); break;
                        case 0x00080060: s.modality = Str(d, valPos, len); break;
                        case 0x0008103E: s.seriesDescription = Str(d, valPos, len); break;
                        case 0x0020000E: s.series = Str(d, valPos, len); break;
                        case 0x00280002: s.samples = U16(d, valPos); break;
                        case 0x00280008: s.frames = (int)Num(Str(d, valPos, len))[0]; break;
                        case 0x00280010: s.rows = U16(d, valPos); break;
                        case 0x00280011: s.cols = U16(d, valPos); break;
                        case 0x00280100: s.bits = U16(d, valPos); break;
                        case 0x00280101: s.bitsStored = U16(d, valPos); break;
                        case 0x00280103: s.signed = U16(d, valPos); break;
                        case 0x00281052: s.intercept = Num(Str(d, valPos, len))[0]; break;
                        case 0x00281053: { double v = Num(Str(d, valPos, len))[0]; s.slope = v == 0 ? 1 : v; break; }
                        case 0x00280030: { var v = Num(Str(d, valPos, len)); if (v.Length >= 2 && v[0] > 0 && v[1] > 0) s.pixelSpacing = new Vector2((float)v[0], (float)v[1]); break; }
                        case 0x00200032: { var v = Num(Str(d, valPos, len)); if (v.Length >= 3) { s.ipp = new Vector3((float)v[0], (float)v[1], (float)v[2]); s.hasIpp = true; } break; }
                        case 0x00200037:
                        {
                            var v = Num(Str(d, valPos, len));
                            if (v.Length < 6) break;
                            s.rowDir = new Vector3((float)v[0], (float)v[1], (float)v[2]).normalized;
                            s.colDir = new Vector3((float)v[3], (float)v[4], (float)v[5]).normalized;
                            break;
                        }
                    }
                }
            }
            catch (Exception) { return null; }
            return s;
        }

        /// <summary>Reads one element header; skips sequences / undefined-length items entirely.
        /// len = -1 only for undefined-length (encapsulated) pixel data.</summary>
        static void ReadElement(byte[] d, ref int pos, bool explicitVr, out uint tag, out int len, out int valPos)
        {
            tag = ((uint)U16(d, pos) << 16) | (uint)U16(d, pos + 2);
            pos += 4;
            string vr = null;
            uint rawLen;
            bool isDelimiter = (tag >> 16) == 0xFFFE;
            if (explicitVr && !isDelimiter)
            {
                vr = Encoding.ASCII.GetString(d, pos, 2);
                if (LongVr.Contains(vr)) { rawLen = U32(d, pos + 4); pos += 8; }
                else { rawLen = (uint)U16(d, pos + 2); pos += 4; }
            }
            else { rawLen = U32(d, pos); pos += 4; }

            valPos = pos;
            if (rawLen == 0xFFFFFFFF)
            {
                if (tag == 0x7FE00010) { len = -1; return; }
                // Sequence (or item) with undefined length; an undefined-length UN holds implicit VR data (PS3.5 6.2.2).
                SkipUndefined(d, ref pos, explicitVr && vr != "UN");
                len = 0;
                return;
            }
            len = (int)rawLen;
            if (vr == "SQ" || (!explicitVr && tag != 0x7FE00010 && LooksLikeSequence(d, pos, len))) { pos += len; return; }
            if (tag != 0x7FE00010) pos += len;
        }

        static bool LooksLikeSequence(byte[] d, int pos, int len) =>
            len >= 8 && pos + 4 <= d.Length && U16(d, pos) == 0xFFFE && U16(d, pos + 2) == 0xE000;

        /// <summary>Skips until the matching sequence / item delimiter.</summary>
        static void SkipUndefined(byte[] d, ref int pos, bool explicitVr)
        {
            while (pos + 8 <= d.Length)
            {
                uint tag = ((uint)U16(d, pos) << 16) | (uint)U16(d, pos + 2);
                if (tag == 0xFFFEE0DD || tag == 0xFFFEE00D) { pos += 8; return; }   // sequence / item delimiter
                if (tag == 0xFFFEE000)
                {
                    uint ilen = U32(d, pos + 4); pos += 8;
                    if (ilen == 0xFFFFFFFF) SkipUndefined(d, ref pos, explicitVr);   // item content up to item delimiter
                    else pos += (int)ilen;
                    continue;
                }
                ReadElement(d, ref pos, explicitVr, out _, out int len, out _);
                if (len < 0) throw new Exception("pixel data inside a sequence");
            }
            throw new Exception("truncated sequence");
        }

        static int U16(byte[] d, int p) => d[p] | (d[p + 1] << 8);
        static uint U32(byte[] d, int p) => (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24));
        static string Str(byte[] d, int p, int len) => p >= d.Length ? "" : Encoding.ASCII.GetString(d, p, Math.Min(len, d.Length - p)).Trim('\0', ' ');

        static double[] Num(string s) =>
            s.Split('\\').Select(t => double.TryParse(t.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0).ToArray();
    }
}
