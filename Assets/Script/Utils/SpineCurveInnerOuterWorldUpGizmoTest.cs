using UnityEngine;

/// <summary>
/// Gizmo test for SpineCurveInnerOuterWorldUp.
/// Attach to any object; assign spineChain and leg roots.
/// Visualizes:
/// - A/B/C spine points (first/mid/last)
/// - curvature center
/// - inner/outer color on leg root spheres
/// </summary>
[ExecuteAlways]
public class SpineCurveInnerOuterWorldUpGizmoTest : MonoBehaviour
{
    [Header("Inputs")]
    public Transform[] spineChain;
    public Transform leftLegRoot;
    public Transform rightLegRoot;

    [Header("Plane (World Up)")]
    [Tooltip("If set, planeY is taken from this transform's Y (recommended: hip/root). Otherwise uses this component transform Y.")]
    public Transform planeYFrom;

    [Header("Thresholds")]
    public float minBendAngleDeg = 2.0f;
    public float minAreaEps = 1e-6f;

    [Header("Gizmos")]
    public bool draw = true;
    public float spinePointRadius = 0.02f;
    public float legRootRadius = 0.03f;
    public float centerRadius = 0.03f;

    public bool drawLinesToCenter = true;

    private void OnDrawGizmos()
    {
        if (!draw) return;

        float planeY = (planeYFrom != null) ? planeYFrom.position.y : transform.position.y;

        var res = SpineCurveInnerOuterWorldUp.Evaluate(
            spineChain,
            leftLegRoot,
            rightLegRoot,
            planeY,
            minBendAngleDeg,
            minAreaEps
        );

        // Draw spine sample points
        if (spineChain != null && spineChain.Length >= 3)
        {
            Transform aT = spineChain[0];
            Transform bT = spineChain[spineChain.Length / 2];
            Transform cT = spineChain[spineChain.Length - 1];

            if (aT != null) DrawPoint(aT.position, Color.white, spinePointRadius);
            if (bT != null) DrawPoint(bT.position, Color.white, spinePointRadius);
            if (cT != null) DrawPoint(cT.position, Color.white, spinePointRadius);

            if (aT != null && bT != null) DrawLine(aT.position, bT.position, new Color(1,1,1,0.35f));
            if (bT != null && cT != null) DrawLine(bT.position, cT.position, new Color(1,1,1,0.35f));
        }

        // Draw center
        if (res.hasTurn)
        {
            DrawPoint(res.centerWorld, new Color(0.8f, 0.2f, 1.0f, 1f), centerRadius);

            // Leg spheres with inner/outer color
            if (leftLegRoot != null)
            {
                Color c = res.leftIsInner ? new Color(1f, 0.25f, 0.25f, 1f) : new Color(0.2f, 0.7f, 1.0f, 0.95f);
                DrawPoint(leftLegRoot.position, c, legRootRadius);

                if (drawLinesToCenter)
                    DrawLine(leftLegRoot.position, res.centerWorld, new Color(c.r, c.g, c.b, 0.4f));
            }

            if (rightLegRoot != null)
            {
                bool rightIsInner = !res.leftIsInner;
                Color c = rightIsInner ? new Color(1f, 0.25f, 0.25f, 1f) : new Color(0.2f, 0.7f, 1.0f, 0.95f);
                DrawPoint(rightLegRoot.position, c, legRootRadius);

                if (drawLinesToCenter)
                    DrawLine(rightLegRoot.position, res.centerWorld, new Color(c.r, c.g, c.b, 0.4f));
            }
        }
        else
        {
            // No stable turn: both yellow
            if (leftLegRoot != null) DrawPoint(leftLegRoot.position, new Color(1.0f, 0.9f, 0.2f, 0.95f), legRootRadius);
            if (rightLegRoot != null) DrawPoint(rightLegRoot.position, new Color(1.0f, 0.9f, 0.2f, 0.95f), legRootRadius);
        }
    }

    private static void DrawPoint(Vector3 p, Color c, float r)
    {
        Gizmos.color = c;
        Gizmos.DrawSphere(p, r);
    }

    private static void DrawLine(Vector3 a, Vector3 b, Color c)
    {
        Gizmos.color = c;
        Gizmos.DrawLine(a, b);
    }
}
