using System;
using UnityEngine;

[DefaultExecutionOrder(360)] // after head tracking, before camera
public class SoftSpineSwayDriver : MonoBehaviour
{
    [Serializable]
    public class Bone
    {
        public Transform bone;

        [Header("Bone Axis (LOCAL)")]
        [Tooltip("Which LOCAL axis is considered 'forward' for this bone.")]
        public Vector3 forwardLocal = Vector3.forward;

        [Tooltip("Which LOCAL axis is considered 'up' for this bone (must not be parallel to forward).")]
        public Vector3 upLocal = Vector3.up;

        [Header("Distribution")]
        [Range(0f, 1f)]
        [Tooltip("How much sway this segment receives (0 disables). Farther segments usually higher.")]
        public float weight = 0.7f;

        [Header("Limits (relative to bind pose)")]
        public bool useLimits = true;
        public float yawLimit = 25f;
        public float pitchLimit = 10f;
        public float rollLimit = 10f;
    }

    [Header("References")]
    public MovementController movement;
    public Transform characterRoot;

    [Header("Spine / Tail Bones (center -> tip)")]
    public Bone[] bones;

    [Header("Core: Turn-driven sway")]
    [Tooltip("How many degrees of yaw offset per 1 deg/s of body angular velocity.\nStart around 0.03~0.08.")]
    public float yawFromTurn = 0.05f;

    [Tooltip("Optional: roll offset per 1 deg/s (gives a banking feel). Start small 0~0.03.")]
    public float rollFromTurn = 0.00f;

    [Tooltip("Optional: pitch offset per 1 deg/s (usually 0 for top-down).")]
    public float pitchFromTurn = 0.00f;

    [Header("Chain feel")]
    [Tooltip("How much additional sway the far segments get (0=uniform, 1=tip much larger).")]
    [Range(0f, 1f)]
    public float tipAmplify = 0.65f;

    [Tooltip("Extra delay along chain (seconds). 0.02~0.10 feels good.")]
    public float delayPerBone = 0.05f;

    [Header("Spring (whip / overshoot)")]
    [Tooltip("Higher = stiffer (less lag). 8~18 is common.")]
    public float spring = 12f;

    [Tooltip("Higher = more damping (less oscillation). 0.6~1.2 is common.")]
    public float damping = 0.9f;

    [Header("Optional: speed wave")]
    public bool enableWave = false;
    public float waveAmplitudeYaw = 6f;
    public float waveFrequency = 1.6f;
    public float wavePhasePerBone = 0.55f;

    [Header("Planar")]
    public bool planarOnly = true;

    [Header("Bind pose")]
    public bool restoreOnDisable = true;

    // runtime states
    private Quaternion[] _bindLocal;
    private Vector3[] _angles;      // x=pitch, y=yaw, z=roll (relative to bind)
    private Vector3[] _angVel;
    private bool _inited;

    // simple delay buffer for omega
    private float[] _omegaDelay;
    private int _omegaDelaySize;
    private int _omegaDelayHead;

    private void Awake() => Init();
    private void OnEnable() => Init();

    private void OnDisable()
    {
        if (restoreOnDisable) RestoreBindPose();
    }

    private void Init()
    {
        if (bones == null || bones.Length == 0) return;

        int n = bones.Length;

        _bindLocal = new Quaternion[n];
        _angles = new Vector3[n];
        _angVel = new Vector3[n];

        for (int i = 0; i < n; i++)
        {
            _bindLocal[i] = (bones[i] != null && bones[i].bone != null) ? bones[i].bone.localRotation : Quaternion.identity;
            _angles[i] = Vector3.zero;
            _angVel[i] = Vector3.zero;
        }

        // omega delay ring buffer
        // size = ceil(maxDelay / dt) is dynamic, but we choose a safe fixed length
        _omegaDelaySize = Mathf.Clamp(256, 128, 1024);
        _omegaDelay = new float[_omegaDelaySize];
        _omegaDelayHead = 0;

        _inited = true;
    }

