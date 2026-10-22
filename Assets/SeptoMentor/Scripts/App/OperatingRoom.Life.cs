using System.Collections.Generic;
using System.Threading.Tasks;
using SeptoMentor.Nav;
using UnityEngine;

namespace SeptoMentor
{
    /// <summary>
    /// The parts of the operating room that make it feel occupied: OR staff, a live anesthesia monitor
    /// (ECG / pleth / capnography, numbers, pulse-oximeter beep), the patient's CT on a wall screen,
    /// extra equipment and soft contact shadows.
    /// </summary>
    public partial class OperatingRoom
    {
        static readonly Vector3 VitalsScreen = new Vector3(-1.05f, 1.6f, 0.95f);

        readonly List<(Transform head, Quaternion rest, float phase, float sway)> _heads = new List<(Transform, Quaternion, float, float)>();
        Transform _staffRoot;

        // ------------------------------------------------------------------ equipment, shadows

        void BuildDetails()
        {
            const float z1 = 3.5f;
            var steel = Mat(new Color(0.74f, 0.76f, 0.78f), 0.8f, 0f, 0f, 0.8f);
            var drape = Mat(new Color(0.22f, 0.42f, 0.62f), 0.1f);
            var white = Mat(new Color(0.9f, 0.9f, 0.9f), 0.4f);
            var dark = Mat(new Color(0.16f, 0.17f, 0.19f), 0.4f);

            // Wall finish: skirting and a bumper rail.
            var skirt = Mat(new Color(0.32f, 0.38f, 0.4f), 0.4f);
            Box(new Vector3(0, 0.08f, z1 - 0.025f), new Vector3(7f, 0.16f, 0.015f), skirt);
            Box(new Vector3(0, 0.08f, -2.475f), new Vector3(7f, 0.16f, 0.015f), skirt);
            Box(new Vector3(-3.475f, 0.08f, 0.5f), new Vector3(0.015f, 0.16f, 6f), skirt);
            Box(new Vector3(3.475f, 0.08f, 0.5f), new Vector3(0.015f, 0.16f, 6f), skirt);
            Box(new Vector3(0, 0.95f, z1 - 0.03f), new Vector3(7f, 0.08f, 0.02f), steel);

            // Back table (scrub nurse) with draped instrument sets.
            var bt = new Vector3(1.55f, 0.9f, -0.75f);
            Box(bt, new Vector3(1.2f, 0.03f, 0.65f), drape);
            Box(bt + new Vector3(0, -0.2f, 0.33f), new Vector3(1.2f, 0.4f, 0.01f), drape);
            Box(bt + new Vector3(0, -0.2f, -0.33f), new Vector3(1.2f, 0.4f, 0.01f), drape);
            foreach (var dx in new[] { -0.55f, 0.55f }) foreach (var dz in new[] { -0.28f, 0.28f })
                Rod(new Vector3(bt.x + dx, 0, bt.z + dz), new Vector3(bt.x + dx, bt.y - 0.02f, bt.z + dz), 0.015f, steel);
            Box(bt + new Vector3(-0.25f, 0.04f, 0), new Vector3(0.45f, 0.05f, 0.3f), steel);           // instrument tray
            Box(bt + new Vector3(0.3f, 0.03f, -0.1f), new Vector3(0.25f, 0.03f, 0.2f), white);          // gauze / towels
            Prim(PrimitiveType.Cylinder, bt + new Vector3(0.35f, 0.05f, 0.15f), new Vector3(0.12f, 0.035f, 0.12f), steel);   // bowl
            for (int i = 0; i < 6; i++)
                Rod(bt + new Vector3(-0.42f + i * 0.04f, 0.07f, -0.12f), bt + new Vector3(-0.42f + i * 0.04f, 0.07f, 0.12f), 0.003f, steel);
            Shadow(new Vector3(bt.x, 0, bt.z), new Vector2(1.4f, 0.8f), 0.35f);

            // Electrosurgical unit and suction canisters on a cart next to the tower.
            var cart = new Vector3(0.95f, 0, 1.3f);
            Box(cart + new Vector3(0, 0.4f, 0), new Vector3(0.45f, 0.8f, 0.45f), white);
            Box(cart + new Vector3(0, 0.88f, 0), new Vector3(0.4f, 0.14f, 0.35f), dark);
            Box(cart + new Vector3(-0.08f, 0.9f, -0.177f), new Vector3(0.12f, 0.05f, 0.005f), Mat(new Color(0.95f, 0.5f, 0.1f), 0f, 0f, 1f));
            Box(cart + new Vector3(0.08f, 0.9f, -0.177f), new Vector3(0.12f, 0.05f, 0.005f), Mat(new Color(0.2f, 0.6f, 1f), 0f, 0f, 1f));
            var glass = Mat(new Color(0.8f, 0.85f, 0.9f), 0.9f, 0f, 0f, 0.3f);
            var blood = Mat(new Color(0.45f, 0.05f, 0.06f), 0.8f);
            for (int i = 0; i < 2; i++)
            {
                var c = cart + new Vector3(-0.1f + i * 0.2f, 1.08f, 0.05f);
                Prim(PrimitiveType.Cylinder, c, new Vector3(0.12f, 0.11f, 0.12f), glass);
                Prim(PrimitiveType.Cylinder, c - new Vector3(0, 0.07f, 0), new Vector3(0.11f, 0.035f, 0.11f), blood);
            }
            Shadow(cart, new Vector2(0.6f, 0.6f), 0.4f);

            // Kick bucket and step stool by the table.
            Prim(PrimitiveType.Cylinder, new Vector3(0.45f, 0.17f, -0.45f), new Vector3(0.35f, 0.17f, 0.35f), steel);
            Box(new Vector3(-0.55f, 0.1f, -0.15f), new Vector3(0.45f, 0.2f, 0.3f), Mat(new Color(0.7f, 0.72f, 0.74f), 0.6f, 0f, 0f, 0.6f));

            // Ceiling equipment pendant beside the anesthesia machine.
            var pend = new Vector3(-1.75f, 0, 1.6f);
            Rod(pend + new Vector3(0, 3f, 0), pend + new Vector3(0, 2.55f, 0), 0.05f, white);
            Box(pend + new Vector3(0, 1.75f, 0), new Vector3(0.3f, 1.6f, 0.3f), white);
            for (int i = 0; i < 4; i++)   // gas / power outlets
                Prim(PrimitiveType.Cylinder, pend + new Vector3(-0.06f + (i % 2) * 0.12f, 1.4f + (i / 2) * 0.15f, -0.152f), new Vector3(0.05f, 0.005f, 0.05f),
                     Mat(i == 0 ? new Color(0.9f, 0.9f, 0.9f) : i == 1 ? new Color(0.95f, 0.85f, 0.2f) : i == 2 ? new Color(0.2f, 0.2f, 0.2f) : new Color(0.3f, 0.7f, 0.35f), 0.4f),
                     Quaternion.Euler(90, 0, 0));

            // Contact shadows under the big pieces.
            Shadow(new Vector3(0.75f, 0, 0.45f), new Vector2(2.2f, 0.75f), 0.4f);              // table
            Shadow(new Vector3(MonitorPosition.x, 0, MonitorPosition.z + 0.1f), new Vector2(0.8f, 0.7f), 0.45f);
            Shadow(new Vector3(-1.05f, 0, 1.0f), new Vector2(0.9f, 0.75f), 0.45f);             // anesthesia
            Shadow(new Vector3(0.7f, 0, 0.05f), new Vector2(0.6f, 0.45f), 0.3f);               // Mayo stand
            foreach (var c in new[] { new Vector3(-1.2f, 0, -2.2f), new Vector3(-0.3f, 0, -2.2f), new Vector3(0.6f, 0, -2.2f), new Vector3(1.5f, 0, -2.2f) })
                Shadow(c, new Vector2(0.95f, 0.7f), 0.3f);
        }

