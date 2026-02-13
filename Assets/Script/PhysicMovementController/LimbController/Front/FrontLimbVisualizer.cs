using UnityEngine;

[ExecuteAlways]
public class FrontLimbVisualizer : MonoBehaviour
{
    public FrontLimbTrajectory trajectory;
    [Range(16, 240)] public int segments = 96;

    [Header("Plane Gizmo")]
    public float planeSize = 0.20f;
    public Color normalColor = new Color(0.2f, 1f, 0.2f, 1f);
    public Color uColor = new Color(1f, 0.9f, 0.1f, 1f);
    public Color vColor = new Color(0.2f, 0.9f, 1f, 1f);
    public Color planeColor = new Color(1f, 1f, 1f, 0.25f);

    private void OnDrawGizmos()
    {
        if (!trajectory || !trajectory.ikSource) return;
        DrawLimb(true);
        DrawLimb(false);
    }

    private void DrawLimb(bool isLeft)
    {
        var ik = trajectory.ikSource;
        Transform sJoint = isLeft ? ik.leftShoulder : ik.rightShoulder;
        if (!sJoint) return;

        bool isOuter = isLeft ? ik.isLeftOuter : ik.isRightOuter;
        Color themeColor = isOuter ? Color.red : Color.blue;

        // centerW：必须和 Trajectory 一致（localCenter + TransformPoint）
        Vector3 lc = trajectory.LocalCenterBaked;
        if (isLeft) lc.x *= -1f;
        Vector3 centerW = sJoint.TransformPoint(lc);

        // 1) 椭球体
        Quaternion ellipsoidRotW = trajectory.GetEllipsoidWorldRotation(isLeft);
        Gizmos.matrix = Matrix4x4.TRS(centerW, ellipsoidRotW, trajectory.radius);
        Gizmos.color = new Color(themeColor.r, themeColor.g, themeColor.b, 0.12f);
        Gizmos.DrawWireSphere(Vector3.zero, 1f);
        Gizmos.matrix = Matrix4x4.identity;

        // 2) 平面/基底
        if (trajectory.TryGetPlaneFrame(isLeft, out Vector3 cW, out Vector3 u, out Vector3 v, out Vector3 n))
        {
            Gizmos.color = normalColor;
            Gizmos.DrawLine(cW, cW + n * planeSize * 1.2f);

            Gizmos.color = uColor;
            Gizmos.DrawLine(cW, cW + u * planeSize);

            Gizmos.color = vColor;
            Gizmos.DrawLine(cW, cW + v * planeSize);

            Gizmos.color = planeColor;
            Vector3 p0 = cW + ( u + v) * planeSize;
            Vector3 p1 = cW + ( u - v) * planeSize;
            Vector3 p2 = cW + (-u - v) * planeSize;
            Vector3 p3 = cW + (-u + v) * planeSize;

            Gizmos.DrawLine(p0, p1);
            Gizmos.DrawLine(p1, p2);
            Gizmos.DrawLine(p2, p3);
            Gizmos.DrawLine(p3, p0);
        }

        // 3) 截线
        Gizmos.color = themeColor;
        Vector3 prev = trajectory.GetWorldPoint(isLeft, 0f);
        for (int i = 1; i <= segments; i++)
        {
            float t = (i / (float)segments) * Mathf.PI * 2f;
            Vector3 next = trajectory.GetWorldPoint(isLeft, t);
            Gizmos.DrawLine(prev, next);
            prev = next;
        }

        // 4) lag anchor（方便验证外侧判定）
        Vector3 lag = isLeft ? ik.WorldLeftPoint : ik.WorldRightPoint;
        Gizmos.color = isOuter ? new Color(1f, 0.2f, 0.2f, 1f) : new Color(0.6f, 0.6f, 0.6f, 0.4f);
        Gizmos.DrawSphere(lag, 0.03f);
        if (isOuter) Gizmos.DrawLine(sJoint.position, lag);
    }
}
