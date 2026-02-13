using UnityEngine;

[ExecuteAlways]
public class FrontLimbVisualizerNeo : MonoBehaviour
{
    public FrontLimbTrajectoryNeo trajectory;

    private void OnDrawGizmos()
    {
        if (!trajectory || !trajectory.ikSource || !trajectory.ikSource.spineSource) return;

        DrawLimbUI(true);
        DrawLimbUI(false);
    }

    private void DrawLimbUI(bool isLeft)
    {
        bool isOuter = isLeft ? trajectory.ikSource.isLeftOuter : trajectory.ikSource.isRightOuter;
        Color themeColor = isOuter ? Color.red : Color.blue;

        // 1. 绘制椭圆轨道
        Gizmos.color = themeColor;
        Vector3 prev = trajectory.GetWorldPoint(isLeft, 0);
        for (int i = 1; i <= 64; i++) {
            Vector3 next = trajectory.GetWorldPoint(isLeft, (i / 64f) * Mathf.PI * 2f);
            Gizmos.DrawLine(prev, next);
            prev = next;
        }

        // 2. 绘制休息点
        Vector3 restP = trajectory.GetRestWorldPos(isLeft); 
        Vector3 startP = trajectory.GetStartPoint(isLeft);  
        
        Gizmos.color = Color.green; 
        Gizmos.DrawSphere(restP, 0.02f);
        Gizmos.color = Color.yellow; 
        Gizmos.DrawSphere(startP, 0.02f);
        
        Gizmos.color = new Color(1, 1, 1, 0.2f);
        Gizmos.DrawLine(restP, startP);
    }
}