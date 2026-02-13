using UnityEngine;
using System.Collections.Generic;

public class SpineDecouplerTest : MonoBehaviour
{
    [Header("输入你的脊椎关节链 (由父到子)")]
    public Transform[] spineJoints;

    // 用于存储原始层级关系的字典：Key 是关节，Value 是它的原始父物体
    private Dictionary<Transform, Transform> originalParents = new Dictionary<Transform, Transform>();
    
    // 存储运行时的虚拟根节点，方便统一管理
    private GameObject runtimeRoot;

    private void Awake()
    {
        if (spineJoints == null || spineJoints.Length == 0)
        {
            Debug.LogWarning("请先在 Inspector 中指定 spineJoints！");
            return;
        }

        // 1. 记录所有关节的原始父物体
        RecordOriginalHierarchy();

        // 2. 执行解耦逻辑
        DecoupleHierarchy();
    }

    private void RecordOriginalHierarchy()
    {
        originalParents.Clear();
        foreach (var joint in spineJoints)
        {
            if (joint != null)
            {
                originalParents[joint] = joint.parent;
            }
        }
        Debug.Log("已记录原始层级关系。");
    }

    private void DecoupleHierarchy()
    {
        // 创建一个临时的 Container，模拟 Rigging 的空间
        runtimeRoot = new GameObject("Runtime_Spine_Root");
        
        foreach (var joint in spineJoints)
        {
            if (joint != null)
            {
                // 将所有关节直接挂在临时根节点下，或者直接置于 Root (null)
                // 这样它们在物理层级上就互不隶属了
                joint.SetParent(runtimeRoot.transform, true);
            }
        }
        Debug.Log("层级已解耦：所有关节现在是平级关系。");
    }

    private void OnDisable()
    {
        // 3. 当停止播放或组件禁用时，恢复层级
        RestoreHierarchy();
    }

    private void RestoreHierarchy()
    {
        // 按照记录的字典还原
        foreach (var joint in spineJoints)
        {
            if (joint != null && originalParents.ContainsKey(joint))
            {
                joint.SetParent(originalParents[joint], true);
            }
        }

        // 清理运行时生成的物体
        if (runtimeRoot != null)
        {
            Destroy(runtimeRoot);
        }

        Debug.Log("层级已成功恢复。");
    }
}