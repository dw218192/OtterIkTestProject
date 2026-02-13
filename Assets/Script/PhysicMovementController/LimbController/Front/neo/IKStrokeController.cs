using UnityEngine;

/// <summary>
/// IKStrokeController
///
/// Drives real IK targets on top of:
/// - DynamicIndirectIk_V2: outer-side + lag points + stable center/basis.
/// - IKStrokeTrajectory_V2: solved per-side trajectory frame and curve sampling.
///
/// Per side:
/// 1) When side becomes outer -> start stroke and accumulate Alpha.
/// 2) Instant Alpha magnitude maps to curve progress (0..1), no delta accumulation.
/// 3) EventB: one full loop done -> notify listeners and optionally continue next loop.
/// 4) EventA: side loses outer state before loop complete -> interrupt and return to rest.
/// </summary>
[DefaultExecutionOrder(120)]
public class IKStrokeController : MonoBehaviour
{
    public System.Action<bool> OnEventAInterrupted;
    public System.Action<bool> OnEventBLoopCompleted;

    [Header("Input")]
    public DynamicIndirectIk_V2 dynamicSource;
    public IKStrokeTrajectory_V2 trajectorySource;

    [Header("IK Target Transforms (real IK points)")]
    public Transform leftLimbIkTarget;
    public Transform rightLimbIkTarget;

    [Header("Alpha -> Stroke Mapping")]
    [Tooltip("Absolute Alpha(deg) needed to reach one full loop progress (progress=1).")]
    [Min(1f)] public float alphaForFullLoopDeg = 70f;
    [Tooltip("Smoothness for mapping Alpha to progress; higher = faster response, lower = more stable.")]
    [Min(0f)] public float alphaToProgressSharpness = 24f;
    [Tooltip("Ignore tiny Alpha near zero to suppress noise.")]
    [Min(0f)] public float alphaDeadzoneDeg = 1f;
    [Range(0.5f, 1f)] public float eventBTriggerProgress = 0.98f;
    [Range(0f, 0.8f)] public float eventBRearmProgress = 0.2f;

    [Header("Target Motion")]
    [Tooltip("Following sharpness while active stroke tracking trajectory.")]
    [Min(0f)] public float strokeFollowSharpness = 30f;
    [Tooltip("Return sharpness when interrupted or idle.")]
    [Min(0f)] public float returnToRestSharpness = 16f;
    [Tooltip("If true, targets are initialized to rest at startup.")]
    public bool snapToRestOnStart = true;
    [Tooltip("If true, after EventB this side waits until outer releases before next stroke. If false, continuous strokes are allowed.")]
    public bool lockUntilOuterReleaseAfterLoop = false;
    [Tooltip("Minimum interval between two EventB triggers on the same side, preventing high-frequency retrigger.")]
    [Min(0f)] public float minLoopIntervalSec = 0.2f;

    [Header("Debug")]
    public bool logEvents = false;
    public bool drawDebug = true;
    public float debugSphereRadius = 0.025f;
    public Color debugLeftColor = new Color(0.25f, 0.85f, 1f, 1f);
    public Color debugRightColor = new Color(1f, 0.6f, 0.2f, 1f);
    [Header("Runtime Alpha (deg)")]
    [Tooltip("Current left-side absolute Alpha in degrees (runtime).")]
    public float leftAlphaDegree;
    [Tooltip("Current right-side absolute Alpha in degrees (runtime).")]
    public float rightAlphaDegree;

    public float LeftProgress01 => leftState.progress01;
    public float RightProgress01 => rightState.progress01;
    public float LeftAccumAlphaDeg => leftState.alphaAbsDeg;
    public float RightAccumAlphaDeg => rightState.alphaAbsDeg;

    private struct SideState
    {
        public bool isStroking;
        public bool lockUntilOuterRelease;
        public float progress01;
        public float alphaAbsDeg;
        public bool eventBArmed;
        public float eventBCooldownTimer;
        public Vector3 currentTargetWorld;
        public bool hasCurrentTarget;
    }

    private SideState leftState;
    private SideState rightState;

    private static float ExpAlpha(float sharpness, float dt)
    {
        return 1f - Mathf.Exp(-Mathf.Max(0f, sharpness) * Mathf.Max(0f, dt));
    }

    private void Reset()
    {
        if (!dynamicSource) dynamicSource = GetComponent<DynamicIndirectIk_V2>();
        if (!trajectorySource) trajectorySource = GetComponent<IKStrokeTrajectory_V2>();
    }

