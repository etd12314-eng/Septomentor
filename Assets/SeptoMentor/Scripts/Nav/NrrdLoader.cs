using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using UnityEngine;

namespace SeptoMentor.Nav
{
    /// <summary>
    /// NRRD reader for 3D scalar volumes as saved by 3D Slicer (File > Save, .nrrd):
    /// raw or gzip encoding, attached or detached data, LPS or RAS space.
    /// </summary>
    public static class NrrdLoader
    {
        public static CtVolume Load(string path)
        {
            var bytes = File.ReadAllBytes(path);
            int dataStart = HeaderEnd(bytes);
            if (dataStart < 0) throw new Exception("Not a NRRD file: " + path);
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in Encoding.ASCII.GetString(bytes, 0, dataStart).Split('\n').Skip(1))
            {
                var l = line.TrimEnd('\r');
                if (l.StartsWith("#") || l.Length == 0) continue;
                int c = l.IndexOf(':');
                if (c > 0 && !l.Substring(0, c).Contains("=")) fields[l.Substring(0, c).Trim()] = l.Substring(c + 1).Trim();
            }

            int[] sizes = Ints(Req(fields, "sizes"));
            if (sizes.Length != 3) throw new Exception("Only 3D scalar NRRD volumes are supported.");
            string type = Req(fields, "type").ToLowerInvariant();
            string enc = fields.TryGetValue("encoding", out var e) ? e.ToLowerInvariant() : "raw";
            bool big = fields.TryGetValue("endian", out var en) && en.ToLowerInvariant() == "big";
            string space = fields.TryGetValue("space", out var sp) ? sp.ToLowerInvariant() : "left-posterior-superior";
            bool ras = space.StartsWith("right-anterior") || space == "ras";

            var vol = new CtVolume(sizes[0], sizes[1], sizes[2]) { description = $"NRRD {sizes[0]}x{sizes[1]}x{sizes[2]}" };
            var dirs = fields.TryGetValue("space directions", out var sd) ? Vectors(sd) : null;
            if (dirs == null || dirs.Count != 3)
            {
                var spc = fields.TryGetValue("spacings", out var s2) ? Doubles(s2) : new double[] { 1, 1, 1 };
                dirs = new List<Vector3> { new Vector3((float)spc[0], 0, 0), new Vector3(0, (float)spc[1], 0), new Vector3(0, 0, (float)spc[2]) };
            }
            var origin = fields.TryGetValue("space origin", out var so) ? Vectors(so)[0] : Vector3.zero;
            if (ras)
            {
                for (int i = 0; i < 3; i++) dirs[i] = new Vector3(-dirs[i].x, -dirs[i].y, dirs[i].z);
                origin = new Vector3(-origin.x, -origin.y, origin.z);
            }
            vol.spacing = new Vector3(dirs[0].magnitude, dirs[1].magnitude, dirs[2].magnitude);
            vol.axes = new Matrix4x4(dirs[0].normalized, dirs[1].normalized, dirs[2].normalized, new Vector4(0, 0, 0, 1));
            vol.origin = origin;

            Stream data;
            if (fields.TryGetValue("data file", out var df) || fields.TryGetValue("datafile", out df))
                data = File.OpenRead(Path.Combine(Path.GetDirectoryName(path), df));
            else
                data = new MemoryStream(bytes, dataStart, bytes.Length - dataStart);
            if (enc == "gzip" || enc == "gz") data = new GZipStream(data, CompressionMode.Decompress);
            else if (enc != "raw") throw new Exception("NRRD encoding not supported: " + enc);

            using (data)
            {
                int bpp = BytesPer(type);
                var buf = new byte[bpp * 65536];
                int n = vol.hu.Length, done = 0;
                while (done < n)
                {
                    int want = Math.Min(65536, n - done) * bpp, got = 0;
                    while (got < want) { int r = data.Read(buf, got, want - got); if (r <= 0) throw new Exception("NRRD data truncated"); got += r; }
                    for (int i = 0; i < want / bpp; i++)
                        vol.hu[done + i] = (short)Mathf.Clamp((float)Value(buf, i * bpp, type, big), -1024, 3071);
                    done += want / bpp;
                }
            }
            return vol;
        }

        static int HeaderEnd(byte[] b)
        {
            if (b.Length < 8 || Encoding.ASCII.GetString(b, 0, 4) != "NRRD") return -1;
            for (int i = 0; i < b.Length - 1; i++)
            {
                if (b[i] == '\n' && b[i + 1] == '\n') return i + 2;
                if (i < b.Length - 3 && b[i] == '\r' && b[i + 1] == '\n' && b[i + 2] == '\r' && b[i + 3] == '\n') return i + 4;
            }
            return -1;
        }

        static int BytesPer(string t)
        {
            switch (t)
            {
                case "uchar": case "unsigned char": case "uint8": case "uint8_t": case "signed char": case "int8": case "int8_t": return 1;
                case "short": case "short int": case "signed short": case "int16": case "int16_t":
                case "ushort": case "unsigned short": case "uint16": case "uint16_t": return 2;
                case "int": case "signed int": case "int32": case "int32_t": case "uint": case "unsigned int": case "uint32": case "uint32_t": case "float": return 4;
                case "double": return 8;
            }
            throw new Exception("NRRD type not supported: " + t);
        }

        static double Value(byte[] b, int p, string t, bool big)
        {
            int n = BytesPer(t);
            if (big) Array.Reverse(b, p, n);
            switch (t)
            {
                case "uchar": case "unsigned char": case "uint8": case "uint8_t": return b[p];
                case "signed char": case "int8": case "int8_t": return (sbyte)b[p];
                case "ushort": case "unsigned short": case "uint16": case "uint16_t": return BitConverter.ToUInt16(b, p);
                case "float": return BitConverter.ToSingle(b, p);
                case "double": return BitConverter.ToDouble(b, p);
                case "uint": case "unsigned int": case "uint32": case "uint32_t": return BitConverter.ToUInt32(b, p);
                case "int": case "signed int": case "int32": case "int32_t": return BitConverter.ToInt32(b, p);
                default: return BitConverter.ToInt16(b, p);
            }
        }

        static string Req(Dictionary<string, string> f, string k) =>
            f.TryGetValue(k, out var v) ? v : throw new Exception("NRRD field missing: " + k);

        static int[] Ints(string s) => s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(t => int.Parse(t)).ToArray();

        static double[] Doubles(string s) =>
            s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
             .Select(t => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 1).ToArray();

        /// <summary>Parses "(a,b,c) (d,e,f) none" -> vectors (skips "none").</summary>
        static List<Vector3> Vectors(string s)
        {
            var list = new List<Vector3>();
            foreach (var tok in s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!tok.StartsWith("(")) continue;
                var v = tok.Trim('(', ')').Split(',').Select(t => float.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
                list.Add(new Vector3(v[0], v[1], v[2]));
            }
            return list;
        }
    }
}
