using UnityEngine;

/// <summary>
/// Idle-only hind-leg kick emitter (animation-only).
/// Attach to HIP joint.
/// Drives HindLegKickControllerRB (NOT the old HindLegKickController).
///
/// Requirements:
/// - HindLegKickControllerRB must expose:
///     bool IsMovementKickActive { get; }
///     bool IsIdleLegActive(bool left)
///     bool TryStartIdleKick(bool left, float cycleDuration, float strength01)
/// - MovementControllerRB public API:
///     IsDragging(), GetTargetSpeed(), GetSpeed(), GetSteerDir()
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(220)]
public class IdleHipTurnKickControllerRB : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private MovementControllerRB movement;
    [SerializeField] private HindLegKickControllerRB hind;

    [Tooltip("Yaw reference used to measure turning angular speed. Usually body/root. If null uses hind.rootSpace, else this transform.")]
    [SerializeField] private Transform yawReference;

    [Header("Activation gates (avoid conflicts with movement kicks)")]
    [SerializeField] private bool requireDragging = true;

    [Tooltip("Idle kicks only when MovementControllerRB targetSpeed <= eps (Aim rotate-only).")]
    [SerializeField] private float aimTargetSpeedEps = 0.01f;

    [Tooltip("Idle kicks only when rigidbody planar speed is below this (so we don't interfere with swimming kicks).")]
    [SerializeField] private float maxPlanarSpeedForIdleKicks = 0.25f;

    [Header("Turn intensity -> strength")]
    [Tooltip("Measure turning by yaw angular speed (deg/sec). (Recommended)")]
    [SerializeField] private bool useYawAngularSpeed = true;

    [SerializeField] private float yawDegPerSecTrigger = 30f;
    [SerializeField] private float yawDegPerSecFull = 180f;

    [Tooltip("If useYawAngularSpeed == false: use hip planar speed (m/s) as trigger.")]
    [SerializeField] private float hipSpeedTrigger = 0.15f;
    [SerializeField] private float hipSpeedFull = 1.2f;

    [Header("Per-leg independent cadence")]
    [SerializeField] private float leftLegCooldown = 0.22f;
    [SerializeField] private float rightLegCooldown = 0.22f;

    [Tooltip("Idle kick cycle duration (Prepare+Kick equivalent) used by HindLegKickControllerRB.")]
    [SerializeField] private float idleCycleDuration = 0.28f;

    [Header("Per-leg start delay / jitter")]
    [SerializeField] private float leftStartDelay = 0f;
    [SerializeField] private float rightStartDelay = 0f;
    [SerializeField] private float delayJitter = 0.03f;

    [Header("Strength shaping")]
    [SerializeField] private AnimationCurve strengthCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

    [Range(0f, 1f)]
    [SerializeField] private float maxIdleStrength = 0.85f;

    // runtime
    private float _cdL;
    private float _cdR;
    private bool _schedL, _schedR;
    private float _schedLT, _schedRT;
    private float _schedLStrength, _schedRStrength;
    private Quaternion _prevYaw;
    private Vector3 _prevHipPos;

    private void Reset()
    {
        movement = GetComponentInParent<MovementControllerRB>();
        hind = GetComponentInParent<HindLegKickControllerRB>();
    }

    private void OnEnable()
    {
        _cdL = _cdR = 0f;
        _schedL = _schedR = false;
        _schedLT = _schedRT = 0f;

        Transform yawT = ResolveYawReference();
        _prevYaw = yawT != null ? yawT.rotation : transform.rotation;

        _prevHipPos = transform.position;
    }

    private void Update()
    {
        if (!Application.isPlaying) return;
        if (movement == null || hind == null) return;

        float dt = Time.deltaTime;
        if (dt <= 1e-6f) return;

        // Cooldowns tick independently
        if (_cdL > 0f) _cdL -= dt;
        if (_cdR > 0f) _cdR -= dt;

        // ----- Hard gates to avoid conflict with movement kicks -----
        if (requireDragging && !movement.IsDragging()) return;

        // Aim rotate-only
        if (movement.GetTargetSpeed() > aimTargetSpeedEps) return;

        // Not actually translating fast
        if (movement.GetSpeed() > maxPlanarSpeedForIdleKicks) return;

        // ----- Compute turn intensity -----
        float strength01 = 0f;

        if (useYawAngularSpeed)
        {
            Transform yawT = ResolveYawReference();
            Quaternion now = yawT != null ? yawT.rotation : transform.rotation;

            float yawDegPerSec = ComputeSignedYawDegPerSec(_prevYaw, now, dt, out _);
            _prevYaw = now;

            float a = Mathf.Abs(yawDegPerSec);
            if (a < yawDegPerSecTrigger) return;

            strength01 = Mathf.InverseLerp(
                yawDegPerSecTrigger,
                Mathf.Max(yawDegPerSecTrigger + 1e-3f, yawDegPerSecFull),
                a
            );
        }
        else
        {
            // Hip planar speed trigger
            Vector3 hipNow = transform.position;
            Vector3 v = (hipNow - _prevHipPos) / dt;
            _prevHipPos = hipNow;

            v.y = 0f;
            float spd = v.magnitude;
            if (spd < hipSpeedTrigger) return;

            strength01 = Mathf.InverseLerp(
                hipSpeedTrigger,
                Mathf.Max(hipSpeedTrigger + 1e-3f, hipSpeedFull),
                spd
            );
        }

        strength01 = Mathf.Clamp01(strength01);
        if (strengthCurve != null) strength01 = Mathf.Clamp01(strengthCurve.Evaluate(strength01));
        strength01 = Mathf.Min(strength01, maxIdleStrength);

        // Movement has higher priority: cancel pending schedules and bail.
        if (hind.IsMovementKickActive)
        {
            _schedL = _schedR = false;
            return;
        }

        // 1) Schedule independently (no coordination)
        TryScheduleLeft(strength01);
        TryScheduleRight(strength01);

        // 2) Tick scheduled delays
        if (_schedL) _schedLT -= dt;
        if (_schedR) _schedRT -= dt;

        // 3) Fire independently (can fire same frame)
        if (_schedL && _schedLT <= 0f)
        {
            if (hind.TryStartIdleKick(true, idleCycleDuration, _schedLStrength))
            {
                _schedL = false;
                _cdL = Mathf.Max(0.01f, leftLegCooldown);
            }
        }

        if (_schedR && _schedRT <= 0f)
        {
            if (hind.TryStartIdleKick(false, idleCycleDuration, _schedRStrength))
            {
                _schedR = false;
                _cdR = Mathf.Max(0.01f, rightLegCooldown);
            }
        }
    }

    private Transform ResolveYawReference()
    {
        if (yawReference != null) return yawReference;
        if (hind != null && hind.rootSpace != null) return hind.rootSpace;
        return transform;
    }

    private static float ComputeSignedYawDegPerSec(Quaternion prev, Quaternion now, float dt, out float signedTurn)
    {
        Quaternion delta = now * Quaternion.Inverse(prev);
        delta.ToAngleAxis(out float angleDeg, out Vector3 axis);

        if (axis.sqrMagnitude < 1e-8f)
        {
            signedTurn = 0f;
            return 0f;
        }

        if (angleDeg > 180f) angleDeg -= 360f;

        signedTurn = Mathf.Sign(Vector3.Dot(axis.normalized, Vector3.up));
        return angleDeg / Mathf.Max(1e-5f, dt);
    }

    private void TryScheduleLeft(float strength01)
    {
        if (_schedL) return;
        if (_cdL > 0f) return;
        if (hind.IsIdleLegActive(true)) return;

        _schedL = true;
        _schedLStrength = strength01;
        _schedLT = Mathf.Max(0f, leftStartDelay) + Random.Range(0f, Mathf.Max(0f, delayJitter));
    }

    private void TryScheduleRight(float strength01)
    {
        if (_schedR) return;
        if (_cdR > 0f) return;
        if (hind.IsIdleLegActive(false)) return;

        _schedR = true;
        _schedRStrength = strength01;
        _schedRT = Mathf.Max(0f, rightStartDelay) + Random.Range(0f, Mathf.Max(0f, delayJitter));
    }
}
