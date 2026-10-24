using System;
using System.IO;
using System.Linq;
using SeptoMentor.Nav;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SeptoMentor.EditorTools
{
    /// <summary>
    /// Generates Assets/SeptoMentor/Scenes/SeptoMentor.unity entirely in code, so the scene
    /// never has to be hand-merged. Menu: SeptoMentor > Build Scene. Also called by BuildScript.
    /// </summary>
    public static class SceneBuilder
    {
        public const string ScenePath = "Assets/SeptoMentor/Scenes/SeptoMentor.unity";
        public const string LauncherPath = "Assets/SeptoMentor/Scenes/Launcher.unity";
        public const string NavigationPath = "Assets/SeptoMentor/Scenes/SeptoMentor_XR.unity";
        /// <summary>Build order: the launcher (menu) starts first.</summary>
        public static readonly string[] AllScenes = { LauncherPath, NavigationPath, ScenePath };
        public const string ObjPath = "Assets/SeptoMentor/Models/Segmentation-1.obj";
        static readonly Vector3 PatientOrigin = new Vector3(500f, 0f, 0f);   // far outside the headset camera's far clip

        [MenuItem("SeptoMentor/Build Scene")]
        public static void Build()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // --- XR camera rig ---------------------------------------------------------------
            var xrOrigin = new GameObject("XROrigin").transform;
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            camGo.transform.SetParent(xrOrigin, false);
            camGo.transform.localPosition = new Vector3(0, 1.6f, 0);
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.02f, 0.02f, 0.03f, 1f);
            cam.nearClipPlane = 0.02f;   // the resident leans in close to the patient's nose
            cam.farClipPlane = 50f;
            camGo.AddComponent<AudioListener>();
            camGo.AddComponent<HeadTracker>();

            // --- Virtual patient (far away) ------------------------------------------------------
            var patientGo = new GameObject("VirtualPatient");
            patientGo.transform.position = PatientOrigin;
            var patient = patientGo.AddComponent<VirtualPatient>();
            patient.tissueShader = Shader.Find("SeptoMentor/EndoTissue");
            patient.mapping = LabelMapping.LoadOrDefault();
            patient.objModel = AssetDatabase.LoadAssetAtPath<GameObject>(ObjPath);
            patient.generated = BakeNasalAnatomy();
            Debug.Log(patient.generated.Count > 0 ? $"[SeptoMentor] Using the procedural nasal cavity ({patient.generated.Count} parts)"
                : patient.objModel != null ? "[SeptoMentor] Using " + ObjPath
                : "[SeptoMentor] " + ObjPath + " not found - procedural PLACEHOLDER anatomy will be used.");

            // --- Systems -------------------------------------------------------------------------
            var sys = new GameObject("SeptoMentor");
            var provider = sys.AddComponent<SyntheticSegmentationProvider>();
            provider.patient = patient;
            provider.labelShader = Shader.Find("SeptoMentor/LabelId");

            var mentor = sys.AddComponent<MentorEngine>();
            mentor.provider = provider;
            var ai = sys.AddComponent<AIMentorClient>();
            ai.provider = provider; ai.engine = mentor;
            var speech = sys.AddComponent<SpeechIO>();
            var logger = sys.AddComponent<SessionLogger>();
            logger.provider = provider;
            var input = sys.AddComponent<ControllerInput>();

            // --- Operating room; the monitor sits on the endoscopy tower ---------------------------
            var room = new GameObject("OperatingRoom").AddComponent<OperatingRoom>();
            room.surfaceShader = Shader.Find("SeptoMentor/ORSurface");
            room.shadowShader = Shader.Find("SeptoMentor/ORShadow");
            room.skinShader = Shader.Find("SeptoMentor/ORSkin");
            BakePatient(room);
            patient.skinMesh = room.headMesh != null ? AssetDatabase.LoadAllAssetsAtPath(PatientPath).OfType<Mesh>().FirstOrDefault(x => x.name == "EndoscopeHead") : null;
            var userRig = new GameObject("UserRig").transform;
            userRig.position = OperatingRoom.MonitorPosition;
            var monitor = new GameObject("Monitor").transform;
            monitor.SetParent(userRig, false);
            var overlay = monitor.gameObject.AddComponent<OverlayView>();
            overlay.provider = provider;
            overlay.monitorShader = Shader.Find("SeptoMentor/MonitorComposite");
            overlay.monitorSize = 66f;   // ~0.66 m, about 32 deg from the surgeon's spot: the endoscope image gets ~800 headset pixels

            var holo = new GameObject("AnatomyHologram").transform;
            holo.SetParent(userRig, false);
            holo.localPosition = new Vector3(-0.62f, -0.12f, 0.0f);
            holo.localRotation = Quaternion.Euler(0, 30f, 0);
            var hologram = holo.gameObject.AddComponent<AnatomyHologram>();
            hologram.synthetic = provider;
            hologram.ghostShader = Shader.Find("SeptoMentor/Ghost");

            var app = sys.AddComponent<SeptoMentorApp>();
            app.provider = provider; app.mentor = mentor; app.overlay = overlay; app.ai = ai;
            app.speech = speech; app.logger = logger; app.input = input;
            app.head = camGo.transform; app.userRig = userRig; app.room = room;
            var scope = sys.AddComponent<HandheldScope>();
            scope.provider = provider; scope.room = room; scope.head = camGo.transform;
            app.scope = scope;
            var sim = sys.AddComponent<SeptoplastySim>();
            sim.provider = provider; sim.mentor = mentor;
            var fess = sys.AddComponent<FessSim>();
            fess.provider = provider;
            var hands = sys.AddComponent<HandInstruments>();
            hands.provider = provider; hands.room = room; hands.sim = sim; hands.fess = fess; hands.head = camGo.transform;
            app.hands = hands; app.sim = sim; app.fess = fess;
            var voice = sys.AddComponent<VoiceCoach>();
            voice.speech = speech;
            var commands = sys.AddComponent<VoiceCommands>();
            commands.voice = voice;
            app.voice = voice; app.commands = commands;

            sys.AddComponent<ModeSwitch>().gripsReturn = false;   // hold the left menu button: back to the menu

            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log("[SeptoMentor] Scene generated: " + ScenePath);

            BuildLauncher();
            BuildNavigation();
            EditorBuildSettings.scenes = AllScenes.Select(p => new EditorBuildSettingsScene(p, true)).ToArray();
            AlwaysIncludeShaders("SeptoMentor/EndoTissue", "SeptoMentor/LabelId", "SeptoMentor/MonitorComposite", "SeptoMentor/Ghost",
                                 "SeptoMentor/CtSlice", "SeptoMentor/NavGhost", "SeptoMentor/NavMarker", "SeptoMentor/ORSurface",
                                 "SeptoMentor/ORShadow", "SeptoMentor/ORSkin", "SeptoMentor/XRayShaft", "Unlit/Texture");
        }

        /// <summary>XR origin + camera. Passthrough = transparent clear + AR Foundation camera manager (Meta OpenXR).</summary>
        static Camera CameraRig(bool passthrough)
        {
            var xrOrigin = new GameObject("XROrigin").transform;
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            camGo.transform.SetParent(xrOrigin, false);
            camGo.transform.localPosition = new Vector3(0, 1.6f, 0);
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = passthrough ? new Color(0, 0, 0, 0) : new Color(0.02f, 0.02f, 0.03f, 1f);
            cam.nearClipPlane = 0.02f;
            cam.farClipPlane = 50f;
            camGo.AddComponent<AudioListener>();
            camGo.AddComponent<HeadTracker>();
            if (passthrough)
            {
                // By type name so this file compiles even if AR Foundation is missing.
                var session = FindType("UnityEngine.XR.ARFoundation.ARSession");
                var camMgr = FindType("UnityEngine.XR.ARFoundation.ARCameraManager");
                if (session != null) new GameObject("AR Session").AddComponent(session);
                if (camMgr != null) camGo.AddComponent(camMgr);
                Debug.Log($"[SeptoMentor] Passthrough: ARSession {(session != null ? "ok" : "MISSING")}, ARCameraManager {(camMgr != null ? "ok" : "MISSING")}");
            }
            return cam;
        }

        static Type FindType(string fullName) =>
            AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(fullName, false)).FirstOrDefault(t => t != null);

        static void BuildLauncher()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cam = CameraRig(true);
            var menu = new GameObject("Menu").AddComponent<ModeSwitch>();
            menu.isLauncher = true;
            menu.head = cam.transform;
            menu.transform.position = new Vector3(0, 1.5f, 1f);
            EditorSceneManager.SaveScene(scene, LauncherPath);
            Debug.Log("[SeptoMentor] Scene generated: " + LauncherPath);
        }

        static void BuildNavigation()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cam = CameraRig(true);

            var sys = new GameObject("SeptoMentorNav");
            var ai = sys.AddComponent<AIMentorClient>();
            var speech = sys.AddComponent<SpeechIO>();
            var logger = sys.AddComponent<SessionLogger>();

            var dash = new GameObject("NavDashboard").AddComponent<NavDashboard>();
            dash.sliceShader = Shader.Find("SeptoMentor/CtSlice");
            dash.solidShader = Shader.Find("SeptoMentor/ORSurface");
            dash.markerShader = Shader.Find("SeptoMentor/NavMarker");
            dash.transform.position = new Vector3(-0.25f, 1.55f, 0.75f);

            var nav = sys.AddComponent<NavigationApp>();
            nav.ghostShader = Shader.Find("SeptoMentor/NavGhost");
            nav.markerShader = Shader.Find("SeptoMentor/NavMarker");
            nav.dashboard = dash; nav.ai = ai; nav.speech = speech; nav.logger = logger;
            nav.head = cam.transform; nav.trackingOrigin = cam.transform.parent;
            ai.contextSource = nav;

            EditorSceneManager.SaveScene(scene, NavigationPath);
            Debug.Log("[SeptoMentor] Scene generated: " + NavigationPath);
        }

        const string NasalAnatomyPath = "Assets/SeptoMentor/Generated/NasalAnatomy.asset";
        const string PatientPath = "Assets/SeptoMentor/Generated/Patient.asset";

        /// <summary>Meshes the procedural nasal cavity (NasalAnatomy) into one asset with a mesh per part.</summary>
        /// <summary>Patient head (1.25 mm, vertex-coloured) and draped body for the operating room, as mesh assets.</summary>
        static void BakePatient(OperatingRoom room)
        {
            try
            {
                var t0 = DateTime.Now;
                var head = PatientModel.BuildHead();
                var drape = PatientModel.BuildDrape();
                Directory.CreateDirectory(Path.GetDirectoryName(PatientPath));
                AssetDatabase.DeleteAsset(PatientPath);
                AssetDatabase.CreateAsset(head, PatientPath);
                AssetDatabase.AddObjectToAsset(drape, PatientPath);
                AssetDatabase.AddObjectToAsset(PatientModel.EndoscopeHead(head), PatientPath);
                AssetDatabase.SaveAssets();
                room.headMesh = head; room.drapeMesh = drape;
                Debug.Log($"[SeptoMentor] Patient baked: head {head.vertexCount} vertices, drape {drape.vertexCount} vertices, {(DateTime.Now - t0).TotalSeconds:0.0} s");
            }
            catch (Exception e) { Debug.LogError("[SeptoMentor] Patient bake failed: " + e); }
        }

        static System.Collections.Generic.List<VirtualPatient.GeneratedPart> BakeNasalAnatomy()
        {
            var list = new System.Collections.Generic.List<VirtualPatient.GeneratedPart>();
            try
            {
                var t0 = DateTime.Now;
                var pieces = NasalAnatomy.Build(0.5f, s => Debug.Log("[SeptoMentor] nasal anatomy: " + s));
                Directory.CreateDirectory(Path.GetDirectoryName(NasalAnatomyPath));
                AssetDatabase.DeleteAsset(NasalAnatomyPath);
                int verts = 0;
                foreach (var p in pieces)
                {
                    var m = new Mesh
                    {
                        name = p.name,
                        indexFormat = p.verts.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16,
                    };
                    m.SetVertices(p.verts);
                    m.SetColors(p.colors);
                    m.SetUVs(0, p.uvs);
                    m.SetUVs(1, p.uvs2);
                    m.SetTriangles(p.tris, 0);
                    m.RecalculateNormals();
                    m.RecalculateBounds();
                    if (list.Count == 0) AssetDatabase.CreateAsset(m, NasalAnatomyPath);
                    else AssetDatabase.AddObjectToAsset(m, NasalAnatomyPath);
                    list.Add(new VirtualPatient.GeneratedPart { name = p.name, structure = p.structure, tint = p.tint, phaseMask = p.phaseMask, mesh = m });
                    verts += p.verts.Count;
                }
                AssetDatabase.SaveAssets();
                Debug.Log($"[SeptoMentor] Nasal anatomy baked: {pieces.Count} parts, {verts} vertices, {(DateTime.Now - t0).TotalSeconds:0.0} s");
            }
            catch (Exception e) { Debug.LogError("[SeptoMentor] Nasal anatomy bake failed: " + e); list.Clear(); }
            return list;
        }

        /// <summary>Shaders created via code at runtime must be in the build.</summary>
        static void AlwaysIncludeShaders(params string[] names)
        {
            var gs = UnityEngine.Rendering.GraphicsSettings.GetGraphicsSettings();
            if (gs == null) return;
            var so = new SerializedObject(gs);
            var arr = so.FindProperty("m_AlwaysIncludedShaders");
            foreach (var n in names)
            {
                var sh = Shader.Find(n);
                if (sh == null) { Debug.LogError("[SeptoMentor] Shader not found: " + n); continue; }
                bool present = false;
                for (int i = 0; i < arr.arraySize; i++) if (arr.GetArrayElementAtIndex(i).objectReferenceValue == sh) present = true;
                if (present) continue;
                arr.InsertArrayElementAtIndex(arr.arraySize);
                arr.GetArrayElementAtIndex(arr.arraySize - 1).objectReferenceValue = sh;
            }
            so.ApplyModifiedProperties();
        }

        /// <summary>OPEN ITEM 1 helper: lists OBJ sub-objects so the label mapping can be confirmed.</summary>
        [MenuItem("SeptoMentor/Inspect OBJ Segments")]
        public static void InspectObj()
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(ObjPath);
            if (go == null) { Debug.LogWarning("Put the mesh at " + ObjPath); return; }
            var mapping = LabelMapping.LoadOrDefault();
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                var m = mf.sharedMesh;
                var s = mapping.Resolve(mf.name, out bool ok);
                Debug.Log($"[SeptoMentor] segment '{mf.name}': {m.triangles.Length / 3} tris, bounds size {m.bounds.size * 1000f} mm, centre {m.bounds.center * 1000f} mm -> {s} (confirmed={ok})");
            }
        }
    }
}
