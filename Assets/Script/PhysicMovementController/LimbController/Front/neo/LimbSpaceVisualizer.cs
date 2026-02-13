using UnityEngine;

/// <summary>
/// 架构修正版 Visualizer：
/// 1. 输入源锁定为 ikSource 维护的稳定参考系。
/// 2. 专注外侧活跃状态。
/// 3. 强制向下（腹侧）翻转。
/// </summary>
public class OtterLimbStableVisualizer : MonoBehaviour
{
    [Header("核心输入源 (Stable System)")]
    public DynamicIndirectIK_Stabilized ikSource;

    [Header("阈值与尺寸")]
    public float alphaReturnThreshold = 1.2f;
    public float planeSize = 0.5f;

    private void OnDrawGizmos()
    {
        // 检查 ikSource 及其引用的稳定节点是否有效
        if (ikSource == null || ikSource.spineSource == null || ikSource.leftShoulder == null) return;

        // 获取稳定参考系的方向（Shadow/Stable 节点）
        Vector3 stableForward = ikSource.spineSource.transform.forward; 
        Vector3 stableUp = ikSource.spineSource.transform.up;

        // 计算物理参考中心
        Vector3 center = (ikSource.leftShoulder.position + ikSource.rightShoulder.position) * 0.5f;

        // 独立绘制左右
        DrawStablePlane(true, center, stableForward, stableUp, ikSource.isLeftOuter);
        DrawStablePlane(false, center, stableForward, stableUp, ikSource.isRightOuter);
    }

    private void DrawStablePlane(bool isLeft, Vector3 center, Vector3 sForward, Vector3 sUp, bool isOuter)
    {
        // 锚点依然在物理肩膀，但旋转逻辑参考稳定坐标系
        Transform sJoint = isLeft ? ikSource.leftShoulder : ikSource.rightShoulder;
        Vector3 lagPoint = GetLagPoint(isLeft);

        // 1. 物理向量采样 (参考稳定中心)
        Vector3 vecS = (sJoint.position - center).normalized;
        Vector3 vecL = (lagPoint - center).normalized;
        float alpha = Vector3.Angle(vecS, vecL);

        Vector3 finalNormal;
        Color gizmoColor;

        // 2. 状态判定：外侧活跃 vs 稳定回归
        if (isOuter && alpha > alphaReturnThreshold)
        {
            // 物理活跃态：三点定面
            Vector3 rawNormal = Vector3.Cross(vecS, vecL).normalized;
            
            // 核心约束：统一向腹部（Stable Down）翻转
            if (Vector3.Dot(rawNormal, sUp) > 0) rawNormal = -rawNormal;
            
            finalNormal = rawNormal;
            gizmoColor = new Color(1f, 0.2f, 0.2f, 0.6f); // 活跃红色
        }
        else
        {
            // 稳定回归态：直接对齐稳定坐标系的下方向
            finalNormal = -sUp; 
            gizmoColor = new Color(0.2f, 0.8f, 1f, 0.15f); // 静息青色
        }

        // 3. 构建坐标系：锁定稳定前向 (sForward)
        // 这样在连续 Roll 时，平面的 Z 轴是绝对纯净的
        Vector3 tRight = Vector3.Cross(finalNormal, sForward).normalized;
        Vector3 tForward = Vector3.Cross(tRight, finalNormal).normalized;
        Quaternion rot = Quaternion.LookRotation(tForward, finalNormal);

        // 4. 可视化绘制
        Gizmos.matrix = Matrix4x4.TRS(sJoint.position, rot, Vector3.one);
        
        Gizmos.color = gizmoColor;
        Gizmos.DrawCube(Vector3.zero, new Vector3(planeSize, 0.01f, planeSize));
        
        // 边框反馈
        Gizmos.color = (isOuter && alpha > alphaReturnThreshold) ? Color.red : Color.cyan;
        Gizmos.DrawWireCube(Vector3.zero, new Vector3(planeSize, 0.01f, planeSize));

        Gizmos.matrix = Matrix4x4.identity;
    }

    private Vector3 GetLagPoint(bool isLeft)
    {
        // 从 ikSource 获取由 Shadow/Stable 逻辑驱动的滞后点
        var field = ikSource.GetType().GetField(isLeft ? "worldLeftPoint" : "worldRightPoint", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return field != null ? (Vector3)field.GetValue(ikSource) : Vector3.zero;
    }
}