    private void Start()
    {
        InitializeTargets();
    }

    private void InitializeTargets()
    {
        if (!snapToRestOnStart) return;

        Vector3 lRest = GetRestWorld(true);
        Vector3 rRest = GetRestWorld(false);

        if (leftLimbIkTarget) leftLimbIkTarget.position = lRest;
        if (rightLimbIkTarget) rightLimbIkTarget.position = rRest;

        leftState.currentTargetWorld = lRest;
        rightState.currentTargetWorld = rRest;
        leftState.hasCurrentTarget = true;
        rightState.hasCurrentTarget = true;
        leftState.eventBArmed = true;
        rightState.eventBArmed = true;
        leftState.eventBCooldownTimer = Mathf.Max(0f, minLoopIntervalSec);
        rightState.eventBCooldownTimer = Mathf.Max(0f, minLoopIntervalSec);
    }

    private void LateUpdate()
    {
        float dt = Application.isPlaying ? Time.deltaTime : (1f / 60f);
        if (dt < 1e-6f) dt = 1f / 60f;

        UpdateSide(true, dt, ref leftState, leftLimbIkTarget);
        UpdateSide(false, dt, ref rightState, rightLimbIkTarget);
    }

    private void UpdateSide(bool isLeft, float dt, ref SideState state, Transform ikTarget)
    {
        state.eventBCooldownTimer += dt;

        bool outerNow = IsOuterNow(isLeft);
        Vector3 restWorld = GetRestWorld(isLeft);

        // Unlock after side is no longer outer, allowing next stroke entry.
        if (!outerNow)
        {
            state.lockUntilOuterRelease = false;
            // Side released: allow next loop trigger when it becomes outer again.
            state.eventBArmed = true;
        }

        if (outerNow && !state.lockUntilOuterRelease)
        {
            if (!state.isStroking)
            {
                StartStroke(isLeft, ref state);
            }

            if (TryGetSignedAlphaDeg(isLeft, out float signedAlphaDeg))
            {
                float absAlpha = Mathf.Abs(signedAlphaDeg);
                state.alphaAbsDeg = absAlpha >= alphaDeadzoneDeg ? absAlpha : 0f;
                if (isLeft) leftAlphaDegree = state.alphaAbsDeg;
                else rightAlphaDegree = state.alphaAbsDeg;
            }
            else
            {
                state.alphaAbsDeg = 0f;
                if (isLeft) leftAlphaDegree = 0f;
                else rightAlphaDegree = 0f;
            }

            float fullLoopAlpha = Mathf.Max(1f, alphaForFullLoopDeg);
            float mappedProgress = Mathf.Clamp01(state.alphaAbsDeg / fullLoopAlpha);
            state.progress01 = Mathf.Lerp(state.progress01, mappedProgress, ExpAlpha(alphaToProgressSharpness, dt));

            Vector3 strokeTarget = EvaluateTrajectoryWorld(isLeft, state.progress01, restWorld);
            MoveTargetTowards(ref state, ikTarget, strokeTarget, strokeFollowSharpness, dt);

            // Rearm EventB only after raw mapped progress falls back sufficiently.
            // Use mappedProgress (not smoothed progress01) to avoid false rearm right after a reset.
            if (!state.eventBArmed && mappedProgress <= Mathf.Min(eventBRearmProgress, eventBTriggerProgress - 0.05f))
            {
                state.eventBArmed = true;
            }

            bool cooldownReady = state.eventBCooldownTimer >= Mathf.Max(0f, minLoopIntervalSec);
            if (state.eventBArmed && cooldownReady && state.progress01 >= Mathf.Max(0.5f, eventBTriggerProgress))
            {
                // Event B: completed one full loop.
                state.isStroking = false;
                state.lockUntilOuterRelease = lockUntilOuterReleaseAfterLoop;
                state.eventBArmed = false;
                state.eventBCooldownTimer = 0f;
                state.progress01 = 0f;
                state.alphaAbsDeg = 0f;
                dynamicSource?.OnStrokeLoopCompleted(isLeft);
                OnEventBLoopCompleted?.Invoke(isLeft);
                if (logEvents) Debug.Log($"[IKStrokeController] EventB loop completed ({(isLeft ? "L" : "R")})", this);
            }
        }
        else
        {
            if (state.isStroking)
            {
                // Event A: outer side changed/released before one-loop completion.
                state.isStroking = false;
                state.eventBArmed = true;
                OnEventAInterrupted?.Invoke(isLeft);
                if (logEvents) Debug.Log($"[IKStrokeController] EventA interrupted ({(isLeft ? "L" : "R")})", this);
            }

            state.progress01 = 0f;
            state.alphaAbsDeg = 0f;
            if (isLeft) leftAlphaDegree = 0f;
            else rightAlphaDegree = 0f;
            MoveTargetTowards(ref state, ikTarget, restWorld, returnToRestSharpness, dt);
        }
    }

