using UnityEngine;
using System.Collections.Generic;

public class SpineDecouplerWithShadows : MonoBehaviour
{
    [Header("配置")]
    public Transform[] spineJoints;
    
    [Tooltip("解耦后的脊椎根节点将挂载到此物体下。若为空，则默认为当前物体。")]
    public Transform customRuntimeParent;

    [Header("运行时调试 (无需手动赋值)")]
    public List<Transform> shadowNodes = new List<Transform>();
    
    private Dictionary<Transform, Transform> originalParents = new Dictionary<Transform, Transform>();
    private GameObject runtimeRoot;

    void Awake()
    {
        if (spineJoints == null || spineJoints.Length == 0) return;

        // 1. 初始化 Runtime 根节点
        runtimeRoot = new GameObject("Spine_Runtime_Root");
        
        // 设置用户自定义的父物体
        Transform finalParent = (customRuntimeParent != null) ? customRuntimeParent : this.transform;
        runtimeRoot.transform.SetParent(finalParent, false);
        
        // 保证 RuntimeRoot 的初始位姿是干净的（可选，通常建议 Reset）
        runtimeRoot.transform.localPosition = Vector3.zero;
        runtimeRoot.transform.localRotation = Quaternion.identity;

        // 2. 遍历并创建影子节点
        foreach (var joint in spineJoints)
        {
            if (joint == null) continue;

            // 备份原始层级
            originalParents[joint] = joint.parent;

            // 创建影子父节点
            GameObject shadow = new GameObject(joint.name + "_Shadow");
            
            // 关键优化：影子节点在层级中保持“干净”的坐标系（继承 RuntimeRoot 的旋转：localRotation = Identity）
            // worldPositionStays = false：确保 shadow 的 localRotation / localScale 不被“烘焙”进来
            shadow.transform.SetParent(runtimeRoot.transform, false);
            shadow.transform.position = joint.position;                 // 位置对齐到 joint 的世界位置
            shadow.transform.localRotation = Quaternion.identity;       // 旋转保持为 Identity（相对 RuntimeRoot）
            shadow.transform.localScale = Vector3.one;

            // 将 joint 挂载到它专属的 shadow 节点下（worldPositionStays = true）
            // 由于 shadow.rotation == runtimeRoot.rotation（通常为 Identity），
            // 此时 joint.localRotation 会保留它在世界空间中的原始姿态（存入骨骼自身 localRotation）
            joint.SetParent(shadow.transform, true);
            joint.localPosition = Vector3.zero; // 数值稳定：shadow 已对齐 joint.position，因此这里应为 0

            shadowNodes.Add(shadow.transform);
        }
        
        Debug.Log($"<color=green>脊椎解耦完成！</color> 运行根节点已挂载至: {runtimeRoot.transform.parent.name}");
    }

    // 可以在这里操作 shadowNodes 实现你的“胸部到臀部”传播逻辑
    void LateUpdate()
    {
        // 示例：如果你旋转 shadowNodes[shadowNodes.Count - 1] (胸部)
        // 你可以根据它的旋转来驱动前面的影子节点
    }

    void OnDisable()
    {
        // 停止播放时恢复所有原始隶属关系
        foreach (var joint in spineJoints)
        {
            if (joint != null && originalParents.ContainsKey(joint))
            {
                joint.SetParent(originalParents[joint], true);
            }
        }

        if (runtimeRoot != null)
        {
            Destroy(runtimeRoot);
        }
        
        shadowNodes.Clear();
        Debug.Log("<color=yellow>层级已恢复，影子节点已销毁。</color>");
    }
}