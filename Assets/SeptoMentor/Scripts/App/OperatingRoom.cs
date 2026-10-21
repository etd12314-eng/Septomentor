using System.Threading.Tasks;
using SeptoMentor.Nav;
using UnityEngine;

namespace SeptoMentor
{
    /// <summary>
    /// Procedural operating room for the VR trainer (no imported art): tiled room, OR table with a
    /// draped supine patient (head = the CT phantom's skin surface), two surgical lamps, endoscopy
    /// tower carrying the video monitor, anesthesia machine, Mayo stand with septoplasty instruments.
    /// The resident stands at the patient's right side, at the head (world origin, facing +Z); the
    /// head lies to their left with the face up.
    /// </summary>
    public partial class OperatingRoom : MonoBehaviour
    {
        public Shader surfaceShader, shadowShader, skinShader;
        [Tooltip("Baked by SceneBuilder (PatientModel). Null: the head is meshed at runtime and the body is simple shapes.")]
        public Mesh headMesh, drapeMesh;

        /// <summary>Where the video monitor's centre goes (top of the endoscopy tower, across the table).</summary>
        public static readonly Vector3 MonitorPosition = new Vector3(0.25f, 1.62f, 1.18f);
        public static readonly Vector3 SurgeonSpot = Vector3.zero;
        /// <summary>Top of the Mayo stand's towel, where HandInstruments lays out the grabbable instruments.</summary>
        public static readonly Vector3 MayoTray = new Vector3(0.55f, 0.98f, 0.05f);

        static readonly Vector3 HeadPosition = new Vector3(-0.15f, 1.03f, 0.45f);

        /// <summary>Patient head frame: x = patient's right, y = superior, z = anterior (metres).</summary>
        public Transform PatientHead { get; private set; }

        /// <summary>Nostril entrance in the head frame (phantom geometry).</summary>
        public static Vector3 Nostril(bool right) => new Vector3(right ? 0.0076f : -0.0076f, -0.0283f, 0.088f);

        Transform _root;
        Task<SurfaceNets> _headTask;

        void Awake()
        {
            _root = new GameObject("Room").transform;
            _root.SetParent(transform, false);
            BuildRoom();
            BuildTable();
            BuildLamps();
            BuildTower();
            BuildAnesthesia();
            BuildMayoStand();
            BuildDetails();
            StaticBatchingUtility.Combine(_root.gameObject);
            BuildStaff();
            BuildVitals();
            BuildCtViewer();
            BuildAudio();

            PatientHead = new GameObject("PatientHead").transform;
            PatientHead.SetParent(transform, false);
            PatientHead.SetPositionAndRotation(HeadPosition, Quaternion.LookRotation(Vector3.up, Vector3.left));
            BuildHeadAccessories();
            if (headMesh != null) AddHead(headMesh);
            else _headTask = Task.Run(() => HeadSurface());
            if (drapeMesh != null)
            {
                var drape = new GameObject("PatientDrape");
                drape.transform.SetParent(PatientHead, false);
                drape.AddComponent<MeshFilter>().sharedMesh = drapeMesh;
                drape.AddComponent<MeshRenderer>().sharedMaterial = Mat(new Color(0.22f, 0.42f, 0.62f), 0.1f);
            }
        }

        void Update()
        {
            UpdateLife();
            if (_headTask == null || !_headTask.IsCompleted) return;
            if (_headTask.Status == TaskStatus.RanToCompletion) AddHead(_headTask.Result.ToMesh("PatientHead"));
            else Debug.LogError("[SeptoMentor] head surface failed: " + _headTask.Exception);
            _headTask = null;
        }