        void Shadow(Vector3 centre, Vector2 size, float strength)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(go.GetComponent<Collider>());
            go.name = "ContactShadow";
            go.transform.SetParent(_root, false);
            go.transform.localPosition = new Vector3(centre.x, 0.003f, centre.z);
            go.transform.localRotation = Quaternion.Euler(90, 0, 0);
            go.transform.localScale = new Vector3(size.x * 1.15f, size.y * 1.15f, 1);
            var m = new Material(shadowShader != null ? shadowShader : Shader.Find("SeptoMentor/ORShadow"));
            m.SetFloat("_Strength", strength);
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        // ------------------------------------------------------------------ staff

        void BuildStaff()
        {
            _staffRoot = new GameObject("Staff").transform;
            _staffRoot.SetParent(transform, false);
            var scrubs = new Color(0.32f, 0.55f, 0.5f);
            var gown = new Color(0.25f, 0.45f, 0.68f);
            // Anesthesiologist at the head end, beside the machine, sitting on a stool with a phone.
            Person(new Vector3(-1.15f, 0, 0.42f), 80f, scrubs, scrubs, new Color(0.2f, 0.3f, 0.65f), false, false, true);
            // Scrub nurse between the Mayo stand and the back table, gowned and gloved.
            Person(new Vector3(1.1f, 0, -0.2f), -60f, gown, scrubs, new Color(0.35f, 0.65f, 0.75f), true, true);
            // Circulating nurse by the door.
            Person(new Vector3(-2.0f, 0, 2.6f), 200f, scrubs, scrubs, new Color(0.85f, 0.45f, 0.6f), false, false);
        }

