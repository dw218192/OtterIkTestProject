using UnityEngine;

[ExecuteAlways]
public class EllipsoidGizmoFollower : MonoBehaviour
{
    [Header("Local ellipsoid (like a child)")]
    [Tooltip("Ellipsoid center in LOCAL space of this object (like a child offset).")]
    public Vector3 localCenter = Vector3.zero;

    [Tooltip("Ellipsoid radii along LOCAL X/Y/Z (in meters).")]
    public Vector3 localRadii = new Vector3(1f, 0.5f, 0.8f);

    [Header("Draw")]
    public int segments = 64;
    public Color color = new Color(1f, 0f, 1f, 0.9f);

    [Tooltip("If true, radii will be affected by this object's lossyScale (like a real child).")]
    public bool includeScale = true;

    void OnDrawGizmos()
    {
        if (segments < 12) segments = 12;

        Gizmos.color = color;

        // Build a transform matrix that behaves like a child:
        // world = parentTRS * localCenter * (optional scale) * unitSphere
        Vector3 worldCenter = transform.TransformPoint(localCenter);

        Vector3 r = localRadii;
        if (includeScale)
        {
            // "Child-like" scaling in world space
            Vector3 s = transform.lossyScale;
            r = new Vector3(r.x * s.x, r.y * s.y, r.z * s.z);
        }

        // Draw 3 great circles (XY, XZ, YZ) to look like a wireframe ellipsoid
        DrawEllipse(worldCenter, transform.rotation, Vector3.right, Vector3.up, r.x, r.y, segments);
        DrawEllipse(worldCenter, transform.rotation, Vector3.right, Vector3.forward, r.x, r.z, segments);
        DrawEllipse(worldCenter, transform.rotation, Vector3.up, Vector3.forward, r.y, r.z, segments);
    }

    static void DrawEllipse(
        Vector3 center,
        Quaternion rotation,
        Vector3 localAxisA,
        Vector3 localAxisB,
        float radiusA,
        float radiusB,
        int segments)
    {
        Vector3 prev = center + rotation * (localAxisA * radiusA);

        for (int i = 1; i <= segments; i++)
        {
            float t = (i / (float)segments) * Mathf.PI * 2f;
            float ca = Mathf.Cos(t);
            float sa = Mathf.Sin(t);

            Vector3 localPoint = localAxisA * (ca * radiusA) + localAxisB * (sa * radiusB);
            Vector3 curr = center + rotation * localPoint;

            Gizmos.DrawLine(prev, curr);
            prev = curr;
        }
    }
}
