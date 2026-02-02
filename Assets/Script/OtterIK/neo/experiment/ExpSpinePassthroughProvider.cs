using UnityEngine;

namespace OtterIK.Neo.Experiment
{
    /// <summary>
    /// Provider that treats the CURRENT local pose as the raw target pose (like WavingTestRig.CopyCurrentAsRawTarget).
    /// Useful for lagging another system's output.
    /// </summary>
    [DisallowMultipleComponent]
    public class ExpSpinePassthroughProvider : ExpSpinePoseProviderBase
    {
        [Header("Channels")]
        public bool passthroughRotation = true;
        public bool passthroughPosition = false;

        [Header("Weight")]
        [Range(0f, 1f)]
        public float perJointWeight = 1f;

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

            var j = spine.GetJoint(index);
            if (j == null || j.bone == null) return false;

            if (!passthroughRotation && !passthroughPosition) return false;

            if (passthroughRotation) targetLocalRot = j.bone.localRotation;
            if (passthroughPosition) targetLocalPos = j.bone.localPosition;

            weight = perJointWeight;
            return true;
        }
    }
}