        void Person(Vector3 pos, float yaw, Color top, Color pants, Color cap, bool gowned, bool handsForward, bool seatedWithPhone = false)
        {
            var root = new GameObject("Person").transform;
            root.SetParent(_staffRoot, false);
            root.SetPositionAndRotation(pos, Quaternion.Euler(0, yaw, 0));
            var skin = Mat(new Color(0.84f, 0.66f, 0.55f), 0.2f);
            var topM = Mat(top, 0.1f); var pantsM = Mat(pants, 0.1f); var capM = Mat(cap, 0.1f);
            var glove = Mat(new Color(0.85f, 0.8f, 0.62f), 0.5f);
            var shoe = Mat(new Color(0.92f, 0.92f, 0.92f), 0.3f);

            // Standing: hips at 0.8 m. Seated: on a stool, hips at 0.55 m, thighs forward, the upper body lowered with them.
            float dy = seatedWithPhone ? -0.25f : 0f;
            if (seatedWithPhone) Stool(root);
            foreach (float x in new[] { -0.1f, 0.1f })
            {
                if (seatedWithPhone)
                {
                    Limb(new Vector3(x, 0.56f, 0.02f), new Vector3(x * 1.2f, 0.56f, 0.42f), 0.075f, pantsM, root);     // thigh
                    Limb(new Vector3(x * 1.2f, 0.56f, 0.42f), new Vector3(x * 1.2f, 0.09f, 0.46f), 0.062f, pantsM, root);   // shin
                    Prim(PrimitiveType.Cube, new Vector3(x * 1.2f, 0.04f, 0.5f), new Vector3(0.11f, 0.08f, 0.27f), shoe, null, root);
                    continue;
                }
                Limb(new Vector3(x, 0.08f, 0), new Vector3(x, 0.8f, 0), 0.075f, pantsM, root);
                Prim(PrimitiveType.Cube, new Vector3(x, 0.04f, 0.04f), new Vector3(0.11f, 0.08f, 0.27f), shoe, null, root);
            }
            Prim(PrimitiveType.Capsule, new Vector3(0, 1.13f + dy, 0), new Vector3(0.42f, 0.36f, 0.27f), topM, null, root);
            if (gowned) Prim(PrimitiveType.Cylinder, new Vector3(0, 0.7f, 0), new Vector3(0.44f, 0.32f, 0.32f), topM, null, root);
            Prim(PrimitiveType.Cylinder, new Vector3(0, 1.5f + dy, 0), new Vector3(0.1f, 0.05f, 0.1f), skin, null, root);

            foreach (float sx in new[] { -1f, 1f })
            {
                var shoulder = new Vector3(0.22f * sx, 1.38f + dy, 0);
                var elbow = seatedWithPhone ? new Vector3(0.21f * sx, 1.1f + dy, 0.1f)
                          : handsForward ? new Vector3(0.24f * sx, 1.13f, 0.12f) : new Vector3(0.26f * sx, 1.1f, 0.02f);
                var hand = seatedWithPhone ? new Vector3(0.065f * sx, 1.17f + dy, 0.28f)
                         : handsForward ? new Vector3(0.14f * sx, 1.08f, 0.33f) : new Vector3(0.25f * sx, 0.84f, 0.06f);
                Limb(shoulder, elbow, 0.055f, topM, root);
                Limb(elbow, hand, 0.045f, gowned ? topM : skin, root);
                Prim(PrimitiveType.Sphere, hand, new Vector3(0.075f, 0.09f, 0.075f), gowned ? glove : skin, null, root);
            }
            if (seatedWithPhone) Phone(new Vector3(0, 1.21f + dy, 0.29f), root);

            var head = new GameObject("Head").transform;
            head.SetParent(root, false);
            head.localPosition = new Vector3(0, 1.62f + dy, 0);
            if (seatedWithPhone) head.localRotation = Quaternion.Euler(32f, 0, 0);   // looking down at the phone
            Prim(PrimitiveType.Sphere, new Vector3(0, 0.02f, 0), new Vector3(0.19f, 0.23f, 0.21f), skin, null, head);
            Prim(PrimitiveType.Sphere, new Vector3(0, 0.08f, -0.01f), new Vector3(0.21f, 0.15f, 0.23f), capM, null, head);
            Prim(PrimitiveType.Cube, new Vector3(0, -0.02f, 0.095f), new Vector3(0.14f, 0.09f, 0.03f), Mat(new Color(0.55f, 0.75f, 0.9f), 0.1f), null, head);
            var eye = Mat(new Color(0.08f, 0.07f, 0.06f), 0.8f);
            Prim(PrimitiveType.Sphere, new Vector3(-0.035f, 0.045f, 0.096f), Vector3.one * 0.02f, eye, null, head);
            Prim(PrimitiveType.Sphere, new Vector3(0.035f, 0.045f, 0.096f), Vector3.one * 0.02f, eye, null, head);
            _heads.Add((head, head.localRotation, Random.value * 10f, seatedWithPhone ? 0.25f : 1f));

            Shadow(pos, new Vector2(0.55f, 0.45f), 0.35f);
        }

