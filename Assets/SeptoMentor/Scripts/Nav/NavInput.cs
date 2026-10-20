using UnityEngine;
using UnityEngine.XR;

namespace SeptoMentor.Nav
{
    /// <summary>
    /// Touch controller state for the navigation mode, with press edges.
    /// Editor fallback keys: Space = trigger, Enter = A, Backspace = B, O = X, Q = Y,
    /// P = left trigger, C = right stick click, arrows = right stick.
    /// </summary>
    public class NavInput
    {
        public bool TriggerHeld, TriggerDown, A, B, X, Y, LeftTriggerDown, StickClick, LeftGrip, BothGripsHeld;
        public Vector2 LeftStick, RightStick;
        public int StickStepY;   // -1 / +1 on a right-stick flick

        bool _t, _a, _b, _x, _y, _lt, _sc, _flick;

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
            TriggerHeld = trig;
            TriggerDown = Edge(trig, ref _t, r);
            A = Edge(a, ref _a, r); B = Edge(b, ref _b, r);
            X = Edge(x, ref _x, l); Y = Edge(y, ref _y, l);
            LeftTriggerDown = Edge(ltrig, ref _lt, l);
            StickClick = Edge(sc, ref _sc, r);

            StickStepY = 0;
            if (Mathf.Abs(RightStick.y) > 0.7f) { if (!_flick) StickStepY = RightStick.y > 0 ? 1 : -1; _flick = true; }
            else if (Mathf.Abs(RightStick.y) < 0.3f) _flick = false;
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

        /// <summary>Right controller pose in tracking space (OpenXR grip pose).</summary>
        public static bool RightPose(out Vector3 pos, out Quaternion rot)
        {
            var r = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            pos = default; rot = Quaternion.identity;
            return r.isValid && r.TryGetFeatureValue(CommonUsages.devicePosition, out pos) && r.TryGetFeatureValue(CommonUsages.deviceRotation, out rot);
        }
    }
}
