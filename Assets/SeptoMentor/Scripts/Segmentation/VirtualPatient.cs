using System;
using System.Collections.Generic;
using UnityEngine;

namespace SeptoMentor
{
    /// <summary>
    /// The virtual nasal anatomy the synthetic endoscope flies through. In order of preference:
    /// the generated procedural nasal cavity (NasalAnatomy, meshes baked by SceneBuilder; aligned
    /// with the OR head and switched per surgical phase), the imported Slicer OBJ, or a crude
    /// PROCEDURAL PLACEHOLDER so the app runs without patient-derived data.
    /// Lives far from the user (see SceneBuilder) so the headset camera never sees it.
    /// Convention for the placeholder: +X right (lateral), +Y superior, +Z posterior, metres.
    /// </summary>
    public class VirtualPatient : MonoBehaviour
    {
        [Tooltip("Imported Segmentation-1.obj (model prefab). Null = procedural placeholder.")]
        public GameObject objModel;

        [Serializable]
        public class GeneratedPart
        {
            public string name;
            public Structure structure;
            public Color tint = Color.white;
            [Tooltip("Bit per SurgicalPhase: the part is shown only in those phases.")]
            public int phaseMask = 0x1F;
            public Mesh mesh;
        }

        [Tooltip("Baked procedural nasal cavity (SceneBuilder). Preferred over the OBJ when present.")]
        public List<GeneratedPart> generated = new List<GeneratedPart>();

        public Shader tissueShader;
        public LabelMapping mapping = new LabelMapping();

        [Tooltip("Endoscope path in this object's local space (auto-filled if both are zero).")]
        public Vector3 pathStart, pathEnd;
        [Tooltip("Scope yaw off the path direction, deg (+ = toward +X). Auto-set with the path.")]
        public float scopeYaw;

        public bool UsingPlaceholderGeometry { get; private set; }
        /// <summary>True when the anatomy is in the CT phantom's frame (L, S, P metres), i.e. it lines up with the OR head.</summary>
        public bool PhantomAligned { get; private set; }
        public bool AllMappingsConfirmed { get; private set; }
        public Transform AnatomyRoot { get; private set; }
        public readonly List<AnatomyPart> Parts = new List<AnatomyPart>();
        public Bounds LocalBounds { get; private set; }

        Material _tissueMat;
        readonly List<KeyValuePair<GameObject, int>> _phased = new List<KeyValuePair<GameObject, int>>();
        SurgicalPhase _phase = SurgicalPhase.Setup;

        public void Build()
        {
            if (AnatomyRoot != null) return;
            _tissueMat = new Material(tissueShader != null ? tissueShader : Shader.Find("SeptoMentor/EndoTissue")) { name = "EndoTissue" };
            AnatomyRoot = new GameObject("Anatomy").transform;
            AnatomyRoot.SetParent(transform, false);

            if (generated != null && generated.Count > 0) BuildGenerated();
            else if (objModel != null) BuildFromObj();
            else BuildPlaceholder();

            ComputeBounds();
            if (pathStart == Vector3.zero && pathEnd == Vector3.zero) AutoPath();
        }

        void BuildGenerated()
        {
            UsingPlaceholderGeometry = true;   // synthetic anatomy, not patient data
            AllMappingsConfirmed = true;       // labels are known by construction
            PhantomAligned = true;
            foreach (var g in generated)
            {
                if (g.mesh == null) continue;
                var go = new GameObject(g.name);
                go.transform.SetParent(AnatomyRoot, false);
                go.AddComponent<MeshFilter>().sharedMesh = g.mesh;
                go.AddComponent<MeshRenderer>();
                if (g.structure != Structure.Background) go.AddComponent<MeshCollider>().sharedMesh = g.mesh;
                var p = AddPart(go, g.structure, true);
                p.tissueColor = g.tint;
                if (g.structure == Structure.Background) p.wetness = 0.25f;   // vestibular skin, hairs
                p.Apply();
                if (g.phaseMask != 0x1F) _phased.Add(new KeyValuePair<GameObject, int>(go, g.phaseMask));
            }
            pathStart = NasalAnatomy.PathStart;
            pathEnd = NasalAnatomy.PathEnd;
            scopeYaw = 6f;   // slightly toward the septum
            SetPhase(_phase);
        }

