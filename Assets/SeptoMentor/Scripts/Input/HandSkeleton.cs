using UnityEngine;
using UnityEngine.XR.Hands;

namespace SeptoMentor
{
    /// <summary>
    /// Draws tracked hands as small joint spheres. In VR the user cannot see their real hands;
    /// in passthrough only the index fingertips are drawn, as a poke cursor.
    /// </summary>
    public class HandSkeleton : MonoBehaviour
    {
        public Shader shader;
        public bool tipsOnly;
        public Color color = new Color(0.55f, 0.85f, 1f, 0.7f);

        Transform[,] _joints;
        static readonly XRHandJointID[] Tips = { XRHandJointID.IndexTip };

        void Start()
        {
            int n = tipsOnly ? Tips.Length : XRHandJointID.EndMarker.ToIndex();
            _joints = new Transform[2, n];
            var mat = new Material(shader != null ? shader : Shader.Find("SeptoMentor/NavMarker")) { color = color };
            for (int h = 0; h < 2; h++)
                for (int j = 0; j < n; j++)
                {
                    var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    Destroy(go.GetComponent<Collider>());
                    go.name = (h == 1 ? "R_" : "L_") + Id(j);
                    go.GetComponent<MeshRenderer>().sharedMaterial = mat;
                    go.transform.SetParent(transform, false);
                    go.transform.localScale = Vector3.one * (tipsOnly ? 0.008f : 0.007f);
                    go.SetActive(false);
                    _joints[h, j] = go.transform;
                }
        }

        XRHandJointID Id(int j) => tipsOnly ? Tips[j] : XRHandJointIDUtility.FromIndex(j);

        void Update()
        {
            for (int h = 0; h < 2; h++)
                for (int j = 0; j < _joints.GetLength(1); j++)
                {
                    bool ok = HandTracking.TryGetJointWorld(h == 1, Id(j), out var p);
                    var t = _joints[h, j];
                    if (t.gameObject.activeSelf != ok) t.gameObject.SetActive(ok);
                    if (ok) t.position = p.position;
                }
        }
    }
}
