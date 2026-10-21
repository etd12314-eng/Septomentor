using System;
using UnityEngine;
using UnityEngine.XR;
// UnityEngine.InputSystem is NOT imported: its CommonUsages/InputDevice would clash with UnityEngine.XR's.

namespace SeptoMentor
{
    /// <summary>
    /// Quest Touch controllers via UnityEngine.XR (OpenXR Oculus Touch profile).
    ///   A: next phase   B: cycle instrument   X: recenter   Y: ask AI
    ///   Right stick: scope look (hold right grip: twist instrument)   Left stick Y: advance scope
    ///   Right trigger: toggle labels   Left trigger: simulate mucosal tear (synthetic only)
    ///   Right stick click: hand-held scope on/off (the right controller is the endoscope)
    /// Editor keys: N, I, R, Q, L, T, arrows (look), W/S (advance), G (hold = twist).
    /// Hands (controllers put down): left palm toward the face = button menu (poke with the right index finger);
    /// right pinch and drag = scope look (left/right/up/down) and advance (push away / pull back).
    /// </summary>
    public class ControllerInput : MonoBehaviour
    {
        public event Action NextPhase, CycleInstrument, Recenter, AskAI, ToggleLabels, SimulateTear, ToggleScope;

        public Vector2 Look { get; private set; }
        public float Advance { get; private set; }
        public float Twist { get; private set; }

        bool _a, _b, _x, _y, _rt, _lt, _rs;
        HandMenu _menu;
        readonly Pinch _pinch = new Pinch(true);
        Vector3 _pinchStart;

        void Start()
        {
            _menu = new GameObject("HandMenu").AddComponent<HandMenu>();
            _menu.head = Camera.main != null ? Camera.main.transform : null;
            _menu.AddButton("next", "Next phase\n(A)"); _menu.AddButton("instr", "Instrument\n(B)"); _menu.AddButton("recenter", "Recenter\n(X)");
            _menu.AddButton("ai", "Ask AI\n(Y)"); _menu.AddButton("labels", "Labels"); _menu.AddButton("tear", "Sim. tear");
            _menu.AddButton("scope", "Scope:\nhand / auto"); _menu.AddButton("home", "Hold 2 s:\nmenu");
            new GameObject("Hands").AddComponent<HandSkeleton>();   // VR: the real hands are not visible
        }

        void Update()
        {
            var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            var left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);

            Edge(right, CommonUsages.primaryButton, ref _a, NextPhase);
            Edge(right, CommonUsages.secondaryButton, ref _b, CycleInstrument);
            Edge(left, CommonUsages.primaryButton, ref _x, Recenter);
            Edge(left, CommonUsages.secondaryButton, ref _y, AskAI);
            EdgeTrigger(right, ref _rt, ToggleLabels);
            EdgeTrigger(left, ref _lt, SimulateTear);
            Edge(right, CommonUsages.primary2DAxisClick, ref _rs, ToggleScope);

