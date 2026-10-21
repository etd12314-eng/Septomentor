using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

namespace SeptoMentor
{
    /// <summary>
    /// Grid of buttons pressed by poking them with an index fingertip (hand tracking).
    ///   LeftPalm: appears above the left hand when its palm faces the head; poked with the right index finger.
    ///     It stops following the hand while the right finger is near it (so it does not slide away while
    ///     being poked) and stays up briefly after the palm turns away.
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
        public Vector2 buttonSize = new Vector2(0.056f, 0.032f);
        public float gap = 0.007f;

        class Button
        {
            public string id;
            public Transform t;
            public Material mat;
            public Label3D label;
            public string text;
            public bool touching, down, hover;
            public float since;
        }

        static readonly List<HandMenu> Active = new List<HandMenu>();
        static readonly Color Idle = new Color(0.08f, 0.16f, 0.24f, 0.85f), Hover = new Color(0.12f, 0.35f, 0.5f, 0.9f),
                              Touch = new Color(0.2f, 0.75f, 1f, 0.95f);

        readonly List<Button> _buttons = new List<Button>();
        readonly Dictionary<string, Button> _byId = new Dictionary<string, Button>();
        Transform _root, _cursor;
        bool _shown;
        float _cooldownUntil, _lastFacing = -10f;

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
            _root.gameObject.SetActive(false);
        }

        /// <summary>Small dot at the detected fingertip, so the user sees where the touch is registered.</summary>
        void MakeCursor()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "FingerCursor";
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(_root, false);
            go.transform.localScale = Vector3.one * 0.008f;
            go.GetComponent<MeshRenderer>().sharedMaterial =
                new Material(shader != null ? shader : Shader.Find("SeptoMentor/NavMarker")) { color = new Color(1f, 0.9f, 0.2f, 0.9f) };
            _cursor = go.transform;
        }

        void OnEnable() => Active.Add(this);
        void OnDisable() => Active.Remove(this);

        public void AddButton(string id, string text)
        {
            if (_cursor == null) MakeCursor();
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
            var near = Nearest(f1, f2);
            _cursor.gameObject.SetActive(near.HasValue);
            if (near.HasValue) _cursor.position = near.Value;
            foreach (var b in _buttons)
            {
                bool now = Touches(b, f1) || Touches(b, f2);
                b.hover = !now && (Hovers(b, f1) || Hovers(b, f2));
                if (now && !b.touching)
                {
                    b.since = Time.time;
                    // Sliding across the grid must not fire every button on the way.
                    if (Time.time >= _cooldownUntil) { b.down = true; _cooldownUntil = Time.time + 0.35f; }
                }
                b.touching = now;
                b.mat.color = now ? Touch : b.hover ? Hover : Idle;
            }
        }

        static Vector3? Fingertip(bool right) =>
            HandTracking.TryGetJointWorld(right, XRHandJointID.IndexTip, out var p) ? p.position : (Vector3?)null;

        /// <summary>The fingertip closest to the menu, if it is within 20 cm.</summary>
        Vector3? Nearest(Vector3? a, Vector3? b)
        {
            float da = a.HasValue ? Vector3.Distance(a.Value, transform.position) : float.MaxValue;
            float db = b.HasValue ? Vector3.Distance(b.Value, transform.position) : float.MaxValue;
            var p = da <= db ? a : b;
            return Mathf.Min(da, db) < 0.2f ? p : null;
        }

        /// <summary>Finger in front of the button (up to 6 cm), not yet touching.</summary>
        bool Hovers(Button b, Vector3? tip)
        {
            if (!tip.HasValue) return false;
            var l = b.t.parent.InverseTransformPoint(tip.Value) - b.t.localPosition;
            return Mathf.Abs(l.x) < buttonSize.x * 0.5f && Mathf.Abs(l.y) < buttonSize.y * 0.5f && l.z > -0.06f && l.z < 0;
        }

        bool Touches(Button b, Vector3? tip)
        {
            if (!tip.HasValue) return false;
            var l = b.t.parent.InverseTransformPoint(tip.Value) - b.t.localPosition;
            // In front up to 8 mm (toward the viewer is -z), through the button up to 15 mm;
            // a touched button lets go only with some margin, so tracking jitter can't re-press it.
            float m = b.touching ? 0.006f : 0f;
            return Mathf.Abs(l.x) < buttonSize.x * 0.5f + m && Mathf.Abs(l.y) < buttonSize.y * 0.5f + m &&
                   l.z > -0.01f - 2 * m && l.z < 0.03f + 2 * m;
        }

        /// <summary>Follows the left hand while its palm faces the head; returns whether it should be shown.</summary>
        bool PlacePalm()
        {
            if (head == null) return false;
            bool fingerNear = _shown && Fingertip(true) is Vector3 f && Vector3.Distance(f, transform.position) < 0.15f;
            if (!HandTracking.TryGetPalmWorld(false, out var palm, out var normal))
                return _shown && (fingerNear || Time.time - _lastFacing < 0.8f);
            float facing = Vector3.Dot(normal, (head.position - palm).normalized);
            if (facing >= (_shown ? 0.25f : 0.5f)) _lastFacing = Time.time;
            else if (!_shown || !fingerNear && Time.time - _lastFacing > 0.8f) return false;
            if (fingerNear) return true;   // hold still while being poked

            // Beside the palm toward the thumb side is hard to reach; above the hand works for either size.
            var target = palm + normal * 0.05f + Vector3.up * 0.1f;
            var rot = Quaternion.LookRotation(target - head.position, Vector3.up);
            if (!_shown) transform.SetPositionAndRotation(target, rot);
            else
            {
                float k = 1f - Mathf.Exp(-Time.deltaTime * 12f);
                transform.SetPositionAndRotation(Vector3.Lerp(transform.position, target, k), Quaternion.Slerp(transform.rotation, rot, k));
            }
            return true;
        }
    }
}
