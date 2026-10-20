using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

namespace SeptoMentor
{
    /// <summary>
    /// Main menu: A = XR navigation (passthrough, operating room), B = VR training simulator.
    /// Also added to the other scenes: hold BOTH grips for 2 s to come back here.
    /// </summary>
    public class ModeSwitch : MonoBehaviour
    {
        public const string LauncherScene = "Launcher", NavigationScene = "SeptoMentor_XR", TrainingScene = "SeptoMentor";

        [Tooltip("True only in the launcher scene: shows the menu and reacts to A / B.")]
        public bool isLauncher;
        public Transform head;

        Label3D _title, _body;
        bool _a, _b;
        float _gripsSince = -1;

        void Start()
        {
            if (!isLauncher) return;
            transform.localScale = Vector3.one * 0.01f;
            _title = new Label3D(transform, "Title", 22);
            _title.go.transform.localPosition = new Vector3(0, 8, 0);
            _title.SetBox(80, 6);
            _title.Set("SeptoMentor", Color.white);
            _body = new Label3D(transform, "Menu", 12);
            _body.go.transform.localPosition = new Vector3(0, -4, 0);
            _body.SetBox(80, 20);
            _body.Set("A  -  XR Navigation (operating room, passthrough, patient CT)\n\n" +
                      "B  -  VR Training simulator (virtual endoscope)\n\n" +
                      "Research prototype - not a medical device. Hold both grips for 2 s in any mode to return here.",
                      new Color(0.85f, 0.9f, 1f));
            Invoke(nameof(Place), 0.5f);
        }

        void Place()
        {
            if (head == null) return;
            var fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            transform.SetPositionAndRotation(head.position + fwd.normalized * 1.0f, Quaternion.LookRotation(fwd.normalized, Vector3.up));
        }

        void Update()
        {
            var r = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            var l = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            if (isLauncher)
            {
                if (Pressed(r, CommonUsages.primaryButton, ref _a) || KeyA()) SceneManager.LoadScene(NavigationScene);
                else if (Pressed(r, CommonUsages.secondaryButton, ref _b) || KeyB()) SceneManager.LoadScene(TrainingScene);
                return;
            }
            bool both = Held(r, CommonUsages.gripButton) && Held(l, CommonUsages.gripButton);
            if (!both) { _gripsSince = -1; return; }
            if (_gripsSince < 0) _gripsSince = Time.time;
            if (Time.time - _gripsSince > 2f) SceneManager.LoadScene(LauncherScene);
        }

        static bool Held(InputDevice d, InputFeatureUsage<bool> u) => d.isValid && d.TryGetFeatureValue(u, out bool v) && v;

        static bool Pressed(InputDevice d, InputFeatureUsage<bool> u, ref bool prev)
        {
            bool now = Held(d, u), down = now && !prev;
            prev = now;
            return down;
        }

#if ENABLE_INPUT_SYSTEM
        static bool KeyA() => UnityEngine.InputSystem.Keyboard.current?.digit1Key.wasPressedThisFrame ?? false;
        static bool KeyB() => UnityEngine.InputSystem.Keyboard.current?.digit2Key.wasPressedThisFrame ?? false;
#else
        static bool KeyA() => false;
        static bool KeyB() => false;
#endif
    }
}
