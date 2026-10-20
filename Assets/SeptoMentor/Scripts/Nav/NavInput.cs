using UnityEngine;
using UnityEngine.XR;

namespace SeptoMentor.Nav
{
    /// <summary>
    /// Touch controller state for the navigation mode, with press edges.
    /// Editor fallback keys: Space = trigger, Enter = A, Backspace = B, O = X, Q = Y,
    /// P = left trigger, C = right stick click, arrows = right stick.
    /// Hand tracking (controllers put down): right index fingertip = pointer, left pinch = trigger,
    /// left palm toward the face = button menu (poke with the right index finger) for everything else.
    /// </summary>
    public class NavInput
    {
        public bool TriggerHeld, TriggerDown, A, B, X, Y, LeftTriggerDown, StickClick, LeftGrip, BothGripsHeld;
        public Vector2 LeftStick, RightStick;
        public int StickStepY;   // -1 / +1 on a right-stick flick
        public bool HandsActive;  // hand tracking in use instead of the controllers

        /// <summary>Left-palm poke menu; created by the owner, read here.</summary>
        public HandMenu menu;

        bool _t, _a, _b, _x, _y, _lt, _sc, _flick;
        readonly Pinch _leftPinch = new Pinch(false);

        public static void AddMenuButtons(HandMenu m)
        {
            m.AddButton("a", "Next\n(A)"); m.AddButton("b", "Back\n(B)"); m.AddButton("up", "Up");
            m.AddButton("x", "Overlay\n(X)"); m.AddButton("window", "CT window"); m.AddButton("down", "Down");
            m.AddButton("y", "Ask AI\n(Y)"); m.AddButton("panel", "Panel\nhere"); m.AddButton("home", "Hold 2 s:\nmenu");
        }

        public void Update()
        {
            var r = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            var l = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);

            float rt = Axis(r, CommonUsages.trigger), lt = Axis(l, CommonUsages.trigger);
            bool trig = rt > (_t ? 0.5f : 0.7f);
            bool ltrig = lt > (_lt ? 0.5f : 0.7f);
            bool a = Btn(r, CommonUsages.primaryButton), b = Btn(r, CommonUsages.secondaryButton);
            bool x = Btn(l, CommonUsages.primaryButton), y = Btn(l, CommonUsages.secondaryButton);
            bool sc = Btn(r, CommonUsages.primary2DAxisClick);
            LeftGrip = Btn(l, CommonUsages.gripButton);
            BothGripsHeld = LeftGrip && Btn(r, CommonUsages.gripButton);
            r.TryGetFeatureValue(CommonUsages.primary2DAxis, out RightStick);
            l.TryGetFeatureValue(CommonUsages.primary2DAxis, out LeftStick);

#if ENABLE_INPUT_SYSTEM
            var k = UnityEngine.InputSystem.Keyboard.current;
            if (k != null)
            {
                trig |= k.spaceKey.isPressed; a |= k.enterKey.isPressed; b |= k.backspaceKey.isPressed;
                x |= k.oKey.isPressed; y |= k.qKey.isPressed; ltrig |= k.pKey.isPressed; sc |= k.cKey.isPressed;
                var ks = new Vector2((k.rightArrowKey.isPressed ? 1 : 0) - (k.leftArrowKey.isPressed ? 1 : 0),
                                     (k.upArrowKey.isPressed ? 1 : 0) - (k.downArrowKey.isPressed ? 1 : 0));
                if (ks != Vector2.zero) RightStick = ks;
            }
#endif
            HandsActive = HandTracking.AnyTracked;
            int handStep = 0;
            if (HandsActive)
            {
                // The pinch is ignored while the menu is up, so poking a button can't also capture a point.
                _leftPinch.Update(menu == null || !menu.Visible);
                trig |= _leftPinch.Held;
                if (menu != null)
                {
                    // Pressed (one frame, with the menu's anti-slide cooldown) still makes a clean edge below.
                    a |= menu.Pressed("a"); b |= menu.Pressed("b"); x |= menu.Pressed("x"); y |= menu.Pressed("y");
                    ltrig |= menu.Pressed("window"); sc |= menu.Pressed("panel");
                    if (menu.Pressed("up")) handStep = 1;
                    if (menu.Pressed("down")) handStep = -1;
                    BothGripsHeld |= menu.Held("home");
                }
            }

            TriggerHeld = trig;
            TriggerDown = Edge(trig, ref _t, r);
            A = Edge(a, ref _a, r); B = Edge(b, ref _b, r);
            X = Edge(x, ref _x, l); Y = Edge(y, ref _y, l);
            LeftTriggerDown = Edge(ltrig, ref _lt, l);
            StickClick = Edge(sc, ref _sc, r);

            StickStepY = 0;
            if (Mathf.Abs(RightStick.y) > 0.7f) { if (!_flick) StickStepY = RightStick.y > 0 ? 1 : -1; _flick = true; }
            else if (Mathf.Abs(RightStick.y) < 0.3f) _flick = false;
            if (handStep != 0) StickStepY = handStep;
        }

        static float Axis(InputDevice d, InputFeatureUsage<float> u) => d.isValid && d.TryGetFeatureValue(u, out float v) ? v : 0f;
        static bool Btn(InputDevice d, InputFeatureUsage<bool> u) => d.isValid && d.TryGetFeatureValue(u, out bool v) && v;

        static bool Edge(bool now, ref bool prev, InputDevice d)
        {
            bool down = now && !prev;
            prev = now;
            if (down) Haptic(d, 0.3f, 0.03f);
            return down;
        }

        public static void Haptic(InputDevice d, float amp, float sec)
        {
            if (d.isValid && d.TryGetHapticCapabilities(out var caps) && caps.supportsImpulse) d.SendHapticImpulse(0, amp, sec);
        }

        public static void HapticRight(float amp, float sec) => Haptic(InputDevices.GetDeviceAtXRNode(XRNode.RightHand), amp, sec);

        /// <summary>Right index finger in tracking space: tip and the joint behind it (for the pointing direction).</summary>
        public static bool RightIndex(out Vector3 tip, out Vector3 distal, out Quaternion rot)
        {
            tip = distal = default; rot = Quaternion.identity;
            if (!HandTracking.TryGetJoint(true, UnityEngine.XR.Hands.XRHandJointID.IndexTip, out var t) ||
                !HandTracking.TryGetJoint(true, UnityEngine.XR.Hands.XRHandJointID.IndexDistal, out var d)) return false;
            tip = t.position; distal = d.position; rot = t.rotation;
            return true;
        }

        /// <summary>Right controller pose in tracking space (OpenXR grip pose).</summary>
        public static bool RightPose(out Vector3 pos, out Quaternion rot)
        {
            var r = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            pos = default; rot = Quaternion.identity;
            return r.isValid && r.TryGetFeatureValue(CommonUsages.devicePosition, out pos) && r.TryGetFeatureValue(CommonUsages.deviceRotation, out rot);
        }
    }
}
