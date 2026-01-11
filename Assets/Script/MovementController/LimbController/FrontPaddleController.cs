// FrontPaddleController.cs
// Optimized: outside-of-arc paddling using signed spine curvature (bendSign).
// Controls ONLY IK target positions (no IK solving here).
// Visualizes spine outside direction and target chasing.
//
// Attach this to CHEST / UpperSpine (stable trunk node).
// Assign: MovementController, left/right limb roots, left/right IK targets.
// Optional: spineChain (Chest -> ... -> Tail).

using UnityEngine;

[DefaultExecutionOrder(250)]
public class FrontPaddleController : MonoBehaviour
{
    public enum PaddleState { Idle, ExtendHold, Recover }

    [Header("References")]
    [SerializeField] private MovementController movement;

    [Tooltip("Left front limb root (shoulder/upper limb root)")]
    [SerializeField] private Transform leftLimbRoot;

    [Tooltip("Right front limb root (shoulder/upper limb root)")]
    [SerializeField] private Transform rightLimbRoot;

    [Tooltip("IK target transform for LEFT limb (read by your AdvancedTwoBoneIK)")]
    [SerializeField] private Transform leftTarget;

    [Tooltip("IK target transform for RIGHT limb")]
    [SerializeField] private Transform rightTarget;

    [Header("Spine (optional, Chest -> Tail order)")]
    [Tooltip("Used to compute bend amount + bend SIGN (outside direction). Do NOT include neck/head.")]
    [SerializeField] private Transform[] spineChain;

    [Header("Gecko-style Turning Signal")]
    [Tooltip("Max angular speed (deg/s) used as 'full turn'.")]
    [SerializeField] private float maxTurnOmegaDeg = 180f;

    [Tooltip("Angle (deg) that maps to full turn omega (bigger => slower response).")]
    [SerializeField] private float angleForFullOmega = 60f;

    [Tooltip("How fast omega can change (deg/s^2). Bigger = snappier.")]
    [SerializeField] private float omegaAccelDeg = 720f;

    [Tooltip("Start paddling when abs(omega) exceeds this (deg/s).")]
    [SerializeField] private float omegaStartThreshold = 35f;

    [Tooltip("Consider turn finished when abs(omega) drops below this (deg/s).")]
    [SerializeField] private float omegaStopThreshold = 15f;

    [Header("Stroke Timing")]
    [Tooltip("Max time we allow one continuous stroke before forcing recovery.")]
    [SerializeField] private float maxStrokeTime = 0.45f;

    [Tooltip("Recovery duration back to rest.")]
    [SerializeField] private float recoverTime = 0.20f;

    [Tooltip("When turning direction flips, wait briefly before allowing the other side to fire.")]
    [SerializeField] private float flipCooldown = 0.08f;

    [Header("Amplitude / Pose (Chest local space)")]
    [Tooltip("Target rest position in CHEST local space (Left). If zero and Auto Capture is on, captured at Start/OnEnable.")]
    [SerializeField] private Vector3 leftRestLocal;

    [Tooltip("Target rest position in CHEST local space (Right). If zero and Auto Capture is on, captured at Start/OnEnable.")]
    [SerializeField] private Vector3 rightRestLocal;

    [Tooltip("Auto capture restLocal from current target positions at Start/OnEnable.")]
    [SerializeField] private bool autoCaptureRestOnStart = true;

    [Header("Stroke Offsets")]
    [Tooltip("If TRUE: lateral direction comes from spine outside (world) => robust to axis mirroring. Recommended.")]
    [SerializeField] private bool useWorldOutsideOffset = true;

    [Tooltip("How far to push to the OUTSIDE (meters) at full stroke.")]
    [SerializeField] private float lateralOutAtFull = 0.18f;

    [Tooltip("How far to sweep backward (meters) at full stroke.")]
    [SerializeField] private float backwardAtFull = 0.10f;

    [Tooltip("How much to press downward (meters) at full stroke.")]
    [SerializeField] private float downwardAtFull = 0.05f;

    [Tooltip("Optional extra lift/open when turning strongly (meters along chest +Y).")]
    [SerializeField] private float extraLiftAtFull = 0.03f;

