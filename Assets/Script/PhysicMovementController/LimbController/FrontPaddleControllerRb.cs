using UnityEngine;

[DefaultExecutionOrder(250)]
public class FrontPaddleControllerRb : MonoBehaviour
{
    private enum Side { Left = 0, Right = 1 }
    private enum PaddleState { Idle, Prepare, Power, Recover, InterruptRecover }

    [System.Serializable]
    private struct SideRuntime
    {
        public PaddleState state;
        public float stateTime;

        public float theta;      // ellipse phase (rad)
        public float strength;   // 0..1
        public bool locked;      // while paddling, keep target driven

        // per-side cooldown between strokes
        public float cooldown;

        // Interrupt Recover Bezier
        public Vector3 interruptP0;
        public Vector3 interruptC;
        public Vector3 interruptP1;

        public float interruptTimer; // accum while locked (for flip)
    }

    [Header("Refs")]
    [SerializeField] private Transform leftTarget;
    [SerializeField] private Transform rightTarget;

    [Tooltip("Optional: used for gizmos only")]
    [SerializeField] private Transform leftLimbRoot;
    [SerializeField] private Transform rightLimbRoot;

    [Header("Optional Movement Controller RB (for intent/carrot direction)")]
    [SerializeField] private MovementControllerRB movement;

    [Header("Rest (local space of this controller transform)")]
    [SerializeField] private Vector3 leftRestLocal;
    [SerializeField] private Vector3 rightRestLocal;
    [SerializeField] private bool captureRestOnEnable = true;

    [Header("Turning Signal (robust at idle)")]
    [Tooltip("Angle error (deg) that maps to maxTurnOmegaDeg.")]
    [SerializeField] private float angleForFullOmega = 60f;

    [Tooltip("Max omega used by the paddle trigger/strength (deg/s).")]
    [SerializeField] private float maxTurnOmegaDeg = 180f;

    [Tooltip("Ignore tiny heading error to avoid idle jitter.")]
    [SerializeField] private float angleDeadzoneDeg = 1.5f;

    [Tooltip("Low-pass accel for omega smoothing (bigger = snappier).")]
    [SerializeField] private float omegaAccelDeg = 720f;

    [Header("Optional Rigidbody Source")]
    [Tooltip("If set, used to read angular velocity to continue paddling while the body is still rotating.")]
    [SerializeField] private Rigidbody body;

    [Header("Start/Stop Thresholds")]
    [Tooltip("Start paddling when |omega| >= this (deg/s).")]
    [SerializeField] private float omegaStartThreshold = 35f;

    [Tooltip("Stop paddling when |omega| <= this (deg/s).")]
    [SerializeField] private float omegaStopThreshold = 15f;

    [Tooltip("Max omega used to normalize strength (deg/s).")]
    [SerializeField] private float omegaMaxForStrength = 140f;

    [Header("Require Actual Movement (optional, fixes 'idle paddling')")]
    [SerializeField] private bool requireTranslationSpeed = true;

    [Tooltip("If requireTranslationSpeed, paddling is disabled unless speed >= this (m/s).")]
    [SerializeField] private float minSpeedToPaddle = 0.05f;

    [Header("Stroke Interval / Repetition")]
    [Tooltip("Minimum time between strokes on the same side (sec).")]
    [SerializeField] private float strokeInterval = 0.28f;

    [Tooltip("If true, while turning strongly we keep repeating strokes with the above interval. If false, one stroke per 'want' rising edge.")]
    [SerializeField] private bool repeatWhileTurning = true;

    [Header("Ellipse Shape (at full strength)")]
    [SerializeField] private float outwardRadiusFull = 0.18f;
    [SerializeField] private float backwardRadiusFull = 0.12f;

    [Tooltip("Downward bias magnitude (meters). Only applied during stroke (not at rest).")]
    [SerializeField] private float downBiasFull = 0.05f;

    [SerializeField] private float extraLiftAtFull = 0.03f;

