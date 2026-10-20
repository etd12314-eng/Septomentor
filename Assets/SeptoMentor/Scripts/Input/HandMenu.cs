using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

namespace SeptoMentor
{
    /// <summary>
    /// Grid of buttons pressed by poking them with an index fingertip (hand tracking).
    ///   LeftPalm: appears above the left hand when its palm faces the head; poked with the right index finger.
    ///   Fixed: stays where the owner puts it; poked with either index finger.
    /// Owners read Pressed(id) (one frame) / Held(id). A button with id "home" is the hold-to-exit button.
    /// </summary>
    public class HandMenu : MonoBehaviour
    {
        public enum Anchor { LeftPalm, Fixed }

        public Anchor anchor = Anchor.LeftPalm;
        public Transform head;
        public Shader shader;
        public int columns = 3;
        public Vector2 buttonSize = new Vector2(0.05f, 0.026f);
        public float gap = 0.006f;

        class Button
        {
            public string id;
            public Transform t;
            public Material mat;
            public Label3D label;
            public string text;
            public bool touching, down;
            public float since;
        }

        static readonly List<HandMenu> Active = new List<HandMenu>();
        static readonly Color Idle = new Color(0.08f, 0.16f, 0.24f, 0.85f), Touch = new Color(0.2f, 0.75f, 1f, 0.95f);

        readonly List<Button> _buttons = new List<Button>();
        readonly Dictionary<string, Button> _byId = new Dictionary<string, Button>();
        Transform _root;
        bool _shown;
        float _cooldownUntil;

        /// <summary>True while any menu's "home" button has been held for longer than <paramref name="seconds"/>.</summary>
        public static bool HomeHeldFor(float seconds)
        {
            foreach (var m in Active) if (m._byId.TryGetValue("home", out var b) && b.touching && Time.time - b.since > seconds) return true;
            return false;
        }

        public bool Visible => _shown;

        void Awake()
        {
            _root = new GameObject("Buttons").transform;
            _root.SetParent(transform, false);
        }

        void OnEnable() => Active.Add(this);
        void OnDisable() => Active.Remove(this);

        public void AddButton(string id, string text)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "Btn_" + id;
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(_root, false);
            var mat = new Material(shader != null ? shader : Shader.Find("SeptoMentor/NavMarker")) { color = Idle };
            mat.renderQueue = 2990;   // before the (transparent) text, which must draw on top
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            var label = new Label3D(go.transform.parent, "Lbl_" + id, 60);
            label.go.transform.localScale = Vector3.one * 0.001f;
            label.SetBox(buttonSize.x * 1000f, buttonSize.y * 1000f);
            label.Set(text, Color.white);
            var b = new Button { id = id, t = go.transform, mat = mat, label = label, text = text };
            _buttons.Add(b);
            _byId[id] = b;
            Layout();
        }

        public void SetText(string id, string text)
        {
            if (_byId.TryGetValue(id, out var b) && b.text != text) { b.text = text; b.label.Set(text, Color.white); }
        }

        public bool Pressed(string id) => _shown && _byId.TryGetValue(id, out var b) && b.down;
        public bool Held(string id) => _shown && _byId.TryGetValue(id, out var b) && b.touching;

        void Layout()
        {
            int rows = (_buttons.Count + columns - 1) / columns;
            for (int i = 0; i < _buttons.Count; i++)
            {
                int r = i / columns, c = i % columns;
                var p = new Vector3((c - (columns - 1) * 0.5f) * (buttonSize.x + gap), ((rows - 1) * 0.5f - r) * (buttonSize.y + gap), 0);
                _buttons[i].t.localPosition = p;
                _buttons[i].t.localScale = new Vector3(buttonSize.x, buttonSize.y, 1);
                _buttons[i].label.go.transform.localPosition = p + new Vector3(0, 0, -0.001f);
            }
        }

        void Update()
        {
            foreach (var b in _buttons) b.down = false;
            bool show = anchor == Anchor.Fixed ? HandTracking.AnyTracked : PlacePalm();
            if (show != _shown) { _shown = show; _root.gameObject.SetActive(show); }
            if (!show) { foreach (var b in _buttons) b.touching = false; return; }

            Vector3? f1 = Fingertip(true), f2 = anchor == Anchor.Fixed ? Fingertip(false) : null;
            foreach (var b in _buttons)
            {
                bool now = Touches(b, f1) || Touches(b, f2);
                if (now && !b.touching)
                {
                    b.since = Time.time;
                    // Sliding across the grid must not fire every button on the way.
                    if (Time.time >= _cooldownUntil) { b.down = true; _cooldownUntil = Time.time + 0.35f; }
                }
                b.touching = now;
                b.mat.color = now ? Touch : Idle;
            }
        }

        static Vector3? Fingertip(bool right) =>
            HandTracking.TryGetJointWorld(right, XRHandJointID.IndexTip, out var p) ? p.position : (Vector3?)null;

        bool Touches(Button b, Vector3? tip)
        {
            if (!tip.HasValue) return false;
            var l = b.t.parent.InverseTransformPoint(tip.Value) - b.t.localPosition;
            // In front up to 8 mm (toward the viewer is -z), through the button up to 15 mm;
            // a touched button lets go only with some margin, so tracking jitter can't re-press it.
            float m = b.touching ? 0.006f : 0f;
            return Mathf.Abs(l.x) < buttonSize.x * 0.5f + m && Mathf.Abs(l.y) < buttonSize.y * 0.5f + m &&
                   l.z > -0.008f - 2 * m && l.z < 0.015f + 2 * m;
        }

        /// <summary>Follows the left hand while its palm faces the head; returns whether it should be shown.</summary>
        bool PlacePalm()
        {
            if (head == null || !HandTracking.TryGetJointWorld(false, XRHandJointID.Palm, out var palm)) return false;
            var normal = -(palm.rotation * Vector3.up);   // XR Hands: +Y is the back of the hand
            float facing = Vector3.Dot(normal, (head.position - palm.position).normalized);
            if (facing < (_shown ? 0.3f : 0.55f)) return false;

            var target = palm.position + normal * 0.04f + Vector3.up * 0.09f;
            var rot = Quaternion.LookRotation(target - head.position, Vector3.up);
            if (!_shown) transform.SetPositionAndRotation(target, rot);
            else
            {
                float k = 1f - Mathf.Exp(-Time.deltaTime * 15f);
                transform.SetPositionAndRotation(Vector3.Lerp(transform.position, target, k), Quaternion.Slerp(transform.rotation, rot, k));
            }
            return true;
        }
    }
}
