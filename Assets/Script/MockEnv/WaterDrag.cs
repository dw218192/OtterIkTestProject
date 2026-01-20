using UnityEngine;

/// <summary>
/// Water resistance model for a Rigidbody.
///
/// Applies:
/// - Linear drag:    F = -k * v
/// - Quadratic drag: F = -k2 * |v| * v
///
/// Notes:
/// - Default coefficients are tuned for "reach then cruise" with impulse propulsion.
/// - In top-down surface swimming, we usually apply drag on horizontal (XZ) only.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(100)]
public class WaterDrag : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Rigidbody rb;

    [Header("Linear drag (N per (m/s))")]
    [Tooltip("Higher slows more at low speeds. Too high makes start/cruise feel tractor-like.")]
    [SerializeField] private float linear = 3.0f;

    [Header("Quadratic drag (N per (m/s)^2)")]
    [Tooltip("Higher slows MUCH more at high speeds, providing a natural terminal speed.")]
    [SerializeField] private float quadratic = 0.5f;

    [Header("Angular drag (torque)")]
    [SerializeField] private float angularLinear = 1.0f;
    [SerializeField] private float angularQuadratic = 0.0f;

    [Header("Plane")]
    [SerializeField] private bool horizontalOnly = true;

    private void Reset() => rb = GetComponent<Rigidbody>();

    private void Awake()
    {
        if (rb == null) rb = GetComponent<Rigidbody>();
    }

    private void FixedUpdate()
    {
        if (rb == null) return;

        // Velocity drag
        Vector3 v = rb.velocity;
        if (horizontalOnly) v.y = 0f;

        float speed = v.magnitude;
        if (speed > 1e-4f)
        {
            // F = -(linear + quadratic*|v|) * v
            float coeff = Mathf.Max(0f, linear) + Mathf.Max(0f, quadratic) * speed;
            rb.AddForce(-coeff * v, ForceMode.Force);
        }

        // Angular drag (optional)
        Vector3 w = rb.angularVelocity;
        if (horizontalOnly)
        {
            w.x = 0f;
            w.z = 0f;
        }

        float wMag = w.magnitude;
        if (wMag > 1e-4f)
        {
            float coeff = Mathf.Max(0f, angularLinear) + Mathf.Max(0f, angularQuadratic) * wMag;
            rb.AddTorque(-coeff * w, ForceMode.Force);
        }
    }

    public void SetCoefficients(float linearCoeff, float quadraticCoeff)
    {
        linear = Mathf.Max(0f, linearCoeff);
        quadratic = Mathf.Max(0f, quadraticCoeff);
    }

    public float Linear => linear;
    public float Quadratic => quadratic;
}