    [Header("Ellipse Plane (WATER PLANE)")]
    [Tooltip("If true, ellipse is forced parallel to water surface using waterNormalWorld.")]
    [SerializeField] private bool forceWaterPlane = true;

    [Tooltip("Water surface normal in WORLD space. Default = (0,1,0).")]
    [SerializeField] private Vector3 waterNormalWorld = Vector3.up;

    [Tooltip("If true, swap outward directions (fixes left hand going the wrong way).")]
    [SerializeField] private bool invertOutward = false;

    [Tooltip("If true, the stroke starts by reaching toward the head (forward) before pulling back. (Flips ellipse travel direction along forward/back axis.)")]
    [SerializeField] private bool startTowardHead = true;

    [Header("Stroke Timing / Phase")]
    [SerializeField] private float prepareTime = 0.12f;

    [Tooltip("Duration of the powered stroke segment (sec).")]
    [SerializeField] private float powerTime = 0.32f;

    [Tooltip("Recover duration (sec). In this version Recover goes straight to rest (not along the ellipse).")]
    [SerializeField] private float recoverTime = 0.18f;

    [Tooltip("Arc (rad) during prepare: 0 -> prepareArcRad")]
    [SerializeField] private float prepareArcRad = 0.65f;

    [Tooltip("End arc (rad) during power. Set to 2π for a full ellipse loop.")]
    [SerializeField] private float powerArcRad = Mathf.PI * 2f;

    [Header("Mutual Exclusion")]
    [SerializeField] private bool mutualExclusion = true;

    [Header("Interrupt Recover (when turn direction flips)")]
    [SerializeField] private bool enableInterruptRecover = true;

    [Tooltip("If turn direction flips within this time during a stroke, interrupt and recover.")]
    [SerializeField] private float interruptWindow = 0.22f;

    [Tooltip("Bezier interrupt recover duration (sec).")]
    [SerializeField] private float interruptRecoverTime = 0.18f;

    [Tooltip("If true, use bezier to go back to rest on interrupt. Otherwise just switch to Recover.")]
    [SerializeField] private bool useBezierInterruptRecover = true;

    [Tooltip("Bezier control offsets (in world basis of ellipse): out, up, back.")]
    [SerializeField] private float interruptCtrlOut = 0.05f;
    [SerializeField] private float interruptCtrlUp = 0.03f;
    [SerializeField] private float interruptCtrlBack = 0.03f;

    [Header("Target Smoothing")]
    [Tooltip("Lerp speed for target transforms (bigger = snappier).")]
    [SerializeField] private float targetLerpSpeed = 16f;

    [Tooltip("Unlock when target (in CHEST local space) is within this distance of restLocal.")]
    [SerializeField] private float restEpsilonLocal = 0.015f;

    [Tooltip("If the recovery time has elapsed but the target never gets within restEpsilonLocal (due to smoothing / moving rest), we will snap to rest after this extra grace time to avoid getting stuck locked.")]
    [SerializeField] private float unlockSnapGraceTime = 0.12f;

    [Header("Gizmos / Visualization")]
    [SerializeField] private bool drawGizmos = true;
    [SerializeField] private bool drawEllipses = true;
    [Range(8, 128)] [SerializeField] private int ellipseSegments = 36;

    [SerializeField] private bool drawMovingPoints = true;
    [SerializeField] private bool drawRootToTargetLines = true;
    [SerializeField] private float gizmoSphereRadius = 0.025f;

    // ===== Runtime =====
    private SideRuntime _L, _R;
    private int _activeSide = -1; // -1 none, 0 left, 1 right

    private Vector3 _desiredWorldL, _desiredWorldR;
    private Vector3 _desiredPhasePointL, _desiredPhasePointR;

    private float _omegaDeg;
    private bool _prevWantLeft, _prevWantRight;

    // simple local speed estimate (to suppress idle paddling even if carrot jitters)
    private Vector3 _prevPos;
    private float _speed;
    private Rigidbody _rb;

