using UnityEngine;

/// <summary>
/// Passthrough provider (old CopyCurrentAsRawTarget equivalent).
/// It reads current bone local pose and outputs deltas relative to bind,
/// so the cascaded chain treats the CURRENT pose as raw target (instead of falling back to bind).
/// </summary>
[DisallowMultipleComponent]
public class SpinePassthroughProvider : SpinePoseProviderBase
{
    [Header("Channels")]
    [Tooltip("If true, passthrough rotation (current localRotation becomes raw target).")]
    public bool passthroughRotation = true;

    [Tooltip("If true, passthrough position (current localPosition becomes raw target).")]
    public bool passthroughPosition = false;

    [Header("Weight")]
    [Tooltip("Per-joint output weight (0..1). Usually keep 1.0.")]
    [Range(0f, 1f)]
    public float perJointWeight = 1f;

    // SpineChainDefinition caches bind LOCAL ROT, but not bind LOCAL POS.
    // The damping chain captures bind localPosition on init; we mirror that here by capturing
    // the first time we see each joint index.
    private Vector3[] _bindLocalPos;
    private bool[] _bindLocalPosCaptured;

    private void OnDisable()
    {
        // Keep arrays allocated, but allow re-capture next enable.
        ClearBindLocalPosCaptureFlags();
    }

    private void OnEnable()
    {
        ClearBindLocalPosCaptureFlags();
    }

    private void ClearBindLocalPosCaptureFlags()
    {
        if (_bindLocalPosCaptured == null) return;
        for (int i = 0; i < _bindLocalPosCaptured.Length; i++)
            _bindLocalPosCaptured[i] = false;
    }

    private void EnsureBindLocalPosCache(SpineChainDefinition spine)
    {
        int n = (spine != null) ? spine.Count : 0;
        if (n <= 0) return;

        if (_bindLocalPos == null || _bindLocalPos.Length != n)
            _bindLocalPos = new Vector3[n];

        if (_bindLocalPosCaptured == null || _bindLocalPosCaptured.Length != n)
            _bindLocalPosCaptured = new bool[n];
    }

    private Vector3 GetBindLocalPos(int index, SpineChainDefinition spine, Transform bone, bool captureIfMissing = true)
    {
        EnsureBindLocalPosCache(spine);
        if (_bindLocalPos == null || _bindLocalPosCaptured == null) return Vector3.zero;
        if (index < 0 || index >= _bindLocalPos.Length) return Vector3.zero;

        if (captureIfMissing && !_bindLocalPosCaptured[index])
        {
            _bindLocalPos[index] = bone.localPosition;
            _bindLocalPosCaptured[index] = true;
        }

        return _bindLocalPos[index];
    }

    public override bool Evaluate(
        int index,
        SpineChainDefinition spine,
        out Quaternion localRotDelta,
        out Vector3 localPosDelta,
        out float weight)
    {
        localRotDelta = Quaternion.identity;
        localPosDelta = Vector3.zero;
        weight = 0f;

        if (spine == null || spine.Count == 0) return false;

        var j = spine.GetJoint(index);
        if (j == null || j.bone == null) return false;

        if (!passthroughRotation && !passthroughPosition) return false;

        // Bind pose from definition (captured once; used as reference).
        Quaternion bindLocalRot = spine.GetBindLocalRot(index, captureIfMissing: true);
        Vector3 bindLocalPos = GetBindLocalPos(index, spine, j.bone, captureIfMissing: true);

        // Rotation passthrough: make rawTargetLocalRot == current localRotation
        if (passthroughRotation)
        {
            Quaternion current = j.bone.localRotation;
            localRotDelta = current * Quaternion.Inverse(bindLocalRot);
        }

        // Position passthrough: make rawTargetLocalPos == current localPosition
        if (passthroughPosition)
        {
            Vector3 currentPos = j.bone.localPosition;
            localPosDelta = currentPos - bindLocalPos;
        }

        weight = perJointWeight;
        return true;
    }
}
