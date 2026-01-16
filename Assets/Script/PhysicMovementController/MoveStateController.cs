using UnityEngine;

/// <summary>
/// High-level locomotion state controller for the otter.
///
/// States (stable):
/// - BackFloatSwim  (low effort for a while): hind legs only (policy output)
/// - ProneSwim      (high effort for a while): all limbs (policy output)
/// - Sprint         (speed-only gate): wave / dolphin-like (policy output)
/// - IdleUpright    (only when nearly stopped + interact request)
///
/// Special is treated as a temporary override (transition/ability).
///
/// This script does NOT drive IK/animation directly; it outputs policy decisions
/// and can override MovementControllerRB propulsion limits.
/// </summary>
public class MoveStateController : MonoBehaviour
{
    public enum MoveState { ProneSwim, BackFloatSwim, Sprint, IdleUpright, Special }

    [Header("References")]
    [SerializeField] private MovementControllerRB movement;
    [SerializeField] private Rigidbody rb;

    [Header("Effort (for Prone <-> BackFloat)")]
    [Tooltip("Effort is derived from targetSpeed normalized to swimSpeed (0..1).")]
    [SerializeField] private float effortSmoothing = 1.2f; // seconds-ish (bigger = smoother)

    [Tooltip("Below this effort for a while -> BackFloat.")]
    [SerializeField] private float backFloatEnterEffort = 0.18f;
    [Tooltip("Above this effort for a while -> Prone.")]
    [SerializeField] private float proneEnterEffort = 0.35f;

    [SerializeField] private float backFloatEnterTime = 1.2f;
    [SerializeField] private float proneEnterTime = 0.6f;

    [Header("Sprint (speed-only)")]
    [SerializeField] private float sprintEnterSpeed = 3.7f;
    [SerializeField] private float sprintExitSpeed = 3.2f;
    [SerializeField] private float sprintEnterHoldTime = 0.15f;
    [SerializeField] private float sprintExitHoldTime = 0.2f;

    [Header("Idle Upright (interaction)")]
    [SerializeField] private float uprightEnterSpeed = 0.12f;
    [SerializeField] private float uprightExitIntentSpeed = 0.05f; // if targetSpeed exceeds -> leave upright
    [SerializeField] private KeyCode debugInteractKey = KeyCode.E;

    [Header("Propulsion overrides (optional)")]
    [Tooltip("If true, this controller will override kick rate/impulse for each state.")]
    [SerializeField] private bool drivePropulsionOverrides = true;

    [Header("BackFloat (hind only) kick limits")]
    [SerializeField] private float backFloatKickRateMax = 2.2f;
    [SerializeField] private float backFloatImpulseMax = 1.2f;
    [SerializeField] private float backFloatIdleBrake = 9.0f;

    [Header("Prone (all limbs) kick limits")]
    [SerializeField] private float proneKickRateMax = 3.2f;
    [SerializeField] private float proneImpulseMax = 1.6f;
    [SerializeField] private float proneIdleBrake = 8.0f;

    [Header("Sprint kick limits (or near-continuous feel via higher rate)")]
    [SerializeField] private float sprintKickRateMax = 6.0f;
    [SerializeField] private float sprintImpulseMax = 2.4f;
    [SerializeField] private float sprintIdleBrake = 6.0f;

    [Header("Upright")]
    [SerializeField] private float uprightIdleBrake = 12.0f;

    // State
    [SerializeField] private MoveState current = MoveState.ProneSwim;

    // Effort tracking
    private float effortSmoothed;
    private float lowEffortTime;
    private float highEffortTime;

    // Sprint timers
    private float sprintAboveTime;
    private float sprintBelowTime;

    // Upright request
    private bool uprightRequested;

    // Special
    private float specialTimer;
    private MoveState specialReturnTo = MoveState.ProneSwim;

    // Public policy outputs
    public bool HindOnly { get; private set; }

    public MoveState CurrentState => current;
    public float Effort01 => effortSmoothed;

    public bool IsInSpecial() => current == MoveState.Special;
    public bool IsUpright() => current == MoveState.IdleUpright;

    private void Reset()
    {
        movement = GetComponent<MovementControllerRB>();
        rb = GetComponent<Rigidbody>();
    }

    private void Awake()
    {
        if (movement == null) movement = GetComponent<MovementControllerRB>();
        if (rb == null) rb = GetComponent<Rigidbody>();

        // initialize overrides
        ApplyPolicyForState(current, immediate:true);
    }

    private void Update()
    {
        // Debug interaction ("ææå®å°±ç´ç«")
        if (debugInteractKey != KeyCode.None && Input.GetKeyDown(debugInteractKey))
        {
            RequestUprightInteract();
        }

        if (uprightRequested && CanEnterUpright())
        {
            SetState(MoveState.IdleUpright);
            uprightRequested = false;
        }
    }

    private void FixedUpdate()
    {
        if (movement == null) return;

        // Handle Special override countdown
        if (current == MoveState.Special)
        {
            specialTimer -= Time.fixedDeltaTime;
            if (specialTimer <= 0f)
            {
                SetState(specialReturnTo);
            }
            return;
        }

        UpdateEffortTracking();
        UpdateSprintState();
        UpdateSwimPoseState();
        UpdateUprightExit();
    }

    // ---------------- External triggers ----------------

    public void RequestUprightInteract()
    {
        uprightRequested = true;
    }

