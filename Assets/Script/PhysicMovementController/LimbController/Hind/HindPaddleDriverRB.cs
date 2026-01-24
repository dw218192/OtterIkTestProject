using UnityEngine;

/// <summary>
/// Runtime driver:
/// - Subscribes MovementControllerRB.OnKickCycleStart (movement kicks, highest priority) :contentReference[oaicite:4]{index=4}
/// - Merges IdleHipTurnKickControllerRB logic (idle turn kick emitter, per-leg schedule/cooldown/strength) :contentReference[oaicite:5]{index=5}
/// - Owns timing/playback and writes IK target positions using HindPaddleTrajectoryRB.EvaluateKickLocal()
///
/// Visualization is intentionally NOT here (see HindPaddleDebugVisualizerRB).
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
[DefaultExecutionOrder(250)]
public class HindPaddleDriverRB : MonoBehaviour
{
    [System.Serializable]
    public class Leg
    {
        public string name = "HindLeg";

        [Header("Rig references")]
        public Transform ikTarget;

        [Tooltip("Used only to compute left/right side sign relative to spine side axis.")]
        public HindPaddleTrajectoryRB.LegBinding binding = new HindPaddleTrajectoryRB.LegBinding();

        [Header("Rest pose")]
        public Vector3 restLocalPos;
        public bool autoUpdateRestInEditMode = true;

        [HideInInspector] public bool restInitialized;
    }

    [Header("References")]
    public MovementControllerRB movement;
    public HindPaddleTrajectoryRB trajectory;

    [Header("Space")]
    public Transform rootSpace;

    [Header("Hind legs")]
    public Leg leftLeg = new Leg { name = "LeftHindLeg" };
    public Leg rightLeg = new Leg { name = "RightHindLeg" };

    [Header("Idle gating (rest behavior)")]
    [Tooltip("If targetSpeed is 0 (Aim/None) or no intent, legs return to rest when no kick is active.")]
    public bool returnToRestWhenNoPropulsion = true;

    [Header("Smoothing")]
    public float targetPosLerp = 16f;

    // ---------------- Movement kick (higher priority) runtime state ----------------
    public bool IsMovementKickActive => _moveKickActive;

    private bool _moveKickActive;
    private float _moveKickT;
    private float _moveKickDur = 0.28f;
    private float _moveKickDemand01 = 1f;

    // ---------------- Idle kick (lower priority, per-leg) runtime state ----------------
    private bool _idleLActive, _idleRActive;
    private float _idleLT, _idleRT;
    private float _idleLDur = 0.28f, _idleRDur = 0.28f;
    private float _idleLStrength = 0.5f, _idleRStrength = 0.5f;

    // =====================================================================
    // ============ Idle emitter (ported from IdleHipTurnKickControllerRB) ===
    // =====================================================================

    [Header("Idle Turn Kick Emitter (merged)")]
    [SerializeField] private bool enableIdleEmitter = true;

    [Tooltip("Yaw reference used to measure turning angular speed. If null uses rootSpace.")]
    [SerializeField] private Transform yawReference;

    [Header("Activation gates (avoid conflicts with movement kicks)")]
    [SerializeField] private bool requireDragging = true;

    [Tooltip("Idle kicks only when MovementControllerRB targetSpeed <= eps (Aim rotate-only).")]
    [SerializeField] private float aimTargetSpeedEps = 0.01f;

    [Tooltip("Idle kicks only when rigidbody planar speed is below this.")]
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

    [Tooltip("Idle kick cycle duration (Prepare+Kick equivalent) used by Driver.")]
    [SerializeField] private float idleCycleDuration = 0.28f;

    [Header("Per-leg start delay / jitter")]
    [SerializeField] private float leftStartDelay = 0f;
    [SerializeField] private float rightStartDelay = 0f;
    [SerializeField] private float delayJitter = 0.03f;

    [Header("Strength shaping")]
    [SerializeField] private AnimationCurve strengthCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

    [Range(0f, 1f)]
    [SerializeField] private float maxIdleStrength = 0.85f;

    // runtime (emitter)
    private float _cdL;
    private float _cdR;
    private bool _schedL, _schedR;
    private float _schedLT, _schedRT;
    private float _schedLStrength, _schedRStrength;
    private Quaternion _prevYaw;
    private Vector3 _prevHipPos;

    // ---------------- Debug snapshot for Visualizer ----------------
    public struct DebugSnapshot
    {
        public bool moveActive;
        public float movePhase01;
        public float moveDemand01;
        public float moveDuration;

        public bool idleLActive, idleRActive;
        public float idleLPhase01, idleRPhase01;
        public float idleLStrength, idleRStrength;
        public float idleLCycleDur, idleRCycleDur;

        public float cdL, cdR;
        public bool schedL, schedR;
        public float schedLT, schedRT;
        public float schedLStrength, schedRStrength;
    }

