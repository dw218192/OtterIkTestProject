using UnityEngine;

[ExecuteAlways]
public class FrontLimbTrajectoryNeo : MonoBehaviour
{
    [Header("核心输入源")]
    public DynamicIndirectIK_Stabilized ikSource;

    [Header("1. 休息点锚点 (Transform)")]
    public Transform leftRestAnchor;
    public Transform rightRestAnchor;

    [Header("2. 轨道几何预设 (类似子物体的 Local 设定)")]
    public Vector2 curveSize = new Vector2(0.25f, 0.35f);
    
    [Tooltip("相对于中心节点的偏移 (X:右, Y:上, Z:前)。")]
    public Vector3 worldStyleOffset = new Vector3(0.12f, -0.15f, 0.05f);

    [Header("3. 动态控制 (基于世界/本地混合)")]
    [Range(0, 1)] public float alignmentWeight = 0.3f; 
    [Range(0, 1)] public float smoothness = 0.1f;    

    private Quaternion _leftCurrentRot = Quaternion.identity;
    private Quaternion _rightCurrentRot = Quaternion.identity;
    
    [SerializeField, HideInInspector] 
    private Vector3 _localCenterOffset; 

    private void OnValidate() { UpdateLocalOffset(); }
    private void Start() { UpdateLocalOffset(); }

    private void UpdateLocalOffset()
    {
        // 预设逻辑：锁定基于胸腔坐标系的局部偏移
        if (ikSource && ikSource.spineSource)
            _localCenterOffset = ikSource.spineSource.transform.InverseTransformDirection(worldStyleOffset);
    }

    public Vector3 GetRestWorldPos(bool isLeft)
    {
        Transform anchor = isLeft ? leftRestAnchor : rightRestAnchor;
        return anchor != null ? anchor.position : (isLeft ? ikSource.leftShoulder.position : ikSource.rightShoulder.position);
    }

    public Vector3 GetWorldPoint(bool isLeft, float theta)
    {
        if (!ikSource || !ikSource.spineSource) return Vector3.zero;
        Transform sJoint = isLeft ? ikSource.leftShoulder : ikSource.rightShoulder;
        Transform root = ikSource.spineSource.transform;

        // --- 第一重：子物体位置随动 ---
        Vector3 mOffset = _localCenterOffset;
        if (isLeft) mOffset.x *= -1;
        Vector3 centerW = sJoint.position + root.TransformDirection(mOffset);

        // --- 第二重：子物体旋转随动 + 动态对齐 ---
        
        // 1. 默认状态：平行于水面 (XZ平面)
        // 采用你提供的 DrawEllipse 思路：我们希望椭圆面平行于地面。
        // 在局部空间中，XY 为平面，Z 为法线。
        // 为了让法线 Z 指向世界/角色的 Up，需要将 root.rotation 旋转 90 度。
        Quaternion defaultRot = root.rotation * Quaternion.Euler(90, 0, 0);

        // 2. 目标状态：对齐物理拨水平面 (转向平面)
        Vector3 swingNormalW = GetPhysicalNormal(isLeft, sJoint, root);
        // 让轨迹的法线 (Z) 向物理法线方向翻转
        Quaternion targetRot = Quaternion.LookRotation(root.forward, -swingNormalW);

        // 3. 混合与平滑
        ref Quaternion currentRot = ref (isLeft ? ref _leftCurrentRot : ref _rightCurrentRot);
        Quaternion weightedTarget = Quaternion.Slerp(defaultRot, targetRot, alignmentWeight);
        
        float lerpFactor = Application.isPlaying ? smoothness : 1.0f;
        currentRot = Quaternion.Slerp(currentRot, weightedTarget, lerpFactor);

        // 4. 生成轨迹坐标
        // 这里完全采用你提供的 DrawEllipse 里的参数化坐标逻辑
        Vector3 localEllipsePoint = new Vector3(Mathf.Cos(theta) * curveSize.x, Mathf.Sin(theta) * curveSize.y, 0);
        return centerW + currentRot * localEllipsePoint;
    }

    private Vector3 GetPhysicalNormal(bool isLeft, Transform sJoint, Transform root)
    {
        Vector3 center = (ikSource.leftShoulder.position + ikSource.rightShoulder.position) * 0.5f;
        var field = ikSource.GetType().GetField(isLeft ? "worldLeftPoint" : "worldRightPoint", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Vector3 lagPoint = (field != null) ? (Vector3)field.GetValue(ikSource) : Vector3.zero;
        
        Vector3 n = Vector3.Cross((sJoint.position - center).normalized, (lagPoint - center).normalized).normalized;
        return (Vector3.Dot(n, root.up) > 0) ? -n : n;
    }

    public Vector3 GetStartPoint(bool isLeft)
    {
        Vector3 restPos = GetRestWorldPos(isLeft); 
        float bestT = 0; float minDist = float.MaxValue;
        for (int i = 0; i < 24; i++) {
            float t = (i / 24f) * Mathf.PI * 2f;
            Vector3 p = GetWorldPoint(isLeft, t);
            float d = Vector3.Distance(p, restPos);
            if (d < minDist) { minDist = d; bestT = t; }
        }
        return GetWorldPoint(isLeft, bestT);
    }
}