    [Tooltip("Fallback LOCAL offsets (used only when useWorldOutsideOffset = false).")]
    [SerializeField] private Vector3 leftStrokeOffsetLocal = new Vector3(-0.18f, -0.05f, -0.10f);
    [SerializeField] private Vector3 rightStrokeOffsetLocal = new Vector3(+0.18f, -0.05f, -0.10f);

    [Header("Strength Mixing")]
    [Range(0, 1)] [SerializeField] private float turnStrengthWeight = 1.0f;
    [Range(0, 1)] [SerializeField] private float bendStrengthWeight = 0.6f;

    [Tooltip("Normalize bend: absolute bend degrees that counts as 'full'.")]
    [SerializeField] private float bendForFullDeg = 25f;

    [Header("Smoothing")]
    [Tooltip("How quickly targets move toward desired pos.")]
    [SerializeField] private float targetLerpSpeed = 14f;

    [Header("Debug / Gizmos")]
    [SerializeField] private bool drawGizmos = true;
    [SerializeField] private float gizmoSphereRadius = 0.03f;
    [SerializeField] private float gizmoArrowLen = 0.35f;

    // Internal signals
    private float omegaDeg;                 // smoothed signed deg/s
    private float flipCooldownTimer;

    // Bend signals
    private float bendAbs01;                // 0..1
    private float bendSignedDeg;            // signed accumulated degrees
    private int bendSign;                   // -1/0/+1

    // State
    private PaddleState leftState = PaddleState.Idle;
    private PaddleState rightState = PaddleState.Idle;
    private float leftStateTime, rightStateTime;
    private float leftStrokeStrength, rightStrokeStrength;

    private bool restCaptured;

    void Start() => CaptureRestIfNeeded();
    void OnEnable() => CaptureRestIfNeeded();


    private void ForceIdle(
        ref PaddleState state,
        ref float stateTime,
        ref float strokeStrength)
    {
        state = PaddleState.Idle;
        stateTime = 0f;
        strokeStrength = 0f;
    }

    private void CaptureRestIfNeeded()
    {
        if (restCaptured) return;
        if (!autoCaptureRestOnStart) { restCaptured = true; return; }

        if (leftTarget != null)  leftRestLocal  = transform.InverseTransformPoint(leftTarget.position);
        if (rightTarget != null) rightRestLocal = transform.InverseTransformPoint(rightTarget.position);

        restCaptured = true;
    }

    void Update()
    {
        if (movement == null || leftTarget == null || rightTarget == null)
            return;

        float dt = Mathf.Max(Time.deltaTime, 1e-5f);

        // 1) Desired direction from carrot (stable)
        Vector3 carrot = movement.GetCarrotWorld();
        Vector3 toCarrot = carrot - transform.position;
        toCarrot.y = 0f;

        if (toCarrot.sqrMagnitude < 1e-6f)
        {
            omegaDeg = Mathf.MoveTowards(omegaDeg, 0f, omegaAccelDeg * dt);
            ComputeBendSignals();
            TickStateMachine(dt, omegaDeg, 0f);
            ApplyTargets(dt);
            return;
        }

        Vector3 desiredDir = toCarrot.normalized;

        // Current forward on XZ
        Vector3 fwd = transform.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
        fwd.Normalize();

        float angleError = Vector3.SignedAngle(fwd, desiredDir, Vector3.up); // deg, signed

        // 2) Gecko-style omega mapping + smoothing
        float turnStrength01 = Mathf.Clamp01(Mathf.Abs(angleError) / Mathf.Max(1f, angleForFullOmega));
        float targetOmega = Mathf.Sign(angleError) * (turnStrength01 * maxTurnOmegaDeg);
        omegaDeg = Mathf.MoveTowards(omegaDeg, targetOmega, omegaAccelDeg * dt);

        // 3) Spine bend signals (abs + sign)
        ComputeBendSignals();

        // 4) Mix strength (abs)
        float mixedStrength = Mathf.Clamp01(turnStrengthWeight * turnStrength01 + bendStrengthWeight * bendAbs01);

        TickStateMachine(dt, omegaDeg, mixedStrength);
        ApplyTargets(dt);
    }