        void AddHead(Mesh mesh)
        {
            var go = new GameObject("HeadSkin");
            go.transform.SetParent(PatientHead, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var skin = skinShader != null ? skinShader : Shader.Find("SeptoMentor/ORSkin");
            Material mat;
            if (skin != null && mesh.colors.Length > 0)
            {
                mat = new Material(skin);
                mat.SetColor("_Color", new Color(0.88f, 0.69f, 0.6f));
            }
            else mat = Mat(new Color(0.86f, 0.66f, 0.56f), 0.25f);
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        }

        /// <summary>Moves the XR origin so the user stands at the surgeon's spot, facing the table.</summary>
        public void PlaceUser(Transform head)
        {
            var origin = head != null ? head.parent : null;
            if (origin == null) return;
            var fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            origin.RotateAround(head.position, Vector3.up, Vector3.SignedAngle(fwd, Vector3.forward, Vector3.up));
            var d = SurgeonSpot - head.position;
            d.y = 0;
            origin.position += d;
        }

        /// <summary>Phantom skin, meshed off the main thread (2 mm grid), in the head frame.</summary>
        static SurfaceNets HeadSurface()
        {
            // Grid over (L, S, P) mm: L -80..80, S -80..125, P -110..135; head frame = (-L, S, -P) / 1000.
            var m = Matrix4x4.TRS(new Vector3(0.08f, -0.08f, 0.11f), Quaternion.identity, new Vector3(-0.002f, 0.002f, -0.002f));
            return SurfaceNets.Extract((i, j, k) => -CtPhantom.SkinSdf(new Vector3(-80 + 2 * i, -80 + 2 * j, -110 + 2 * k)), 0f,
                                       Vector3Int.zero, new Vector3Int(80, 102, 122), 1, m).Smooth(3);
        }

        // ------------------------------------------------------------------ building blocks

        Material Mat(Color c, float gloss = 0.2f, float tile = 0f, float emission = 0f, float metal = 0f)
        {
            var m = new Material(surfaceShader != null ? surfaceShader : Shader.Find("SeptoMentor/ORSurface"));
            m.SetColor("_Color", c);
            m.SetFloat("_Gloss", gloss);
            m.SetFloat("_Tile", tile);
            m.SetFloat("_Emission", emission);
            m.SetFloat("_Metal", metal);
            return m;
        }

        Transform Prim(PrimitiveType t, Vector3 pos, Vector3 scale, Material mat, Quaternion? rot = null, Transform parent = null)
        {
            var go = GameObject.CreatePrimitive(t);
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent != null ? parent : _root, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = rot ?? Quaternion.identity;
            go.transform.localScale = scale;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go.transform;
        }

        Transform Box(Vector3 pos, Vector3 size, Material mat, Transform parent = null) => Prim(PrimitiveType.Cube, pos, size, mat, null, parent);

        /// <summary>Cylinder from a to b.</summary>
        Transform Rod(Vector3 a, Vector3 b, float radius, Material mat, Transform parent = null)
        {
            var d = b - a;
            return Prim(PrimitiveType.Cylinder, (a + b) * 0.5f, new Vector3(radius * 2, d.magnitude * 0.5f, radius * 2), mat,
                        Quaternion.FromToRotation(Vector3.up, d), parent);
        }

        // ------------------------------------------------------------------ room

        void BuildRoom()
        {
            const float x0 = -3.5f, x1 = 3.5f, z0 = -2.5f, z1 = 3.5f, h = 3f;
            var wall = Mat(new Color(0.58f, 0.72f, 0.72f), 0.3f, 0.25f);
            Box(new Vector3(0, -0.01f, 0.5f), new Vector3(x1 - x0, 0.02f, z1 - z0), Mat(new Color(0.46f, 0.5f, 0.5f), 0.35f, 0.6f));
            Box(new Vector3(0, h, 0.5f), new Vector3(x1 - x0, 0.02f, z1 - z0), Mat(new Color(0.9f, 0.9f, 0.9f), 0.05f, 0.6f));
            Box(new Vector3(0, h / 2, z1), new Vector3(x1 - x0, h, 0.04f), wall);
            Box(new Vector3(0, h / 2, z0), new Vector3(x1 - x0, h, 0.04f), wall);
            Box(new Vector3(x0, h / 2, 0.5f), new Vector3(0.04f, h, z1 - z0), wall);
            Box(new Vector3(x1, h / 2, 0.5f), new Vector3(0.04f, h, z1 - z0), wall);

            var panel = Mat(new Color(1f, 1f, 0.97f), 0f, 0f, 1f);
            foreach (var p in new[] { new Vector3(-1.4f, 0, -0.6f), new Vector3(1.4f, 0, -0.6f), new Vector3(-1.4f, 0, 1.8f), new Vector3(1.4f, 0, 1.8f) })
                Box(new Vector3(p.x, h - 0.02f, p.z), new Vector3(1.2f, 0.02f, 0.6f), panel);

            // Sliding door with a window, wall clock, wall cabinets, scrub-room window.
            var steel = Mat(new Color(0.7f, 0.72f, 0.74f), 0.6f, 0f, 0f, 0.6f);
            Box(new Vector3(-2.2f, 1.1f, z1 - 0.03f), new Vector3(1.4f, 2.2f, 0.04f), Mat(new Color(0.55f, 0.62f, 0.66f), 0.4f));
            Box(new Vector3(-2.2f, 1.5f, z1 - 0.06f), new Vector3(0.4f, 0.4f, 0.02f), Mat(new Color(0.7f, 0.8f, 0.85f), 0.8f, 0, 0.6f));
            Prim(PrimitiveType.Cylinder, new Vector3(1.6f, 2.35f, z1 - 0.03f), new Vector3(0.32f, 0.01f, 0.32f), Mat(Color.white, 0.3f),
                             Quaternion.Euler(90, 0, 0));
            var hand = Mat(new Color(0.1f, 0.1f, 0.1f));
            Box(new Vector3(1.6f, 2.39f, z1 - 0.05f), new Vector3(0.012f, 0.09f, 0.005f), hand);
            Prim(PrimitiveType.Cube, new Vector3(1.64f, 2.35f, z1 - 0.05f), new Vector3(0.012f, 0.12f, 0.005f), hand, Quaternion.Euler(0, 0, -100));
            for (int i = 0; i < 4; i++)
                Box(new Vector3(-1.2f + i * 0.9f, 1.0f, z0 + 0.3f), new Vector3(0.85f, 2.0f, 0.55f), steel);
            Box(new Vector3(x1 - 0.03f, 1.5f, 0.6f), new Vector3(0.04f, 0.8f, 1.6f), Mat(new Color(0.65f, 0.78f, 0.82f), 0.9f, 0, 0.5f));
        }

        void BuildTable()
        {
            var dark = Mat(new Color(0.12f, 0.13f, 0.15f), 0.3f);
            var steel = Mat(new Color(0.72f, 0.74f, 0.76f), 0.7f, 0f, 0f, 0.8f);
            var drape = Mat(new Color(0.22f, 0.42f, 0.62f), 0.1f);
            Box(new Vector3(0.75f, 0.04f, 0.45f), new Vector3(0.8f, 0.08f, 0.5f), steel);
            Box(new Vector3(0.75f, 0.45f, 0.45f), new Vector3(0.3f, 0.8f, 0.25f), steel);
            Box(new Vector3(0.75f, 0.86f, 0.45f), new Vector3(2.1f, 0.08f, 0.56f), dark);
            Prim(PrimitiveType.Cylinder, new Vector3(-0.15f, 0.905f, 0.45f), new Vector3(0.2f, 0.012f, 0.2f), Mat(new Color(0.2f, 0.45f, 0.75f), 0.5f));

            // Draped patient (simple shapes unless the baked body drape is present); side drapes hang to mid-height.
            if (drapeMesh == null)
            {
                Prim(PrimitiveType.Sphere, new Vector3(0.08f, 0.99f, 0.45f), new Vector3(0.34f, 0.16f, 0.46f), drape);
                Prim(PrimitiveType.Sphere, new Vector3(0.5f, 1.02f, 0.45f), new Vector3(0.65f, 0.22f, 0.48f), drape);
                Prim(PrimitiveType.Sphere, new Vector3(1.3f, 0.98f, 0.45f), new Vector3(0.95f, 0.14f, 0.38f), drape);
                Box(new Vector3(0.8f, 0.92f, 0.45f), new Vector3(1.9f, 0.04f, 0.74f), drape);
            }
            Box(new Vector3(0.8f, 0.66f, 0.08f), new Vector3(1.9f, 0.52f, 0.015f), drape);
            Box(new Vector3(0.8f, 0.66f, 0.82f), new Vector3(1.9f, 0.52f, 0.015f), drape);
        }

        void BuildHeadAccessories()
        {
            var towel = Mat(new Color(0.45f, 0.62f, 0.58f), 0.05f);
            // Head towel over the cranium, leaving the face free.
            Prim(PrimitiveType.Sphere, new Vector3(0, 0.05f, -0.035f), new Vector3(0.165f, 0.19f, 0.2f), towel, null, PatientHead);
            // Eye protection tape.
            var tape = Mat(new Color(0.9f, 0.92f, 0.95f), 0.6f);
            Box(new Vector3(0.031f, 0.029f, 0.079f), new Vector3(0.026f, 0.012f, 0.0015f), tape, PatientHead);
            Box(new Vector3(-0.031f, 0.029f, 0.079f), new Vector3(0.026f, 0.012f, 0.0015f), tape, PatientHead);
            // Oral RAE tube out of the mouth toward the chest.
            var tube = Mat(new Color(0.85f, 0.88f, 0.9f), 0.8f);
            var a = new Vector3(0.0f, -0.046f, 0.075f);
            var b = new Vector3(0.0f, -0.05f, 0.115f);
            var c = new Vector3(0.0f, -0.32f, 0.14f);
            Rod(a, b, 0.0045f, tube, PatientHead);
            Rod(b, c, 0.0045f, tube, PatientHead);
            Prim(PrimitiveType.Sphere, b, Vector3.one * 0.009f, tube, null, PatientHead);
        }

        void BuildLamps()
        {
            var white = Mat(new Color(0.88f, 0.89f, 0.9f), 0.5f);
            var glow = Mat(new Color(1f, 1f, 0.95f), 0f, 0f, 1f);
            var target = HeadPosition + new Vector3(0, 0.08f, 0);
            var mount = new Vector3(0.3f, 3f, 0.9f);
            Rod(mount, mount - new Vector3(0, 0.55f, 0), 0.04f, white);
            var lamps = new[] { new Vector3(-0.6f, 2.05f, 0.85f), new Vector3(0.3f, 2.1f, 1.0f) };
            for (int i = 0; i < lamps.Length; i++)
            {
                var p = lamps[i];
                var elbow = new Vector3(p.x, mount.y - 0.55f, p.z);
                Rod(mount - new Vector3(0, 0.55f, 0), elbow, 0.025f, white);
                Rod(elbow, p, 0.025f, white);
                var aim = (target - p).normalized;
                var rot = Quaternion.FromToRotation(Vector3.up, -aim);
                Prim(PrimitiveType.Cylinder, p, new Vector3(0.5f, 0.04f, 0.5f), white, rot);
                Prim(PrimitiveType.Cylinder, p + aim * 0.042f, new Vector3(0.38f, 0.004f, 0.38f), glow, rot);
                Shader.SetGlobalVector(i == 0 ? "_ORLamp0" : "_ORLamp1", new Vector4(p.x, p.y, p.z, 1.6f));
                Shader.SetGlobalVector(i == 0 ? "_ORLampDir0" : "_ORLampDir1", aim);
            }
        }

        void BuildTower()
        {
            var body = Mat(new Color(0.2f, 0.21f, 0.23f), 0.4f);
            var unit = Mat(new Color(0.85f, 0.86f, 0.88f), 0.5f);
            var screen = Mat(new Color(0.15f, 0.55f, 0.85f), 0f, 0f, 1f);
            var x = MonitorPosition.x; var z = MonitorPosition.z + 0.1f;
            Box(new Vector3(x, 0.6f, z), new Vector3(0.62f, 1.1f, 0.5f), body);
            for (int i = 0; i < 3; i++)   // light source, camera control unit, recorder
            {
                float y = 0.75f + i * 0.17f;
                Box(new Vector3(x, y, z - 0.22f), new Vector3(0.5f, 0.13f, 0.08f), unit);
                Box(new Vector3(x - 0.13f, y, z - 0.262f), new Vector3(0.12f, 0.05f, 0.005f), screen);
            }
            Rod(new Vector3(x, 1.15f, z), new Vector3(x, MonitorPosition.y - 0.25f, z), 0.025f, body);
            // Monitor housing behind the video quad (the quad itself belongs to OverlayView).
            Box(MonitorPosition + new Vector3(0, 0, 0.03f), new Vector3(0.72f, 0.7f, 0.05f), Mat(new Color(0.05f, 0.05f, 0.06f), 0.6f));
        }

        void BuildAnesthesia()
        {
            var shell = Mat(new Color(0.78f, 0.8f, 0.82f), 0.4f);
            var screen = Mat(new Color(0.03f, 0.08f, 0.06f), 0.9f, 0f, 0.9f);
            Box(new Vector3(-1.05f, 0.65f, 1.0f), new Vector3(0.7f, 1.3f, 0.55f), shell);
            var mon = VitalsScreen;
            Box(mon, new Vector3(0.45f, 0.32f, 0.04f), shell);
            Box(mon + new Vector3(0, 0, -0.022f), new Vector3(0.4f, 0.27f, 0.005f), screen);
            Box(new Vector3(-1.2f, 1.36f, 0.8f), new Vector3(0.08f, 0.16f, 0.08f), Mat(new Color(0.85f, 0.65f, 0.1f), 0.5f));
            Box(new Vector3(-1.05f, 1.36f, 0.8f), new Vector3(0.08f, 0.16f, 0.08f), Mat(new Color(0.55f, 0.25f, 0.65f), 0.5f));
            // IV pole with a bag.
            var steel = Mat(new Color(0.75f, 0.77f, 0.8f), 0.8f, 0f, 0f, 0.8f);
            Rod(new Vector3(-0.75f, 0f, 0.15f), new Vector3(-0.75f, 1.95f, 0.15f), 0.012f, steel);
            Prim(PrimitiveType.Capsule, new Vector3(-0.75f, 1.75f, 0.2f), new Vector3(0.09f, 0.1f, 0.04f), Mat(new Color(0.85f, 0.9f, 0.95f), 0.9f));
        }

        void BuildMayoStand()
        {
            var steel = Mat(new Color(0.75f, 0.77f, 0.8f), 0.85f, 0f, 0f, 0.85f);
            var towel = Mat(new Color(0.3f, 0.52f, 0.55f), 0.05f);
            var p = new Vector3(0.55f, 0.97f, 0.05f);
            Rod(new Vector3(0.85f, 0f, 0.05f), new Vector3(0.85f, p.y, 0.05f), 0.015f, steel);
            Box(new Vector3(0.7f, p.y - 0.01f, 0.05f), new Vector3(0.36f, 0.015f, 0.04f), steel);
            Box(p, new Vector3(0.5f, 0.02f, 0.35f), towel);
            // Nasal speculum and gauze; the working instruments themselves belong to HandInstruments (grabbable).
            float top = p.y + 0.014f;
            Prim(PrimitiveType.Cube, new Vector3(p.x + 0.17f, top, p.z + 0.15f), new Vector3(0.09f, 0.006f, 0.012f), steel, Quaternion.Euler(0, 12, 0));
            Prim(PrimitiveType.Cube, new Vector3(p.x + 0.17f, top, p.z + 0.13f), new Vector3(0.09f, 0.006f, 0.012f), steel, Quaternion.Euler(0, -12, 0));
            Box(new Vector3(p.x + 0.17f, top, p.z - 0.06f), new Vector3(0.08f, 0.01f, 0.05f), Mat(new Color(0.9f, 0.9f, 0.9f), 0.1f));   // gauze
        }
    }
}
