using System.IO;
using System.Xml;
using UnityEditor.Android;

namespace SeptoMentor.EditorTools
{
    /// <summary>
    /// Makes sure the Quest manifest asks for hand tracking (permission + optional feature) at the high tracking
    /// frequency (less jitter and latency), whatever the OpenXR features already added. Runs after the other
    /// Gradle post-processors.
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
            var app = root.GetElementsByTagName("application").Count > 0 ? (XmlElement)root.GetElementsByTagName("application")[0] : null;
            if (app != null)
            {
                changed |= MetaData(doc, app, "com.oculus.handtracking.frequency", "HIGH");
                changed |= MetaData(doc, app, "com.oculus.handtracking.version", "V2.0");
            }
            if (changed) doc.Save(file);
            UnityEngine.Debug.Log("[SeptoMentor] Hand tracking manifest entries " + (changed ? "added" : "already present"));
        }

        static bool MetaData(XmlDocument doc, XmlElement app, string name, string value)
        {
            foreach (XmlElement e in app.GetElementsByTagName("meta-data"))
                if (e.GetAttribute("name", AndroidNs) == name)
                {
                    if (e.GetAttribute("value", AndroidNs) == value) return false;
                    e.SetAttribute("value", AndroidNs, value);
                    return true;
                }
            var el = doc.CreateElement("meta-data");
            el.SetAttribute("name", AndroidNs, name);
            el.SetAttribute("value", AndroidNs, value);
            app.AppendChild(el);
            return true;
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