    // Computes:
    // - bendAbs01: magnitude 0..1
    // - bendSignedDeg: signed degrees (accum)
    // - bendSign: -1/0/+1
    private void ComputeBendSignals()
    {
        bendAbs01 = 0f;
        bendSignedDeg = 0f;
        bendSign = 0;

        if (spineChain == null || spineChain.Length < 2)
            return;

        Vector3 prev = spineChain[0].forward;
        prev.y = 0f;
        if (prev.sqrMagnitude < 1e-6f) return;
        prev.Normalize();

        float accumAbs = 0f;
        float accumSigned = 0f;

        for (int i = 1; i < spineChain.Length; i++)
        {
            Vector3 cur = spineChain[i].forward;
            cur.y = 0f;
            if (cur.sqrMagnitude < 1e-6f) continue;
            cur.Normalize();

            float a = Vector3.SignedAngle(prev, cur, Vector3.up);
            accumSigned += a;
            accumAbs += Mathf.Abs(a);

            prev = cur;
        }

        bendSignedDeg = accumSigned;
        bendSign = Mathf.Abs(accumSigned) < 0.01f ? 0 : (accumSigned > 0f ? +1 : -1);
        bendAbs01 = Mathf.Clamp01(accumAbs / Mathf.Max(1f, bendForFullDeg));
    }

    private void TickStateMachine(float dt, float omegaSigned, float strength01)
    {
        if (flipCooldownTimer > 0f)
            flipCooldownTimer -= dt;

        float omegaAbs = Mathf.Abs(omegaSigned);
        bool needStroke = omegaAbs >= omegaStartThreshold;

        // Decide which side is OUTSIDE:
        // Use bendSign if available; fallback to omega sign if spineChain is missing or too small.
        int outsideSign = bendSign != 0 ? bendSign : (omegaSigned > 0f ? +1 : (omegaSigned < 0f ? -1 : 0));

        // Convention:
        // outsideSign > 0 => outside is "to the right of base forward"
        // => RIGHT limb strokes (acts as outside paddle)
        // If your rig feels reversed, flip this mapping by swapping wantRight/wantLeft below.
        bool wantRight = needStroke && outsideSign > 0 && flipCooldownTimer <= 0f;
        bool wantLeft  = needStroke && outsideSign < 0 && flipCooldownTimer <= 0f;


        // =========================
        // STRONG RUDDER MODE (mutual exclusion)
        // Only one side can be active at any time
        // =========================
        if (wantLeft)
        {
            // Left wants to stroke → kill right immediately
            ForceIdle(ref rightState, ref rightStateTime, ref rightStrokeStrength);
        }
        else if (wantRight)
        {
            // Right wants to stroke → kill left immediately
            ForceIdle(ref leftState, ref leftStateTime, ref leftStrokeStrength);
        }
        // Flip handling: if currently stroking and outside side changes, force recover and cooldown.
        bool leftActive = (leftState == PaddleState.ExtendHold);
        bool rightActive = (rightState == PaddleState.ExtendHold);

        if (leftActive && wantRight)
        {
            leftState = PaddleState.Recover;
            leftStateTime = 0f;
            flipCooldownTimer = flipCooldown;
        }

        if (rightActive && wantLeft)
        {
            rightState = PaddleState.Recover;
            rightStateTime = 0f;
            flipCooldownTimer = flipCooldown;
        }

        // Update each side
        StepOneSide(dt, wantLeft,  omegaAbs, strength01, ref leftState,  ref leftStateTime,  ref leftStrokeStrength);
        StepOneSide(dt, wantRight, omegaAbs, strength01, ref rightState, ref rightStateTime, ref rightStrokeStrength);
    }

    private void StepOneSide(
        float dt,
        bool want,
        float omegaAbs,
        float strength01,
        ref PaddleState state,
        ref float stateTime,
        ref float strokeStrength
    )
    {
        stateTime += dt;

        switch (state)
        {
            case PaddleState.Idle:
                strokeStrength = 0f;
                if (want)
                {
                    state = PaddleState.ExtendHold;
                    stateTime = 0f;
                    strokeStrength = Mathf.Clamp01(strength01);
                }
                break;

            case PaddleState.ExtendHold:
                strokeStrength = Mathf.Max(strokeStrength, Mathf.Clamp01(strength01));

                bool finishedTurn = omegaAbs <= omegaStopThreshold;
                bool timedOut = stateTime >= maxStrokeTime;

                if (!want || finishedTurn || timedOut)
                {
                    state = PaddleState.Recover;
                    stateTime = 0f;
                }
                break;

            case PaddleState.Recover:
                float t = Mathf.Clamp01(stateTime / Mathf.Max(0.01f, recoverTime));
                strokeStrength = Mathf.Lerp(strokeStrength, 0f, t);

                if (t >= 1f)
                {
                    strokeStrength = 0f;
                    state = PaddleState.Idle;
                    stateTime = 0f;
                }
                break;
        }
    }

