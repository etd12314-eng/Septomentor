using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

namespace SeptoMentor
{
    /// <summary>
    /// Main menu: A = XR navigation (passthrough, operating room), B = VR training simulator.
    /// Also added to the other scenes: hold the left menu button for 1 s (or both grips for 2 s, where grips are free) to come back here.
    /// With hand tracking the two modes are big buttons to poke, and the hand menus' "home" button is held instead of the grips.
    /// </summary>
    public class ModeSwitch : MonoBehaviour
    {
        public const string LauncherScene = "Launcher", NavigationScene = "SeptoMentor_XR", TrainingScene = "SeptoMentor";

        [Tooltip("True only in the launcher scene: shows the menu and reacts to A / B.")]
        public bool isLauncher;
        public Transform head;
        [Tooltip("Both grips held for 2 s also return to the menu. Off in VR training, where the right grip takes instruments.")]
        public bool gripsReturn = true;

        Label3D _title, _body;
        HandMenu _buttons;
        bool _a, _b;
        float _gripsSince = -1;

        /// <summary>
        /// Sharper eye buffers, set ONCE before the first scene renders. Changing the scale (or MSAA,
        /// which is set at build time in the quality settings) while a scene is loading makes the
        /// runtime rebuild its swapchains and could crash the VR scene on Quest.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void SetEyeResolution() => XRSettings.eyeTextureResolutionScale = 1.25f;

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
                      "Research prototype - not a medical device. Hold the menu button (left controller) for 1 s in any mode to return here.\n" +
                      "Hand tracking: poke a button below; later, turn your left palm toward you for the menu.",
                      new Color(0.85f, 0.9f, 1f));
            _buttons = new GameObject("HandButtons").AddComponent<HandMenu>();
            _buttons.anchor = HandMenu.Anchor.Fixed;
            _buttons.columns = 2;
            _buttons.buttonSize = new Vector2(0.14f, 0.06f);
            _buttons.gap = 0.03f;
            _buttons.AddButton("xr", "XR Navigation");
            _buttons.AddButton("vr", "VR Training");
            Invoke(nameof(Place), 0.5f);
        }

        void Place()
        {
            if (head == null) return;
            var fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            transform.SetPositionAndRotation(head.position + fwd.normalized * 1.0f, Quaternion.LookRotation(fwd.normalized, Vector3.up));
            // Within arm's reach, below the line of sight.
            if (_buttons != null)
                _buttons.transform.SetPositionAndRotation(head.position + fwd.normalized * 0.45f - Vector3.up * 0.2f,
                    Quaternion.LookRotation(fwd.normalized, Vector3.up) * Quaternion.Euler(25f, 0, 0));
        }

        void Update()
        {
            var r = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            var l = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            if (isLauncher)
            {
                bool xr = _buttons != null && _buttons.Pressed("xr"), vr = _buttons != null && _buttons.Pressed("vr");
                if (Pressed(r, CommonUsages.primaryButton, ref _a) || KeyA() || xr) SceneManager.LoadScene(NavigationScene);
                else if (Pressed(r, CommonUsages.secondaryButton, ref _b) || KeyB() || vr) SceneManager.LoadScene(TrainingScene);
                return;
            }
            bool menu = Held(l, CommonUsages.menuButton);
            bool both = menu || gripsReturn && Held(r, CommonUsages.gripButton) && Held(l, CommonUsages.gripButton) || HandMenu.HomeHeldFor(0f);
            if (!both) { _gripsSince = -1; return; }
            if (_gripsSince < 0) _gripsSince = Time.time;
            if (Time.time - _gripsSince > (menu ? 1f : 2f)) SceneManager.LoadScene(LauncherScene);
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