        /// <summary>Shows the septal pieces of this phase (intact mucosa, incision, elevated flap, resection window).</summary>
        public void SetPhase(SurgicalPhase phase)
        {
            _phase = phase;
            foreach (var kv in _phased) kv.Key.SetActive((kv.Value >> (int)phase & 1) != 0);
        }

        void BuildFromObj()
        {
            UsingPlaceholderGeometry = false;
            var inst = Instantiate(objModel, AnatomyRoot, false);
            inst.name = objModel.name;
            bool allConfirmed = true;
            foreach (var r in inst.GetComponentsInChildren<MeshRenderer>(true))
            {
                var s = mapping.Resolve(r.gameObject.name, out bool confirmed);
                allConfirmed &= confirmed;
                AddPart(r.gameObject, s, confirmed);
                if (r.GetComponent<Collider>() == null)
                    r.gameObject.AddComponent<MeshCollider>().sharedMesh = r.GetComponent<MeshFilter>().sharedMesh;
                Debug.Log($"[SeptoMentor] OBJ segment '{r.gameObject.name}' -> {s} (confirmed={confirmed})");
            }
            AllMappingsConfirmed = allConfirmed;
        }

        void BuildPlaceholder()
        {
            UsingPlaceholderGeometry = true;
            AllMappingsConfirmed = false;   // placeholder geometry is never validated anatomy

            // Septum (x ~ 0), anterior (z-) to posterior (z+).
            Box("CaudalSeptum", Structure.CaudalSeptum, new Vector3(0, 0.0f, -0.029f), new Vector3(0.003f, 0.030f, 0.006f));
            Box("SeptalCartilage", Structure.SeptalCartilage, new Vector3(0, 0.003f, -0.013f), new Vector3(0.0028f, 0.030f, 0.026f));
            Box("PerpendicularPlate", Structure.PerpendicularPlate, new Vector3(0, 0.010f, 0.015f), new Vector3(0.0025f, 0.020f, 0.030f));
            Box("Vomer", Structure.Vomer, new Vector3(0, -0.009f, 0.015f), new Vector3(0.0025f, 0.018f, 0.030f));
            Box("MaxillaryCrest", Structure.MaxillaryCrest, new Vector3(0, -0.0155f, -0.013f), new Vector3(0.006f, 0.005f, 0.026f));
            // Partly elevated right flap: offset laterally so the bluish-white cartilage shows behind it.
            Box("MucoperichondrialFlap", Structure.MucoperichondrialFlap, new Vector3(0.0032f, 0.004f, -0.019f), new Vector3(0.0005f, 0.022f, 0.012f), Quaternion.Euler(0, -12f, 0));
            Box("NasalFloor", Structure.NasalFloor, new Vector3(0, -0.0195f, 0), new Vector3(0.030f, 0.002f, 0.070f));
            Capsule("InferiorTurbinate_R", Structure.InferiorTurbinate, new Vector3(0.0115f, -0.012f, 0.004f), 0.0045f, 0.045f);
            Capsule("InferiorTurbinate_L", Structure.InferiorTurbinate, new Vector3(-0.0115f, -0.012f, 0.004f), 0.0045f, 0.045f);
            Capsule("MiddleTurbinate_R", Structure.MiddleTurbinate, new Vector3(0.0100f, 0.007f, 0.014f), 0.0032f, 0.032f);
            Capsule("MiddleTurbinate_L", Structure.MiddleTurbinate, new Vector3(-0.0100f, 0.007f, 0.014f), 0.0032f, 0.032f);
            Box("LateralWall_R (placeholder bone)", Structure.SinonasalBone, new Vector3(0.016f, 0, 0.005f), new Vector3(0.002f, 0.042f, 0.070f));
            Box("LateralWall_L (placeholder bone)", Structure.SinonasalBone, new Vector3(-0.016f, 0, 0.005f), new Vector3(0.002f, 0.042f, 0.070f));
            Box("Roof (placeholder bone)", Structure.SinonasalBone, new Vector3(0, 0.0215f, 0.005f), new Vector3(0.034f, 0.002f, 0.070f));
        }

        GameObject Box(string name, Structure s, Vector3 pos, Vector3 size, Quaternion? rot = null)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(AnatomyRoot, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = rot ?? Quaternion.identity;
            go.transform.localScale = size;
            AddPart(go, s, false);
            return go;
        }

