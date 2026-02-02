using UnityEngine;

namespace OtterIK.Neo.Experiment
{
    /// <summary>
    /// Provider base for the experiment cascaded chain.
    /// Providers do NOT write transforms. They output an ABSOLUTE local target pose per joint
    /// (typically "deltaAim * bindLocal"), plus a per-joint weight.
    /// </summary>
    public abstract class ExpSpinePoseProviderBase : MonoBehaviour
    {
        [Range(0f, 1f)]
        public float globalWeight = 1f;

        /// <summary>
        /// For joint index, output absolute local-space targets:
        /// - targetLocalRot: absolute local rotation to aim for (NOT a delta)
        /// - targetLocalPos: absolute local position to aim for
        /// - weight: 0..1 for this joint (will be multiplied by globalWeight)
        /// Return true if provider is active for this joint this frame.
        /// </summary>
        public abstract bool Evaluate(
            int index,
            SpineChainDefinition spine,
            Quaternion bindLocalRot,
            Vector3 bindLocalPos,
            out Quaternion targetLocalRot,
            out Vector3 targetLocalPos,
            out float weight);
    }
}