    private void StartStroke(bool isLeft, ref SideState state)
    {
        state.isStroking = true;
        state.progress01 = 0f;
        state.alphaAbsDeg = 0f;
        // Do not force-arm here; keep hysteresis state from previous loop.
        if (logEvents) Debug.Log($"[IKStrokeController] Stroke start ({(isLeft ? "L" : "R")})", this);
    }

    private void MoveTargetTowards(ref SideState state, Transform target, Vector3 desiredWorld, float sharpness, float dt)
    {
        if (!state.hasCurrentTarget)
        {
            state.currentTargetWorld = desiredWorld;
            state.hasCurrentTarget = true;
        }
        else
        {
            float a = ExpAlpha(sharpness, dt);
            state.currentTargetWorld = Vector3.Lerp(state.currentTargetWorld, desiredWorld, a);
        }

        if (target) target.position = state.currentTargetWorld;
    }

    private bool IsOuterNow(bool isLeft)
    {
        if (!dynamicSource) return false;
        return isLeft ? dynamicSource.isLeftOuter : dynamicSource.isRightOuter;
    }

    private Vector3 EvaluateTrajectoryWorld(bool isLeft, float progress01, Vector3 fallbackRest)
    {
        if (!trajectorySource) return fallbackRest;
        Transform rp = isLeft ? trajectorySource.leftRestPoint : trajectorySource.rightRestPoint;
        if (!rp) return fallbackRest;
        Vector3 p = trajectorySource.EvaluateWorldPoint(isLeft, progress01);
        return p;
    }

    private Vector3 GetRestWorld(bool isLeft)
    {
        if (trajectorySource)
        {
            Transform rp = isLeft ? trajectorySource.leftRestPoint : trajectorySource.rightRestPoint;
            if (rp) return rp.position;
        }

        if (dynamicSource)
        {
            Transform sh = isLeft ? dynamicSource.leftShoulder : dynamicSource.rightShoulder;
            if (sh) return sh.position;
            return dynamicSource.StableCenter;
        }

        return transform.position;
    }

    private bool TryGetSignedAlphaDeg(bool isLeft, out float signedAlphaDeg)
    {
        signedAlphaDeg = 0f;
        if (!dynamicSource) return false;

        Transform shoulder = isLeft ? dynamicSource.leftShoulder : dynamicSource.rightShoulder;
        if (!shoulder) return false;

        Vector3 lag = isLeft ? dynamicSource.WorldLeftPoint : dynamicSource.WorldRightPoint;
        Vector3 dir = lag - shoulder.position;
        if (dir.sqrMagnitude < 1e-10f) return false;
        Vector3 dirN = dir.normalized;

        // Keep Alpha definition consistent with DynamicIndirectIk_V2::UpdateAnchor angleL:
        // angle between per-side "rest side" direction and shoulder->lag direction.
        Quaternion basis = dynamicSource.StableBasis;
        Vector3 side = isLeft ? -(basis * Vector3.right) : (basis * Vector3.right);
        if (side.sqrMagnitude < 1e-10f) side = isLeft ? -Vector3.right : Vector3.right;
        side.Normalize();

        float unsignedAngle = Vector3.Angle(side, dirN);
        Vector3 signAxis = basis * Vector3.forward;
        if (signAxis.sqrMagnitude < 1e-10f) signAxis = basis * Vector3.up;
        if (signAxis.sqrMagnitude < 1e-10f) signAxis = Vector3.up;
        signAxis.Normalize();

        float sign = Mathf.Sign(Vector3.Dot(Vector3.Cross(side, dirN), signAxis));
        if (Mathf.Abs(sign) < 1e-6f) sign = 1f;
        signedAlphaDeg = unsignedAngle * sign;
        return true;
    }

    private void OnDrawGizmos()
    {
        if (!drawDebug) return;

        Gizmos.color = debugLeftColor;
        if (leftLimbIkTarget) Gizmos.DrawSphere(leftLimbIkTarget.position, debugSphereRadius);

        Gizmos.color = debugRightColor;
        if (rightLimbIkTarget) Gizmos.DrawSphere(rightLimbIkTarget.position, debugSphereRadius);
    }
}
