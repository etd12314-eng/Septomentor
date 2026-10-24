using UnityEngine;

namespace SeptoMentor
{
    /// <summary>
    /// Leads a hand-held shaft (endoscope or instrument) into the nostril and keeps it there, like the real naris does:
    /// once the tip comes within ~1 cm of a nostril it is engaged, and from then on the shaft is kept inside the
    /// teardrop opening - a tip that would pass beside it is drawn onto its rim (funnel), a shaft levered against the
    /// ala or the columella slides along the rim instead of cutting through the face (the soft rim gives ~1.5 mm).
    /// Pulling well out, or forcing far past the rim, lets go. Works in OperatingRoom's head frame
    /// (x = patient's right, y = superior, z = anterior, m); the caller shifts the whole tool by the returned offset.
    /// </summary>
    public class NostrilGuide
    {
        /// <summary>Teardrop opening as an ellipse in the nasal base: semi-axes along / across it (mm).</summary>
        public const float HalfLength = 5.8f, HalfWidth = 3.1f;
        const float Capture = 7f, Give = 1.5f;

        // The nasal base plane rises toward the tip (CtPhantom.LowerNose): normal into the nose, up and slightly back.
        static readonly Vector3 Into = new Vector3(0, 0.987f, -0.158f);

        public bool Engaged { get; private set; }
        public bool RightNostril { get; private set; }
        /// <summary>Tip past the opening, into the vestibule.</summary>
        public bool Inside { get; private set; }
        /// <summary>The shaft is being held against the rim (how far it is pushed, 0..1).</summary>
        public float RimPress { get; private set; }

        /// <summary>Unit vectors along the opening (base -> apex, toward the tip) and across it, for one side.</summary>
        public static void Axes(bool right, out Vector3 u, out Vector3 v)
        {
            // Phantom |L|, S, P: apex - base = (-2.5, 1.0, -7.2); head frame x = -L, z = -P.
            var d = new Vector3(right ? -2.5f : 2.5f, 1f, 7.2f);
            u = (d - Into * Vector3.Dot(d, Into)).normalized;
            v = Vector3.Cross(Into, u);
        }

        public static Vector3 Normal => Into;

        float _ramp;

        public void Reset() { Engaged = Inside = false; RimPress = 0; _ramp = 0; }

        /// <summary>
        /// Offset (head frame, m) to add to the tool so its shaft passes through the nostril.
        /// </summary>
        /// <param name="tip">Tip, head frame (m).</param>
        /// <param name="dir">Shaft direction from the hand toward the tip, head frame.</param>
        /// <param name="radius">Shaft radius (m).</param>
        /// <param name="dt">Frame time (s): the hold fades in over 0.2 s after engaging, so the tool does not jump.</param>
        /// <param name="entered">True on the frame the tip passes into the nostril.</param>
        public Vector3 Apply(Vector3 tip, Vector3 dir, float radius, float dt, out bool entered)
        {
            entered = false;
            var t = tip * 1000f;
            var d = dir.normalized;
            float r = radius * 1000f;
            float a = Mathf.Max(HalfLength - 0.5f * r, 2f), b = Mathf.Max(HalfWidth - 0.5f * r, 1.5f);

            if (!Engaged)
            {
                // The nearer nostril; engage when the tip is at its opening (not when it is deep in the face elsewhere).
                bool right = (t - OperatingRoom.Nostril(true) * 1000f).sqrMagnitude < (t - OperatingRoom.Nostril(false) * 1000f).sqrMagnitude;
                Offset(t, d, right, a, b, out float z0, out float out0, out _);
                if (z0 < -10f || z0 > 3f || out0 > Capture || Vector3.Dot(d, Into) < -0.2f) { Inside = false; RimPress = 0; return Vector3.zero; }
                Engaged = true; RightNostril = right; Inside = z0 >= 0f; _ramp = 0f;
            }

            Offset(t, d, RightNostril, a, b, out float z, out float outside, out var outward);
            if (z < -14f || outside > 18f || (z < 0f && outside > Capture + 3f)) { Reset(); return Vector3.zero; }

            bool wasInside = Inside;
            Inside = z >= 0f;
            entered = Inside && !wasInside;

            // Full hold once the tip is at the opening; a gentle draw toward it as it comes in.
            _ramp = Mathf.Min(1f, _ramp + dt / 0.2f);
            float w = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-10f, -1f, z)) * Mathf.SmoothStep(0f, 1f, _ramp);
            float kept = Give * (float)System.Math.Tanh(outside / Give);   // the soft rim stretches a little
            RimPress = Mathf.Clamp01(outside / 4f) * w;
            return -outward * (outside - kept) * w * 0.001f;
        }

        /// <summary>
        /// Where the shaft passes the opening: <paramref name="z"/> = tip depth past the nasal base (mm), <paramref name="outside"/> =
        /// how far that point is beyond the opening's rim (mm) and <paramref name="outward"/> the in-plane direction away from it.
        /// </summary>
        static void Offset(Vector3 t, Vector3 d, bool right, float a, float b, out float z, out float outside, out Vector3 outward)
        {
            var n = OperatingRoom.Nostril(right) * 1000f;
            z = Vector3.Dot(t - n, Into);
            // The shaft point nearest the opening's centre (the tip itself until it has passed it).
            float back = Mathf.Clamp(Vector3.Dot(t - n, d), 0f, 250f);
            var x = t - d * back;
            var e = x - n; e -= Into * Vector3.Dot(e, Into);
            Axes(right, out var u, out var v);
            float eu = Vector3.Dot(e, u), ev = Vector3.Dot(e, v);
            float q = Mathf.Sqrt(eu * eu / (a * a) + ev * ev / (b * b));
            outward = Vector3.zero; outside = 0f;
            if (q <= 1f) return;
            var over = e - (u * eu + v * ev) / q;   // beyond the rim point on the same ray
            outside = over.magnitude;
            outward = outside > 1e-5f ? over / outside : Vector3.zero;
        }
    }
}