    public DebugSnapshot GetDebugSnapshot()
    {
        DebugSnapshot s = new DebugSnapshot();

        s.moveActive   = _moveKickActive;
        s.movePhase01  = _moveKickDur > 1e-5f ? Mathf.Clamp01(_moveKickT / _moveKickDur) : 0f;
        s.moveDemand01 = _moveKickDemand01;
        s.moveDuration = _moveKickDur;

        s.idleLActive    = _idleLActive;
        s.idleRActive    = _idleRActive;
        s.idleLPhase01   = _idleLDur > 1e-5f ? Mathf.Clamp01(_idleLT / _idleLDur) : 0f;
        s.idleRPhase01   = _idleRDur > 1e-5f ? Mathf.Clamp01(_idleRT / _idleRDur) : 0f;
        s.idleLStrength  = _idleLStrength;
        s.idleRStrength  = _idleRStrength;
        s.idleLCycleDur  = _idleLDur;
        s.idleRCycleDur  = _idleRDur;

        s.cdL = _cdL;
        s.cdR = _cdR;

        s.schedL = _schedL;
        s.schedR = _schedR;
        s.schedLT = _schedLT;
        s.schedRT = _schedRT;
        s.schedLStrength = _schedLStrength;
        s.schedRStrength = _schedRStrength;

        return s;
    }

    // ---------------- Unity lifecycle ----------------

    private void Reset()
    {
        rootSpace = transform;
        movement = GetComponentInParent<MovementControllerRB>();
        trajectory = GetComponent<HindPaddleTrajectoryRB>();
    }

    private void OnEnable()
    {
        if (rootSpace == null) rootSpace = transform;
        if (trajectory != null)
        {
            if (trajectory.rootSpace == null) trajectory.rootSpace = rootSpace;
            if (trajectory.spineChain == null || trajectory.spineChain.Length == 0)
                trajectory.spineChain = null; // leave as-is; user fills
        }

        SyncRestIfNeeded(force: false);

        if (movement != null)
            movement.OnKickCycleStart += HandleMovementKickCycleStart;

        ResetIdleEmitterRuntime();
    }

    private void OnValidate()
    {
        if (rootSpace == null) rootSpace = transform;
        if (trajectory != null && trajectory.rootSpace == null) trajectory.rootSpace = rootSpace;
        SyncRestIfNeeded(force: true);
    }

    private void OnDisable()
    {
        if (movement != null)
            movement.OnKickCycleStart -= HandleMovementKickCycleStart;
    }

    private void Start()
    {
        ForceInitRest(leftLeg);
        ForceInitRest(rightLeg);
    }

    private void LateUpdate()
    {
        if (!Application.isPlaying)
        {
            SyncRestIfNeeded(force: false);
            return;
        }

        if (rootSpace == null || trajectory == null) return;

        float dt = Time.deltaTime;

        // Idle emitter runs continuously and may schedule/trigger per-leg idle kicks.
        if (enableIdleEmitter)
            UpdateIdleEmitter(dt);

        // 1) Movement kick (highest priority)
        if (_moveKickActive)
        {
            _moveKickT += dt;
            float phase01 = Mathf.Clamp01(_moveKickT / Mathf.Max(1e-4f, _moveKickDur));

            UpdateLeg(leftLeg,  dt, phase01, _moveKickDemand01, 1f);
            UpdateLeg(rightLeg, dt, phase01, _moveKickDemand01, 1f);

            if (_moveKickT >= _moveKickDur) _moveKickActive = false;
            return;
        }

        // 2) Idle kicks (per-leg, lower priority)
        bool anyIdle = _idleLActive || _idleRActive;

        if (anyIdle)
        {
            if (_idleLActive)
            {
                _idleLT += dt;
                float pL = Mathf.Clamp01(_idleLT / Mathf.Max(1e-4f, _idleLDur));
                UpdateLeg(leftLeg, dt, pL, 1f, _idleLStrength);
                if (_idleLT >= _idleLDur) _idleLActive = false;
            }
            else
            {
                ReturnToRest(leftLeg, dt);
            }

            if (_idleRActive)
            {
                _idleRT += dt;
                float pR = Mathf.Clamp01(_idleRT / Mathf.Max(1e-4f, _idleRDur));
                UpdateLeg(rightLeg, dt, pR, 1f, _idleRStrength);
                if (_idleRT >= _idleRDur) _idleRActive = false;
            }
            else
            {
                ReturnToRest(rightLeg, dt);
            }

            return;
        }

        // 3) No kicks: rest
        if (returnToRestWhenNoPropulsion)
        {
            // Mimic your original behavior: even at rest, just glide back to rest point.
            ReturnToRest(leftLeg, dt);
            ReturnToRest(rightLeg, dt);
        }
    }

    // ---------------- Rest capture ----------------

    private void SyncRestIfNeeded(bool force)
    {
        SyncLegRest(leftLeg, force);
        SyncLegRest(rightLeg, force);
    }

