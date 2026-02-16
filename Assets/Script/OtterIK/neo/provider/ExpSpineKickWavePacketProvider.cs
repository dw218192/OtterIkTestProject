using UnityEngine;

namespace OtterIK.Neo.Experiment
{
    /// <summary>
    /// Kick-driven traveling wave packet along the spine (single clean wave, not endless oscillation).
    /// Uses a gaussian envelope (+ optional sine carrier), then filters angles with a 2nd-order (half-life) dynamic.
    ///
    /// Compatible with ExpShadowSpineHarmonizer + ExpSpinePoseProviderBase.
    /// Assumes shadow local axes match rootReference world axes, spine forward +Z, up +Y.
    /// </summary>
    public class ExpSpineKickWavePacketProvider : ExpSpinePoseProviderBase
    {
        [Header("Chain")]
        [Tooltip("How many shadow joints in the spine chain. (0=Hip .. total-1=Chest)")]
        public int totalJoints = 9;

        [Header("Wave Packet Shape")]
        [Tooltip("Peak pitch in degrees at full demand (very visible: 20~40).")]
        public float maxAmplitudeDeg = 28f;

        [Tooltip("How wide the packet is, in joints. Smaller = sharper bump, larger = smoother wave.")]
        public float packetWidthJoints = 2.2f;

        [Tooltip("Carrier wavelength in joints (controls ripples inside the packet). Typical 4~8.")]
        public float wavelengthJoints = 6f;

        [Tooltip("If false, uses a single-bump packet (clean propulsion wave) instead of a sinusoidal carrier.")]
        public bool useCarrier = false;

        [Tooltip("Direction: +1 means Hip->Chest, -1 means Chest->Hip.")]
        public int travelDirection = +1;

        [Header("Timing")]
        [Tooltip("How long one kick packet lasts (seconds).")]
        public float duration = 0.35f;

        [Tooltip("Extra distance (in joints) beyond the chain ends, so the packet can fully exit cleanly.")]
        public float tailPaddingJoints = 2.0f;

        [Header("Distribution")]
        [Tooltip("0=hip .. 1=chest amplitude multiplier.")]
        public AnimationCurve amplitudeByIndex = AnimationCurve.Linear(0, 1, 1, 1);

        [Header("Dynamics (Half-life)")]
        [Tooltip("Half-life (seconds) for the 2nd-order response. Smaller = snappier, larger = softer.")]
        public float halfLife = 0.10f;

        [Tooltip("Extra damping multiplier. 1=critical-ish, >1 more damped.")]
        public float dampingMul = 1.0f;

        [Header("Visibility Boost")]
        [Tooltip("Boost target angle after envelope/carrier. Typical 2~4 when motion is being diluted by blending.")]
        public float amplitudeBoost = 2.0f; // 2~4

        [Tooltip("Boost weight contribution (then Clamp01). Typical 1~3 when harmonizer averaging/filters dilute the wave.")]
        public float weightBoost = 1.0f; // 1~3

        [Header("Weight")]
        [Range(0f, 1f)]
        public float baseWeight = 1f;

        // runtime
        float _t;
        float _amp;
        bool _active;

        float[] _angle; // filtered angle per joint (deg)
        float[] _vel;   // angular velocity per joint (deg/s)

        void EnsureBuffers()
        {
            int n = Mathf.Max(2, totalJoints);
            if (_angle == null || _angle.Length != n)
            {
                _angle = new float[n];
                _vel = new float[n];
            }
        }

