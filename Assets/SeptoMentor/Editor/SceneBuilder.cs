using System.IO;
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
            cam.backgroundColor = new Color(0.02f, 0.02f, 0.03f, 1f);   // OPEN ITEM 3: passthrough instead
            cam.nearClipPlane = 0.05f;
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
            Debug.Log(patient.objModel != null
                ? "[SeptoMentor] Using " + ObjPath
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

            // --- User-facing rig: monitor + hologram -------------------------------------------
            var userRig = new GameObject("UserRig").transform;
            userRig.position = new Vector3(0, 1.45f, 0.9f);
            var monitor = new GameObject("Monitor").transform;
            monitor.SetParent(userRig, false);
            var overlay = monitor.gameObject.AddComponent<OverlayView>();
            overlay.provider = provider;
            overlay.monitorShader = Shader.Find("SeptoMentor/MonitorComposite");

            var holo = new GameObject("AnatomyHologram").transform;
            holo.SetParent(userRig, false);
            holo.localPosition = new Vector3(-0.48f, 0.0f, 0.05f);
            holo.localRotation = Quaternion.Euler(0, 35f, 0);
            var hologram = holo.gameObject.AddComponent<AnatomyHologram>();
            hologram.synthetic = provider;
            hologram.ghostShader = Shader.Find("SeptoMentor/Ghost");

            var app = sys.AddComponent<SeptoMentorApp>();
            app.provider = provider; app.mentor = mentor; app.overlay = overlay; app.ai = ai;
            app.speech = speech; app.logger = logger; app.input = input;
            app.head = camGo.transform; app.userRig = userRig;

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AlwaysIncludeShaders("SeptoMentor/EndoTissue", "SeptoMentor/LabelId", "SeptoMentor/MonitorComposite", "SeptoMentor/Ghost");
            Debug.Log("[SeptoMentor] Scene generated: " + ScenePath);
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