    private void ApplyTargets(float dt)
    {
        // Desired positions in Chest local space
        Vector3 leftLocalDesired = leftRestLocal;
        Vector3 rightLocalDesired = rightRestLocal;

        if (useWorldOutsideOffset)
        {
            // Build offset in WORLD using outside direction, then convert to Chest local.
            Vector3 baseFwd = transform.forward;
            baseFwd.y = 0f;
            if (baseFwd.sqrMagnitude < 1e-6f) baseFwd = Vector3.forward;
            baseFwd.Normalize();

            // outside direction (world): right of baseFwd times outsideSign (bendSign preferred)
            int outsideSign = bendSign != 0 ? bendSign : (omegaDeg > 0f ? +1 : (omegaDeg < 0f ? -1 : 0));
            Vector3 outsideWorld = Vector3.Cross(Vector3.up, baseFwd).normalized * outsideSign;

            // World stroke vector
            Vector3 strokeWorld =
                outsideWorld * lateralOutAtFull
                + (-baseFwd) * backwardAtFull
                + (-Vector3.up) * downwardAtFull;

            // Convert to chest local as a direction (NOT point)
            Vector3 strokeLocal = transform.InverseTransformDirection(strokeWorld);

            leftLocalDesired += strokeLocal * leftStrokeStrength;
            rightLocalDesired += strokeLocal * rightStrokeStrength;

            leftLocalDesired.y += extraLiftAtFull * leftStrokeStrength;
            rightLocalDesired.y += extraLiftAtFull * rightStrokeStrength;
        }
        else
        {
            leftLocalDesired += leftStrokeOffsetLocal * leftStrokeStrength;
            rightLocalDesired += rightStrokeOffsetLocal * rightStrokeStrength;

            leftLocalDesired.y += extraLiftAtFull * leftStrokeStrength;
            rightLocalDesired.y += extraLiftAtFull * rightStrokeStrength;
        }

        // Transform to world
        Vector3 leftWorldDesired = transform.TransformPoint(leftLocalDesired);
        Vector3 rightWorldDesired = transform.TransformPoint(rightLocalDesired);

        // Smooth chase (framerate independent)
        float a = 1f - Mathf.Exp(-targetLerpSpeed * Mathf.Max(Time.deltaTime, 1e-5f));

        leftTarget.position = Vector3.Lerp(leftTarget.position, leftWorldDesired, a);
        rightTarget.position = Vector3.Lerp(rightTarget.position, rightWorldDesired, a);
    }

    void OnDrawGizmos()
    {
        if (!drawGizmos) return;

        // Limb roots -> targets
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

        // Debug: draw base forward and outside normal (if spine exists)
        Vector3 origin = transform.position;

        Vector3 baseFwd = transform.forward;
        baseFwd.y = 0f;
        if (baseFwd.sqrMagnitude < 1e-6f) baseFwd = Vector3.forward;
        baseFwd.Normalize();

        // base forward arrow
        Gizmos.DrawLine(origin, origin + baseFwd * gizmoArrowLen);

        // outside arrow uses bendSign if possible
        int outsideSign = bendSign;
        if (outsideSign == 0)
        {
            // fallback on omega direction in edit mode (omegaDeg not updated)
            outsideSign = 1;
        }

        Vector3 outside = Vector3.Cross(Vector3.up, baseFwd).normalized * outsideSign;
        Gizmos.DrawLine(origin, origin + outside * (gizmoArrowLen * 0.8f));

        // Spine chain lines
        if (spineChain != null && spineChain.Length >= 2)
        {
            for (int i = 0; i < spineChain.Length - 1; i++)
            {
                if (spineChain[i] == null || spineChain[i + 1] == null) continue;
                Gizmos.DrawLine(spineChain[i].position, spineChain[i + 1].position);
            }
        }
    }
}