        void Update()
        {
            EnsureBuffers();

            float dt = Time.deltaTime;
            if (!_active)
            {
                // settle back to zero smoothly even when inactive
                for (int i = 0; i < _angle.Length; i++)
                    SecondOrderHalfLife(ref _angle[i], ref _vel[i], 0f, halfLife, dampingMul, dt);
                return;
            }

            _t += dt;
            float t01 = (_t <= 0f) ? 0f : Mathf.Clamp01(_t / Mathf.Max(0.01f, duration));

            // When packet finished, fade out and stop
            if (t01 >= 1f)
            {
                _active = false;
                _t = 0f;
                _amp = 0f;
                return;
            }

            // Packet center moves along the chain in joint-index space
            // It starts outside one end and exits beyond the other end for a clean look.
            float nJ = _angle.Length - 1;

            // center position in "joint units"
            // For Hip->Chest: center goes from (-pad) to (nJ+pad)
            // For Chest->Hip: center goes from (nJ+pad) to (-pad)
            float start = (travelDirection >= 0) ? -tailPaddingJoints : (nJ + tailPaddingJoints);
            float end = (travelDirection >= 0) ? (nJ + tailPaddingJoints) : -tailPaddingJoints;
            float center = Mathf.Lerp(start, end, t01);

            // Packet envelope over time (optional) to avoid a harsh start:
            // smoothstep-in/out makes a clean “kick” onset
            float envTime = SmoothStep01(t01) * (1f - SmoothStep01(Mathf.Clamp01((t01 - 0.85f) / 0.15f)));

            // Carrier frequency (radians per joint)
            float k = (wavelengthJoints > 1e-4f) ? (Mathf.PI * 2f / wavelengthJoints) : 0f;
            float invSigma2 = 1f / Mathf.Max(0.0001f, packetWidthJoints * packetWidthJoints);

            for (int i = 0; i < _angle.Length; i++)
            {
                float tIdx = (nJ <= 1e-4f) ? 0f : (i / nJ);
                float ampMul = Mathf.Max(0f, amplitudeByIndex != null ? amplitudeByIndex.Evaluate(tIdx) : 1f);

                // distance from packet center (in joints)
                float d = i - center;

                // gaussian envelope in space
                float envSpace = Mathf.Exp(-0.5f * d * d * invSigma2);

                // carrier inside the packet: optional.
                // When disabled, this becomes a single-bump packet (clean propulsion wave).
                float carrier = (useCarrier && k > 0f) ? Mathf.Sin(d * k) : 1f;

                float targetAngle = carrier * envSpace * envTime * (_amp * maxAmplitudeDeg) * ampMul;
                targetAngle *= amplitudeBoost;

                // 2nd order half-life smoothing -> organic, not robotic
                SecondOrderHalfLife(ref _angle[i], ref _vel[i], targetAngle, halfLife, dampingMul, dt);
            }
        }

        public override bool Evaluate(
            int index,
            SpineChainDefinition spine,
            Transform jointOrShadow,
            Quaternion bindLocalRot,
            out Quaternion targetLocalRot,
            out float weight)
        {
            EnsureBuffers();

            targetLocalRot = bindLocalRot;
            weight = 0f;

            if (index < 0 || index >= _angle.Length)
                return false;

            // If inactive and nearly settled, contribute nothing
            if (!_active && Mathf.Abs(_angle[index]) < 0.01f && Mathf.Abs(_vel[index]) < 0.01f)
                return false;

            // Rotate in vertical plane (pitch around local +X)
            targetLocalRot = Quaternion.Euler(_angle[index], 0f, 0f) * bindLocalRot;
            weight = Mathf.Clamp01(baseWeight * globalWeight * weightBoost);
            return weight > 0f;
        }

        // Call this when your leg kick happens
        public void TriggerKick(float demand01) => TriggerKick(demand01, duration);

        public void TriggerKick(float demand01, float dur)
        {
            EnsureBuffers();

            duration = Mathf.Max(0.05f, dur);
            _t = 0f;
            _active = true;
            _amp = Mathf.Clamp01(demand01);

            // Optional: reset velocities for a "clean" kick, not residual oscillation.
            for (int i = 0; i < _vel.Length; i++) _vel[i] = 0f;
        }

        static float SmoothStep01(float x)
        {
            x = Mathf.Clamp01(x);
            return x * x * (3f - 2f * x);
        }

        /// <summary>
        /// Critically damped-ish 2nd order response using half-life parameterization.
        /// Works well for making motion "alive" without endless oscillation.
        /// </summary>
        static void SecondOrderHalfLife(ref float x, ref float v, float xTarget, float halfLife, float dampingMul, float dt)
        {
            if (dt <= 0f) return;
            halfLife = Mathf.Max(0.0001f, halfLife);

            // Convert half-life to angular frequency
            // ln(2)/halfLife is a nice perceptual knob.
            float omega = (Mathf.Log(2f) / halfLife) * dampingMul;

            // Semi-implicit integration of critically damped spring:
            // x'' + 2ω x' + ω^2 (x - xT) = 0
            float f = 1f + 2f * omega * dt;
            float oo = omega * omega;
            float dt_oo = dt * oo;
            float dt2_oo = dt * dt_oo;

            float detInv = 1f / (f + dt2_oo);
            float xNew = (f * x + dt * v + dt2_oo * xTarget) * detInv;
            float vNew = (v + dt_oo * (xTarget - x)) * detInv;

            x = xNew;
            v = vNew;
        }
    }
}

