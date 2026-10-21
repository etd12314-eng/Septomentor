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
            ImportTmpEssentials();
            AssetDatabase.Refresh();
            SceneBuilder.Build();

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = SceneBuilder.AllScenes,
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
            PlayerSettings.Android.minSdkVersion = (AndroidSdkVersions)32;   // Android 12L: required by Meta Quest Camera (Passthrough)
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

        /// <summary>
        /// TextMeshPro's default font and settings ship as a .unitypackage inside com.unity.ugui; without
        /// them Label3D falls back to blurry legacy TextMesh. AssetDatabase.ImportPackage is deferred in
        /// batch mode (too late for this build), so the package (a gzipped tar of guid folders holding
        /// "pathname", "asset", "asset.meta") is unpacked directly into the project instead.
        /// </summary>
        static void ImportTmpEssentials()
        {
            if (AssetDatabase.FindAssets("t:TMP_Settings").Length > 0) return;
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(TMPro.TextMeshPro).Assembly);
            var pkg = info != null ? Path.Combine(info.resolvedPath, "Package Resources", "TMP Essential Resources.unitypackage") : null;
            if (pkg == null || !File.Exists(pkg)) { Debug.LogWarning("[SeptoMentor] TMP Essential Resources not found; legacy text will be used."); return; }
            try
            {
                var entries = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, byte[]>>();
                using (var gz = new System.IO.Compression.GZipStream(File.OpenRead(pkg), System.IO.Compression.CompressionMode.Decompress))
                {
                    var header = new byte[512];
                    while (ReadFull(gz, header) == 512)
                    {
                        string name = System.Text.Encoding.ASCII.GetString(header, 0, 100).Split('\0')[0];
                        if (name.Length == 0) break;
                        string octal = System.Text.Encoding.ASCII.GetString(header, 124, 12).Trim('\0', ' ');
                        long size = octal.Length > 0 ? Convert.ToInt64(octal, 8) : 0;
                        var data = new byte[size];
                        ReadFull(gz, data);
                        long pad = (512 - size % 512) % 512;
                        if (pad > 0) ReadFull(gz, new byte[pad]);
                        if (header[156] != (byte)'0' && header[156] != 0) continue;   // regular files only
                        var parts = name.TrimStart('.', '/').Split('/');
                        if (parts.Length != 2) continue;
                        if (!entries.TryGetValue(parts[0], out var e)) entries[parts[0]] = e = new System.Collections.Generic.Dictionary<string, byte[]>();
                        e[parts[1]] = data;
                    }
                }
                int n = 0;
                foreach (var e in entries.Values)
                {
                    if (!e.TryGetValue("pathname", out var pn) || !e.TryGetValue("asset", out var asset)) continue;
                    string path = System.Text.Encoding.UTF8.GetString(pn).Split('\n')[0].Trim();
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllBytes(path, asset);
                    if (e.TryGetValue("asset.meta", out var meta)) File.WriteAllBytes(path + ".meta", meta);
                    n++;
                }
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                Debug.Log($"[SeptoMentor] TMP Essential Resources unpacked ({n} files): " + (AssetDatabase.FindAssets("t:TMP_Settings").Length > 0 ? "ok" : "settings asset missing"));
            }
            catch (Exception ex) { Debug.LogWarning("[SeptoMentor] TMP Essential Resources unpack failed: " + ex.Message); }
        }

        static int ReadFull(Stream s, byte[] buf)
        {
            int got = 0;
            while (got < buf.Length) { int r = s.Read(buf, got, buf.Length - got); if (r <= 0) break; got += r; }
            return got;
        }

        static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
            return null;
        }
    }
}
