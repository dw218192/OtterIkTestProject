// ChestIKAutoSetup.cs
// Attach to CHEST / UPPER SPINE.
// This tool builds Left/Right Target+Pole anchors under the chest,
// then assigns them to TWO manually-referenced AdvancedTwoBoneIK components.
//
// Key change vs auto-discovery:
// - You explicitly assign leftIK/rightIK in Inspector (recommended).
//
// Works in Edit Mode via the custom inspector button (Editor script) or ContextMenu.
//
// NOTE: Requires the AdvancedTwoBoneIK class to exist in project.

using UnityEngine;

[ExecuteAlways]
public class ChestIKAutoSetup : MonoBehaviour
{
    [System.Serializable]
    public class Limb
    {
        public string label = "Left";

        [Header("Bones")]
        public Transform root;  // shoulder/hip
        public Transform mid;   // elbow/knee
        public Transform end;   // wrist/ankle

        [Header("Generated anchors under Chest")]
        public string targetName = "IK_Target_L";
        public string poleName = "IK_Pole_L";

        [HideInInspector] public Transform target;
        [HideInInspector] public Transform pole;
    }

    [Header("Manual Advanced IK Assignment (recommended)")]
    public AdvancedTwoBoneIK leftIK;
    public AdvancedTwoBoneIK rightIK;

    [Header("Limbs (fill these)")]
    public Limb left = new Limb { label = "Left", targetName = "IK_Target_L", poleName = "IK_Pole_L" };
    public Limb right = new Limb { label = "Right", targetName = "IK_Target_R", poleName = "IK_Pole_R" };

    [Header("Auto Behavior")]
    [Tooltip("If enabled, Setup() will run when component is enabled (in Play + Edit).")]
    public bool autoSetupOnEnable = false;

    [Tooltip("If enabled, re-run setup continuously in Edit Mode (useful while rigging).")]
    public bool liveUpdateInEditMode = false;

    [Header("Pole Placement")]
    [Tooltip("Pole distance = (root->mid length) * poleDistanceMul")]
    [Range(0.2f, 2.0f)]
    public float poleDistanceMul = 0.9f;

    [Tooltip("Fallback axis used if bend direction degenerates.")]
    public Vector3 fallbackWorldUp = Vector3.up;

    [Header("Axis Auto-Fix (optional)")]
    [Tooltip("If true, auto-fix forward/up when they are invalid (e.g., forward == up).")]
    public bool autoFixBoneAxes = true;

    [Tooltip("Treat axes as invalid if cross(forward, up) magnitude is below this.")]
    public float axisDegenerateEps = 1e-4f;

    [Header("Debug")]
    public bool verboseLog = true;

    private void OnEnable()
    {
        if (autoSetupOnEnable)
            Setup();
    }

    private void Update()
    {
        if (!Application.isPlaying && liveUpdateInEditMode)
            Setup();
    }

    [ContextMenu("Setup Now")]
    public void Setup()
    {
        // 1) Build anchors
        BuildAnchors(left);
        BuildAnchors(right);

        // 2) Assign to IKs (manual)
        AssignLimbToIK(left, leftIK, "LeftIK");
        AssignLimbToIK(right, rightIK, "RightIK");

        if (verboseLog)
            Debug.Log("[ChestIKAutoSetup] Setup complete.");
    }

    private void BuildAnchors(Limb limb)
    {
        if (!IsLimbValid(limb))
        {
            if (verboseLog) Debug.LogWarning($"[ChestIKAutoSetup] Limb '{limb?.label}' bones not assigned.");
            return;
        }

        // Target at END bone world position
        limb.target = FindOrCreateChild(limb.targetName, limb.target, desiredWorldPos: limb.end.position);

        // Pole from default pose geometry
        Vector3 poleWorld = ComputeDefaultPoleWorld(limb.root.position, limb.mid.position, limb.end.position, poleDistanceMul);
        limb.pole = FindOrCreateChild(limb.poleName, limb.pole, desiredWorldPos: poleWorld);
    }

    private Transform FindOrCreateChild(string name, Transform existing, Vector3 desiredWorldPos)
    {
        Transform t = existing;

        if (t == null)
            t = transform.Find(name);

        if (t == null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, worldPositionStays: true);
            t = go.transform;
        }

        if (t.name != name) t.name = name;

        t.position = desiredWorldPos;
        t.rotation = Quaternion.identity;