    private void OnEnable()
    {
        if (captureRestOnEnable)
        {
            if (leftTarget != null) leftRestLocal = transform.InverseTransformPoint(leftTarget.position);
            if (rightTarget != null) rightRestLocal = transform.InverseTransformPoint(rightTarget.position);
        }

        _L = default;
        _R = default;
        _L.state = PaddleState.Idle;
        _R.state = PaddleState.Idle;
        _activeSide = -1;

        _prevWantLeft = _prevWantRight = false;

        _prevPos = transform.position;
        _speed = 0f;

        // cache rigidbody once to avoid per-frame GetComponent
        if (body != null) _rb = body;
        else if (movement != null) _rb = movement.GetComponent<Rigidbody>();
    }

    private void Update()
    {
        float dt = Mathf.Max(0.0001f, Time.deltaTime);

        // --- compute translation speed (optional gate) ---
        if (movement != null)
        {
            _speed = movement.GetSpeed();
            _prevPos = transform.position;
        }
        else
        {
            Vector3 dp = transform.position - _prevPos;
            _speed = dp.magnitude / dt;
            _prevPos = transform.position;
        }

        // --- compute omega from movement intent (stable even at idle) ---
        float angleError = 0f;

        if (movement != null)
        {
            Vector3 intent = movement.GetWorldDir();
            intent.y = 0f;

            if (intent.sqrMagnitude <= 1e-6f)
            {
                // Fallback to carrot direction (if any)
                Vector3 carrotW = movement.GetCarrotWorld();
                intent = carrotW - transform.position;
                intent.y = 0f;
            }

            if (intent.sqrMagnitude > 1e-6f)
            {
                Vector3 fwd = transform.forward; fwd.y = 0f;
                if (fwd.sqrMagnitude > 1e-6f) fwd.Normalize();
                intent.Normalize();

                angleError = Vector3.SignedAngle(fwd, intent, Vector3.up); // deg
            }
        }

        if (Mathf.Abs(angleError) < angleDeadzoneDeg) angleError = 0f;

        float desiredOmega =
            Mathf.Clamp(angleError / Mathf.Max(1f, angleForFullOmega), -1f, 1f) * maxTurnOmegaDeg;

        // Also consider current rigidbody angular velocity so paddling persists while still spinning
        float physicsOmega = 0f;
        if (_rb != null)
        {
            physicsOmega = _rb.angularVelocity.y * Mathf.Rad2Deg;

            // ignore tiny jitter; clamp to expected range
            if (Mathf.Abs(physicsOmega) < omegaStopThreshold) physicsOmega = 0f;
            physicsOmega = Mathf.Clamp(physicsOmega, -maxTurnOmegaDeg, maxTurnOmegaDeg);
        }

        float finalTargetOmega = desiredOmega;
        if (Mathf.Abs(physicsOmega) > Mathf.Abs(desiredOmega))
            finalTargetOmega = physicsOmega;

        float aOmega = 1f - Mathf.Exp(-Mathf.Max(0.01f, omegaAccelDeg) * dt);
        _omegaDeg = Mathf.Lerp(_omegaDeg, finalTargetOmega, aOmega);

        float omegaAbs = Mathf.Abs(_omegaDeg);

        // Gate at low translation speed (prevents "idle paddling")
        bool canPaddleNow = !requireTranslationSpeed || _speed >= minSpeedToPaddle;

        bool hasSteerInput = (movement == null) || movement.IsDragging();

        bool want = hasSteerInput && canPaddleNow && (omegaAbs >= omegaStartThreshold);

        // Decide which side should paddle
        // sign >0 => turning right => left paddles; sign <0 => turning left => right paddles
        Side desiredSide = (_omegaDeg >= 0f) ? Side.Left : Side.Right;
        float strength01 = Mathf.Clamp01(omegaAbs / Mathf.Max(1f, omegaMaxForStrength));

        bool wantLeft = want && desiredSide == Side.Left;
        bool wantRight = want && desiredSide == Side.Right;

        // Interrupt when direction flips mid-stroke (mutual exclusion)
        if (enableInterruptRecover && mutualExclusion && _activeSide != -1)
        {
            Side active = (Side)_activeSide;
            Side other = (active == Side.Left) ? Side.Right : Side.Left;

            bool flip = want && desiredSide == other;

            if (flip)
            {
                ref SideRuntime sAct = ref (active == Side.Left ? ref _L : ref _R);

                if (sAct.locked && sAct.interruptTimer <= interruptWindow)
                {
                    BeginInterruptRecover(active);
                    if (_activeSide == (int)active) _activeSide = -1;
                }
            }
        }

        // cooldown tick
        _L.cooldown = Mathf.Max(0f, _L.cooldown - dt);
        _R.cooldown = Mathf.Max(0f, _R.cooldown - dt);

        // Update each side runtime
        StepSide(Side.Left, dt, wantLeft, strength01);
        StepSide(Side.Right, dt, wantRight, strength01);

        // Apply target transforms
        ApplyTargets(dt);

        _prevWantLeft = wantLeft;
        _prevWantRight = wantRight;
    }

