using System.IO;
using System.Xml;
using UnityEditor.Android;

namespace SeptoMentor.EditorTools
{
    /// <summary>
    /// Makes sure the Quest manifest asks for hand tracking (permission + optional feature), whatever
    /// the OpenXR features already added. Runs after the other Gradle post-processors.
    /// </summary>
    public class HandTrackingManifest : IPostGenerateGradleAndroidProject
    {
        const string AndroidNs = "http://schemas.android.com/apk/res/android";
        public int callbackOrder => 10000;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            var file = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(file)) return;
            var doc = new XmlDocument();
            doc.Load(file);
            var root = doc.DocumentElement;
            bool changed = Ensure(doc, root, "uses-permission", "com.oculus.permission.HAND_TRACKING", null);
            changed |= Ensure(doc, root, "uses-feature", "oculus.software.handtracking", "false");
            if (changed) doc.Save(file);
            UnityEngine.Debug.Log("[SeptoMentor] Hand tracking manifest entries " + (changed ? "added" : "already present"));
        }

        static bool Ensure(XmlDocument doc, XmlElement root, string tag, string name, string required)
        {
            foreach (XmlElement e in root.GetElementsByTagName(tag))
                if (e.GetAttribute("name", AndroidNs) == name) return false;
            var el = doc.CreateElement(tag);
            el.SetAttribute("name", AndroidNs, name);
            if (required != null) el.SetAttribute("required", AndroidNs, required);
            root.AppendChild(el);
            return true;
        }
    }
}