    /// <summary>
    /// Enter a temporary Special state for transitions/abilities.
    /// During Special, locomotion switching is frozen and propulsion can be overridden.
    /// </summary>
    public void EnterSpecial(float durationSeconds, MoveState returnTo)
    {
        durationSeconds = Mathf.Max(0.01f, durationSeconds);
        specialTimer = durationSeconds;
        specialReturnTo = returnTo;
        SetState(MoveState.Special);
    }

    // ---------------- Core logic ----------------

    private void UpdateEffortTracking()
    {
        float swim = Mathf.Max(0.01f, movement.GetSwimSpeed());
        float effortInstant = Mathf.Clamp01(movement.GetTargetSpeed() / swim);

        // Exponential smoothing
        float dt = Time.fixedDeltaTime;
        float k = (effortSmoothing <= 1e-3f) ? 1f : (1f - Mathf.Exp(-dt / effortSmoothing));
        effortSmoothed = Mathf.Lerp(effortSmoothed, effortInstant, k);

        // timers for hysteresis
        if (effortSmoothed < backFloatEnterEffort) lowEffortTime += dt; else lowEffortTime = 0f;
        if (effortSmoothed > proneEnterEffort) highEffortTime += dt; else highEffortTime = 0f;
    }

    private void UpdateSprintState()
    {
        // Sprint is speed-only (per your spec)
        float speed = movement.GetSpeed();
        float dt = Time.fixedDeltaTime;

        if (current == MoveState.Sprint)
        {
            if (speed <= sprintExitSpeed)
                sprintBelowTime += dt;
            else
                sprintBelowTime = 0f;

            if (sprintBelowTime >= sprintExitHoldTime)
            {
                sprintBelowTime = 0f;
                // Return to swim pose based on effort after sprint
                SetState(effortSmoothed < backFloatEnterEffort ? MoveState.BackFloatSwim : MoveState.ProneSwim);
            }
            return;
        }

        // Non-sprint states: count time above enter threshold
        if (speed >= sprintEnterSpeed)
            sprintAboveTime += dt;
        else
            sprintAboveTime = 0f;

        if (sprintAboveTime >= sprintEnterHoldTime)
        {
            sprintAboveTime = 0f;
            sprintBelowTime = 0f;
            // Do not enter sprint if upright (shouldn't happen due to speed gate)
            if (current != MoveState.IdleUpright)
                SetState(MoveState.Sprint);
        }
    }

    private void UpdateSwimPoseState()
    {
        // Only switch between Prone/BackFloat in non-sprint, non-upright
        if (current == MoveState.Sprint || current == MoveState.IdleUpright) return;

        if (current == MoveState.ProneSwim)
        {
            if (lowEffortTime >= backFloatEnterTime)
            {
                lowEffortTime = 0f;
                SetState(MoveState.BackFloatSwim);
            }
        }
        else if (current == MoveState.BackFloatSwim)
        {
            if (highEffortTime >= proneEnterTime)
            {
                highEffortTime = 0f;
                SetState(MoveState.ProneSwim);
            }
        }
    }

    private void UpdateUprightExit()
    {
        if (current != MoveState.IdleUpright) return;

        // Leave upright as soon as the player expresses any move intent
        if (movement.GetTargetSpeed() > uprightExitIntentSpeed && movement.IsDragging())
        {
            SetState(effortSmoothed < backFloatEnterEffort ? MoveState.BackFloatSwim : MoveState.ProneSwim);
        }
    }

    private bool CanEnterUpright()
    {
        // Only when nearly stopped
        float speed = movement != null ? movement.GetSpeed() : (rb != null ? new Vector3(rb.velocity.x, 0f, rb.velocity.z).magnitude : 999f);
        return speed <= uprightEnterSpeed;
    }

    private void SetState(MoveState next)
    {
        if (current == next) return;
        current = next;
        ApplyPolicyForState(current, immediate:false);
    }

    private void ApplyPolicyForState(MoveState s, bool immediate)
    {
        // Limb policy (for your IK/anim to read)
        HindOnly = (s == MoveState.BackFloatSwim);

        if (movement == null) return;

        if (!drivePropulsionOverrides)
        {
            movement.ClearPropulsionOverride();
            return;
        }

        // For each state, override kick limits & idle brake.
        switch (s)
        {
            case MoveState.BackFloatSwim:
                movement.SetPropulsionOverride(backFloatKickRateMax, backFloatImpulseMax, backFloatIdleBrake, disableKicks:false);
                break;

            case MoveState.ProneSwim:
                movement.SetPropulsionOverride(proneKickRateMax, proneImpulseMax, proneIdleBrake, disableKicks:false);
                break;

            case MoveState.Sprint:
                movement.SetPropulsionOverride(sprintKickRateMax, sprintImpulseMax, sprintIdleBrake, disableKicks:false);
                break;

            case MoveState.IdleUpright:
                // Disable kicks in upright; strong brake.
                movement.SetPropulsionOverride(0f, 0f, uprightIdleBrake, disableKicks:true);
                break;

            case MoveState.Special:
                // Default Special policy: disable kicks and use strong brake.
                // If you need special abilities with propulsion, override by calling movement.SetPropulsionOverride(...) yourself.
                movement.SetPropulsionOverride(0f, 0f, uprightIdleBrake, disableKicks:true);
                break;
        }
    }
}