    private void StepSide(Side side, float dt, bool wantThis, float strength01)
    {
        ref SideRuntime s = ref (side == Side.Left ? ref _L : ref _R);

        bool isThisActive = (_activeSide == (int)side);
        s.stateTime += dt;

        // Interrupt timer increments while locked (i.e. in stroke)
        if (s.locked) s.interruptTimer += dt;
        else s.interruptTimer = 0f;

        // repetition control
        bool wantRising = (side == Side.Left) ? (wantThis && !_prevWantLeft) : (wantThis && !_prevWantRight);
        bool allowStart = repeatWhileTurning ? wantThis : wantRising;

        switch (s.state)
        {
            case PaddleState.Idle:
            {
                // keep targets at rest while idle
                s.theta = 0f;
                s.strength = 0f;

                if (!allowStart) break;
                if (s.cooldown > 0f) break;

                if (mutualExclusion && _activeSide != -1 && _activeSide != (int)side) break;

                s.locked = true;
                s.state = PaddleState.Prepare;
                s.stateTime = 0f;
                s.theta = 0f;
                s.strength = Mathf.Clamp01(strength01);
                s.interruptTimer = 0f;

                if (mutualExclusion) _activeSide = (int)side;
                break;
            }

            case PaddleState.Prepare:
            {
                if (!isThisActive && mutualExclusion) return;

                // If user stops wanting mid-prepare, go recover
                if (!wantThis || Mathf.Abs(_omegaDeg) <= omegaStopThreshold)
                {
                    s.state = PaddleState.Recover;
                    s.stateTime = 0f;
                    break;
                }

                s.strength = Mathf.Max(s.strength, Mathf.Clamp01(strength01));

                float t = Mathf.Clamp01(s.stateTime / Mathf.Max(0.01f, prepareTime));
                float thetaEnd = Mathf.Clamp(prepareArcRad, 0.01f, Mathf.PI * 0.95f);
                s.theta = Mathf.Lerp(0f, thetaEnd, t);
                Debug.Log("theta: " + s.theta);

                if (t >= 1f)
                {
                    s.state = PaddleState.Power;
                    s.stateTime = 0f;
                }
                break;
            }

            case PaddleState.Power:
            {
                if (!isThisActive && mutualExclusion) return;

                s.strength = Mathf.Max(s.strength, Mathf.Clamp01(strength01));

                float t = Mathf.Clamp01(s.stateTime / Mathf.Max(0.01f, powerTime));
                float thetaStart = Mathf.Clamp(prepareArcRad, 0.01f, Mathf.PI * 0.95f);
                float thetaEnd = Mathf.Clamp(powerArcRad, thetaStart + 0.05f, Mathf.PI * 2f);

                s.theta = Mathf.Lerp(thetaStart, thetaEnd, t);

                // Power segment ends when finished OR want drops below stop threshold
                if (t >= 1f || !wantThis || Mathf.Abs(_omegaDeg) <= omegaStopThreshold)
                {
                    s.state = PaddleState.Recover;
                    s.stateTime = 0f;
                }
                break;
            }

            case PaddleState.Recover:
            {
                if (!isThisActive && mutualExclusion) return;

                // In this version, Recover is a direct return to rest (not tracing ellipse backwards).
                // Unlock is handled in FinalizeUnlockIfReturned.
                if (s.stateTime >= recoverTime)
                {
                    // keep theta at 0 so desired becomes rest immediately
                    s.theta = 0f;
                }
                break;
            }

            case PaddleState.InterruptRecover:
            {
                if (!isThisActive && mutualExclusion) return;
                break;
            }
        }
    }

