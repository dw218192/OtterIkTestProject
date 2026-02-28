using UnityEngine;

/// <summary>
/// High-level locomotion state controller for the otter.
///
/// States (stable):
/// - BackFloatSwim  (low effort for a while)
/// - ProneSwim      (high effort for a while)
/// - Sprint         (speed gate; optionally requires sprint intent zone)
/// - IdleUpright    (only when nearly stopped + interact request)
///
/// Special is treated as a temporary override (transition/ability).
///
/// This script does NOT drive IK/animation directly; it outputs policy decisions.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(250)]
public class MoveStateController : MonoBehaviour
{
    public enum MoveState { ProneSwim, BackFloatSwim, Sprint, IdleUpright, Special }

    [Header("References")]
    [SerializeField] private CrestMovementControllerRB movement;
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

    [Header("Sprint")]
    [Tooltip("Enter sprint when actual speed is above this threshold long enough.")]
    [SerializeField] private float sprintEnterSpeed = 3.7f;
    [SerializeField] private float sprintExitSpeed = 3.2f;
    [SerializeField] private float sprintEnterHoldTime = 0.15f;
    [SerializeField] private float sprintExitHoldTime = 0.2f;

    [Tooltip("If true, sprint can only be entered when the cursor is outside the outer ring (Sprint zone).")]
    [SerializeField] private bool requireSprintIntentZone = true;

    [Header("Idle Upright (interaction)")]
    [SerializeField] private float uprightEnterSpeed = 0.12f;
    [SerializeField] private float uprightExitIntentSpeed = 0.05f; // if targetSpeed exceeds -> leave upright
    [SerializeField] private KeyCode debugInteractKey = KeyCode.E;

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
        movement = GetComponent<CrestMovementControllerRB>();
        rb = GetComponent<Rigidbody>();
    }

    private void Awake()
    {
        if (movement == null) movement = GetComponent<CrestMovementControllerRB>();
        if (rb == null) rb = GetComponent<Rigidbody>();

        ApplyPolicyForState(current);
    }

    private void Update()
    {
        // Debug interaction ("pat to stand")
        if (debugInteractKey != KeyCode.None && Input.GetKeyDown(debugInteractKey))
            RequestUprightInteract();

        if (uprightRequested && CanEnterUpright())
        {
            SetState(MoveState.IdleUpright);
            uprightRequested = false;
        }
    }

    private void FixedUpdate()
    {
        if (movement == null) return;

        // Special override countdown
        if (current == MoveState.Special)
        {
            specialTimer -= Time.fixedDeltaTime;
            if (specialTimer <= 0f)
                SetState(specialReturnTo);
            return;
        }

        UpdateEffortTracking();
        UpdateSprintState();
        UpdateSwimPoseState();
        UpdateUprightExit();
    }

    // ---------------- External triggers ----------------

    public void RequestUprightInteract() => uprightRequested = true;

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

        // Timers for hysteresis
        if (effortSmoothed < backFloatEnterEffort) lowEffortTime += dt; else lowEffortTime = 0f;
        if (effortSmoothed > proneEnterEffort) highEffortTime += dt; else highEffortTime = 0f;
    }

    private void UpdateSprintState()
    {
        float dt = Time.fixedDeltaTime;
        float speed = movement.GetSpeed();

        bool sprintIntentOk = !requireSprintIntentZone || (movement.GetZone() == CrestMovementControllerRB.MoveZone.Sprint && movement.IsDragging());

        if (current == MoveState.Sprint)
        {
            if (speed <= sprintExitSpeed) sprintBelowTime += dt; else sprintBelowTime = 0f;

            if (sprintBelowTime >= sprintExitHoldTime)
            {
                sprintBelowTime = 0f;
                SetState(effortSmoothed < backFloatEnterEffort ? MoveState.BackFloatSwim : MoveState.ProneSwim);
            }
            return;
        }

        // Non-sprint states: count time above enter threshold
        if (sprintIntentOk && speed >= sprintEnterSpeed) sprintAboveTime += dt; else sprintAboveTime = 0f;

        if (sprintAboveTime >= sprintEnterHoldTime)
        {
            sprintAboveTime = 0f;
            sprintBelowTime = 0f;
            if (current != MoveState.IdleUpright)
                SetState(MoveState.Sprint);
        }
    }

    private void UpdateSwimPoseState()
    {
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

        if (movement.GetTargetSpeed() > uprightExitIntentSpeed && movement.IsDragging())
            SetState(effortSmoothed < backFloatEnterEffort ? MoveState.BackFloatSwim : MoveState.ProneSwim);
    }

    private bool CanEnterUpright()
    {
        float speed = movement != null ? movement.GetSpeed()
            : (rb != null ? new Vector3(rb.velocity.x, 0f, rb.velocity.z).magnitude : 999f);
        return speed <= uprightEnterSpeed;
    }

    private void SetState(MoveState next)
    {
        if (current == next) return;
        current = next;
        ApplyPolicyForState(current);
    }

    private void ApplyPolicyForState(MoveState s)
    {
        // Limb policy (for your IK/anim to read)
        HindOnly = (s == MoveState.BackFloatSwim);

        // If you later want "Special" to suppress propulsion, call:
        // movement.SetPropulsionOverride(0f, 0f, 0f);  // (and Clear on exit)
    }
}
