using System;
using System.Collections.Generic;

namespace SeptoMentor.Nav
{
    /// <summary>
    /// Decoders for the compressed DICOM transfer syntaxes hospital CDs use for CT:
    /// JPEG Lossless (ITU T.81 process 14, 1.2.840.10008.1.2.4.57 / .70) and RLE Lossless (1.2.840.10008.1.2.5).
    /// Grey-scale, single frame only. Output is the raw stored sample (no sign handling, no rescale).
    /// </summary>
    public static class DicomCodecs
    {
        public const string JpegLossless = "1.2.840.10008.1.2.4.57", JpegLosslessSV1 = "1.2.840.10008.1.2.4.70", RleLossless = "1.2.840.10008.1.2.5";

        public static bool Supports(string ts) => ts == JpegLossless || ts == JpegLosslessSV1 || ts == RleLossless;

        public static string Describe(string ts)
        {
            switch (ts)
            {
                case "1.2.840.10008.1.2.2": return "explicit VR big endian";
                case "1.2.840.10008.1.2.1.99": return "deflated";
                case "1.2.840.10008.1.2.4.50": case "1.2.840.10008.1.2.4.51": return "lossy JPEG";
                case "1.2.840.10008.1.2.4.80": case "1.2.840.10008.1.2.4.81": return "JPEG-LS";
                case "1.2.840.10008.1.2.4.90": case "1.2.840.10008.1.2.4.91": return "JPEG 2000";
                case "1.2.840.10008.1.2.4.201": case "1.2.840.10008.1.2.4.202": case "1.2.840.10008.1.2.4.203": return "HTJ2K";
                default: return ts;
            }
        }

        /// <summary>Concatenates the fragments of encapsulated pixel data that starts (after the 7FE0,0010 header) at pos.</summary>
        public static byte[] Fragments(byte[] d, int pos)
        {
            var parts = new List<ArraySegment<byte>>();
            bool first = true;
            int total = 0;
            while (pos + 8 <= d.Length)
            {
                int g = d[pos] | (d[pos + 1] << 8), e = d[pos + 2] | (d[pos + 3] << 8);
                int len = d[pos + 4] | (d[pos + 5] << 8) | (d[pos + 6] << 16) | (d[pos + 7] << 24);
                pos += 8;
                if (g != 0xFFFE || e == 0xE0DD) break;           // sequence delimiter
                if (e != 0xE000 || len < 0) throw new Exception("bad pixel data fragment");
                len = Math.Min(len, d.Length - pos);
                if (!first) { parts.Add(new ArraySegment<byte>(d, pos, len)); total += len; }   // the first item is the offset table
                first = false;
                pos += len;
            }
            var o = new byte[total];
            int at = 0;
            foreach (var p in parts) { Buffer.BlockCopy(p.Array, p.Offset, o, at, p.Count); at += p.Count; }
            return o;
        }

        public static ushort[] Decode(string ts, byte[] data, int rows, int cols, int bitsAllocated)
        {
            if (ts == RleLossless) return Rle(data, rows, cols, bitsAllocated);
            return JpegLosslessDecode(data, rows, cols);
        }

        // ---- RLE (PS3.5 annex G) ---------------------------------------------------------------

        static ushort[] Rle(byte[] d, int rows, int cols, int bitsAllocated)
        {
            int n = rows * cols, bytes = bitsAllocated / 8;
            int segs = I32(d, 0);
            if (segs != bytes) throw new Exception($"RLE: {segs} segments for {bitsAllocated}-bit grey-scale data");
            var o = new ushort[n];
            var seg = new byte[n];
            for (int s = 0; s < segs; s++)
            {
                int start = I32(d, 4 + 4 * s), end = s + 1 < segs ? I32(d, 8 + 4 * s) : d.Length;
                int p = start, q = 0;
                while (p < end && q < n)
                {
                    int c = (sbyte)d[p++];
                    if (c >= 0) { int k = Math.Min(c + 1, Math.Min(n - q, end - p)); Buffer.BlockCopy(d, p, seg, q, k); p += k; q += k; }
                    else if (c != -128) { byte b = d[p++]; for (int k = Math.Min(1 - c, n - q); k > 0; k--) seg[q++] = b; }
                }
                int shift = 8 * (segs - 1 - s);   // segment 0 holds the most significant bytes
                for (int i = 0; i < n; i++) o[i] |= (ushort)(seg[i] << shift);
                Array.Clear(seg, 0, n);
            }
            return o;
        }

        static int I32(byte[] d, int p) => d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24);

        // ---- JPEG lossless (ITU T.81 annex H) --------------------------------------------------

        class Huffman
        {
            // 16-bit look-ahead: entry = (code length << 8) | symbol, 0 = invalid code.
            public readonly ushort[] fast = new ushort[1 << 16];

            public Huffman(byte[] counts, byte[] symbols)
            {
                int code = 0, k = 0;
                for (int len = 1; len <= 16; len++)
                {
                    for (int i = 0; i < counts[len - 1]; i++, k++, code++)
                    {
                        int lo = code << (16 - len), hi = (code + 1) << (16 - len);
                        if (hi > fast.Length) throw new Exception("JPEG: bad Huffman table");
                        for (int c = lo; c < hi; c++) fast[c] = (ushort)((len << 8) | symbols[k]);
                    }
                    code <<= 1;
                }
            }
        }

        class Bits
        {
            readonly byte[] d; int p; ulong buf; int n; public bool marker;
            public Bits(byte[] d, int p) { this.d = d; this.p = p; }
            public int Pos => p;

