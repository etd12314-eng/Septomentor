using UnityEngine;

namespace SeptoMentor
{
    /// <summary>
    /// Enlarged, translucent copy of the virtual anatomy beside the monitor, with a live
    /// marker for the scope tip and viewing direction. Shares meshes with the VirtualPatient.
    /// </summary>
    public class AnatomyHologram : MonoBehaviour
    {
        public SyntheticSegmentationProvider synthetic;
        public Shader ghostShader;
        [Tooltip("Hologram size of the longest anatomy dimension, metres.")]
        public float targetSize = 0.30f;
        public float spinDegPerSec = 0f;

        Transform _content, _marker, _ray;
        readonly System.Collections.Generic.List<Mirror> _mirror = new System.Collections.Generic.List<Mirror>();
        struct Mirror { public GameObject part, ghost; }
        bool _built;

        void LateUpdate()
        {
            if (synthetic == null || synthetic.patient == null) return;
            if (!_built)
            {
                if (synthetic.patient.AnatomyRoot == null || synthetic.EndoCamera == null) return;
                Build();
            }
            foreach (var m in _mirror)   // phase-dependent parts (flap, resection window)
                if (m.ghost.activeSelf != m.part.activeInHierarchy) m.ghost.SetActive(m.part.activeInHierarchy);
            if (spinDegPerSec != 0) transform.Rotate(0, spinDegPerSec * Time.deltaTime, 0, Space.Self);

            _marker.localPosition = synthetic.ScopeLocalPosition;
            _ray.localPosition = synthetic.ScopeLocalPosition;
            var fwd = synthetic.ScopeLocalForward;
            if (fwd.sqrMagnitude > 1e-6f) _ray.localRotation = Quaternion.LookRotation(fwd);
        }

        void Build()
        {
            _built = true;
            var patient = synthetic.patient;
            var b = patient.LocalBounds;
            float longest = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            float scale = longest > 1e-5f ? targetSize / longest : 1f;

            _content = new GameObject("HologramContent").transform;
            _content.SetParent(transform, false);
            _content.localScale = Vector3.one * scale;
            _content.localPosition = -b.center * scale;

            var shader = ghostShader != null ? ghostShader : Shader.Find("SeptoMentor/Ghost");
            foreach (var part in patient.Parts)
            {
                var mf = part.GetComponent<MeshFilter>();
                if (mf == null) continue;
                var go = new GameObject("Ghost_" + part.name);
                go.transform.SetParent(_content, false);
                var local = patient.transform.worldToLocalMatrix * part.transform.localToWorldMatrix;
                go.transform.localPosition = local.GetColumn(3);
                go.transform.localRotation = local.rotation;
                go.transform.localScale = local.lossyScale;
                go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                var mat = new Material(shader);
                Color c = StructureInfo.OverlayColor(part.structure);
                c.a = 0.35f;
                mat.SetColor("_Color", c);
                go.AddComponent<MeshRenderer>().sharedMaterial = mat;
                _mirror.Add(new Mirror { part = part.gameObject, ghost = go });
            }

            _marker = GameObject.CreatePrimitive(PrimitiveType.Sphere).transform;
            Destroy(_marker.GetComponent<Collider>());
            _marker.name = "ScopeMarker";
            _marker.SetParent(_content, false);
            _marker.localScale = Vector3.one * (0.012f / scale);   // ~12 mm on screen
            _marker.GetComponent<Renderer>().sharedMaterial = Solid(shader, new Color(1f, 1f, 0.2f, 0.95f));

            _ray = new GameObject("ScopeRay").transform;
            _ray.SetParent(_content, false);
            var stick = GameObject.CreatePrimitive(PrimitiveType.Cylinder).transform;
            Destroy(stick.GetComponent<Collider>());
            stick.SetParent(_ray, false);
            float len = 0.06f / scale;
            stick.localRotation = Quaternion.Euler(90, 0, 0);
            stick.localPosition = new Vector3(0, 0, len * 0.5f);
            stick.localScale = new Vector3(0.003f / scale, len * 0.5f, 0.003f / scale);
            stick.GetComponent<Renderer>().sharedMaterial = Solid(shader, new Color(1f, 1f, 0.2f, 0.8f));
        }

        static Material Solid(Shader s, Color c)
        {
            var m = new Material(s);
            m.SetColor("_Color", c);
            m.SetFloat("_Rim", 0.5f);
            return m;
        }
    }
}