        /// <summary>Wheeled OR stool: seat, gas-lift column, five-star base.</summary>
        void Stool(Transform root)
        {
            var black = Mat(new Color(0.12f, 0.12f, 0.13f), 0.4f);
            var chrome = Mat(new Color(0.75f, 0.77f, 0.8f), 0.85f, 0f, 0f, 0.85f);
            Prim(PrimitiveType.Cylinder, new Vector3(0, 0.49f, 0.05f), new Vector3(0.44f, 0.035f, 0.44f), black, null, root);      // seat
            Prim(PrimitiveType.Cube, new Vector3(0, 0.74f, -0.16f), new Vector3(0.36f, 0.22f, 0.05f), black, Quaternion.Euler(-8, 0, 0), root);   // back
            Limb(new Vector3(0, 0.5f, -0.13f), new Vector3(0, 0.66f, -0.17f), 0.015f, chrome, root);
            Limb(new Vector3(0, 0.08f, 0.05f), new Vector3(0, 0.46f, 0.05f), 0.025f, chrome, root);                                    // column
            for (int k = 0; k < 5; k++)
            {
                var dir = Quaternion.Euler(0, k * 72f, 0) * Vector3.forward;
                var foot = new Vector3(0, 0.06f, 0.05f) + dir * 0.28f;
                Limb(new Vector3(0, 0.08f, 0.05f), foot, 0.018f, black, root);
                Prim(PrimitiveType.Sphere, foot - new Vector3(0, 0.03f, 0), Vector3.one * 0.045f, black, null, root);                 // caster
            }
        }