            void Fill()
            {
                while (n <= 56)
                {
                    int b = 0;
                    if (!marker && p < d.Length)
                    {
                        b = d[p];
                        if (b == 0xFF)
                        {
                            int nx = p + 1 < d.Length ? d[p + 1] : 0xD9;
                            if (nx == 0) p += 2;               // stuffed zero
                            else { marker = true; b = 0; }    // marker: feed zeros from here on
                        }
                        else p++;
                    }
                    buf |= (ulong)b << (56 - n);
                    n += 8;
                }
            }

            public int Peek16() { if (n < 16) Fill(); return (int)(buf >> 48); }
            public void Skip(int k) { buf <<= k; n -= k; }
            public int Get(int k)
            {
                if (k == 0) return 0;
                if (n < k) Fill();
                int v = (int)(buf >> (64 - k)); buf <<= k; n -= k;
                return v;
            }

            /// <summary>Drops buffered bits and moves past the next RSTn marker.</summary>
            public void Restart()
            {
                buf = 0; n = 0; marker = false;
                while (p + 1 < d.Length && !(d[p] == 0xFF && d[p + 1] >= 0xD0 && d[p + 1] <= 0xD7)) p++;
                p += 2;
            }
        }

        static ushort[] JpegLosslessDecode(byte[] d, int rows, int cols)
        {
            var tables = new Huffman[4];
            int precision = 16, height = 0, width = 0, restart = 0, p = 0;
            if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) throw new Exception("JPEG: missing SOI");
            p = 2;
            while (p + 4 <= d.Length)
            {
                if (d[p] != 0xFF) { p++; continue; }
                int m = d[p + 1];
                if (m == 0xFF) { p++; continue; }
                p += 2;
                if (m == 0xD8 || (m >= 0xD0 && m <= 0xD7) || m == 0x01) continue;
                if (m == 0xD9) break;
                int len = (d[p] << 8) | d[p + 1];
                int seg = p + 2, next = p + len;
                switch (m)
                {
                    case 0xC3:
                        precision = d[seg]; height = (d[seg + 1] << 8) | d[seg + 2]; width = (d[seg + 3] << 8) | d[seg + 4];
                        if (d[seg + 5] != 1) throw new Exception("JPEG: only single-component images are supported");
                        break;
                    case 0xC0: case 0xC1: case 0xC2: case 0xC5: case 0xC6: case 0xC7: case 0xC9: case 0xCA: case 0xCB: case 0xCD: case 0xCE: case 0xCF:
                        throw new Exception($"JPEG: SOF{m - 0xC0} is not lossless Huffman (process 14)");
                    case 0xC4:
                        for (int q = seg; q < next;)
                        {
                            int th = d[q] & 0x0F; q++;
                            var counts = new byte[16]; Buffer.BlockCopy(d, q, counts, 0, 16); q += 16;
                            int total = 0; foreach (var c in counts) total += c;
                            var sym = new byte[total]; Buffer.BlockCopy(d, q, sym, 0, total); q += total;
                            tables[th & 3] = new Huffman(counts, sym);
                        }
                        break;
                    case 0xDD: restart = (d[seg] << 8) | d[seg + 1]; break;
                    case 0xDA:
                    {
                        if (d[seg] != 1) throw new Exception("JPEG: interleaved scans are not supported");
                        var table = tables[(d[seg + 2] >> 4) & 3] ?? throw new Exception("JPEG: missing Huffman table");
                        int predictor = d[seg + 3], pt = d[seg + 5] & 0x0F;
                        if (width == 0 || height == 0) throw new Exception("JPEG: missing SOF3 before SOS");
                        if (width != cols || height != rows) throw new Exception($"JPEG: {width}x{height} image for a {cols}x{rows} DICOM");
                        return Scan(d, next, table, predictor, pt, precision, width, height, restart);
                    }
                }
                p = next;
            }
            throw new Exception("JPEG: no scan found");
        }

        static ushort[] Scan(byte[] d, int start, Huffman h, int predictor, int pt, int precision, int w, int hgt, int restart)
        {
            var o = new ushort[w * hgt];
            var bits = new Bits(d, start);
            int mask = 0xFFFF, initial = 1 << (precision - pt - 1);
            int sinceRestart = 0, intervalRow = 0;   // row in which the current restart interval began
            var raw = new int[w * hgt];
            for (int y = 0; y < hgt; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (restart > 0 && sinceRestart == restart)
                    {
                        bits.Restart(); sinceRestart = 0; intervalRow = y;
                    }
                    int i = y * w + x, pred;
                    bool intervalStart = sinceRestart == 0;
                    if (intervalStart) pred = initial;
                    else if (y == intervalRow) pred = raw[i - 1];
                    else if (x == 0) pred = raw[i - w];
                    else
                    {
                        int ra = raw[i - 1], rb = raw[i - w], rc = raw[i - w - 1];
                        switch (predictor)
                        {
                            case 1: pred = ra; break;
                            case 2: pred = rb; break;
                            case 3: pred = rc; break;
                            case 4: pred = ra + rb - rc; break;
                            case 5: pred = ra + ((rb - rc) >> 1); break;
                            case 6: pred = rb + ((ra - rc) >> 1); break;
                            case 7: pred = (ra + rb) >> 1; break;
                            default: pred = ra; break;
                        }
                    }
                    int code = h.fast[bits.Peek16()];
                    if (code == 0) throw new Exception("JPEG: corrupt entropy-coded data");
                    bits.Skip(code >> 8);
                    int s = code & 0xFF, diff;
                    if (s == 0) diff = 0;
                    else if (s == 16) diff = 32768;
                    else
                    {
                        int v = bits.Get(s);
                        diff = v < (1 << (s - 1)) ? v - (1 << s) + 1 : v;
                    }
                    int val = (pred + diff) & mask;
                    raw[i] = val;
                    o[i] = (ushort)(val << pt);
                    sinceRestart++;
                }
            }
            return o;
        }
    }
}
