// FrontPaddleController.cs
// Controls ONLY IK target positions (no IK solving here).
// Uses Gecko-style: angle error -> target angular velocity -> smoothed angular velocity
// Uses spine curvature (optional) to scale stroke amplitude
// Visualizes root->target lines.
//
// Attach this to CHEST (or a stable upper body node).
// Assign: MovementController, left/right limb ROOT bones, left/right IK Targets,
// and optionally spine bones (root->tail order).

using UnityEngine;

[DefaultExecutionOrder(250)]
public class FrontPaddleController : MonoBehaviour
{
    public enum PaddleState { Idle, ExtendHold, Recover }

    [Header("References")]
    [SerializeField] private MovementController movement;
    [Tooltip("Left front limb root (shoulder / upper arm / thigh root)")]
    [SerializeField] private Transform leftLimbRoot;
    [Tooltip("Right front limb root (shoulder / upper arm / thigh root)")]
    [SerializeField] private Transform rightLimbRoot;

    [Tooltip("IK target transform for LEFT limb (the thing your AdvancedTwoBoneIK reads)")]
    [SerializeField] private Transform leftTarget;
    [Tooltip("IK target transform for RIGHT limb")]
    [SerializeField] private Transform rightTarget;

    [Header("Spine (optional, root -> tail order)")]
    [Tooltip("Used only to compute a bend amount (0..1) to scale stroke amplitude.")]
    [SerializeField] private Transform[] spineChain;

    [Header("Gecko-style Turning Signal")]
    [Tooltip("Max angular speed (deg/s) used as 'full turn'.")]
    [SerializeField] private float maxTurnOmegaDeg = 180f;

    [Tooltip("Angle (deg) that maps to full turn omega (bigger => slower response).")]
    [SerializeField] private float angleForFullOmega = 60f;

    [Tooltip("How fast omega can change (deg/s^2). Bigger = snappier.")]
    [SerializeField] private float omegaAccelDeg = 720f;

    [Tooltip("Ignore tiny turns. Below this omega, we tend to recover/idle.")]
    [SerializeField] private float omegaStartThreshold = 35f;

    [Tooltip("Consider turn finished when abs(omega) drops below this.")]
    [SerializeField] private float omegaStopThreshold = 15f;

    [Header("Stroke Timing")]
    [Tooltip("Max time we allow one continuous stroke before forcing recovery.")]
    [SerializeField] private float maxStrokeTime = 0.45f;

    [Tooltip("Recovery duration back to rest.")]
    [SerializeField] private float recoverTime = 0.20f;

    [Tooltip("When direction flips, wait a tiny moment before allowing the other side to fire (anti-chatter).")]
    [SerializeField] private float flipCooldown = 0.08f;

    [Header("Amplitude / Pose")]
    [Tooltip("Target rest position in CHEST local space (Left). If empty, captured at Start.")]
    [SerializeField] private Vector3 leftRestLocal;
    [Tooltip("Target rest position in CHEST local space (Right). If empty, captured at Start.")]
    [SerializeField] private Vector3 rightRestLocal;

    [Tooltip("Local-space offset at full stroke strength (Left). Typically outward + a bit backward/down.")]
    [SerializeField] private Vector3 leftStrokeOffsetLocal = new Vector3(-0.18f, -0.05f, -0.10f);

    [Tooltip("Local-space offset at full stroke strength (Right). Typically outward + a bit backward/down.")]
    [SerializeField] private Vector3 rightStrokeOffsetLocal = new Vector3(+0.18f, -0.05f, -0.10f);

    [Tooltip("Optional extra lift/open when turning strongly (applied along local +Y).")]
    [SerializeField] private float extraLiftAtFull = 0.03f;

    [Header("Strength Mixing")]
    [Tooltip("How much turn strength contributes (0..1).")]
    [Range(0, 1)] [SerializeField] private float turnStrengthWeight = 1.0f;

    [Tooltip("How much spine bend contributes (0..1).")]
    [Range(0, 1)] [SerializeField] private float bendStrengthWeight = 0.6f;

    [Tooltip("Normalize bend: bend amount that counts as 'full'.")]
    [SerializeField] private float bendForFull = 25f; // degrees

    [Header("Smoothing")]
    [Tooltip("How quickly targets move toward desired pos.")]
    [SerializeField] private float targetLerpSpeed = 14f;

    [Header("Debug / Gizmos")]
    [SerializeField] private bool drawGizmos = true;
    [SerializeField] private float gizmoSphereRadius = 0.03f;

    // internal
    private float omegaDeg;               // smoothed angular velocity (deg/s), signed
    private float flipCooldownTimer;

    private PaddleState leftState = PaddleState.Idle;
    private PaddleState rightState = PaddleState.Idle;
    private float leftStateTime, rightStateTime;

    private float leftStrokeStrength;     // 0..1
    private float rightStrokeStrength;    // 0..1

    private bool restCaptured;

    void Start()
    {
        CaptureRestIfNeeded();
    }

    void OnEnable()
    {
        CaptureRestIfNeeded();
    }

    private void CaptureRestIfNeeded()
    {
        if (restCaptured) return;
        if (leftTarget != null) leftRestLocal = transform.InverseTransformPoint(leftTarget.position);
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
            // No meaningful aim direction: decay and recover
            omegaDeg = Mathf.MoveTowards(omegaDeg, 0f, omegaAccelDeg * dt);
            TickStateMachine(dt, 0f, 0f);
            ApplyTargets(dt);
            return;
        }

        Vector3 desiredDir = toCarrot.normalized;

        // Current forward on XZ plane
        Vector3 fwd = transform.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
        fwd.Normalize();

