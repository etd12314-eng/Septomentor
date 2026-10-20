using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

namespace SeptoMentor.EditorTools
{
    /// <summary>
    /// CI entry point: -executeMethod SeptoMentor.EditorTools.BuildScript.Build
    /// (game-ci passes -customBuildPath). Env: MENTOR_ENDPOINT, MENTOR_TOKEN, MENTOR_ALLOW_FRAMES.
    /// </summary>
    public static class BuildScript
    {
        const string ConfigPath = "Assets/SeptoMentor/Resources/mentor_config.json";

        [MenuItem("SeptoMentor/Build Quest APK")]
        public static void Build()
        {
            string output = Arg("-customBuildPath") ?? "Builds/Android/SeptoMentor.apk";
            if (!output.EndsWith(".apk")) output = Path.Combine(output, "SeptoMentor.apk");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));

            ConfigurePlayer();
            XRSetup.Configure();
            WriteMentorConfig();
            AssetDatabase.Refresh();
            SceneBuilder.Build();

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { SceneBuilder.ScenePath },
                locationPathName = output,
                target = BuildTarget.Android,
                options = BuildOptions.None,
            });
            Debug.Log($"[SeptoMentor] Build {report.summary.result}: {output} ({report.summary.totalSize / (1024 * 1024)} MB)");
            if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
        }

        static void ConfigurePlayer()
        {
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            var android = NamedBuildTarget.Android;
            PlayerSettings.companyName = "SeptoMentor";
            PlayerSettings.productName = "SeptoMentor";
            PlayerSettings.SetApplicationIdentifier(android, "org.septomentor.app");
            if (string.IsNullOrEmpty(PlayerSettings.bundleVersion) || PlayerSettings.bundleVersion == "1.0") PlayerSettings.bundleVersion = "0.1.0";
            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.Android.forceInternetPermission = true;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            EditorUserBuildSettings.exportAsGoogleAndroidProject = false;
            EditorUserBuildSettings.buildAppBundle = false;
            // No keystore configured -> Unity signs with the debug key.
        }

        [Serializable] class MentorConfig { public string endpoint; public string token; public bool allowFrames; }

        /// <summary>
        /// Bakes the proxy URL + proxy token into Resources (gitignored). The token is
        /// extractable from the APK: make it revocable and rate-limited on the proxy.
        /// </summary>
        static void WriteMentorConfig()
        {
            var cfg = new MentorConfig
            {
                endpoint = Environment.GetEnvironmentVariable("MENTOR_ENDPOINT") ?? "",
                token = Environment.GetEnvironmentVariable("MENTOR_TOKEN") ?? "",
                allowFrames = Environment.GetEnvironmentVariable("MENTOR_ALLOW_FRAMES") == "1",
            };
            if (string.IsNullOrEmpty(cfg.endpoint) && File.Exists(ConfigPath))
            {
                Debug.Log("[SeptoMentor] MENTOR_ENDPOINT unset; keeping existing " + ConfigPath);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            File.WriteAllText(ConfigPath, JsonUtility.ToJson(cfg, true));
            Debug.Log($"[SeptoMentor] mentor_config: endpoint={(cfg.endpoint == "" ? "(none, AI off)" : cfg.endpoint)}, token={(cfg.token == "" ? "none" : "set")}, allowFrames={cfg.allowFrames}");
        }

        static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
            return null;
        }
    }
}
