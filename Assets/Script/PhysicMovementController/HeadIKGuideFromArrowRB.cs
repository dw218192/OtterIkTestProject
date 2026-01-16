// HeadIKGuideFromArrow.cs
using UnityEngine;

/// <summary>
/// Updates a Head IK guide transform.
/// - MovementControllerRB computes an input-intent "carrotWorld" ahead of the character.
/// - This script positions the HeadIKGuide at/near that carrot, with optional extra lead.
/// - When idle (no drag), it keeps a stable forward guide so neck/head can rest naturally.
/// </summary>
public class HeadIKGuideFromArrowRB : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private MovementControllerRB movement;

    [Tooltip("Optional state controller. If assigned, can disable head guide in certain states.")]
    [SerializeField] private MoveStateController moveState;

    [SerializeField] private bool disableInUpright = false;
    [SerializeField] private bool disableInSpecial = true;

    [Tooltip("Anchor for the guide (usually a head/neck root or body pivot). If null, uses movement.transform.")]
    [SerializeField] private Transform anchor;

    [Tooltip("The transform used by IK as the look target.")]
    [SerializeField] private Transform headIKGuide;

    [Header("Plane")]
    [SerializeField] private bool useFixedPlaneY = true;
    [SerializeField] private float fixedPlaneY = 0f;

    [Header("Idle")]
    [Tooltip("When not dragging, place guide ahead of anchor by this many world units. Set 0 to disable guide on idle.")]
    [SerializeField] private float idleForward = 1.2f;

    [Header("Lead")]
    [Tooltip("Additional lead (world units) added in the carrot direction, on top of carrotWorld.")]
    [SerializeField] private float extraLead = 0.0f;

    [Header("Smoothing")]
    [Tooltip("Follow smoothing speed. Higher = snappier. 0 = no smoothing.")]
    [SerializeField] private float followSpeed = 22f;

    [Tooltip("If true, keep guide movement on XZ plane.")]
    [SerializeField] private bool planarOnly = true;

    [Header("Release Blend")]
    [Tooltip("Blend time when leaving drag to avoid snap back to idle forward.")]
    [SerializeField] private float releaseBlendTime = 0.35f;

    [Header("Debug")]
    [SerializeField] private bool drawGizmos = true;
    [SerializeField] private float gizmoRadius = 0.06f;

    private float PlaneY
        => useFixedPlaneY ? fixedPlaneY : (anchor != null ? anchor.position.y : (movement != null ? movement.transform.position.y : 0f));

    private Vector3 lastActiveTarget;
    private float releaseBlendTimer;
    private bool releaseBlendActive;
    private bool wasDragging;

    private void Awake() => EnsureRefs();
    private void OnEnable() => EnsureRefs();

    private void LateUpdate()
    {
        EnsureRefs();
        if (movement == null || headIKGuide == null) return;
        if (anchor == null) anchor = movement.transform;

        // Optional state gating
        if (moveState != null)
        {
            if (disableInSpecial && moveState.IsInSpecial())
            {
                headIKGuide.gameObject.SetActive(false);
                return;
            }
            if (disableInUpright && moveState.IsUpright())
            {
                headIKGuide.gameObject.SetActive(false);
                return;
            }
        }

        bool dragging = movement.IsDragging();
        bool justReleased = (!dragging && wasDragging);
        var zone = movement.GetZone();

        // If idle and user wants no idle guide, hide.
        if (!dragging && idleForward <= 0f)
        {
            headIKGuide.gameObject.SetActive(false);
            return;
        }

        Vector3 targetPos;

        if (dragging)
        {
            releaseBlendActive = false;
            releaseBlendTimer = 0f;
        }
        else if (justReleased)
        {
            releaseBlendActive = true;
            releaseBlendTimer = 0f;
            if (lastActiveTarget == Vector3.zero) lastActiveTarget = headIKGuide.position;
        }

        if (!dragging)
        {
            // Idle: keep a stable point in front of the body (or last-known facing).
            Vector3 fwd = movement.GetWorldDir();
            if (planarOnly) fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f)
            {
                Vector3 v = movement.GetVelocity();
                v.y = 0f;
                if (v.sqrMagnitude > 1e-6f) fwd = v.normalized;
            }
            if (fwd.sqrMagnitude < 1e-6f) fwd = movement.transform.forward;
            if (planarOnly) fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            fwd.Normalize();

            Vector3 idleTarget = anchor.position + fwd * idleForward;

            if (releaseBlendActive)
            {
                float dt = Time.deltaTime;
                releaseBlendTimer += dt;
                float t = releaseBlendTime <= 0f ? 1f : Mathf.Clamp01(releaseBlendTimer / releaseBlendTime);
                targetPos = Vector3.Lerp(lastActiveTarget, idleTarget, t);
                if (t >= 0.999f)
                {
                    releaseBlendActive = false;
                    lastActiveTarget = idleTarget;
                }
            }
            else
            {
                targetPos = idleTarget;
            }
        }
        else
        {
            // Active drag:
            // Aim zone still drives head direction, but we keep guide position near carrot.
            Vector3 carrot = movement.GetCarrotWorld();
            Vector3 dir = carrot - anchor.position;
            if (planarOnly) dir.y = 0f;

            Vector3 lead = Vector3.zero;
            if (extraLead > 0f && dir.sqrMagnitude > 1e-6f)
                lead = dir.normalized * extraLead;

            targetPos = carrot + lead;
            lastActiveTarget = targetPos;

            // Optional: in Aim zone you may want the guide a bit closer (tighter head control).
            // Keeping it as-is is usually fine, because carrotRadiusAim should already be smaller.
            _ = zone; // (kept to make it easy to add per-zone tweaks later)
        }

        if (planarOnly) targetPos.y = PlaneY;

        headIKGuide.position = SmoothMove(headIKGuide.position, targetPos);
        headIKGuide.gameObject.SetActive(true);

        wasDragging = dragging;
    }

    private Vector3 SmoothMove(Vector3 current, Vector3 target)
    {
        if (followSpeed <= 0f) return target;
        float dt = Time.deltaTime;
        float k = 1f - Mathf.Exp(-followSpeed * dt);
        return Vector3.Lerp(current, target, k);
    }

    private void EnsureRefs()
    {
        if (movement == null)
            movement = FindObjectOfType<MovementControllerRB>();
        if (movement != null && anchor == null)
            anchor = movement.transform;

        if (moveState == null && movement != null)
            moveState = movement.GetComponent<MoveStateController>();
    }

    private void OnDrawGizmos()
    {
        if (!drawGizmos) return;
        if (headIKGuide == null) return;

        Gizmos.color = new Color(1f, 0.6f, 0.1f, 0.9f);
        Gizmos.DrawSphere(headIKGuide.position, gizmoRadius);
        Gizmos.DrawLine(headIKGuide.position, headIKGuide.position + Vector3.up * 0.35f);
    }

    public Transform GetHeadIKGuide() => headIKGuide;
}
