using UnityEngine;

public class IKJointTwistDebugger : MonoBehaviour
{
    [System.Serializable]
    public class Joint
    {
        public string name;
        public Transform t;

        [Header("Axis Gizmo (optional)")]
        public float axisLen = 0.08f;
    }

    public Joint[] joints;

    [Header("Bind capture")]
    public bool recaptureBindNow = false;

    [Header("Debug output")]
    public bool logToConsole = false;
    public float logInterval = 0.25f;

    private Quaternion[] _bindLocal;
    private float _logTimer;

    void Start()
    {
        CaptureBind();
    }

    void Update()
    {
        if (recaptureBindNow)
        {
            recaptureBindNow = false;
            CaptureBind();
        }

        if (!logToConsole) return;

        _logTimer += Time.deltaTime;
        if (_logTimer >= logInterval)
        {
            _logTimer = 0f;
            PrintDeltas();
        }
    }

    void OnDrawGizmos()
    {
        if (joints == null) return;

        for (int i = 0; i < joints.Length; i++)
        {
            var j = joints[i];
            if (j == null || j.t == null) continue;

            // Draw axes
            var p = j.t.position;
            Gizmos.color = Color.red;   Gizmos.DrawLine(p, p + j.t.right   * j.axisLen);
            Gizmos.color = Color.green; Gizmos.DrawLine(p, p + j.t.up      * j.axisLen);
            Gizmos.color = Color.blue;  Gizmos.DrawLine(p, p + j.t.forward * j.axisLen);

#if UNITY_EDITOR
            // Show angle info in Scene view
            if (_bindLocal != null && i < _bindLocal.Length)
            {
                var delta = Quaternion.Inverse(_bindLocal[i]) * j.t.localRotation;
                float angle = Quaternion.Angle(Quaternion.identity, delta);
                Vector3 e = NormalizeEuler(delta.eulerAngles);

                UnityEditor.Handles.color = Color.white;
                UnityEditor.Handles.Label(p, $"{(string.IsNullOrEmpty(j.name)? j.t.name : j.name)}\n" +
                                            $"ΔAngle: {angle:0.00}°\n" +
                                            $"ΔEuler: ({e.x:0.0},{e.y:0.0},{e.z:0.0})");
            }
#endif
        }
    }

    private void CaptureBind()
    {
        if (joints == null) return;
        _bindLocal = new Quaternion[joints.Length];

        for (int i = 0; i < joints.Length; i++)
        {
            _bindLocal[i] = (joints[i] != null && joints[i].t != null)
                ? joints[i].t.localRotation
                : Quaternion.identity;
        }

        if (logToConsole)
            Debug.Log("[IKJointTwistDebugger] Bind pose captured.");
    }

    private void PrintDeltas()
    {
        if (_bindLocal == null || joints == null) return;

        for (int i = 0; i < joints.Length; i++)
        {
            var j = joints[i];
            if (j == null || j.t == null) continue;

            var delta = Quaternion.Inverse(_bindLocal[i]) * j.t.localRotation;
            float angle = Quaternion.Angle(Quaternion.identity, delta);
            Vector3 e = NormalizeEuler(delta.eulerAngles);

            Debug.Log($"[{(string.IsNullOrEmpty(j.name)? j.t.name : j.name)}] " +
                      $"ΔAngle={angle:0.00}°, ΔEuler=({e.x:0.0},{e.y:0.0},{e.z:0.0})");
        }
    }

    private static Vector3 NormalizeEuler(Vector3 e)
    {
        e.x = NormalizeAngle(e.x);
        e.y = NormalizeAngle(e.y);
        e.z = NormalizeAngle(e.z);
        return e;
    }

    private static float NormalizeAngle(float a)
    {
        while (a > 180f) a -= 360f;
        while (a < -180f) a += 360f;
        return a;
    }
}