    private void BeginInterruptRecover(Side side)
    {
        ref SideRuntime s = ref (side == Side.Left ? ref _L : ref _R);
        if (s.state == PaddleState.Idle || s.state == PaddleState.InterruptRecover) return;

        Vector3 restW = GetRestWorld(side);

        // Use current desired ellipse point as P0 (not the current target) to avoid sudden jumps
        Vector3 curW = GetEllipseWorld(side, s.theta, s.strength, out _, out _, out _);

        s.interruptP0 = curW;
        s.interruptP1 = restW;

        GetEllipseBasisWorld(side, out Vector3 outW, out Vector3 backW, out Vector3 upW);
        s.interruptC = curW + outW * interruptCtrlOut + upW * interruptCtrlUp + backW * interruptCtrlBack;

        s.state = PaddleState.InterruptRecover;
        s.stateTime = 0f;
        s.theta = 0f;
    }

    private void ApplyTargets(float dt)
    {
        _desiredWorldL = ComputeDesiredWorld(Side.Left, ref _L, out _desiredPhasePointL);
        _desiredWorldR = ComputeDesiredWorld(Side.Right, ref _R, out _desiredPhasePointR);

        float a = 1f - Mathf.Exp(-Mathf.Max(0.01f, targetLerpSpeed) * dt);

        if (leftTarget != null) leftTarget.position = Vector3.Lerp(leftTarget.position, _desiredWorldL, a);
        if (rightTarget != null) rightTarget.position = Vector3.Lerp(rightTarget.position, _desiredWorldR, a);

        if (leftTarget != null) FinalizeUnlockIfReturned(Side.Left, ref _L, leftTarget);
        if (rightTarget != null) FinalizeUnlockIfReturned(Side.Right, ref _R, rightTarget);
    }

    // IMPORTANT: unlock in CHEST local space to avoid restWorld moving while rotating
    private void FinalizeUnlockIfReturned(Side side, ref SideRuntime s, Transform targetTf)
    {
        if (!s.locked) return;

        bool eligible = (s.state == PaddleState.Recover) || (s.state == PaddleState.InterruptRecover);
        if (!eligible) return;

        bool timeDone =
            (s.state == PaddleState.Recover && s.stateTime >= recoverTime) ||
            (s.state == PaddleState.InterruptRecover && s.stateTime >= interruptRecoverTime);

        if (!timeDone) return;

        Vector3 restLocal = (side == Side.Left) ? leftRestLocal : rightRestLocal;
        Vector3 curLocal = transform.InverseTransformPoint(targetTf.position);
        float d = Vector3.Distance(curLocal, restLocal);

        float eps = Mathf.Max(0.001f, restEpsilonLocal);
        float graceLimit =
            ((s.state == PaddleState.Recover) ? recoverTime : interruptRecoverTime)
            + Mathf.Max(0f, unlockSnapGraceTime);

        bool graceDone = s.stateTime >= graceLimit;

        if (d <= eps || graceDone)
        {
            // If we never quite reached rest due to smoothing, snap once recovery is definitely over.
            if (graceDone && d > eps)
                targetTf.position = transform.TransformPoint(restLocal);

            s.state = PaddleState.Idle;
            s.stateTime = 0f;
            s.theta = 0f;
            s.strength = 0f;
            s.locked = false;
            s.interruptTimer = 0f;

            // set cooldown so we get a controllable "stroke -> rest -> stroke" rhythm
            s.cooldown = Mathf.Max(0f, strokeInterval);

            if (mutualExclusion && _activeSide == (int)side)
                _activeSide = -1;
        }
    }

