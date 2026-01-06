using UnityEngine;

/// <summary>
/// Creates/updates a world-space Transform at the end of the movement arrow,
/// intended to drive a head IK target.
///
/// Attach this to your "movement guidance" object (e.g., the one with DualRingUIController),
/// or anywhere in the scene.
///
/// Reads MovementController's move direction + drag distance (world units),
/// and places a guide Transform at: otterPos + dir * clamp(dist, 0..outerRingRadius).
/// </summary>
[ExecuteAlways]
public class HeadIKGuideFromArrow : MonoBehaviour
{
    [Header("References")]
    [Tooltip("If null, will FindObjectOfType<MovementController>().")]
    [SerializeField] private MovementController movement;

    [Tooltip("Optional: if you want the guide to follow a specific head bone instead of otter root.")]
    [SerializeField] private Transform headAnchorOverride;

    [Header("Guide Transform")]
    [Tooltip("If null, a GameObject named 'HeadIK_Guide' will be created.")]
    [SerializeField] private Transform headIKGuide;

    [Header("Placement")]
    [Tooltip("Locks the guide onto a fixed Y plane (recommended for top-down XZ motion).")]
    [SerializeField] private bool useFixedPlaneY = true;

    [SerializeField] private float fixedPlaneY = 0f;

    [Tooltip("Extra forward offset beyond the arrow end (can help keep head leading slightly).")]
    [SerializeField] private float forwardExtra = 0f;

    [Tooltip("If not dragging, keep the guide this far forward of head anchor (0 disables).")]
    [SerializeField] private float idleForward = 0.5f;

    [Header("Smoothing")]
    [Tooltip("Higher = snappier. 0 = no smoothing.")]
    [SerializeField] private float positionLerpSpeed = 18f;

    [Header("Debug")]
    [SerializeField] private bool drawGizmos = true;
    [SerializeField] private float gizmoRadius = 0.08f;

    private void OnEnable()
    {
        EnsureRefs();
        EnsureGuide();
    }

    private void Update()
    {
        EnsureRefs();
        EnsureGuide();

        if (movement == null || headIKGuide == null) return;

        Transform anchor = headAnchorOverride != null ? headAnchorOverride : movement.transform;

        // If user isn't dragging, either hide or park it in front
        if (!movement.IsDragging())
        {
            if (idleForward > 0f)
            {
                Vector3 idlePos = anchor.position + ProjectToXZ(anchor.forward).normalized * idleForward;
                idlePos = ApplyPlaneY(idlePos);
                headIKGuide.position = SmoothMove(headIKGuide.position, idlePos);
                headIKGuide.gameObject.SetActive(true);
            }
            else
            {
                headIKGuide.gameObject.SetActive(false);
            }
            return;
        }

        Vector3 dir = ProjectToXZ(movement.GetMoveDirection());
        if (dir.sqrMagnitude < 1e-6f)
        {
            // If direction is invalid, keep it at anchor
            Vector3 p = ApplyPlaneY(anchor.position);
            headIKGuide.position = SmoothMove(headIKGuide.position, p);
            headIKGuide.gameObject.SetActive(true);
            return;
        }
        dir.Normalize();

        // MovementController's dragDistanceWorld is already "screen distance mapped to world units"
        float dist = movement.GetDragDistanceWorld();

        // Clamp with outer ring radius (same behavior as your UI arrow length clamp)
        float outer = movement.GetOuterRingRadius();
        float clamped = Mathf.Clamp(dist, 0f, outer);

        Vector3 targetPos = anchor.position + dir * (clamped + forwardExtra);
        targetPos = ApplyPlaneY(targetPos);

        headIKGuide.position = SmoothMove(headIKGuide.position, targetPos);
        headIKGuide.gameObject.SetActive(true);
    }

    private void EnsureRefs()
    {
        if (movement == null)
            movement = FindObjectOfType<MovementController>();
    }

    private void EnsureGuide()
    {
        if (headIKGuide != null) return;

        // Create a guide transform under this object (world-space usage is fine)
        GameObject go = new GameObject("HeadIK_Guide");
        go.transform.SetParent(transform, worldPositionStays: true);

        // Initialize at movement position if possible
        if (movement != null) go.transform.position = ApplyPlaneY(movement.transform.position);

        headIKGuide = go.transform;
    }

    private Vector3 ProjectToXZ(Vector3 v)
    {
        v.y = 0f;
        return v;
    }

    private Vector3 ApplyPlaneY(Vector3 v)
    {
        if (useFixedPlaneY) v.y = fixedPlaneY;
        return v;
    }

    private Vector3 SmoothMove(Vector3 current, Vector3 target)
    {
        if (!Application.isPlaying || positionLerpSpeed <= 0f) return target;

        float k = 1f - Mathf.Exp(-positionLerpSpeed * Time.deltaTime);
        return Vector3.Lerp(current, target, k);
    }

    private void OnDrawGizmos()
    {
        if (!drawGizmos) return;
        if (headIKGuide == null) return;

        Gizmos.color = new Color(1f, 0.6f, 0.1f, 0.9f);
        Gizmos.DrawSphere(headIKGuide.position, gizmoRadius);
        Gizmos.DrawLine(headIKGuide.position, headIKGuide.position + Vector3.up * 0.35f);
    }

    /// <summary>
    /// Expose for other scripts (IK, debug, etc.)
    /// </summary>
    public Transform GetHeadIKGuide() => headIKGuide;
}