        GameObject Capsule(string name, Structure s, Vector3 pos, float radius, float length)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = name;
            go.transform.SetParent(AnatomyRoot, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(90, 0, 0);   // long axis along Z
            go.transform.localScale = new Vector3(radius * 2, length * 0.5f, radius * 2);
            AddPart(go, s, false);
            return go;
        }

        public AnatomyPart AddPart(GameObject go, Structure s, bool confirmed)
        {
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = _tissueMat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            var p = go.GetComponent<AnatomyPart>();
            if (p == null) p = go.AddComponent<AnatomyPart>();
            p.structure = s;
            p.mappingConfirmed = confirmed;
            p.tissueColor = AnatomyPart.DefaultTint(s);
            p.Apply();
            Parts.Add(p);
            return p;
        }

        public Material TissueMaterial => _tissueMat;

        void ComputeBounds()
        {
            bool any = false; var b = new Bounds();
            var inv = transform.worldToLocalMatrix;
            foreach (var p in Parts)
            {
                var wb = p.Rend.bounds;
                var c = inv.MultiplyPoint3x4(wb.center);
                var e = Vector3.Scale(wb.extents, Abs(transform.lossyScale.Reciprocal()));
                var lb = new Bounds(c, e * 2);
                if (!any) { b = lb; any = true; } else b.Encapsulate(lb);
            }
            LocalBounds = b;
        }

        void AutoPath()
        {
            if (UsingPlaceholderGeometry)
            {
                // Right nasal cavity, between septum/flap and inferior turbinate, looking posteriorly.
                pathStart = new Vector3(0.0065f, -0.002f, -0.045f);
                pathEnd = new Vector3(0.0055f, -0.002f, 0.012f);
                scopeYaw = -14f;
                return;
            }
            if (SeptumPath()) return;
            // Unknown anatomy: fly along the longest bounds axis, offset off-centre on the shortest.
            var b = LocalBounds; var e = b.extents;
            int longAxis = e.x >= e.y && e.x >= e.z ? 0 : (e.y >= e.z ? 1 : 2);
            int shortAxis = e.x <= e.y && e.x <= e.z ? 0 : (e.y <= e.z ? 1 : 2);
            var dir = Vector3.zero; dir[longAxis] = 1;
            var off = Vector3.zero; off[shortAxis] = e[shortAxis] * 1.6f;   // outside the thin septum
            pathStart = b.center - dir * e[longAxis] * 1.3f + off;
            pathEnd = b.center + dir * e[longAxis] * 0.3f + off;
            scopeYaw = 0f;
            Debug.LogWarning("[SeptoMentor] Endoscope path auto-derived from mesh bounds; set pathStart/pathEnd on VirtualPatient for a realistic fly-through.");
        }

        /// <summary>
        /// Prepared OBJs use +X patient-left, +Y superior, +Z posterior (tools/prepare_obj.py).
        /// Enter the patient's right nostril just in front of the septal segment and run back
        /// along the septum's -X face, ending ~9 mm behind it. Checked on Segmentation-1.obj:
        /// at least 2.4 mm clearance from all mesh vertices along the whole path.
        /// </summary>
        bool SeptumPath()
        {
            bool any = false; var sb = new Bounds();
            var inv = transform.worldToLocalMatrix;
            foreach (var p in Parts)
            {
                if (p.structure != Structure.SeptalCartilage && p.structure != Structure.CaudalSeptum) continue;
                var c = inv.MultiplyPoint3x4(p.Rend.bounds.center);
                var lb = new Bounds(c, Vector3.Scale(p.Rend.bounds.size, Abs(transform.lossyScale.Reciprocal())));
                if (!any) { sb = lb; any = true; } else sb.Encapsulate(lb);
            }
            if (!any) return false;
            pathStart = new Vector3(sb.min.x - 0.004f, sb.center.y + 0.004f, sb.min.z - 0.003f);
            pathEnd = new Vector3(sb.min.x + 0.002f, sb.center.y - 0.004f, sb.max.z + 0.009f);
            scopeYaw = 6f;   // slightly toward the septum
            Debug.Log($"[SeptoMentor] Endoscope path along the septum: {pathStart * 1000f} -> {pathEnd * 1000f} mm");
            return true;
        }

        static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
    }

    static class VecExt
    {
        public static Vector3 Reciprocal(this Vector3 v) =>
            new Vector3(v.x != 0 ? 1f / v.x : 0, v.y != 0 ? 1f / v.y : 0, v.z != 0 ? 1f / v.z : 0);
    }
}