        /// <summary>A phone held in both hands, screen lit and tilted toward the face.</summary>
        void Phone(Vector3 at, Transform root)
        {
            var tilt = Quaternion.Euler(-55f, 0, 0);
            Prim(PrimitiveType.Cube, at, new Vector3(0.075f, 0.009f, 0.155f), Mat(new Color(0.06f, 0.06f, 0.07f), 0.8f), tilt, root);
            Prim(PrimitiveType.Cube, at + tilt * new Vector3(0, 0.0048f, 0), new Vector3(0.068f, 0.001f, 0.145f),
                 Mat(new Color(0.55f, 0.75f, 0.95f), 0.9f, 0f, 1.2f), tilt, root);
        }

        /// <summary>Capsule from a to b (local to parent).</summary>
        void Limb(Vector3 a, Vector3 b, float r, Material m, Transform parent)
        {
            var d = b - a;
            Prim(PrimitiveType.Capsule, (a + b) * 0.5f, new Vector3(r * 2, d.magnitude * 0.5f + r * 0.5f, r * 2), m,
                 Quaternion.FromToRotation(Vector3.up, d), parent);
        }

        // ------------------------------------------------------------------ anesthesia monitor

        Mesh[] _waves;
        Label3D _hr, _spo2, _nibp, _etco2;
        float _hrValue = 72f, _nextBeat, _nextNumbers;
        AudioSource _beeper;
        AudioClip _beep;
        const int WavePoints = 120;
        const float WaveWidth = 0.27f, WaveSeconds = 4f;

        void BuildVitals()
        {
            var screen = new GameObject("VitalsScreen").transform;
            screen.SetParent(transform, false);
            screen.position = VitalsScreen + new Vector3(-0.045f, 0, -0.027f);
            var colors = new[] { new Color(0.3f, 1f, 0.45f), new Color(0.35f, 0.85f, 1f), new Color(1f, 0.9f, 0.3f) };
            _waves = new Mesh[3];
            for (int w = 0; w < 3; w++)
            {
                var go = new GameObject("Wave" + w);
                go.transform.SetParent(screen, false);
                go.transform.localPosition = new Vector3(-WaveWidth * 0.5f, 0.075f - w * 0.075f, 0);
                var m = new Mesh { name = "Wave" + w };
                m.MarkDynamic();
                var verts = new Vector3[WavePoints * 2];
                var tris = new int[(WavePoints - 1) * 6];
                for (int i = 0; i < WavePoints - 1; i++)
                {
                    int v = i * 2, t = i * 6;
                    tris[t] = v; tris[t + 1] = v + 1; tris[t + 2] = v + 2;   // clockwise as seen from the room (front faces)
                    tris[t + 3] = v + 1; tris[t + 4] = v + 3; tris[t + 5] = v + 2;
                }
                m.vertices = verts; m.triangles = tris;
                m.bounds = new Bounds(Vector3.zero, Vector3.one);
                go.AddComponent<MeshFilter>().sharedMesh = m;
                go.AddComponent<MeshRenderer>().sharedMaterial = Mat(colors[w], 0f, 0f, 1f);
                _waves[w] = m;
            }
            var numbers = new GameObject("Numbers").transform;
            numbers.SetParent(screen, false);
            numbers.localPosition = new Vector3(WaveWidth * 0.5f + 0.045f, 0, -0.001f);
            numbers.localScale = Vector3.one * 0.001f;   // 1 unit = 1 mm
            _hr = Num(numbers, 75, 200, colors[0]);
            _spo2 = Num(numbers, 0, 160, colors[1]);
            _etco2 = Num(numbers, -75, 140, colors[2]);
            _nibp = Num(numbers, -115, 90, new Color(0.95f, 0.95f, 0.95f));
        }

