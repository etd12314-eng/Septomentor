using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace SeptoMentor.Nav
{
    /// <summary>
    /// Patient cases live on the headset in
    ///   /sdcard/Android/data/org.septomentor.app/files/Cases/&lt;case name&gt;/
    /// (Application.persistentDataPath/Cases). A case folder holds ONE of:
    ///   - an uncompressed DICOM CT series (any file names, sub-folders allowed), or
    ///   - a .nrrd volume saved from 3D Slicer,
    /// plus, optionally:
    ///   - landmarks.mrk.json / *.mrk.json (3D Slicer markups; labels as in RequiredLandmarks),
    ///   - *.obj segment meshes exported from Slicer in LPS millimetres (shown as overlays).
    /// Loading runs on a worker thread; meshes are created later on the main thread.
    /// </summary>
    public static class CaseLoader
    {
        public const string PhantomName = "Phantom (synthetic)";

        /// <summary>Skin landmarks touched with the pointer for paired-point registration.</summary>
        public static readonly string[] RegistrationLandmarks = { "Nasion", "Pronasale", "Subnasale", "Endocanthion R", "Endocanthion L" };

        public static string CasesRoot => Path.Combine(Application.persistentDataPath, "Cases");

        public static List<string> ListCases()
        {
            var list = new List<string> { PhantomName };
            try
            {
                Directory.CreateDirectory(CasesRoot);
                list.AddRange(Directory.GetDirectories(CasesRoot).Select(d => Path.GetFileName(d)).OrderBy(n => n));
            }
            catch (Exception e) { Debug.LogWarning("[SeptoMentor] cannot list cases: " + e.Message); }
            return list;
        }

        /// <summary>Worker-thread safe (no Unity object creation).</summary>
        public static CaseData Load(string name, string casesRoot, Action<string> progress)
        {
            if (name == PhantomName)
            {
                progress?.Invoke("Generating synthetic head CT...");
                return CtPhantom.Generate();
            }
            string dir = Path.Combine(casesRoot, name);
            var data = new CaseData { name = name };
            var nrrd = Directory.GetFiles(dir, "*.nrrd", SearchOption.AllDirectories).FirstOrDefault();
            if (nrrd != null) { progress?.Invoke("Reading " + Path.GetFileName(nrrd)); data.volume = NrrdLoader.Load(nrrd); }
            else data.volume = DicomLoader.LoadFolder(dir, progress);

            progress?.Invoke("Preparing volume...");
            data.volume = data.volume.Downsampled(256);

            foreach (var f in Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories))
                try { ReadMarkups(f, data); } catch (Exception e) { data.warning += $"Landmarks {Path.GetFileName(f)}: {e.Message}. "; }
            if (!data.TryGetLandmark("Pronasale", out _))
            {
                var tip = AutoNoseTip(data.volume);
                if (tip.HasValue) data.landmarks.Add(new Landmark("Pronasale", tip.Value));
            }
            return data;
        }

        [Serializable] class MarkupsFile { public Markup[] markups; }
        [Serializable] class Markup { public string coordinateSystem; public ControlPoint[] controlPoints; }
        [Serializable] class ControlPoint { public string label; public float[] position; }

        static void ReadMarkups(string path, CaseData data)
        {
            var file = JsonUtility.FromJson<MarkupsFile>(File.ReadAllText(path));
            if (file?.markups == null) return;
            foreach (var m in file.markups)
            {
                bool ras = string.Equals(m.coordinateSystem, "RAS", StringComparison.OrdinalIgnoreCase);
                if (m.controlPoints == null) continue;
                foreach (var cp in m.controlPoints)
                {
                    if (cp.position == null || cp.position.Length < 3 || string.IsNullOrEmpty(cp.label)) continue;
                    var lps = new Vector3(cp.position[0], cp.position[1], cp.position[2]);
                    if (ras) lps = new Vector3(-lps.x, -lps.y, lps.z);
                    data.landmarks.RemoveAll(l => string.Equals(l.name, cp.label, StringComparison.OrdinalIgnoreCase));
                    data.landmarks.Add(new Landmark(cp.label.Trim(), CtVolume.LpsToModel(lps)));
                }
            }
        }

        /// <summary>Most anterior skin point near the midline (supine head CT).</summary>
        static Vector3? AutoNoseTip(CtVolume v)
        {
            var b = v.ModelBounds;
            Vector3? best = null; float bestZ = float.MaxValue;
            for (float x = -0.015f; x <= 0.015f; x += 0.003f)
            for (float y = b.min.y; y <= b.max.y; y += 0.002f)
            {
                float cx = b.center.x + x;
                for (float z = b.min.z; z < b.center.z && z < bestZ; z += 0.001f)
                    if (v.SampleModel(new Vector3(cx, y, z)) > -300) { bestZ = z; best = new Vector3(cx, y, z); break; }
            }
            return best;
        }

        // ---- OBJ overlays (LPS mm), parsed off-thread, meshed on the main thread ----

        public class ObjPart { public string name; public List<Vector3> verts = new List<Vector3>(); public List<int> tris = new List<int>(); }

        public static List<ObjPart> ReadObjs(string name, string casesRoot)
        {
            var parts = new List<ObjPart>();
            if (name == PhantomName) return parts;
            foreach (var f in Directory.GetFiles(Path.Combine(casesRoot, name), "*.obj", SearchOption.AllDirectories))
            {
                var all = new List<Vector3>();
                ObjPart cur = null;
                var remap = new Dictionary<int, int>();
                foreach (var line in File.ReadLines(f))
                {
                    if (line.StartsWith("v "))
                    {
                        var t = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        var lps = new Vector3(F(t[1]), F(t[2]), F(t[3]));
                        all.Add(CtVolume.LpsToModel(lps));
                    }
                    else if (line.StartsWith("g ") || line.StartsWith("o ") || cur == null && line.StartsWith("f "))
                    {
                        cur = new ObjPart { name = line.Length > 2 && !line.StartsWith("f ") ? line.Substring(2).Trim() : Path.GetFileNameWithoutExtension(f) };
                        parts.Add(cur); remap.Clear();
                        if (!line.StartsWith("f ")) continue;
                    }
                    if (line.StartsWith("f "))
                    {
                        var idx = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Skip(1)
                                      .Select(t => { int i = int.Parse(t.Split('/')[0]); return i > 0 ? i - 1 : all.Count + i; }).ToArray();
                        for (int k = 1; k < idx.Length - 1; k++)
                            foreach (var vi in new[] { idx[0], idx[k + 1], idx[k] })   // LPS->model mirrors handedness: flip winding
                            {
                                if (!remap.TryGetValue(vi, out int li)) { li = cur.verts.Count; cur.verts.Add(all[vi]); remap[vi] = li; }
                                cur.tris.Add(li);
                            }
                    }
                }
            }
            parts.RemoveAll(p => p.tris.Count == 0);
            return parts;
        }

        static float F(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
