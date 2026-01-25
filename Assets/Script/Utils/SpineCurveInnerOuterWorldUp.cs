using UnityEngine;

/// <summary>
/// Robust inner/outer side detection from a spine curve using WORLD UP (Vector3.up).
/// - Uses three points on the spine: first / mid / last.
/// - Computes circumcenter in the horizontal plane (world up).
/// - The leg root closer to the curvature center is considered INNER.
/// - If spine is nearly straight (collinear), hasTurn=false.
/// 
/// NOTE: This is intentionally independent from hip/root axes. It uses world up only.
/// </summary>
public static class SpineCurveInnerOuterWorldUp
{
    public struct Result
    {
        public bool hasTurn;          // stable enough to classify
        public bool leftIsInner;      // valid only when hasTurn=true
        public Vector3 centerWorld;   // circumcenter on plane (y = planeY)
        public float bendAngleDeg;    // unsigned bend angle (0..180)
        public float stability01;     // heuristic (0..1), bigger is better
    }

    /// <summary>
    /// Determine inner/outer using world-up plane and leg roots.
    /// </summary>
    public static Result Evaluate(
        Transform[] spineChain,
        Transform leftLegRoot,
        Transform rightLegRoot,
        float planeY,
        float minBendAngleDeg = 2.0f,
        float minAreaEps = 1e-6f)
    {
        Result r = new Result
        {
            hasTurn = false,
            leftIsInner = false,
            centerWorld = Vector3.zero,
            bendAngleDeg = 0f,
            stability01 = 0f
        };

        if (spineChain == null || spineChain.Length < 3 || leftLegRoot == null || rightLegRoot == null)
            return r;

        Transform aT = spineChain[0];
        Transform bT = spineChain[spineChain.Length / 2];
        Transform cT = spineChain[spineChain.Length - 1];

        if (aT == null || bT == null || cT == null)
            return r;

        // Project positions onto world-up plane at y = planeY (XZ plane slice)
        Vector3 A = new Vector3(aT.position.x, planeY, aT.position.z);
        Vector3 B = new Vector3(bT.position.x, planeY, bT.position.z);
        Vector3 C = new Vector3(cT.position.x, planeY, cT.position.z);

        // Compute bend angle (unsigned) in plane
        Vector3 v0 = (B - A);
        Vector3 v1 = (C - B);
        v0.y = 0f; v1.y = 0f;

        float v0m = v0.magnitude;
        float v1m = v1.magnitude;
        if (v0m < 1e-6f || v1m < 1e-6f) return r;

        v0 /= v0m;
        v1 /= v1m;
        float bend = Vector3.Angle(v0, v1);
        r.bendAngleDeg = bend;

        if (bend < minBendAngleDeg)
            return r;

        // Circumcenter on plane (world up)
        if (!TryCircumcenterXZ(A, B, C, minAreaEps, out Vector3 center))
            return r;

        r.centerWorld = center;

        // Compare leg roots distance to center (also on same planeY)
        Vector3 L = new Vector3(leftLegRoot.position.x, planeY, leftLegRoot.position.z);
        Vector3 R = new Vector3(rightLegRoot.position.x, planeY, rightLegRoot.position.z);

        float dL = (L - center).sqrMagnitude;
        float dR = (R - center).sqrMagnitude;

        // INNER = closer to curvature center
        r.leftIsInner = dL < dR;
        r.hasTurn = true;

        // Heuristic stability: based on triangle area vs edge lengths
        r.stability01 = ComputeStability01(A, B, C);

        return r;
    }

    /// <summary>
    /// Compute circumcenter of triangle ABC in XZ plane (Y ignored, assumed same).
    /// Returns false if triangle is nearly collinear.
    /// </summary>
    private static bool TryCircumcenterXZ(Vector3 A, Vector3 B, Vector3 C, float minAreaEps, out Vector3 center)
    {
        // Work in 2D (x,z)
        float ax = A.x, az = A.z;
        float bx = B.x, bz = B.z;
        float cx = C.x, cz = C.z;

        // Determinant (2D cross) to detect collinearity
        float d = 2f * (ax * (bz - cz) + bx * (cz - az) + cx * (az - bz));
        if (Mathf.Abs(d) < minAreaEps)
        {
            center = default;
            return false;
        }

        float a2 = ax * ax + az * az;
        float b2 = bx * bx + bz * bz;
        float c2 = cx * cx + cz * cz;

        float ux = (a2 * (bz - cz) + b2 * (cz - az) + c2 * (az - bz)) / d;
        float uz = (a2 * (cx - bx) + b2 * (ax - cx) + c2 * (bx - ax)) / d;

        center = new Vector3(ux, A.y, uz);
        return true;
    }

    private static float ComputeStability01(Vector3 A, Vector3 B, Vector3 C)
    {
        // Area proxy in XZ
        Vector2 a = new Vector2(A.x, A.z);
        Vector2 b = new Vector2(B.x, B.z);
        Vector2 c = new Vector2(C.x, C.z);

        float area2 = Mathf.Abs(Cross(b - a, c - a)); // 2 * area
        float ab = (b - a).magnitude;
        float bc = (c - b).magnitude;
        float ca = (a - c).magnitude;

        float denom = Mathf.Max(1e-6f, (ab + bc + ca));
        float s = Mathf.Clamp01(area2 / (denom * denom)); // scale-invariant-ish
        return s;
    }

    private static float Cross(Vector2 u, Vector2 v) => u.x * v.y - u.y * v.x;
}
