using UnityEngine;

/// <summary>
/// Simple water resistance model for a Rigidbody.
///
/// Applies a combination of:
/// - Linear drag:     F = -k * v
/// - Quadratic drag:  F = -k2 * |v| * v
/// Optionally applies angular drag as torque in the same manner.
///
/// Designed for top-down "on the surface" swimming:
/// - By default it only considers horizontal (XZ) velocity.
/// - Pair with Rigidbody constraints (Freeze Y, Freeze X/Z rotation) for stability.
///
/// Put this on the same GameObject as the Rigidbody.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(100)]
public class WaterDrag : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Rigidbody rb;

    [Header("Linear drag (N per (m/s))")]
    [Tooltip("Higher = slows more at all speeds.")]
    [SerializeField] private float linear = 6.0f;

    [Header("Quadratic drag (N per (m/s)^2)")]
    [Tooltip("Higher = slows MUCH more at high speeds (more water-like).")]
    [SerializeField] private float quadratic = 0.0f;

    [Header("Angular drag (torque)")]
    [Tooltip("Linear angular drag coefficient (torque per (rad/s)).")]
    [SerializeField] private float angularLinear = 1.5f;

    [Tooltip("Quadratic angular drag coefficient (torque per (rad/s)^2).")]
    [SerializeField] private float angularQuadratic = 0.0f;

    [Header("Plane")]
    [Tooltip("Only apply drag to horizontal velocity (XZ).")]
    [SerializeField] private bool horizontalOnly = true;

    private void Reset()
    {
        rb = GetComponent<Rigidbody>();
    }

    private void Awake()
    {
        if (rb == null) rb = GetComponent<Rigidbody>();
    }

    private void FixedUpdate()
    {
        if (rb == null) return;

        // -------- Linear + quadratic drag on velocity --------
        Vector3 v = rb.velocity;
        if (horizontalOnly) v.y = 0f;

        float speed = v.magnitude;
        if (speed > 1e-4f)
        {
            // F = -(linear + quadratic*|v|) * v
            float coeff = linear + quadratic * speed;
            Vector3 dragForce = -coeff * v;
            rb.AddForce(dragForce, ForceMode.Force);
        }

        // -------- Angular drag (optional) --------
        Vector3 w = rb.angularVelocity; // rad/s
        if (horizontalOnly)
        {
            // In top-down swim we typically only care about yaw (y axis).
            w.x = 0f;
            w.z = 0f;
        }

        float wMag = w.magnitude;
        if (wMag > 1e-4f)
        {
            float coeff = angularLinear + angularQuadratic * wMag;
            Vector3 dragTorque = -coeff * w;
            rb.AddTorque(dragTorque, ForceMode.Force);
        }
    }

    // Convenience API for runtime tuning
    public void SetCoefficients(float linearCoeff, float quadraticCoeff)
    {
        linear = Mathf.Max(0f, linearCoeff);
        quadratic = Mathf.Max(0f, quadraticCoeff);
    }

    public float Linear => linear;
    public float Quadratic => quadratic;
}