    private Vector3 ComputeDesiredWorld(Side side, ref SideRuntime s, out Vector3 phasePointWorld)
    {
        // Always go to rest when idle or recovering
        if (s.state == PaddleState.Idle || s.state == PaddleState.Recover)
        {
            phasePointWorld = GetRestWorld(side);
            return phasePointWorld;
        }

        if (s.state == PaddleState.InterruptRecover && useBezierInterruptRecover)
        {
            float t = Mathf.Clamp01(s.stateTime / Mathf.Max(0.01f, interruptRecoverTime));
            Vector3 p = Bezier2(s.interruptP0, s.interruptC, s.interruptP1, t);
            phasePointWorld = p;
            return p;
        }

        // Ellipse normal mode (Prepare/Power)
        Vector3 ellipseW = GetEllipseWorld(side, s.theta, s.strength,
            out float lift01, out Vector3 outW, out Vector3 upW);

        Vector3 desired = ellipseW + upW * (extraLiftAtFull * s.strength * lift01);

        phasePointWorld = ellipseW;
        return desired;
    }

    private Vector3 GetRestWorld(Side side)
    {
        Vector3 restLocal = (side == Side.Left) ? leftRestLocal : rightRestLocal;
        return transform.TransformPoint(restLocal);
    }

    /// <summary>
    /// Ellipse in WATER PLANE:
    ///   p = center + outW*(rOut*cos(thetaUse)) + backW*(rBack*sin(thetaUse)) + downBias
    /// Center is chosen so theta=0 hits restW.
    ///
    /// startTowardHead flips thetaUse -> -theta, which makes early movement go toward forward (head).
    /// </summary>
    private Vector3 GetEllipseWorld(
        Side side,
        float theta,
        float strength01,
        out float lift01,
        out Vector3 outW,
        out Vector3 upW)
    {
        GetEllipseBasisWorld(side, out outW, out Vector3 backW, out upW);

        float rOut = outwardRadiusFull * strength01;
        float rBack = backwardRadiusFull * strength01;

        Vector3 restW = GetRestWorld(side);

        // Center so theta=0 hits restW
        Vector3 center = restW - outW * rOut;

        // Bias envelope: 0 at theta=0 and theta=powerArcRad
        float thetaEnd = Mathf.Max(0.1f, powerArcRad);
        float u = Mathf.Clamp01(theta / thetaEnd);
        float water01 = 4f * u * (1f - u); // 0 -> 1 -> 0
        Vector3 downBias = -upW * (downBiasFull * strength01 * water01);

        float thetaUse = startTowardHead ? -theta : theta;

        Vector3 p =
            center
            + outW * (rOut * Mathf.Cos(thetaUse))
            + backW * (rBack * Mathf.Sin(thetaUse))
            + downBias;

        // lift01 peaks during (prepareArcRad -> powerArcRad)
        float thetaA = Mathf.Clamp(prepareArcRad, 0.01f, Mathf.PI * 0.95f);
        float thetaB = Mathf.Clamp(powerArcRad, thetaA + 0.05f, Mathf.PI * 2f);

        if (theta <= thetaA || theta >= thetaB) lift01 = 0f;
        else
        {
            float uu = Mathf.InverseLerp(thetaA, thetaB, theta);
            lift01 = 4f * uu * (1f - uu);
        }

        return p;
    }

