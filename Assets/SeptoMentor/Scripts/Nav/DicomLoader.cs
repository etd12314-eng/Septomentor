using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace SeptoMentor.Nav
{
    /// <summary>
    /// Minimal DICOM CT series reader: uncompressed little-endian transfer syntaxes only
    /// (implicit 1.2.840.10008.1.2, explicit 1.2.840.10008.1.2.1). Compressed series must be
    /// re-saved uncompressed first (3D Slicer, Horos, or `gdcmconv --raw`), or exported as NRRD.
    /// Picks the series with the most slices, sorts slices along the slice normal.
    /// </summary>
    public static class DicomLoader
    {
        class Slice
        {
            public string series, transferSyntax, modality;
            public int rows, cols, bits = 16, signed;
            public double slope = 1, intercept;
            public Vector3 ipp; public bool hasIpp;
            public Vector3 rowDir = Vector3.right, colDir = Vector3.up;
            public Vector2 pixelSpacing = Vector2.one;
            public long pixelOffset = -1; public int pixelLength;
            public string path;
        }

        const string ImplicitLE = "1.2.840.10008.1.2", ExplicitLE = "1.2.840.10008.1.2.1";

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

        public static CtVolume LoadFolder(string dir, Action<string> progress = null)
        {
            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".json") && !f.EndsWith(".txt") && !f.EndsWith(".nrrd")).ToList();
            var slices = new List<Slice>();
            int n = 0;
            foreach (var f in files)
            {
                if (++n % 20 == 0) progress?.Invoke($"Reading DICOM {n}/{files.Count}");
                var s = ReadHeader(f);
                if (s != null && s.pixelOffset >= 0 && s.hasIpp) slices.Add(s);
            }
            if (slices.Count == 0) throw new Exception("No readable DICOM images in " + dir);

            var group = slices.GroupBy(s => s.series ?? "").OrderByDescending(g => g.Count()).First().ToList();
            var first = group[0];
            if (first.transferSyntax != ImplicitLE && first.transferSyntax != ExplicitLE)
                throw new Exception($"Compressed DICOM ({first.transferSyntax}) not supported: re-save uncompressed or export NRRD from 3D Slicer.");
            if (group.Count < 3) throw new Exception("Series has fewer than 3 slices.");

            var normal = Vector3.Cross(first.rowDir, first.colDir).normalized;
            group = group.Where(s => s.rows == first.rows && s.cols == first.cols)
                         .OrderBy(s => Vector3.Dot(s.ipp, normal)).ToList();
            var gaps = new List<float>();
            for (int i = 1; i < group.Count; i++) gaps.Add(Vector3.Dot(group[i].ipp - group[i - 1].ipp, normal));
            gaps.Sort();
            float dz = gaps[gaps.Count / 2];
            if (dz <= 1e-3f) throw new Exception("Duplicate slice positions: the folder may hold several overlapping series.");

            var vol = new CtVolume(first.cols, first.rows, group.Count)
            {
                spacing = new Vector3(first.pixelSpacing.y, first.pixelSpacing.x, dz),   // PixelSpacing = (row spacing, column spacing)
                origin = first.ipp,
                description = $"DICOM {first.modality} {first.cols}x{first.rows}x{group.Count}",
            };
            vol.axes = new Matrix4x4(first.rowDir, first.colDir, normal, new Vector4(0, 0, 0, 1));

            int plane = first.rows * first.cols;
            for (int k = 0; k < group.Count; k++)
            {
                if (k % 10 == 0) progress?.Invoke($"Loading slice {k + 1}/{group.Count}");
                var s = group[k];
                if (s.bits != 16) throw new Exception("Only 16-bit CT pixels are supported.");
                using (var f = File.OpenRead(s.path))
                {
                    f.Position = s.pixelOffset;
                    var buf = new byte[plane * 2];
                    int got = 0; while (got < buf.Length) { int r = f.Read(buf, got, buf.Length - got); if (r <= 0) break; got += r; }
                    for (int p = 0; p < plane; p++)
                    {
                        int raw = buf[2 * p] | (buf[2 * p + 1] << 8);
                        if (s.signed == 1 && raw > 32767) raw -= 65536;
                        double v = raw * s.slope + s.intercept;
                        vol.hu[p + plane * k] = (short)Math.Max(-1024, Math.Min(3071, v));
                    }
                }
            }
            return vol;
        }

        // ---- header parsing -------------------------------------------------------------------

        static readonly HashSet<string> LongVr = new HashSet<string> { "OB", "OW", "OF", "SQ", "UT", "UN", "UC", "UR", "OD", "OL", "OV", "SV", "UV" };

        static Slice ReadHeader(string path)
        {
            byte[] d;
            try { d = File.ReadAllBytes(path); } catch { return null; }
            int pos = 0;
            if (d.Length >= 132 && d[128] == 'D' && d[129] == 'I' && d[130] == 'C' && d[131] == 'M') pos = 132;
            var s = new Slice { path = path, transferSyntax = ImplicitLE };
            try
            {
                // Group 0002 is always explicit VR little endian.
                while (pos + 8 <= d.Length && U16(d, pos) == 0x0002)
                {
                    ReadElement(d, ref pos, true, out uint tag, out int len, out int valPos);
                    if (tag == 0x00020010) s.transferSyntax = Str(d, valPos, len);
                }
                bool explicitVr = s.transferSyntax != ImplicitLE;
                while (pos + 8 <= d.Length)
                {
                    ReadElement(d, ref pos, explicitVr, out uint tag, out int len, out int valPos);
                    if (tag == 0x7FE00010)
                    {
                        if (len < 0) return s;   // encapsulated (compressed) pixel data
                        s.pixelOffset = valPos; s.pixelLength = len;
                        return s;
                    }
                    switch (tag)
                    {
                        case 0x0020000E: s.series = Str(d, valPos, len); break;
                        case 0x00080060: s.modality = Str(d, valPos, len); break;
                        case 0x00280010: s.rows = U16(d, valPos); break;
                        case 0x00280011: s.cols = U16(d, valPos); break;
                        case 0x00280100: s.bits = U16(d, valPos); break;
                        case 0x00280103: s.signed = U16(d, valPos); break;
                        case 0x00281052: s.intercept = Num(Str(d, valPos, len))[0]; break;
                        case 0x00281053: s.slope = Num(Str(d, valPos, len))[0]; break;
                        case 0x00280030: { var v = Num(Str(d, valPos, len)); s.pixelSpacing = new Vector2((float)v[0], (float)v[1]); break; }
                        case 0x00200032: { var v = Num(Str(d, valPos, len)); s.ipp = new Vector3((float)v[0], (float)v[1], (float)v[2]); s.hasIpp = true; break; }
                        case 0x00200037:
                        {
                            var v = Num(Str(d, valPos, len));
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
        /// len = -1 only for undefined-length pixel data.</summary>
        static void ReadElement(byte[] d, ref int pos, bool explicitVr, out uint tag, out int len, out int valPos)
        {
            tag = ((uint)U16(d, pos) << 16) | U16(d, pos + 2);
            pos += 4;
            string vr = null;
            uint rawLen;
            bool isDelimiter = (tag >> 16) == 0xFFFE;
            if (explicitVr && !isDelimiter)
            {
                vr = Encoding.ASCII.GetString(d, pos, 2);
                if (LongVr.Contains(vr)) { rawLen = U32(d, pos + 4); pos += 8; }
                else { rawLen = U16(d, pos + 2); pos += 4; }
            }
            else { rawLen = U32(d, pos); pos += 4; }

            valPos = pos;
            if (rawLen == 0xFFFFFFFF)
            {
                if (tag == 0x7FE00010) { len = -1; return; }
                SkipUndefined(d, ref pos, explicitVr);   // sequence (or item) with undefined length
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
                uint tag = ((uint)U16(d, pos) << 16) | U16(d, pos + 2);
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
        }

        static int U16(byte[] d, int p) => d[p] | (d[p + 1] << 8);
        static uint U32(byte[] d, int p) => (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24));
        static string Str(byte[] d, int p, int len) => Encoding.ASCII.GetString(d, p, Math.Min(len, d.Length - p)).Trim('\0', ' ');

        static double[] Num(string s) =>
            s.Split('\\').Select(t => double.TryParse(t.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0).ToArray();
    }
}
