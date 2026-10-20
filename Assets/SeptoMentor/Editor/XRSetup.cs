using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace SeptoMentor.EditorTools
{
    /// <summary>
    /// BEST-EFFORT, reflection-based OpenXR setup for Android (so this file compiles even if
    /// package APIs move). The reliable path is OPEN ITEM 2: enable OpenXR + Meta Quest Support
    /// once in Project Settings > XR Plug-in Management, then commit Assets/XR/**.
    /// Every step logs what happened; nothing here throws.
    /// </summary>
    public static class XRSetup
    {
        const string LoaderType = "UnityEngine.XR.OpenXR.OpenXRLoader";
        static readonly string[] FeatureIds =
        {
            "com.unity.openxr.feature.metaquest",            // Meta Quest Support
            "com.unity.openxr.feature.input.oculustouch",    // Oculus Touch controller profile
        };

        [MenuItem("SeptoMentor/Configure OpenXR (Android)")]
        public static void Configure()
        {
            var group = BuildTargetGroup.Android;
            try { AssignLoader(group); } catch (Exception e) { Debug.LogWarning("[XRSetup] loader: " + e.Message); }
            try { EnableFeatures(group); } catch (Exception e) { Debug.LogWarning("[XRSetup] features: " + e.Message); }
            AssetDatabase.SaveAssets();
        }

        static Type Find(string fullName) =>
            AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(fullName, false)).FirstOrDefault(t => t != null);

        static void AssignLoader(BuildTargetGroup group)
        {
            var perTarget = Find("UnityEditor.XR.Management.XRGeneralSettingsPerBuildTarget");
            var store = Find("UnityEditor.XR.Management.Metadata.XRPackageMetadataStore");
            if (perTarget == null || store == null) { Debug.LogWarning("[XRSetup] XR Management not found."); return; }

            const BindingFlags S = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            const BindingFlags I = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            object container = perTarget.GetMethod("GetOrCreate", S)?.Invoke(null, null);
            if (container == null) { Debug.LogWarning("[XRSetup] XRGeneralSettingsPerBuildTarget.GetOrCreate unavailable; configure XR manually."); return; }

            if (!(bool)perTarget.GetMethod("HasManagerSettingsForBuildTarget", I).Invoke(container, new object[] { group }))
                perTarget.GetMethod("CreateDefaultManagerSettingsForBuildTarget", I).Invoke(container, new object[] { group });

            var general = perTarget.GetMethod("SettingsForBuildTarget", I).Invoke(container, new object[] { group });
            general.GetType().GetProperty("InitManagerOnStart")?.SetValue(general, true);
            var manager = general.GetType().GetProperty("Manager").GetValue(general);

            var assign = store.GetMethod("AssignLoader", S);
            bool ok = (bool)assign.Invoke(null, new object[] { manager, LoaderType, group });
            Debug.Log("[XRSetup] OpenXR loader assigned for Android: " + ok);
        }

        static void EnableFeatures(BuildTargetGroup group)
        {
            var helpers = Find("UnityEditor.XR.OpenXR.Features.FeatureHelpers");
            if (helpers == null) { Debug.LogWarning("[XRSetup] OpenXR editor package not found."); return; }
            const BindingFlags S = BindingFlags.Public | BindingFlags.Static;
            helpers.GetMethod("RefreshFeatures", S, null, new[] { typeof(BuildTargetGroup) }, null)?.Invoke(null, new object[] { group });
            var get = helpers.GetMethod("GetFeatureWithIdForBuildTarget", S);
            foreach (var id in FeatureIds)
            {
                var feature = get?.Invoke(null, new object[] { group, id }) as UnityEngine.Object;
                if (feature == null) { Debug.LogWarning("[XRSetup] feature not found: " + id); continue; }
                feature.GetType().GetProperty("enabled")?.SetValue(feature, true);
                EditorUtility.SetDirty(feature);
                Debug.Log("[XRSetup] enabled " + id);
            }
        }
    }
}
