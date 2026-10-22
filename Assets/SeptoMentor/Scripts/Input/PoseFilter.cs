using UnityEngine;

namespace SeptoMentor
{
    /// <summary>
    /// One-euro filter for a tracked controller pose: strong smoothing while the hand is nearly still (tracking noise
    /// and hand tremor, which a 20 cm instrument lever magnifies at the tip), little lag when it moves fast.
    /// </summary>
    [System.Serializable]
    public class PoseFilter
    {
        [Tooltip("Cut-off at rest, Hz: lower = steadier, more lag.")]
        public float minCutoff = 1.6f;
        [Tooltip("Cut-off increase per m/s of hand speed.")]
        public float beta = 12f;
        [Tooltip("Cut-off increase per degree/s of turning.")]
        public float betaAngular = 0.03f;

        Vector3 _pos, _vel;
        Quaternion _rot;
        float _angVel;
        bool _has;

        public void Reset() => _has = false;

        public void Filter(ref Vector3 pos, ref Quaternion rot, float dt)
        {
            if (!_has || dt <= 0f || (pos - _pos).sqrMagnitude > 0.04f)   // first sample or a jump (re-tracked): start over
            {
                _pos = pos; _rot = rot; _vel = Vector3.zero; _angVel = 0; _has = true;
                return;
            }
            var v = (pos - _pos) / dt;
            _vel = Vector3.Lerp(_vel, v, Alpha(1f, dt));
            _pos = Vector3.Lerp(_pos, pos, Alpha(minCutoff + beta * _vel.magnitude, dt));

            float w = Quaternion.Angle(_rot, rot) / dt;
            _angVel = Mathf.Lerp(_angVel, w, Alpha(1f, dt));
            _rot = Quaternion.Slerp(_rot, rot, Alpha(minCutoff + betaAngular * _angVel, dt));

            pos = _pos; rot = _rot;
        }

        static float Alpha(float cutoff, float dt)
        {
            float tau = 1f / (2f * Mathf.PI * cutoff);
            return 1f / (1f + tau / dt);
        }
    }
}