        // Signed angle error (deg), positive means desired is to the right (Unity SignedAngle with up=Y)
        float angleError = Vector3.SignedAngle(fwd, desiredDir, Vector3.up);

        // 2) Gecko-style target omega mapping + smoothing
        float turnStrength01 = Mathf.Clamp01(Mathf.Abs(angleError) / Mathf.Max(1f, angleForFullOmega));
        float targetOmega = Mathf.Sign(angleError) * (turnStrength01 * maxTurnOmegaDeg);

        omegaDeg = Mathf.MoveTowards(omegaDeg, targetOmega, omegaAccelDeg * dt);

        // 3) Spine bend amount (optional) -> 0..1
        float bend01 = ComputeBend01();

        // mix strengths (stable)
        float mixedStrength = Mathf.Clamp01(turnStrengthWeight * turnStrength01 + bendStrengthWeight * bend01);

        TickStateMachine(dt, omegaDeg, mixedStrength);
        ApplyTargets(dt);
    }

    private float ComputeBend01()
    {
        if (spineChain == null || spineChain.Length < 2) return 0f;

        // We measure how much the chain "yaws" along the path relative to the first bone forward.
        // Project to XZ and accumulate abs signed angles between successive segments.
        Vector3 baseFwd = spineChain[0].forward;
        baseFwd.y = 0f;
        if (baseFwd.sqrMagnitude < 1e-6f) return 0f;
        baseFwd.Normalize();

        float accumAbs = 0f;
        Vector3 prevFwd = baseFwd;

        for (int i = 1; i < spineChain.Length; i++)
        {
            Vector3 fwd = spineChain[i].forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f) continue;
            fwd.Normalize();

            float a = Vector3.SignedAngle(prevFwd, fwd, Vector3.up);
            accumAbs += Mathf.Abs(a);
            prevFwd = fwd;
        }

        // Normalize: bendForFull degrees -> 1
        return Mathf.Clamp01(accumAbs / Mathf.Max(1f, bendForFull));
    }

    private void TickStateMachine(float dt, float omegaSigned, float strength01)
    {
        // Anti-chatter on sign flip
        if (flipCooldownTimer > 0f)
            flipCooldownTimer -= dt;

        int sign = (Mathf.Abs(omegaSigned) < 1e-3f) ? 0 : (omegaSigned > 0f ? +1 : -1);

        // We interpret:
        // sign > 0 => turning right => RIGHT limb strokes (acts like a rudder/paddle)
        // sign < 0 => turning left  => LEFT limb strokes
        bool wantRight = (sign > 0 && Mathf.Abs(omegaSigned) >= omegaStartThreshold);
        bool wantLeft  = (sign < 0 && Mathf.Abs(omegaSigned) >= omegaStartThreshold);

        // If direction flips while stroking -> immediate break + recover
        bool leftActive = (leftState == PaddleState.ExtendHold);
        bool rightActive = (rightState == PaddleState.ExtendHold);

        if (leftActive && sign > 0)
        {
            // flipped to right
            leftState = PaddleState.Recover;
            leftStateTime = 0f;
            flipCooldownTimer = flipCooldown;
        }
        if (rightActive && sign < 0)
        {
            // flipped to left
            rightState = PaddleState.Recover;
            rightStateTime = 0f;
            flipCooldownTimer = flipCooldown;
        }

        // LEFT state
        StepOneSide(
            dt,
            want: wantLeft && flipCooldownTimer <= 0f,
            omegaAbs: Mathf.Abs(omegaSigned),
            strength01: strength01,
            ref leftState,
            ref leftStateTime,
            ref leftStrokeStrength
        );

        // RIGHT state
        StepOneSide(
            dt,
            want: wantRight && flipCooldownTimer <= 0f,
            omegaAbs: Mathf.Abs(omegaSigned),
            strength01: strength01,
            ref rightState,
            ref rightStateTime,
            ref rightStrokeStrength
        );
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
            {
                strokeStrength = 0f;
                if (want)
                {
                    state = PaddleState.ExtendHold;
                    stateTime = 0f;
                    strokeStrength = Mathf.Clamp01(strength01);
                }
                break;
            }

            case PaddleState.ExtendHold:
            {
                // Same-side continuous turning: keep extending (allow longer) while omega is still meaningful
                strokeStrength = Mathf.Max(strokeStrength, Mathf.Clamp01(strength01));

                bool finishedTurn = omegaAbs <= omegaStopThreshold;
                bool timedOut = stateTime >= maxStrokeTime;

                if (!want || finishedTurn || timedOut)
                {
                    state = PaddleState.Recover;
                    stateTime = 0f;
                }
                break;
            }

            case PaddleState.Recover:
            {
                // Decay back to rest smoothly
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
    }

    private void ApplyTargets(float dt)
    {
        // Left desired local position
        Vector3 leftLocal = leftRestLocal + leftStrokeOffsetLocal * leftStrokeStrength;
        leftLocal.y += extraLiftAtFull * leftStrokeStrength;

        // Right desired local position
        Vector3 rightLocal = rightRestLocal + rightStrokeOffsetLocal * rightStrokeStrength;
        rightLocal.y += extraLiftAtFull * rightStrokeStrength;

        Vector3 leftWorldDesired = transform.TransformPoint(leftLocal);
        Vector3 rightWorldDesired = transform.TransformPoint(rightLocal);

        float a = 1f - Mathf.Exp(-targetLerpSpeed * dt); // framerate-independent smoothing
        leftTarget.position = Vector3.Lerp(leftTarget.position, leftWorldDesired, a);
        rightTarget.position = Vector3.Lerp(rightTarget.position, rightWorldDesired, a);
    }

    void OnDrawGizmos()
    {
        if (!drawGizmos) return;

        // Roots -> Targets
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
}
