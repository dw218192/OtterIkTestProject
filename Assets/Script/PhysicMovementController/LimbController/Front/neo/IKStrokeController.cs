using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// IKStrokeController
///
/// Drives real IK targets on top of:
/// - DynamicIndirectIk_V2: outer-side + lag points + stable center/basis.
/// - IKStrokeTrajectory_V2: solved per-side trajectory frame and curve sampling.
///
/// Per side:
/// 1) When side becomes outer -> start stroke and accumulate phase angle.
/// 2) Accumulated phase angle (with direction-consistency gate) maps to curve progress (0..1).
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
    public MovementControllerRB movementSource;

    [Header("IK Target Transforms (real IK points)")]
    public Transform leftLimbIkTarget;
    public Transform rightLimbIkTarget;

    [Header("Phase Accumulation Mapping")]
    [FormerlySerializedAs("alphaForFullLoopDeg")]
    [Tooltip("Accumulated phase angle(deg) needed to reach one full loop progress (progress=1).")]
    [Min(1f)] public float phaseDegreesPerLoop = 70f;
    [FormerlySerializedAs("alphaToProgressSharpness")]
    [Tooltip("Smoothness for mapping accumulated phase to progress; higher = faster response, lower = more stable.")]
    [Min(0f)] public float phaseToProgressSharpness = 24f;
    [FormerlySerializedAs("alphaDeadzoneDeg")]
    [Tooltip("Ignore tiny phase-driving spin speed near zero (deg/s).")]
    [Min(0f)] public float minAccumSpinDegPerSec = 1f;
    [Tooltip("Cap accumulated alpha speed to prevent spikes.")]
    [FormerlySerializedAs("maxAccumulationSpeedDegPerSec")]
    [Min(1f)] public float maxPhaseAccumulationSpeedDegPerSec = 180f;
    [Header("Direction Consistency Gate (Trajectory->StableBasis)")]
    [Tooltip("If enabled, phase only accumulates while solved trajectory front/up stay locally stable in StableBasis space.")]
    public bool useTrajectoryDirectionConsistencyGate = true;
    [Range(0.3f, 1f)] public float consistencyEnterDot = 0.96f;
    [Range(0.2f, 1f)] public float consistencyExitDot = 0.9f;
    [Range(0.5f, 1f)] public float eventBTriggerProgress = 0.98f;
    [Range(0f, 0.8f)] public float eventBRearmProgress = 0.2f;
    [Tooltip("Maps normalized accumulated phase to trajectory phase. Use a curve below diagonal at start and above later for slow start then faster mid-stroke.")]
    public AnimationCurve strokePhaseResponse = new AnimationCurve(
        new Keyframe(0f, 0f, 0.6f, 0.6f),
        new Keyframe(0.25f, 0.12f, 0.8f, 0.8f),
        new Keyframe(0.75f, 0.9f, 1.2f, 1.2f),
        new Keyframe(1f, 1f, 0f, 0f));

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
    [Tooltip("When no side is outer, keep IK targets tightly near rest points.")]
    public bool pinTargetsToRestWhenNoOuter = true;
    [Tooltip("When moving with no clear turning/no outer, lock IK target to rest offset in StableBasis space.")]
    public bool holdRelativeToStableWhenMovingStraight = true;
    [Min(0f)] public float movingSpeedThreshold = 0.15f;
    [Min(0f)] public float lowTurnDegPerSecThreshold = 18f;
    [Min(0f)] public float idleRestHoldSharpness = 36f;
    [Min(0f)] public float idleRestSnapDistance = 0.01f;
    [Header("Interrupt & Return Polish")]
    [Tooltip("If true, an active stroke can be interrupted early when accumulation nearly stops.")]
    public bool enableFastInterruptOnStall = true;
    [Min(0f)] public float fastInterruptStallTime = 0.12f;
    [Min(0f)] public float fastInterruptMinAccumSpeedDegPerSec = 6f;
    [Tooltip("Use quadratic bezier return path after interruption.")]
    public bool useBezierReturnOnInterrupt = true;
    [Tooltip("Interruption return speed in world-units per second. Larger value means faster return.")]
    [Min(0.01f)] public float interruptReturnSpeed = 1.4f;
    [Min(-1f)] public float interruptReturnUpOffset = 0.08f;
    [Min(-1f)] public float interruptReturnForwardOffset = -0.03f;

    [Header("Debug")]
    public bool logEvents = false;
    public bool logSpinDiagnostics = false;
    [Range(1, 240)] public int spinLogEveryNFrames = 20;
    public bool drawDebug = true;
    public float debugSphereRadius = 0.025f;
    public Color debugLeftColor = new Color(0.25f, 0.85f, 1f, 1f);
    public Color debugRightColor = new Color(1f, 0.6f, 0.2f, 1f);
    [Header("Gizmo - Interrupt Return")]
    public bool drawInterruptReturnTrajectory = true;
    [Range(8, 128)] public int interruptReturnGizmoSegments = 36;
    public Color interruptReturnLeftColor = new Color(0.2f, 1f, 0.8f, 1f);
    public Color interruptReturnRightColor = new Color(1f, 0.9f, 0.25f, 1f);
    [Header("Runtime Angle (deg)")]
    [FormerlySerializedAs("leftAlphaDegree")]
    [Tooltip("Current left-side absolute shoulder-lag angle in degrees (runtime).")]
    public float leftCurrentAngleDeg;
    [FormerlySerializedAs("rightAlphaDegree")]
    [Tooltip("Current right-side absolute shoulder-lag angle in degrees (runtime).")]
    public float rightCurrentAngleDeg;
    [Header("Runtime Accumulation (deg)")]
    [Tooltip("Current left-side accumulated phase angle in degrees (runtime).")]
    public float leftAccumulatedPhaseDeg;
    [Tooltip("Current right-side accumulated phase angle in degrees (runtime).")]
    public float rightAccumulatedPhaseDeg;
    [Header("Runtime Direction Gate")]
    public bool leftDirectionConsistent = true;
    public bool rightDirectionConsistent = true;

    public float LeftProgress01 => leftState.progress01;
    public float RightProgress01 => rightState.progress01;
    public float LeftAccumPhaseDeg => leftState.accumulatedPhaseDeg;
    public float RightAccumPhaseDeg => rightState.accumulatedPhaseDeg;
    public float LeftAccumAlphaDeg => LeftAccumPhaseDeg;
    public float RightAccumAlphaDeg => RightAccumPhaseDeg;

    private struct SideState
    {
        public bool isStroking;
        public bool lockUntilOuterRelease;
        public float progress01;
        public float currentAngleDeg;
        public float accumulatedPhaseDeg;
        public bool directionConsistent;
        public bool hasPrevLocalPose;
        public Vector3 prevLocalFront;
        public Vector3 prevLocalUp;
        public float stallTimer;
        public float lastAccumulatedPhaseDeg;
        public bool hasStraightMotionHoldOffset;
        public Vector3 straightMotionHoldLocalOffset;
        public bool isInterruptReturning;
        public float interruptReturnT;
        public Vector3 interruptReturnStart;
        public Vector3 interruptReturnControl;
        public Vector3 interruptReturnEnd;
        public bool hasInterruptReturnLocal;
        public Vector3 interruptReturnStartLocal;
        public Vector3 interruptReturnControlLocal;
        public Vector3 interruptReturnEndLocal;
        public float interruptReturnApproxLength;
        public bool eventBArmed;
        public float eventBCooldownTimer;
        public Vector3 currentTargetWorld;
        public bool hasCurrentTarget;
        public Vector3 currentTargetLocalStable;
        public bool hasCurrentTargetLocalStable;
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
        if (!movementSource) movementSource = GetComponent<MovementControllerRB>();
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
        SyncStateLocalFromWorld(ref leftState, lRest);
        SyncStateLocalFromWorld(ref rightState, rRest);
        leftState.eventBArmed = true;
        rightState.eventBArmed = true;
        leftState.eventBCooldownTimer = Mathf.Max(0f, minLoopIntervalSec);
        rightState.eventBCooldownTimer = Mathf.Max(0f, minLoopIntervalSec);
        leftState.hasStraightMotionHoldOffset = false;
        rightState.hasStraightMotionHoldOffset = false;
    }

    private void LateUpdate()
    {
        float dt = Application.isPlaying ? Time.deltaTime : (1f / 60f);
        if (dt < 1e-6f) dt = 1f / 60f;

        bool noOuterNow = !dynamicSource || (!dynamicSource.isLeftOuter && !dynamicSource.isRightOuter);
        UpdateSide(true, dt, noOuterNow, ref leftState, leftLimbIkTarget);
        UpdateSide(false, dt, noOuterNow, ref rightState, rightLimbIkTarget);
    }

    private void UpdateSide(bool isLeft, float dt, bool noOuterNow, ref SideState state, Transform ikTarget)
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
            state.hasStraightMotionHoldOffset = false;
            if (!state.isStroking)
            {
                StartStroke(isLeft, ref state);
            }

            if (TryGetSignedAngleDeg(isLeft, out float signedAngleDeg))
            {
                float absAngle = Mathf.Abs(signedAngleDeg);
                state.currentAngleDeg = absAngle;
                if (isLeft) leftCurrentAngleDeg = state.currentAngleDeg;
                else rightCurrentAngleDeg = state.currentAngleDeg;
            }
            else
            {
                state.currentAngleDeg = 0f;
                if (isLeft) leftCurrentAngleDeg = 0f;
                else rightCurrentAngleDeg = 0f;
            }

            // Phase accumulation is driven by body angular spin, not by clamped lag-angle delta.
            bool directionOk = EvaluateDirectionConsistency(isLeft, ref state);
            if (isLeft) leftDirectionConsistent = directionOk;
            else rightDirectionConsistent = directionOk;

            bool hasSpinSample = TryGetSpinDegPerSec(isLeft, out float projectedSpinDegPerSec, out float totalAngularDegPerSec);

            if (directionOk && hasSpinSample)
            {
                if (totalAngularDegPerSec >= minAccumSpinDegPerSec)
                {
                    float maxStep = Mathf.Max(1f, maxPhaseAccumulationSpeedDegPerSec) * dt;
                    float step = totalAngularDegPerSec * dt;
                    state.accumulatedPhaseDeg += Mathf.Min(step, maxStep);
                }
            }
            if (isLeft) leftAccumulatedPhaseDeg = state.accumulatedPhaseDeg;
            else rightAccumulatedPhaseDeg = state.accumulatedPhaseDeg;

            // Early interrupt if stroke stalls while still marked outer.
            float accumDelta = Mathf.Max(0f, state.accumulatedPhaseDeg - state.lastAccumulatedPhaseDeg);
            float accumSpeed = dt > 1e-6f ? (accumDelta / dt) : 0f;
            if (enableFastInterruptOnStall)
            {
                if (accumSpeed < Mathf.Max(0f, fastInterruptMinAccumSpeedDegPerSec))
                    state.stallTimer += dt;
                else
                    state.stallTimer = 0f;

                if (state.isStroking && state.stallTimer >= Mathf.Max(0f, fastInterruptStallTime))
                {
                    TriggerInterrupt(isLeft, ref state, restWorld);
                    if (isLeft) leftDirectionConsistent = true;
                    else rightDirectionConsistent = true;
                    if (isLeft) leftAccumulatedPhaseDeg = 0f;
                    else rightAccumulatedPhaseDeg = 0f;
                    MoveInterruptReturnOrRest(isLeft, dt, ref state, ikTarget, restWorld, noOuterNow);
                    return;
                }
            }
            else
            {
                state.stallTimer = 0f;
            }
            state.lastAccumulatedPhaseDeg = state.accumulatedPhaseDeg;

            if (logSpinDiagnostics &&
                Application.isPlaying &&
                spinLogEveryNFrames > 0 &&
                (Time.frameCount % spinLogEveryNFrames == 0))
            {
                Debug.Log(
                    $"[IKStrokeController][SpinDiag][{(isLeft ? "L" : "R")}] " +
                    $"outer={outerNow}, dirOK={directionOk}, hasSpin={hasSpinSample}, " +
                    $"projSpinDegPerSec={projectedSpinDegPerSec:F2}, totalAngDegPerSec={totalAngularDegPerSec:F2}, " +
                    $"minSpin={minAccumSpinDegPerSec:F2}, accum={state.accumulatedPhaseDeg:F2}, progress={state.progress01:F3}",
                    this);
            }

            float fullLoopPhase = Mathf.Max(1f, phaseDegreesPerLoop);
            float mappedProgressRaw = Mathf.Clamp01(state.accumulatedPhaseDeg / fullLoopPhase);
            float mappedProgress = Mathf.Clamp01(strokePhaseResponse.Evaluate(mappedProgressRaw));
            state.progress01 = Mathf.Lerp(state.progress01, mappedProgress, ExpAlpha(phaseToProgressSharpness, dt));

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
                state.currentAngleDeg = 0f;
                state.accumulatedPhaseDeg = 0f;
                state.directionConsistent = true;
                state.hasPrevLocalPose = false;
                state.stallTimer = 0f;
                state.lastAccumulatedPhaseDeg = 0f;
                state.isInterruptReturning = false;
                state.interruptReturnT = 0f;
                state.hasInterruptReturnLocal = false;
                if (isLeft) leftAccumulatedPhaseDeg = 0f;
                else rightAccumulatedPhaseDeg = 0f;
                OnEventBLoopCompleted?.Invoke(isLeft);
                if (logEvents) Debug.Log($"[IKStrokeController] EventB loop completed ({(isLeft ? "L" : "R")})", this);
            }
        }
        else
        {
            if (state.isStroking)
            {
                TriggerInterrupt(isLeft, ref state, restWorld);
            }

            bool shouldHoldStableRest =
                holdRelativeToStableWhenMovingStraight &&
                noOuterNow &&
                IsCharacterMoving() &&
                IsLowTurning();

            if (shouldHoldStableRest && TryGetStableRestHoldTarget(isLeft, ref state, restWorld, out Vector3 holdTarget))
            {
                // In straight-motion hold mode, force end of interrupt return and keep stable-relative rest offset.
                state.isInterruptReturning = false;
                state.interruptReturnT = 0f;
                MoveTargetTowards(ref state, ikTarget, holdTarget, Mathf.Max(returnToRestSharpness, idleRestHoldSharpness), dt);
                return;
            }
            else
            {
                state.hasStraightMotionHoldOffset = false;
            }

            MoveInterruptReturnOrRest(isLeft, dt, ref state, ikTarget, restWorld, noOuterNow);
        }
    }

    private void StartStroke(bool isLeft, ref SideState state)
    {
        state.isStroking = true;
        state.progress01 = 0f;
        state.currentAngleDeg = 0f;
        state.accumulatedPhaseDeg = 0f;
        state.directionConsistent = true;
        state.hasPrevLocalPose = false;
        state.stallTimer = 0f;
        state.lastAccumulatedPhaseDeg = 0f;
        state.isInterruptReturning = false;
        state.interruptReturnT = 0f;
        state.hasInterruptReturnLocal = false;
        // Do not force-arm here; keep hysteresis state from previous loop.
        if (logEvents) Debug.Log($"[IKStrokeController] Stroke start ({(isLeft ? "L" : "R")})", this);
    }

    private void MoveTargetTowards(ref SideState state, Transform target, Vector3 desiredWorld, float sharpness, float dt)
    {
        if (TryGetStableFrame(out Vector3 stableCenter, out Quaternion stableBasis))
        {
            Vector3 desiredLocal = Quaternion.Inverse(stableBasis) * (desiredWorld - stableCenter);
            if (!state.hasCurrentTargetLocalStable)
            {
                state.currentTargetLocalStable = desiredLocal;
                state.hasCurrentTargetLocalStable = true;
            }
            else
            {
                float aLocal = ExpAlpha(sharpness, dt);
                state.currentTargetLocalStable = Vector3.Lerp(state.currentTargetLocalStable, desiredLocal, aLocal);
            }

            state.currentTargetWorld = stableCenter + stableBasis * state.currentTargetLocalStable;
            state.hasCurrentTarget = true;
        }
        else
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

    private bool TryGetStableFrame(out Vector3 center, out Quaternion basis)
    {
        center = Vector3.zero;
        basis = Quaternion.identity;
        if (!dynamicSource) return false;

        center = dynamicSource.StableCenter;
        basis = dynamicSource.StableBasis;
        return basis != Quaternion.identity;
    }

    private void SyncStateLocalFromWorld(ref SideState state, Vector3 worldPos)
    {
        if (!TryGetStableFrame(out Vector3 center, out Quaternion basis))
        {
            state.hasCurrentTargetLocalStable = false;
            return;
        }

        state.currentTargetLocalStable = Quaternion.Inverse(basis) * (worldPos - center);
        state.hasCurrentTargetLocalStable = true;
    }

    private bool TryGetSignedAngleDeg(bool isLeft, out float signedAngleDeg)
    {
        signedAngleDeg = 0f;
        if (!dynamicSource) return false;

        Transform shoulder = isLeft ? dynamicSource.leftShoulder : dynamicSource.rightShoulder;
        if (!shoulder) return false;

        Vector3 lag = isLeft ? dynamicSource.WorldLeftPoint : dynamicSource.WorldRightPoint;
        Vector3 dir = lag - shoulder.position;
        if (dir.sqrMagnitude < 1e-10f) return false;
        Vector3 dirN = dir.normalized;

        // Keep angle definition consistent with DynamicIndirectIk_V2::UpdateAnchor angleL:
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
        signedAngleDeg = unsignedAngle * sign;
        return true;
    }

    private bool TryGetSpinDegPerSec(bool isLeft, out float projectedSpinDegPerSec, out float totalAngularDegPerSec)
    {
        projectedSpinDegPerSec = 0f;
        totalAngularDegPerSec = 0f;
        if (!dynamicSource) return false;

        Vector3 angVel = dynamicSource.CurrentAngularVel;
        totalAngularDegPerSec = angVel.magnitude * Mathf.Rad2Deg;

        Quaternion basis = dynamicSource.StableBasis;
        Vector3 spinAxis = basis * Vector3.forward;
        if (spinAxis.sqrMagnitude < 1e-10f) spinAxis = basis * Vector3.up;
        if (spinAxis.sqrMagnitude < 1e-10f) spinAxis = Vector3.up;
        spinAxis.Normalize();

        float spin = Vector3.Dot(angVel, spinAxis) * Mathf.Rad2Deg;

        // Mirror sign on right side to keep comparable convention between sides.
        projectedSpinDegPerSec = isLeft ? spin : -spin;
        return totalAngularDegPerSec > 1e-6f;
    }

    private bool EvaluateDirectionConsistency(bool isLeft, ref SideState state)
    {
        if (!useTrajectoryDirectionConsistencyGate) return true;
        if (!trajectorySource || !dynamicSource) return true;

        Vector3 worldFront = isLeft ? trajectorySource.LeftSolvedFront : trajectorySource.RightSolvedFront;
        Vector3 worldUp = isLeft ? trajectorySource.LeftSolvedUp : trajectorySource.RightSolvedUp;
        if (worldFront.sqrMagnitude < 1e-10f || worldUp.sqrMagnitude < 1e-10f) return state.directionConsistent;

        Quaternion stable = dynamicSource.StableBasis;
        if (stable == Quaternion.identity) return state.directionConsistent;
        Quaternion invStable = Quaternion.Inverse(stable);
        Vector3 localFront = (invStable * worldFront).normalized;
        Vector3 localUp = (invStable * worldUp).normalized;
        if (localFront.sqrMagnitude < 1e-10f || localUp.sqrMagnitude < 1e-10f) return state.directionConsistent;

        if (!state.hasPrevLocalPose)
        {
            state.prevLocalFront = localFront;
            state.prevLocalUp = localUp;
            state.hasPrevLocalPose = true;
            state.directionConsistent = true;
            return true;
        }

        float enter = Mathf.Clamp(consistencyEnterDot, -1f, 1f);
        float exit = Mathf.Clamp(consistencyExitDot, -1f, 1f);
        if (exit > enter) exit = enter;

        float dotF = Vector3.Dot(state.prevLocalFront, localFront);
        float dotU = Vector3.Dot(state.prevLocalUp, localUp);
        bool stableNow = state.directionConsistent ? (dotF >= exit && dotU >= exit) : (dotF >= enter && dotU >= enter);
        state.directionConsistent = stableNow;
        state.prevLocalFront = localFront;
        state.prevLocalUp = localUp;
        return stableNow;
    }

    private bool IsCharacterMoving()
    {
        if (movementSource != null)
        {
            return movementSource.GetSpeed() >= Mathf.Max(0f, movingSpeedThreshold);
        }

        // Fallback from Dynamic angular source owner rigidbody if available.
        if (dynamicSource)
        {
            Rigidbody rb = dynamicSource.GetComponent<Rigidbody>();
            if (rb) return rb.velocity.magnitude >= Mathf.Max(0f, movingSpeedThreshold);
        }
        return false;
    }

    private bool IsLowTurning()
    {
        if (!dynamicSource) return true;
        float angDegPerSec = dynamicSource.CurrentAngularVel.magnitude * Mathf.Rad2Deg;
        return angDegPerSec <= Mathf.Max(0f, lowTurnDegPerSecThreshold);
    }

    private bool TryGetStableRestHoldTarget(bool isLeft, ref SideState state, Vector3 restWorld, out Vector3 holdTarget)
    {
        holdTarget = restWorld;
        if (!dynamicSource) return false;
        Quaternion stable = dynamicSource.StableBasis;
        if (stable == Quaternion.identity) return false;
        Vector3 center = dynamicSource.StableCenter;
        Quaternion inv = Quaternion.Inverse(stable);

        if (!state.hasStraightMotionHoldOffset)
        {
            state.straightMotionHoldLocalOffset = inv * (restWorld - center);
            state.hasStraightMotionHoldOffset = true;
        }

        holdTarget = center + stable * state.straightMotionHoldLocalOffset;
        return true;
    }

    private void TriggerInterrupt(bool isLeft, ref SideState state, Vector3 restWorld)
    {
        // Snapshot before state reset; interruption return should start from current position.
        Vector3 targetBeforeReset = state.currentTargetWorld;

        // Event A: side changed/released (or stalled) before one-loop completion.
        state.isStroking = false;
        state.eventBArmed = true;
        state.directionConsistent = true;
        state.hasPrevLocalPose = false;
        state.progress01 = 0f;
        state.currentAngleDeg = 0f;
        state.accumulatedPhaseDeg = 0f;
        state.stallTimer = 0f;
        state.lastAccumulatedPhaseDeg = 0f;
        if (isLeft) leftCurrentAngleDeg = 0f;
        else rightCurrentAngleDeg = 0f;
        if (isLeft) leftAccumulatedPhaseDeg = 0f;
        else rightAccumulatedPhaseDeg = 0f;
        if (isLeft) leftDirectionConsistent = true;
        else rightDirectionConsistent = true;

        if (useBezierReturnOnInterrupt)
        {
            state.isInterruptReturning = true;
            state.interruptReturnT = 0f;
            state.interruptReturnStart = targetBeforeReset;
            state.interruptReturnEnd = restWorld;
            Vector3 up = dynamicSource ? (dynamicSource.StableBasis * Vector3.up) : Vector3.up;
            if (up.sqrMagnitude < 1e-10f) up = Vector3.up;
            up.Normalize();
            Vector3 forward = dynamicSource ? (dynamicSource.StableBasis * Vector3.forward) : transform.forward;
            if (forward.sqrMagnitude < 1e-10f) forward = Vector3.forward;
            forward.Normalize();
            state.interruptReturnControl =
                0.5f * (state.interruptReturnStart + state.interruptReturnEnd) +
                up * interruptReturnUpOffset +
                forward * interruptReturnForwardOffset;

            // Store bezier points in StableBasis local space so high-speed body translation
            // won't drag the return path backward in world space.
            if (TryGetStableFrame(out Vector3 stableCenter, out Quaternion stableBasis))
            {
                Quaternion inv = Quaternion.Inverse(stableBasis);
                state.interruptReturnStartLocal = inv * (state.interruptReturnStart - stableCenter);
                state.interruptReturnControlLocal = inv * (state.interruptReturnControl - stableCenter);
                state.interruptReturnEndLocal = inv * (state.interruptReturnEnd - stableCenter);
                state.hasInterruptReturnLocal = true;
            }
            else
            {
                state.hasInterruptReturnLocal = false;
            }

            state.interruptReturnApproxLength = EstimateQuadraticBezierLength(
                state.interruptReturnStart,
                state.interruptReturnControl,
                state.interruptReturnEnd);

            if (logEvents)
            {
                Debug.Log(
                    $"[IKStrokeController] InterruptBezierReturn ({(isLeft ? "L" : "R")}): " +
                    $"speed={interruptReturnSpeed:F3}, length={state.interruptReturnApproxLength:F3}, " +
                    $"upOffset={interruptReturnUpOffset:F3}, forwardOffset={interruptReturnForwardOffset:F3}",
                    this);
            }
        }
        else
        {
            state.isInterruptReturning = false;
            state.interruptReturnT = 0f;
            state.hasInterruptReturnLocal = false;
        }

        OnEventAInterrupted?.Invoke(isLeft);
        if (logEvents) Debug.Log($"[IKStrokeController] EventA interrupted ({(isLeft ? "L" : "R")})", this);
    }

    private void MoveInterruptReturnOrRest(bool isLeft, float dt, ref SideState state, Transform ikTarget, Vector3 restWorld, bool noOuterNow)
    {
        if (state.isInterruptReturning)
        {
            float speed = Mathf.Max(0.01f, interruptReturnSpeed);
            float length = Mathf.Max(1e-4f, state.interruptReturnApproxLength);
            state.interruptReturnT = Mathf.Clamp01(state.interruptReturnT + (speed * dt) / length);
            float t = state.interruptReturnT;
            float te = t * t * (3f - 2f * t);

            if (state.hasInterruptReturnLocal && TryGetStableFrame(out Vector3 stableCenter, out Quaternion stableBasis))
            {
                Vector3 aL = Vector3.Lerp(state.interruptReturnStartLocal, state.interruptReturnControlLocal, te);
                Vector3 bL = Vector3.Lerp(state.interruptReturnControlLocal, state.interruptReturnEndLocal, te);
                Vector3 pL = Vector3.Lerp(aL, bL, te);
                state.currentTargetWorld = stableCenter + stableBasis * pL;
            }
            else
            {
                Vector3 a = Vector3.Lerp(state.interruptReturnStart, state.interruptReturnControl, te);
                Vector3 b = Vector3.Lerp(state.interruptReturnControl, state.interruptReturnEnd, te);
                state.currentTargetWorld = Vector3.Lerp(a, b, te);
            }

            if (ikTarget) ikTarget.position = state.currentTargetWorld;
            if (state.interruptReturnT >= 0.999f)
            {
                state.isInterruptReturning = false;
                state.hasInterruptReturnLocal = false;
                state.currentTargetWorld = restWorld;
                if (ikTarget) ikTarget.position = restWorld;
                SyncStateLocalFromWorld(ref state, restWorld);
            }
            return;
        }

        float holdSharpness = returnToRestSharpness;
        if (pinTargetsToRestWhenNoOuter && noOuterNow)
        {
            holdSharpness = Mathf.Max(returnToRestSharpness, idleRestHoldSharpness);
        }

        MoveTargetTowards(ref state, ikTarget, restWorld, holdSharpness, dt);

        if (pinTargetsToRestWhenNoOuter && noOuterNow)
        {
            float snapDist = Mathf.Max(0f, idleRestSnapDistance);
            if ((state.currentTargetWorld - restWorld).sqrMagnitude <= snapDist * snapDist)
            {
                state.currentTargetWorld = restWorld;
                if (ikTarget) ikTarget.position = restWorld;
                SyncStateLocalFromWorld(ref state, restWorld);
            }
        }
    }

    private void OnDrawGizmos()
    {
        if (!drawDebug) return;

        Gizmos.color = debugLeftColor;
        if (leftLimbIkTarget) Gizmos.DrawSphere(leftLimbIkTarget.position, debugSphereRadius);

        Gizmos.color = debugRightColor;
        if (rightLimbIkTarget) Gizmos.DrawSphere(rightLimbIkTarget.position, debugSphereRadius);

        if (drawInterruptReturnTrajectory)
        {
            DrawInterruptReturnGizmo(true, leftState, interruptReturnLeftColor);
            DrawInterruptReturnGizmo(false, rightState, interruptReturnRightColor);
        }
    }

    private void DrawInterruptReturnGizmo(bool isLeft, SideState state, Color col)
    {
        if (!state.isInterruptReturning) return;
        int seg = Mathf.Max(8, interruptReturnGizmoSegments);
        Gizmos.color = col;

        Vector3 prev = EvalInterruptReturnPoint(state, Mathf.Clamp01(state.interruptReturnT));
        float startT = Mathf.Clamp01(state.interruptReturnT);
        for (int i = 1; i <= seg; i++)
        {
            float t = Mathf.Lerp(startT, 1f, i / (float)seg);
            Vector3 p = EvalInterruptReturnPoint(state, t);
            Gizmos.DrawLine(prev, p);
            prev = p;
        }

        Gizmos.DrawSphere(state.interruptReturnStart, debugSphereRadius * 0.8f);
        Gizmos.DrawSphere(state.interruptReturnEnd, debugSphereRadius * 0.8f);
    }

    private Vector3 EvalInterruptReturnPoint(SideState state, float normalizedT)
    {
        float te = normalizedT * normalizedT * (3f - 2f * normalizedT);
        Vector3 a = Vector3.Lerp(state.interruptReturnStart, state.interruptReturnControl, te);
        Vector3 b = Vector3.Lerp(state.interruptReturnControl, state.interruptReturnEnd, te);
        return Vector3.Lerp(a, b, te);
    }

    private float EstimateQuadraticBezierLength(Vector3 start, Vector3 control, Vector3 end)
    {
        const int seg = 12;
        float len = 0f;
        Vector3 prev = start;
        for (int i = 1; i <= seg; i++)
        {
            float t = i / (float)seg;
            float te = t * t * (3f - 2f * t);
            Vector3 a = Vector3.Lerp(start, control, te);
            Vector3 b = Vector3.Lerp(control, end, te);
            Vector3 p = Vector3.Lerp(a, b, te);
            len += Vector3.Distance(prev, p);
            prev = p;
        }
        return Mathf.Max(1e-4f, len);
    }
}