    private void LateUpdate()
    {
        if (!_inited) Init();
        if (!_inited) return;
        if (bones == null || bones.Length == 0) return;
        if (movement == null) return;

        float dt = Time.deltaTime;
        if (dt <= 0f) return;
        //Debug.Log($"SoftSpine tick. movement={(movement?movement.name:"NULL")} bones={(bones==null? -1 : bones.Length)}");

        // Body angular velocity (deg/s), signed around up.
        float omega = movement.GetAngularVelocityDeg();
        //Debug.Log($"omega={omega}");

        // push into delay buffer
        _omegaDelay[_omegaDelayHead] = omega;
        _omegaDelayHead = (_omegaDelayHead + 1) % _omegaDelaySize;

        float speed01 = 0f;
        {
            float maxSpeed = Mathf.Max(movement.IsSprinting() ? 0.0001f : 0.0001f, 0.0001f);
            // You may replace with a real maxSpeed reference if you expose it.
            // For now, speed01 is estimated from current speed / sprintSpeed-ish:
            speed01 = Mathf.Clamp01(movement.GetCurrentSpeed() / Mathf.Max(0.001f, 4.0f));
        }

        Vector3 worldUp = GetWorldUp();

        int n = bones.Length;
        for (int i = 0; i < n; i++)
        {
            var b = bones[i];
            if (b == null || b.bone == null) continue;

            float w = Mathf.Clamp01(b.weight);
            if (w <= 0f) continue;

            // chain factor: near center small, near tip bigger
            float t = (n <= 1) ? 1f : (i / (float)(n - 1));
            float chainGain = Mathf.Lerp(1f, 1f + tipAmplify, t);

            // delayed omega for this bone
            float delayedOmega = SampleDelayedOmega(i, dt);

            // target offsets (degrees) driven by turn rate
            float targetYaw = -delayedOmega * yawFromTurn * chainGain;
            float targetRoll = -delayedOmega * rollFromTurn * chainGain;
            float targetPitch = -delayedOmega * pitchFromTurn * chainGain;

            // optional swimming wave (small, speed-scaled)
            if (enableWave)
            {
                float phase = -i * wavePhasePerBone;
                float wave = Mathf.Sin((Time.time * waveFrequency) + phase);
                targetYaw += wave * waveAmplitudeYaw * speed01 * chainGain;
            }

            // apply bone weight
            Vector3 targetAngles = new Vector3(targetPitch, targetYaw, targetRoll) * w;

            // spring (2nd order) toward targetAngles
            // simple stable spring integration in Euler-angle space (small angles assumption)
            StepSpring(ref _angles[i], ref _angVel[i], targetAngles, spring, damping, dt);

            // clamp relative angles if desired
            Vector3 a = _angles[i];
            if (b.useLimits)
            {
                a.y = Mathf.Clamp(a.y, -Mathf.Abs(b.yawLimit), Mathf.Abs(b.yawLimit));
                a.x = Mathf.Clamp(a.x, -Mathf.Abs(b.pitchLimit), Mathf.Abs(b.pitchLimit));
                a.z = Mathf.Clamp(a.z, -Mathf.Abs(b.rollLimit), Mathf.Abs(b.rollLimit));
            }

            // Build local delta rotation around this bone's configured axes
            // We apply yaw around "character up" expressed in bone-parent local space, to keep planar feel stable.
            Quaternion parentRot = b.bone.parent ? b.bone.parent.rotation : Quaternion.identity;
            Vector3 upInParentLocal = Quaternion.Inverse(parentRot) * worldUp;

            // Ensure axes are usable
            Vector3 fL = SafeNormalize(b.forwardLocal, Vector3.forward);
            Vector3 uL = SafeNormalize(b.upLocal, Vector3.up);
            if (Vector3.Cross(fL, uL).sqrMagnitude < 1e-6f)
                uL = (Mathf.Abs(Vector3.Dot(fL, Vector3.up)) < 0.95f) ? Vector3.up : Vector3.right;

            // Pitch around bone local "right" derived from (upLocal x forwardLocal)
            Vector3 rL = Vector3.Cross(uL, fL);
            if (rL.sqrMagnitude < 1e-6f) rL = Vector3.right;
            rL.Normalize();

            Quaternion delta = Quaternion.identity;

            // PlanarOnly: mainly yaw around worldUp (in parent-local), minimal pitch/roll unless you enabled them
            delta = Quaternion.AngleAxis(a.y, upInParentLocal.normalized) * delta;

            if (!planarOnly)
            {
                delta = Quaternion.AngleAxis(a.x, rL) * delta;   // pitch
                delta = Quaternion.AngleAxis(a.z, fL) * delta;   // roll
            }
            else
            {
                // Still allow small roll if you set rollFromTurn (nice for banking), but avoid pitch in top-down.
                if (Mathf.Abs(a.z) > 0.001f)
                    delta = Quaternion.AngleAxis(a.z, fL) * delta;
            }

            b.bone.localRotation = _bindLocal[i] * delta;
            //if(i==n-1) Debug.Log($"writing tail tip, a={a} targetYaw={targetYaw}");
        }
    }

    private Vector3 GetWorldUp()
    {
        Transform t = characterRoot != null ? characterRoot : transform;
        return t != null ? t.up : Vector3.up;
    }

    private float SampleDelayedOmega(int boneIndex, float dt)
    {
        // delay increases down the chain
        float delay = Mathf.Max(0f, delayPerBone) * boneIndex;

        // convert delay time to samples back in ring buffer
        int samplesBack = Mathf.Clamp(Mathf.RoundToInt(delay / Mathf.Max(1e-4f, dt)), 0, _omegaDelaySize - 1);

        int idx = _omegaDelayHead - 1 - samplesBack;
        while (idx < 0) idx += _omegaDelaySize;
        idx %= _omegaDelaySize;

        return _omegaDelay[idx];
    }

    private static void StepSpring(ref Vector3 x, ref Vector3 v, Vector3 target, float spring, float damping, float dt)
    {
        // Critically damped-ish spring toward target
        // x'' + 2*z*w*x' + w^2*(x-target)=0
        float w = Mathf.Max(0.01f, spring);
        float z = Mathf.Max(0.01f, damping);

        Vector3 f = (target - x) * (w * w);
        v += (f - 2f * z * w * v) * dt;
        x += v * dt;
    }

    private static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
    {
        if (v.sqrMagnitude < 1e-8f) return fallback;
        return v.normalized;
    }

    public void RestoreBindPose()
    {
        if (!_inited) return;
        if (bones == null || _bindLocal == null) return;

        int n = Mathf.Min(bones.Length, _bindLocal.Length);
        for (int i = 0; i < n; i++)
        {
            if (bones[i] != null && bones[i].bone != null)
                bones[i].bone.localRotation = _bindLocal[i];
        }
    }
}