        static Label3D Num(Transform parent, float y, float size, Color c)
        {
            var l = new Label3D(parent, "Vital", size);
            l.go.transform.localPosition = new Vector3(0, y, 0);
            l.SetBox(120, size * 0.16f);
            l.Set("", c);
            return l;
        }

        static float Gauss(float x, float c, float w) => Mathf.Exp(-(x - c) * (x - c) / (2 * w * w));

        static float Ecg(float ph) =>
            0.12f * Gauss(ph, 0.1f, 0.025f) - 0.1f * Gauss(ph, 0.2f, 0.008f) + 1f * Gauss(ph, 0.225f, 0.009f)
            - 0.25f * Gauss(ph, 0.25f, 0.01f) + 0.28f * Gauss(ph, 0.45f, 0.045f);

        static float Pleth(float ph)
        {
            float up = Mathf.Clamp01((ph - 0.25f) / 0.12f);
            return ph < 0.25f ? 0.15f * (1 - ph * 4) : up < 1 ? Mathf.Sin(up * Mathf.PI * 0.5f) : 0.15f + 0.85f * Mathf.Exp(-(ph - 0.37f) * 4.5f) + 0.08f * Gauss(ph, 0.6f, 0.04f);
        }

        static float Capno(float ph) => Mathf.Clamp01((ph - 0.05f) / 0.06f) * (1 - Mathf.Clamp01((ph - 0.58f) / 0.05f)) * (0.9f + 0.1f * ph);

        void UpdateVitals()
        {
            if (_waves == null) return;
            float t = Time.time;
            float beat = 60f / _hrValue, breath = 5f;
            float[] amp = { 0.025f, 0.022f, 0.02f };
            for (int w = 0; w < 3; w++)
            {
                var v = new Vector3[WavePoints * 2];
                float prevY = 0;
                for (int i = 0; i < WavePoints; i++)
                {
                    float tt = t - (WavePoints - 1 - i) * WaveSeconds / WavePoints;
                    float y = w == 0 ? Ecg(Mathf.Repeat(tt, beat) / beat) * amp[0]
                            : w == 1 ? (Pleth(Mathf.Repeat(tt - 0.2f, beat) / beat) - 0.4f) * amp[1]
                            : (Capno(Mathf.Repeat(tt, breath) / breath) - 0.45f) * amp[2];
                    float x = WaveWidth * i / (WavePoints - 1);
                    // Thickness across the local slope so steep QRS strokes stay visible.
                    float slope = i > 0 ? (y - prevY) / (WaveWidth / WavePoints) : 0;
                    float half = 0.0012f * Mathf.Sqrt(1 + slope * slope);
                    v[i * 2] = new Vector3(x, y - half, 0);
                    v[i * 2 + 1] = new Vector3(x, y + half, 0);
                    prevY = y;
                }
                _waves[w].vertices = v;
            }

            if (t >= _nextBeat)
            {
                _nextBeat = t + beat;
                if (_beeper != null) _beeper.PlayOneShot(_beep);
            }
            if (t >= _nextNumbers)
            {
                _nextNumbers = t + 2f;
                _hrValue = Mathf.Clamp(_hrValue + Random.Range(-1.5f, 1.5f), 66f, 80f);
                _hr.Set($"HR  {_hrValue:0}", new Color(0.3f, 1f, 0.45f));
                _spo2.Set($"SpO2  {Random.Range(98, 100)}%", new Color(0.35f, 0.85f, 1f));
                _etco2.Set($"EtCO2  {Random.Range(34, 37)}", new Color(1f, 0.9f, 0.3f));
                _nibp.Set("NIBP 118/72 (85)", new Color(0.95f, 0.95f, 0.95f));
            }
        }

