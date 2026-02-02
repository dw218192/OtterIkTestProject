using UnityEngine;

[DisallowMultipleComponent]
public class SpineAimBendProvider : SpinePoseProviderBase
{
    [Header("References")]
    public Transform spineAnchor;
    public Transform headIKGuide;
    public Transform upReference;
    public bool useWorldUp = false;

    [Header("Aim Mode")]
    public bool yawOnly = true;

    [Range(0f, 10f)]
    public float deadZoneDeg = 0.25f;

    [Range(0f, 180f)]
    public float maxTotalAngleDeg = 80f;

    [Tooltip("If true, ignore if references are missing rather than throwing.")]
    public bool safeIfMissingRefs = true;

    private void Reset()
    {
        spineAnchor = transform;
        upReference = transform;
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
        if (safeIfMissingRefs && (spineAnchor == null || headIKGuide == null)) return false;

        var j = spine.GetJoint(index);
        if (j == null || j.bone == null) return false;

        Transform bone = j.bone;
        Transform parent = bone.parent;
        if (parent == null) return false;

        // IMPORTANT: if upReference isn't assigned, fall back to spineAnchor.up (character up),
        // not world up. Using world up can cause "rest pose" drift/bend when the character is tilted.
        Vector3 upWorld = useWorldUp
            ? Vector3.up
            : (upReference != null ? upReference.up : (spineAnchor != null ? spineAnchor.up : Vector3.up));
        if (upWorld.sqrMagnitude < 1e-6f) upWorld = Vector3.up;
        upWorld.Normalize();

        // Desired direction in world
        Vector3 desiredDirWorld = (headIKGuide.position - spineAnchor.position);
        if (desiredDirWorld.sqrMagnitude < 1e-6f) return false;
        desiredDirWorld.Normalize();

        // Anchor forward (world)
        Vector3 anchorFwdWorld = spineAnchor.forward;

        if (yawOnly)
        {
            Vector3 desiredPlane = Vector3.ProjectOnPlane(desiredDirWorld, upWorld);
            Vector3 anchorPlane = Vector3.ProjectOnPlane(anchorFwdWorld, upWorld);

            if (desiredPlane.sqrMagnitude < 1e-6f || anchorPlane.sqrMagnitude < 1e-6f)
                return false;

            desiredPlane.Normalize();
            anchorPlane.Normalize();

            float yawDeg = Vector3.SignedAngle(anchorPlane, desiredPlane, upWorld);
            // Treat deadzone as "no input" -> no bend at all.
            // Without this, even yawDeg==0 can still produce a non-identity delta if the chosen anchor forward
            // doesn't match the bind forward basis, causing a constant rest offset (often seen as "bending down").
            if (Mathf.Abs(yawDeg) < deadZoneDeg) return false;
            yawDeg = Mathf.Clamp(yawDeg, -maxTotalAngleDeg, maxTotalAngleDeg);

            desiredDirWorld = (Quaternion.AngleAxis(yawDeg, upWorld) * anchorPlane).normalized;
        }
        else
        {
            // Optional: full 3D clamp
            float ang = Vector3.Angle(anchorFwdWorld, desiredDirWorld);
            // Deadzone means "no input" -> no bend.
            if (ang < deadZoneDeg) return false;
            else if (ang > maxTotalAngleDeg)
            {
                Vector3 axis = Vector3.Cross(anchorFwdWorld, desiredDirWorld);
                if (axis.sqrMagnitude > 1e-6f)
                {
                    axis.Normalize();
                    desiredDirWorld = (Quaternion.AngleAxis(maxTotalAngleDeg, axis) * anchorFwdWorld).normalized;
                }
            }
        }

        // Convert desired direction into parent space
        Vector3 desiredDirParent = parent.InverseTransformDirection(desiredDirWorld);
        if (yawOnly)
        {
            Vector3 upParent = parent.InverseTransformDirection(upWorld);
            desiredDirParent = Vector3.ProjectOnPlane(desiredDirParent, upParent);
        }
        if (desiredDirParent.sqrMagnitude < 1e-6f) return false;
        desiredDirParent.Normalize();

        // Bind local rot from definition
        Quaternion bindLocal = spine.GetBindLocalRot(index, captureIfMissing: true);

        // Custom axes
        Vector3 fAxis = j.forwardAxis.sqrMagnitude < 1e-6f ? Vector3.forward : j.forwardAxis.normalized;
        Vector3 uAxis = j.upAxis.sqrMagnitude < 1e-6f ? Vector3.up : j.upAxis.normalized;

        if (Vector3.Cross(fAxis, uAxis).sqrMagnitude < 1e-6f)
            uAxis = (Mathf.Abs(Vector3.Dot(fAxis, Vector3.up)) < 0.95f) ? Vector3.up : Vector3.right;

        // Bind basis in parent space
        Vector3 bindFwdParent = (bindLocal * fAxis).normalized;
        Vector3 bindUpParentRaw = (bindLocal * uAxis);

        Vector3 bindUpParent = Vector3.ProjectOnPlane(bindUpParentRaw, bindFwdParent);
        if (bindUpParent.sqrMagnitude < 1e-6f)
            bindUpParent = Vector3.ProjectOnPlane(Vector3.up, bindFwdParent);
        if (bindUpParent.sqrMagnitude < 1e-6f) bindUpParent = Vector3.forward;
        bindUpParent.Normalize();

        Quaternion bindBasis = Quaternion.LookRotation(bindFwdParent, bindUpParent);

        // Target basis
        Vector3 targetUpParent = Vector3.ProjectOnPlane(bindUpParentRaw, desiredDirParent);
        if (targetUpParent.sqrMagnitude < 1e-6f) targetUpParent = bindUpParent;
        targetUpParent.Normalize();

        Quaternion targetBasis = Quaternion.LookRotation(desiredDirParent, targetUpParent);

        Quaternion deltaAim = targetBasis * Quaternion.Inverse(bindBasis);

        // Per-joint clamp by maxAngleDeg (uses definition)
        float aimAngle = Quaternion.Angle(Quaternion.identity, deltaAim);
        float maxA = Mathf.Max(0f, j.maxAngleDeg);
        if (aimAngle > 1e-4f && maxA < aimAngle)
        {
            float t = maxA / aimAngle;
            deltaAim = Quaternion.Slerp(Quaternion.identity, deltaAim, t);
        }

        localRotDelta = deltaAim;
        localPosDelta = Vector3.zero;
        weight = 1f; // Let chain optionally scale by joint.weight
        return true;
    }
}
