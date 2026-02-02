using UnityEngine;

namespace OtterIK.Neo.Experiment
{
    /// <summary>
    /// Experiment roll-flip provider (barrel roll / belly-up/down).
    /// Mirrors neo/provider/SpineFlipRollProvider behavior, but outputs ABSOLUTE local targets:
    /// targetLocalRot = (deltaRoll * bindLocalRot).
    /// </summary>
    [DisallowMultipleComponent]
    public class ExpSpineFlipRollProvider : ExpSpinePoseProviderBase
    {
        [Header("Flip Signal")]
        [Tooltip("Total flip roll magnitude (deg). 180 = belly-up/down, 360 = full barrel roll.")]
        public float flipDegrees = 180f;

        [Range(0.05f, 1.2f)]
        public float flipDuration = 0.55f;

        public AnimationCurve flipEase = AnimationCurve.EaseInOut(0, 0, 1, 1);

        [Header("Along chain")]
        [Tooltip("Envelope along chain. x=normalizedFromHip01 (0 hip, 1 chest), y=multiplier.")]
        public AnimationCurve envelope = new AnimationCurve(
            new Keyframe(0f, 0.15f),
            new Keyframe(0.35f, 0.65f),
            new Keyframe(0.75f, 1.0f),
            new Keyframe(1f, 1.0f)
        );

        [Tooltip("Roll axis in bone LOCAL space. Usually Vector3.forward if your bone's twist axis is local Z.")]
        public Vector3 rollAxisLocal = Vector3.forward;

        public bool invertSign = false;

        [Header("Optional gating")]
        public bool activeOnlyWhileFlipping = true;

        private bool _active;
        private float _startTime;

        /// <summary>Call from a trigger script or gameplay event.</summary>
        public void TriggerFlip()
        {
            _startTime = Time.time;
            _active = true;
        }

        public override bool Evaluate(
            int index,
            SpineChainDefinition spine,
            Quaternion bindLocalRot,
            Vector3 bindLocalPos,
            out Quaternion targetLocalRot,
            out Vector3 targetLocalPos,
            out float weight)
        {
            targetLocalRot = bindLocalRot;
            targetLocalPos = bindLocalPos;
            weight = 0f;

            if (spine == null || spine.Count == 0) return false;
            if (activeOnlyWhileFlipping && !_active) return false;

            float u = (Time.time - _startTime) / Mathf.Max(0.01f, flipDuration);
            if (u >= 1f)
            {
                _active = false;
                return false;
            }
            if (u <= 0f) return false;

            float e = flipEase != null ? flipEase.Evaluate(Mathf.Clamp01(u)) : Mathf.Clamp01(u);

            // Bell shape: starts 0 -> peaks -> ends 0 (feels natural)
            float bell = Mathf.Sin(e * Mathf.PI);
            float signalDeg = flipDegrees * bell;
            if (invertSign) signalDeg = -signalDeg;

            var j = spine.GetJoint(index);
            if (j == null) return false;

            float hip01 = spine.GetNormalizedFromHip01(index);
            float env = Mathf.Max(0f, envelope.Evaluate(hip01));

            // Use per-joint rollWeight + clamp by maxRollDeg (from definition)
            float perBoneDeg = signalDeg * env * Mathf.Clamp01(j.rollWeight);

            float maxR = Mathf.Max(0f, j.maxRollDeg);
            if (maxR > 0f) perBoneDeg = Mathf.Clamp(perBoneDeg, -maxR, maxR);

            Vector3 axis = rollAxisLocal;
            if (axis.sqrMagnitude < 1e-6f) axis = Vector3.forward;
            axis.Normalize();

            Quaternion deltaRoll = Quaternion.AngleAxis(perBoneDeg, axis);

            targetLocalRot = deltaRoll * bindLocalRot;
            targetLocalPos = bindLocalPos;
            weight = 1f;
            return true;
        }
    }
}