    private void SyncLegRest(Leg leg, bool force)
    {
        if (leg == null || leg.ikTarget == null || rootSpace == null) return;

        if (!Application.isPlaying)
        {
            if (!leg.autoUpdateRestInEditMode && leg.restInitialized && !force) return;
            leg.restLocalPos = rootSpace.InverseTransformPoint(leg.ikTarget.position);
            leg.restInitialized = true;
        }
    }

    private void ForceInitRest(Leg leg)
    {
        if (leg == null || leg.ikTarget == null || rootSpace == null) return;
        leg.restLocalPos = rootSpace.InverseTransformPoint(leg.ikTarget.position);
        leg.restInitialized = true;
    }

    // ---------------- External API (for compatibility/debug) ----------------

    public bool IsIdleLegActive(bool left) => left ? _idleLActive : _idleRActive;

    public bool TryStartIdleKick(bool left, float cycleDuration, float strength01)
    {
        // Movement has higher priority: do not start idle kick while moving kick is active
        if (_moveKickActive) return false;

        cycleDuration = Mathf.Max(0.05f, cycleDuration);
        strength01 = Mathf.Clamp01(strength01);

        if (left)
        {
            if (_idleLActive) return false;
            _idleLActive = true;
            _idleLT = 0f;
            _idleLDur = cycleDuration;
            _idleLStrength = strength01;
            return true;
        }

        if (_idleRActive) return false;
        _idleRActive = true;
        _idleRT = 0f;
        _idleRDur = cycleDuration;
        _idleRStrength = strength01;
        return true;
    }

    private void CancelAllIdleKicks()
    {
        _idleLActive = _idleRActive = false;
    }

    // ---------------- Movement kick event ----------------

    private void HandleMovementKickCycleStart(MovementControllerRB.KickCycleEvent e)
    {
        _moveKickActive = true;
        _moveKickT = 0f;
        _moveKickDur = Mathf.Max(0.05f, e.cycleDuration);
        _moveKickDemand01 = Mathf.Clamp01(e.demand01);

        // Movement overrides idle instantly
        CancelAllIdleKicks();

        // Cancel pending idle schedules too (matches old emitter behavior)
        _schedL = _schedR = false;
    }

    // ---------------- Core motion application ----------------

    private void ReturnToRest(Leg leg, float dt)
    {
        if (leg == null || leg.ikTarget == null || rootSpace == null) return;
        Vector3 restWorld = rootSpace.TransformPoint(leg.restLocalPos);
        leg.ikTarget.position = Vector3.Lerp(
            leg.ikTarget.position,
            restWorld,
            1f - Mathf.Exp(-targetPosLerp * dt)
        );
    }

    private void UpdateLeg(Leg leg, float dt, float phase01, float demand01, float scale01)
    {
        if (leg == null || leg.ikTarget == null || rootSpace == null || trajectory == null) return;

        Vector3 localPos = trajectory.EvaluateKickLocal(
            leg.binding,
            leg.restLocalPos,
            phase01,
            demand01,
            scale01
        );

        Vector3 desiredWorld = rootSpace.TransformPoint(localPos);

        leg.ikTarget.position = Vector3.Lerp(
            leg.ikTarget.position,
            desiredWorld,
            1f - Mathf.Exp(-targetPosLerp * dt)
        );
    }

    // =====================================================================
    // ===================== Idle emitter (ported) ==========================
    // =====================================================================

    private void ResetIdleEmitterRuntime()
    {
        _cdL = _cdR = 0f;
        _schedL = _schedR = false;
        _schedLT = _schedRT = 0f;

        Transform yawT = ResolveYawReference();
        _prevYaw = yawT != null ? yawT.rotation : transform.rotation;

        _prevHipPos = transform.position;
    }

    private void UpdateIdleEmitter(float dt)
    {
        if (!Application.isPlaying) return;
        if (movement == null) return;
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
        float strength01;

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
        if (IsMovementKickActive)
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
            if (TryStartIdleKick(true, idleCycleDuration, _schedLStrength))
            {
                _schedL = false;
                _cdL = Mathf.Max(0.01f, leftLegCooldown);
            }
        }

        if (_schedR && _schedRT <= 0f)
        {
            if (TryStartIdleKick(false, idleCycleDuration, _schedRStrength))
            {
                _schedR = false;
                _cdR = Mathf.Max(0.01f, rightLegCooldown);
            }
        }
    }

    private Transform ResolveYawReference()
    {
        if (yawReference != null) return yawReference;
        if (rootSpace != null) return rootSpace;
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
        if (IsIdleLegActive(true)) return;

        _schedL = true;
        _schedLStrength = strength01;
        _schedLT = Mathf.Max(0f, leftStartDelay) + Random.Range(0f, Mathf.Max(0f, delayJitter));
    }

    private void TryScheduleRight(float strength01)
    {
        if (_schedR) return;
        if (_cdR > 0f) return;
        if (IsIdleLegActive(false)) return;

        _schedR = true;
        _schedRStrength = strength01;
        _schedRT = Mathf.Max(0f, rightStartDelay) + Random.Range(0f, Mathf.Max(0f, delayJitter));
    }
}