        void BuildAudio()
        {
            const int rate = 44100;
            var data = new float[(int)(rate * 0.09f)];
            for (int i = 0; i < data.Length; i++)
            {
                float tt = i / (float)rate, env = Mathf.Min(1f, tt / 0.005f) * Mathf.Min(1f, (0.09f - tt) / 0.02f);
                data[i] = Mathf.Sin(2 * Mathf.PI * 880f * tt) * 0.5f * env;
            }
            _beep = AudioClip.Create("PulseBeep", data.Length, 1, rate, false);
            _beep.SetData(data, 0);
            _beeper = new GameObject("MonitorAudio").AddComponent<AudioSource>();
            _beeper.transform.SetParent(transform, false);
            _beeper.transform.position = VitalsScreen;
            _beeper.spatialBlend = 1f;
            _beeper.minDistance = 0.8f;
            _beeper.maxDistance = 12f;
            _beeper.volume = 0.25f;
            _beeper.playOnAwake = false;
        }

        // ------------------------------------------------------------------ CT on the wall

        Task<Color32[][]> _ctTask;
        Material[] _ctMats;
        const int CtRes = 256;

        void BuildCtViewer()
        {
            const float z = 3.5f - 0.035f;
            var centre = new Vector3(-1.2f, 1.75f, z);
            Box(centre + new Vector3(0, 0, 0.012f), new Vector3(1.18f, 0.68f, 0.03f), Mat(new Color(0.06f, 0.06f, 0.07f), 0.6f), transform);
            var unlit = Shader.Find("Unlit/Texture");
            _ctMats = new Material[2];
            for (int i = 0; i < 2; i++)
            {
                var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Destroy(q.GetComponent<Collider>());
                q.transform.SetParent(transform, false);
                q.transform.position = centre + new Vector3(i == 0 ? -0.28f : 0.28f, -0.02f, -0.005f);
                q.transform.localScale = new Vector3(0.54f, 0.54f, 1);
                _ctMats[i] = new Material(unlit != null ? unlit : Shader.Find("SeptoMentor/ORSurface"));
                q.GetComponent<MeshRenderer>().sharedMaterial = _ctMats[i];
            }
            var labels = new GameObject("CtLabels").transform;
            labels.SetParent(transform, false);
            labels.position = centre + new Vector3(0, 0.305f, -0.006f);
            labels.localScale = Vector3.one * 0.01f;
            var l = new Label3D(labels, "CtTitle", 28f);
            l.SetBox(115, 4);
            l.Set("CT phantom (synthetic)   coronal | axial", new Color(0.8f, 0.85f, 0.9f));
            _ctTask = Task.Run(() => new[] { CtImage(true), CtImage(false) });
        }

        /// <summary>Bone-window slice of the phantom: coronal at P = -40 mm or axial at S = -14 mm (patient's right on the left).</summary>
        static Color32[] CtImage(bool coronal)
        {
            var px = new Color32[CtRes * CtRes];
            for (int y = 0; y < CtRes; y++)
            for (int x = 0; x < CtRes; x++)
            {
                float u = x / (CtRes - 1f), v = y / (CtRes - 1f);
                float l = -80f + 160f * u;
                var p = coronal ? new Vector3(l, -85f + 160f * v, -40f) : new Vector3(l, -14f, 50f - 160f * v);
                float hu = CtPhantom.SampleHu(p);
                byte g = (byte)(Mathf.Clamp01((hu + 700f) / 2000f) * 255f);
                px[y * CtRes + x] = new Color32(g, g, g, 255);
            }
            return px;
        }

        // ------------------------------------------------------------------ per frame

        void UpdateLife()
        {
            float t = Time.time;
            foreach (var h in _heads)
                h.head.localRotation = h.rest * Quaternion.Euler(Mathf.Sin(t * 0.31f + h.phase) * 6f * h.sway, Mathf.Sin(t * 0.17f + h.phase * 2f) * 18f * h.sway, 0);
            UpdateVitals();

            if (_ctTask != null && _ctTask.IsCompleted)
            {
                if (_ctTask.Status == TaskStatus.RanToCompletion)
                    for (int i = 0; i < 2; i++)
                    {
                        var tex = new Texture2D(CtRes, CtRes, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
                        tex.SetPixels32(_ctTask.Result[i]);
                        tex.Apply(true, true);
                        _ctMats[i].mainTexture = tex;
                    }
                _ctTask = null;
            }
        }
    }
}