        return t;
    }

    private void AssignLimbToIK(Limb limb, AdvancedTwoBoneIK ik, string ikLabel)
    {
        if (!IsLimbValid(limb)) return;

        if (ik == null)
        {
            if (verboseLog)
                Debug.LogWarning($"[ChestIKAutoSetup] {ikLabel} is not assigned. Drag your AdvancedTwoBoneIK here.");
            return;
        }

        if (limb.target == null || limb.pole == null)
        {
            if (verboseLog)
                Debug.LogWarning($"[ChestIKAutoSetup] Limb '{limb.label}' anchors not created (target/pole missing).");
            return;
        }

        // Assign anchors
        ik.target = limb.target;
        ik.pole = limb.pole;

        // Optional: auto-fix axes if invalid
        if (autoFixBoneAxes)
            TryAutoFixAxesIfInvalid(ik, limb);

        if (verboseLog)
        {
            Debug.Log($"[ChestIKAutoSetup] Assigned '{limb.label}' => IK '{ik.name}'. " +
                      $"Target='{limb.target.name}', Pole='{limb.pole.name}'.");
        }
    }

    private void TryAutoFixAxesIfInvalid(AdvancedTwoBoneIK ik, Limb limb)
    {
        // ROOT points toward MID
        if (ik.root != null && ik.root.bone != null)
        {
            if (IsAxisDegenerate(ik.root.forwardLocal, ik.root.upLocal))
            {
                InferForwardUpFromChild(ik.root.bone, limb.mid.position, limb,
                    out Vector3 fLocal, out Vector3 uLocal);
                ik.root.forwardLocal = fLocal;
                ik.root.upLocal = uLocal;

                if (verboseLog)
                    Debug.Log($"[ChestIKAutoSetup] Auto-fixed ROOT axes on '{ik.name}': forward={fLocal}, up={uLocal}");
            }
        }

        // MID points toward END
        if (ik.mid != null && ik.mid.bone != null)
        {
            if (IsAxisDegenerate(ik.mid.forwardLocal, ik.mid.upLocal))
            {
                InferForwardUpFromChild(ik.mid.bone, limb.end.position, limb,
                    out Vector3 fLocal, out Vector3 uLocal);
                ik.mid.forwardLocal = fLocal;
                ik.mid.upLocal = uLocal;

                if (verboseLog)
                    Debug.Log($"[ChestIKAutoSetup] Auto-fixed MID axes on '{ik.name}': forward={fLocal}, up={uLocal}");
            }
        }
    }

    private bool IsAxisDegenerate(Vector3 forwardLocal, Vector3 upLocal)
    {
        if (forwardLocal.sqrMagnitude < 1e-8f || upLocal.sqrMagnitude < 1e-8f) return true;
        var f = forwardLocal.normalized;
        var u = upLocal.normalized;
        return Vector3.Cross(f, u).sqrMagnitude < axisDegenerateEps;
    }

    /// <summary>
    /// Conservative inference used ONLY when axes are invalid.
    /// - forwardLocal: closest cardinal axis (±X/±Y/±Z) pointing toward the child direction (in local space)
    /// - upLocal: closest remaining cardinal axis pointing toward the default bend direction (orthogonalized)
    /// </summary>
    private void InferForwardUpFromChild(Transform bone, Vector3 childPosWorld, Limb limb,
        out Vector3 forwardLocal, out Vector3 upLocal)
    {
        Vector3 childDirWorld = (childPosWorld - bone.position);
        if (childDirWorld.sqrMagnitude < 1e-8f) childDirWorld = bone.forward;
        childDirWorld.Normalize();

        Vector3 childDirLocal = bone.InverseTransformDirection(childDirWorld).normalized;

        forwardLocal = BestCardinalAxis(childDirLocal);

        // Use default bend direction (from limb A-B-W)
        Vector3 bendDirWorld = ComputeBendDirWorld(limb.root.position, limb.mid.position, limb.end.position);

        // Make an "up world" that is perpendicular to forwardWorld
        Vector3 forwardWorld = bone.TransformDirection(forwardLocal).normalized;
        Vector3 upWorld = bendDirWorld - forwardWorld * Vector3.Dot(bendDirWorld, forwardWorld);

        if (upWorld.sqrMagnitude < 1e-8f)
        {
            upWorld = Vector3.Cross(forwardWorld, fallbackWorldUp);
            if (upWorld.sqrMagnitude < 1e-8f)
                upWorld = Vector3.Cross(forwardWorld, Vector3.right);
        }
        upWorld.Normalize();

        Vector3 upLocalCandidate = bone.InverseTransformDirection(upWorld).normalized;
        upLocal = BestCardinalAxisNotParallel(upLocalCandidate, forwardLocal);

        // Final safety
        if (Vector3.Cross(forwardLocal.normalized, upLocal.normalized).sqrMagnitude < axisDegenerateEps)
            upLocal = PickAnyOrthogonalCardinal(forwardLocal);
    }

    private static Vector3 BestCardinalAxis(Vector3 v)
    {
        v.Normalize();
        Vector3[] axes =
        {
            Vector3.right, -Vector3.right,
            Vector3.up, -Vector3.up,
            Vector3.forward, -Vector3.forward
        };

        float best = float.NegativeInfinity;
        Vector3 chosen = Vector3.forward;
        for (int i = 0; i < axes.Length; i++)
        {
            float d = Vector3.Dot(v, axes[i]);
            if (d > best)
            {
                best = d;
                chosen = axes[i];
            }
        }
        return chosen;
    }

    private static Vector3 BestCardinalAxisNotParallel(Vector3 v, Vector3 forwardLocal)
    {
        v.Normalize();
        var f = forwardLocal.normalized;

        Vector3[] axes =
        {
            Vector3.right, -Vector3.right,
            Vector3.up, -Vector3.up,
            Vector3.forward, -Vector3.forward
        };

        float best = float.NegativeInfinity;
        Vector3 chosen = Vector3.up;
        for (int i = 0; i < axes.Length; i++)
        {
            if (Mathf.Abs(Vector3.Dot(axes[i], f)) > 0.95f) continue; // skip near-parallel
            float d = Vector3.Dot(v, axes[i]);
            if (d > best)
            {
                best = d;
                chosen = axes[i];
            }
        }
        return chosen;
    }

    private static Vector3 PickAnyOrthogonalCardinal(Vector3 forwardLocal)
    {
        var f = forwardLocal.normalized;
        if (Mathf.Abs(Vector3.Dot(f, Vector3.up)) < 0.9f) return Vector3.up;
        return Vector3.right;
    }

    private static bool IsLimbValid(Limb limb)
    {
        return limb != null && limb.root != null && limb.mid != null && limb.end != null;
    }

    private Vector3 ComputeDefaultPoleWorld(Vector3 A, Vector3 B, Vector3 W, float distMul)
    {
        float upperLen = Vector3.Distance(A, B);
        float poleDist = Mathf.Max(upperLen * Mathf.Max(0.05f, distMul), 0.01f);

        Vector3 AW = W - A;
        float awLen = AW.magnitude;

        if (awLen < 1e-5f)
        {
            Vector3 fwd = (B - A);
            if (fwd.sqrMagnitude < 1e-8f) fwd = Vector3.forward;

            Vector3 ortho = Vector3.Cross(fwd.normalized, fallbackWorldUp);
            if (ortho.sqrMagnitude < 1e-8f)
                ortho = Vector3.Cross(fwd.normalized, Vector3.right);

            return A + ortho.normalized * poleDist;
        }

        Vector3 dirAW = AW / awLen;

        // elbow projection onto A->W line
        Vector3 AB = B - A;
        float t = Vector3.Dot(AB, dirAW);
        Vector3 proj = A + dirAW * t;

        Vector3 bend = B - proj;
        if (bend.sqrMagnitude < 1e-8f)
        {
            Vector3 ortho = Vector3.Cross(dirAW, fallbackWorldUp);
            if (ortho.sqrMagnitude < 1e-8f)
                ortho = Vector3.Cross(dirAW, Vector3.right);
            bend = ortho;
        }

        return A + bend.normalized * poleDist;
    }

    private static Vector3 ComputeBendDirWorld(Vector3 A, Vector3 B, Vector3 W)
    {
        Vector3 AW = W - A;
        float awLen = AW.magnitude;
        if (awLen < 1e-6f) return Vector3.up;

        Vector3 dirAW = AW / awLen;
        Vector3 AB = B - A;
        float t = Vector3.Dot(AB, dirAW);
        Vector3 proj = A + dirAW * t;

        Vector3 bend = B - proj;
        if (bend.sqrMagnitude < 1e-8f)
            return Vector3.up;

        return bend.normalized;
    }
}