            right.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 rs);
            left.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 ls);
            right.TryGetFeatureValue(CommonUsages.gripButton, out bool grip);
            rs = Dead(rs); ls = Dead(ls);

            Look = grip ? Vector2.zero : rs;
            Twist = grip ? rs.x : 0f;
            Advance = ls.y;

            HandControls();

            KeyboardFallback();
        }

        void HandControls()
        {
            if (_menu == null) return;
            if (_menu.Pressed("next")) NextPhase?.Invoke();
            if (_menu.Pressed("instr")) CycleInstrument?.Invoke();
            if (_menu.Pressed("recenter")) Recenter?.Invoke();
            if (_menu.Pressed("ai")) AskAI?.Invoke();
            if (_menu.Pressed("labels")) ToggleLabels?.Invoke();
            if (_menu.Pressed("tear")) SimulateTear?.Invoke();
            if (_menu.Pressed("scope")) ToggleScope?.Invoke();

            // Pinch-and-drag acts as a joystick: 6 cm of hand travel = full deflection.
            _pinch.Update(!_menu.Visible);
            var head = _menu.head;
            if (head == null || !HandTracking.TryGetJointWorld(true, UnityEngine.XR.Hands.XRHandJointID.ThumbTip, out var thumb)) return;
            if (_pinch.Down) _pinchStart = thumb.position;
            if (!_pinch.Held) return;
            var d = (thumb.position - _pinchStart) / 0.06f;
            var fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            var right = Vector3.Cross(Vector3.up, fwd);
            Look = Dead(Vector2.ClampMagnitude(new Vector2(Vector3.Dot(d, right), d.y), 1f));
            float adv = Mathf.Clamp(Vector3.Dot(d, fwd), -1f, 1f);
            Advance = Mathf.Abs(adv) < 0.15f ? 0f : adv;
        }

        static Vector2 Dead(Vector2 v) => v.magnitude < 0.15f ? Vector2.zero : v;

        static void Edge(InputDevice d, InputFeatureUsage<bool> usage, ref bool prev, Action a)
        {
            bool now = d.isValid && d.TryGetFeatureValue(usage, out bool v) && v;
            if (now && !prev) { a?.Invoke(); Tick(d); }
            prev = now;
        }

        /// <summary>Short haptic click so every press is confirmed, even when nothing visible changes.</summary>
        static void Tick(InputDevice d)
        {
            if (d.TryGetHapticCapabilities(out var caps) && caps.supportsImpulse) d.SendHapticImpulse(0, 0.3f, 0.03f);
        }

        static void EdgeTrigger(InputDevice d, ref bool prev, Action a)
        {
            // Hysteresis: press above 0.7, release below 0.5, so a half-held trigger can't double-fire.
            bool now = d.isValid && d.TryGetFeatureValue(CommonUsages.trigger, out float v) && v > (prev ? 0.5f : 0.7f);
            if (now && !prev) { a?.Invoke(); Tick(d); }
            prev = now;
        }

        void KeyboardFallback()
        {
#if ENABLE_INPUT_SYSTEM
            var k = UnityEngine.InputSystem.Keyboard.current;
            if (k == null) return;
            if (k.nKey.wasPressedThisFrame) NextPhase?.Invoke();
            if (k.iKey.wasPressedThisFrame) CycleInstrument?.Invoke();
            if (k.rKey.wasPressedThisFrame) Recenter?.Invoke();
            if (k.qKey.wasPressedThisFrame) AskAI?.Invoke();
            if (k.lKey.wasPressedThisFrame) ToggleLabels?.Invoke();
            if (k.tKey.wasPressedThisFrame) SimulateTear?.Invoke();
            var look = new Vector2((k.rightArrowKey.isPressed ? 1 : 0) - (k.leftArrowKey.isPressed ? 1 : 0),
                                   (k.upArrowKey.isPressed ? 1 : 0) - (k.downArrowKey.isPressed ? 1 : 0));
            float adv = (k.wKey.isPressed ? 1 : 0) - (k.sKey.isPressed ? 1 : 0);
            bool twist = k.gKey.isPressed;
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.N)) NextPhase?.Invoke();
            if (Input.GetKeyDown(KeyCode.I)) CycleInstrument?.Invoke();
            if (Input.GetKeyDown(KeyCode.R)) Recenter?.Invoke();
            if (Input.GetKeyDown(KeyCode.Q)) AskAI?.Invoke();
            if (Input.GetKeyDown(KeyCode.L)) ToggleLabels?.Invoke();
            if (Input.GetKeyDown(KeyCode.T)) SimulateTear?.Invoke();
            var look = new Vector2((Input.GetKey(KeyCode.RightArrow) ? 1 : 0) - (Input.GetKey(KeyCode.LeftArrow) ? 1 : 0),
                                   (Input.GetKey(KeyCode.UpArrow) ? 1 : 0) - (Input.GetKey(KeyCode.DownArrow) ? 1 : 0));
            float adv = (Input.GetKey(KeyCode.W) ? 1 : 0) - (Input.GetKey(KeyCode.S) ? 1 : 0);
            bool twist = Input.GetKey(KeyCode.G);
#else
            var look = Vector2.zero; float adv = 0; bool twist = false;
#endif
            if (twist) { Twist = 1f; return; }
            if (look != Vector2.zero) Look = look;
            if (adv != 0) Advance = adv;
        }
    }
}