    private void GetEllipseBasisWorld(Side side, out Vector3 outW, out Vector3 backW, out Vector3 upW)
    {
        // Water normal
        upW = forceWaterPlane ? waterNormalWorld : transform.up;
        if (upW.sqrMagnitude < 1e-6f) upW = Vector3.up;
        upW.Normalize();

        // Forward projected onto water plane
        Vector3 fwdW = transform.forward;
        fwdW = Vector3.ProjectOnPlane(fwdW, upW);
        if (fwdW.sqrMagnitude < 1e-6f) fwdW = Vector3.ProjectOnPlane(Vector3.forward, upW);
        fwdW.Normalize();

        Vector3 rightW = Vector3.Cross(upW, fwdW);
        if (rightW.sqrMagnitude < 1e-6f) rightW = Vector3.right;
        rightW.Normalize();

        // Outward: left outward = -right, right outward = +right, with optional invert
        bool inv = invertOutward;
        Vector3 outwardLeft = inv ? rightW : -rightW;
        Vector3 outwardRight = inv ? -rightW : rightW;

        outW = (side == Side.Left) ? outwardLeft : outwardRight;

        // NOTE: backW points toward tail, fwdW points toward head
        backW = -fwdW;
    }

    private static Vector3 Bezier2(Vector3 p0, Vector3 c, Vector3 p1, float t)
    {
        t = Mathf.Clamp01(t);
        float uu = 1f - t;
        return (uu * uu) * p0 + 2f * uu * t * c + (t * t) * p1;
    }

    // ===== Gizmos =====
    private void OnDrawGizmos()
    {
        if (!drawGizmos) return;

        if (drawRootToTargetLines)
        {
            if (leftLimbRoot != null && leftTarget != null)
            {
                Gizmos.DrawLine(leftLimbRoot.position, leftTarget.position);
                Gizmos.DrawWireSphere(leftTarget.position, gizmoSphereRadius);
            }

            if (rightLimbRoot != null && rightTarget != null)
            {
                Gizmos.DrawLine(rightLimbRoot.position, rightTarget.position);
                Gizmos.DrawWireSphere(rightTarget.position, gizmoSphereRadius);
            }
        }

        if (!drawEllipses && !drawMovingPoints) return;

        DrawSideViz(Side.Left, ref _L, leftTarget, _desiredPhasePointL);
        DrawSideViz(Side.Right, ref _R, rightTarget, _desiredPhasePointR);
    }

    private void DrawSideViz(Side side, ref SideRuntime s, Transform targetTf, Vector3 phasePointW)
    {
        if (targetTf == null) return;

        if (drawEllipses)
        {
            Gizmos.color = new Color(0.6f, 0.6f, 0.6f, 0.85f);
            DrawEllipse(side, 1f);

            float st = (s.state == PaddleState.Idle) ? 0f : Mathf.Clamp01(s.strength);
            if (st > 0.001f)
            {
                Gizmos.color = new Color(1f, 1f, 1f, 0.9f);
                DrawEllipse(side, st);
            }
        }

        if (drawMovingPoints)
        {
            Gizmos.color = new Color(0.2f, 0.9f, 1f, 0.95f);
            Gizmos.DrawSphere(phasePointW, gizmoSphereRadius * 0.85f);

            Gizmos.color = new Color(1f, 0.4f, 0.1f, 0.95f);
            Gizmos.DrawSphere(targetTf.position, gizmoSphereRadius);

            if (s.state == PaddleState.InterruptRecover && useBezierInterruptRecover)
            {
                Gizmos.color = new Color(1f, 0.92f, 0.2f, 0.9f);
                int seg = Mathf.Max(8, ellipseSegments / 2);
                Vector3 prev = s.interruptP0;
                for (int i = 1; i <= seg; i++)
                {
                    float t = i / (float)seg;
                    Vector3 p = Bezier2(s.interruptP0, s.interruptC, s.interruptP1, t);
                    Gizmos.DrawLine(prev, p);
                    prev = p;
                }
            }
        }
    }

    private void DrawEllipse(Side side, float strength01)
    {
        int seg = Mathf.Max(8, ellipseSegments);
        Vector3 prev = GetEllipseWorld(side, 0f, strength01, out _, out _, out _);

        for (int i = 1; i <= seg; i++)
        {
            float t = i / (float)seg;
            float theta = t * Mathf.PI * 2f;
            Vector3 p = GetEllipseWorld(side, theta, strength01, out _, out _, out _);
            Gizmos.DrawLine(prev, p);
            prev = p;
        }
    }
}
