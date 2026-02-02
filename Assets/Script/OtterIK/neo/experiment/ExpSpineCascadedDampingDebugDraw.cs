using UnityEngine;

namespace OtterIK.Neo.Experiment
{
    /// <summary>
    /// Debug-only visualization for ExpSpineCascadedDampingChain.
    /// Does NOT write transforms. It re-simulates the chain's internal raw/filtered/apply pose and draws rays/lines.
    /// Attach this to the same GameObject as ExpSpineCascadedDampingChain (or assign reference).
    /// </summary>
    [DefaultExecutionOrder(9100)] // run after the chain (chain uses 9000)
    [DisallowMultipleComponent]
    public class ExpSpineCascadedDampingDebugDraw : MonoBehaviour
    {
        [Header("Target")]
        public ExpSpineCascadedDampingChain chain;

        [Header("Toggles")]
        public bool drawBoneLinks = true;
        public bool drawRawTargets = true;
        public bool drawFilteredTargets = true;
        public bool drawAppliedTargets = false;

        [Header("Appearance")]
        [Range(0.01f, 1.0f)]
        public float rayLength = 0.20f;

        public Color boneLinkColor = new(1f, 1f, 1f, 0.65f);
        public Color rawColor = new(1f, 0.85f, 0.25f, 0.90f);
        public Color filteredColor = new(0.25f, 1f, 0.90f, 0.90f);
        public Color appliedColor = new(0.65f, 0.65f, 1f, 0.90f);

        [Tooltip("If true, also draw an 'up' ray per target using Joint.upAxis.")]
        public bool drawUpAxis = false;

        private Quaternion[] _bindLocalRot;
        private Vector3[] _bindLocalPos;

        private Quaternion[] _rawTargetLocalRot;
        private Vector3[] _rawTargetLocalPos;

        private Quaternion[] _filteredLocalRot;
        private Vector3[] _filteredLocalPos;

        private bool _initialized;

        private void Reset()
        {
            chain = GetComponent<ExpSpineCascadedDampingChain>();
        }

        private void OnEnable()
        {
            _initialized = false;
        }

        private void EnsureArrays(int n)
        {
            if (_bindLocalRot == null || _bindLocalRot.Length != n) _bindLocalRot = new Quaternion[n];
            if (_bindLocalPos == null || _bindLocalPos.Length != n) _bindLocalPos = new Vector3[n];

            if (_rawTargetLocalRot == null || _rawTargetLocalRot.Length != n) _rawTargetLocalRot = new Quaternion[n];
            if (_rawTargetLocalPos == null || _rawTargetLocalPos.Length != n) _rawTargetLocalPos = new Vector3[n];

            if (_filteredLocalRot == null || _filteredLocalRot.Length != n) _filteredLocalRot = new Quaternion[n];
            if (_filteredLocalPos == null || _filteredLocalPos.Length != n) _filteredLocalPos = new Vector3[n];
        }

        private void InitializeIfNeeded()
        {
            if (_initialized) return;
            if (chain == null) chain = GetComponent<ExpSpineCascadedDampingChain>();
            if (chain == null || chain.spine == null || chain.spine.Count == 0) return;

            int n = chain.spine.Count;
            EnsureArrays(n);

            chain.spine.CaptureBindLocalRotations(force: false);

            for (int i = 0; i < n; i++)
            {
                var j = chain.spine.GetJoint(i);
                if (j == null || j.bone == null) continue;

                _bindLocalRot[i] = chain.spine.GetBindLocalRot(i, captureIfMissing: true);
                _bindLocalPos[i] = j.bone.localPosition;

                _filteredLocalRot[i] = j.bone.localRotation;
                _filteredLocalPos[i] = j.bone.localPosition;
            }

            _initialized = true;
        }

        private void LateUpdate()
        {
            InitializeIfNeeded();
            if (!_initialized) return;

            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            var spine = chain.spine;
            int n = spine.Count;

            // --- 1) Rebuild raw targets from providers (same as chain) ---
            for (int i = 0; i < n; i++)
            {
                var joint = spine.GetJoint(i);
                if (joint == null || joint.bone == null)
                {
                    _rawTargetLocalRot[i] = Quaternion.identity;
                    _rawTargetLocalPos[i] = Vector3.zero;
                    continue;
                }

                Quaternion bindRot = _bindLocalRot[i];
                Vector3 bindPos = _bindLocalPos[i];

                Vector3 rotVecSum = Vector3.zero;
                float rotWSum = 0f;

                Vector3 posSum = Vector3.zero;
                float posWSum = 0f;

                var providers = chain.providers;
                for (int p = 0; p < providers.Count; p++)
                {
                    var prov = providers[p];
                    if (prov == null || !prov.isActiveAndEnabled) continue;

                    if (prov.Evaluate(i, spine, bindRot, bindPos, out Quaternion targetRot, out Vector3 targetPos, out float w))
                    {
                        w = Mathf.Clamp01(w) * Mathf.Clamp01(prov.globalWeight);
                        if (w <= 0f) continue;

                        Quaternion dRot = targetRot * Quaternion.Inverse(bindRot);
                        rotVecSum += ToAxisAngleVector(dRot) * w;
                        rotWSum += w;

                        Vector3 dPos = targetPos - bindPos;
                        posSum += dPos * w;
                        posWSum += w;
                    }
                }

                float rotWTotal = chain.allowOverdriveWeight ? rotWSum : Mathf.Clamp01(rotWSum);
                float posWTotal = chain.allowOverdriveWeight ? posWSum : Mathf.Clamp01(posWSum);

                Vector3 rotVec = (rotWSum > 1e-6f) ? ((rotVecSum / rotWSum) * rotWTotal) : Vector3.zero;
                Vector3 dPosMean = (posWSum > 1e-6f) ? ((posSum / posWSum) * posWTotal) : Vector3.zero;

                Quaternion deltaRot = (rotVec.sqrMagnitude > 1e-12f) ? FromAxisAngleVector(rotVec) : Quaternion.identity;

                _rawTargetLocalRot[i] = deltaRot * bindRot;
                _rawTargetLocalPos[i] = bindPos + dPosMean;
            }

            // --- 2) Propagation + damping (same as chain) ---
            bool chestToHip = (chain.propagation == ExpSpineCascadedDampingChain.PropagationDirection.ChestToHip);
            int start = chestToHip ? (n - 1) : 0;
            int end = chestToHip ? -1 : n;
            int step = chestToHip ? -1 : 1;

            for (int i = start; i != end; i += step)
            {
                var joint = spine.GetJoint(i);
                if (joint == null || joint.bone == null) continue;

                float hip01 = spine.GetNormalizedFromHip01(i);

                int upstreamIndex = chestToHip ? (i + 1) : (i - 1);
                bool hasUpstream = upstreamIndex >= 0 && upstreamIndex < n && spine.GetBoneHipToChest(upstreamIndex) != null;

                Quaternion upstreamRot = hasUpstream ? _filteredLocalRot[upstreamIndex] : _rawTargetLocalRot[i];
                Vector3 upstreamPos = hasUpstream ? _filteredLocalPos[upstreamIndex] : _rawTargetLocalPos[i];

                Quaternion stageRotTarget = Quaternion.Slerp(_rawTargetLocalRot[i], upstreamRot, chain.propagationBlend);
                Vector3 stagePosTarget = Vector3.Lerp(_rawTargetLocalPos[i], upstreamPos, chain.propagationBlend);

                float rotHL = Mathf.Max(0.001f, chain.rotationHalfLife) * Mathf.Max(0.01f, chain.rotHalfLifeMul.Evaluate(hip01));
                float posHL = Mathf.Max(0.001f, chain.positionHalfLife) * Mathf.Max(0.01f, chain.posHalfLifeMul.Evaluate(hip01));

                float rotAlpha = HalfLifeToAlpha(rotHL, dt);
                float posAlpha = HalfLifeToAlpha(posHL, dt);

                _filteredLocalRot[i] = Quaternion.Slerp(_filteredLocalRot[i], stageRotTarget, rotAlpha);
                _filteredLocalPos[i] = Vector3.Lerp(_filteredLocalPos[i], stagePosTarget, posAlpha);
            }

            // --- 3) Draw ---
            for (int i = 0; i < n; i++)
            {
                var j = spine.GetJoint(i);
                if (j == null || j.bone == null) continue;

                Transform bone = j.bone;
                Transform parent = bone.parent;

                // Links
                if (drawBoneLinks)
                {
                    int next = i + 1;
                    if (next < n)
                    {
                        var j2 = spine.GetJoint(next);
                        if (j2 != null && j2.bone != null)
                            Debug.DrawLine(bone.position, j2.bone.position, boneLinkColor);
                    }
                }

                if (parent == null) continue;

                Vector3 fAxis = (j.forwardAxis.sqrMagnitude < 1e-6f) ? Vector3.forward : j.forwardAxis.normalized;
                Vector3 uAxis = (j.upAxis.sqrMagnitude < 1e-6f) ? Vector3.up : j.upAxis.normalized;

                if (drawRawTargets)
                {
                    DrawTargetAxes(parent, bone.position, _rawTargetLocalRot[i], fAxis, uAxis, rawColor);
                }

                if (drawFilteredTargets)
                {
                    DrawTargetAxes(parent, bone.position, _filteredLocalRot[i], fAxis, uAxis, filteredColor);
                }

                if (drawAppliedTargets)
                {
                    Quaternion appliedRot = _filteredLocalRot[i];
                    if (chain.applyDefinitionWeight)
                    {
                        float w = Mathf.Clamp01(j.weight);
                        appliedRot = Quaternion.Slerp(_bindLocalRot[i], appliedRot, w);
                    }
                    DrawTargetAxes(parent, bone.position, appliedRot, fAxis, uAxis, appliedColor);
                }
            }
        }

        private void DrawTargetAxes(Transform parent, Vector3 originWorld, Quaternion localRot, Vector3 fAxis, Vector3 uAxis, Color c)
        {
            Vector3 fwdWorld = parent.rotation * (localRot * fAxis);
            Debug.DrawRay(originWorld, fwdWorld.normalized * rayLength, c);

            if (!drawUpAxis) return;

            Vector3 upWorld = parent.rotation * (localRot * uAxis);
            Debug.DrawRay(originWorld, upWorld.normalized * (rayLength * 0.7f), new Color(c.r, c.g, c.b, Mathf.Clamp01(c.a * 0.75f)));
        }

        private static float HalfLifeToAlpha(float halfLife, float dt)
        {
            return 1f - Mathf.Exp(-0.69314718056f * dt / Mathf.Max(1e-6f, halfLife));
        }

        private static Vector3 ToAxisAngleVector(Quaternion q)
        {
            if (q.w > 1f) q.Normalize();
            q.ToAngleAxis(out float angleDeg, out Vector3 axis);
            if (axis.sqrMagnitude < 1e-12f) return Vector3.zero;
            if (angleDeg > 180f) angleDeg -= 360f;
            float angleRad = angleDeg * Mathf.Deg2Rad;
            axis.Normalize();
            return axis * angleRad;
        }

        private static Quaternion FromAxisAngleVector(Vector3 v)
        {
            float angleRad = v.magnitude;
            if (angleRad < 1e-8f) return Quaternion.identity;
            Vector3 axis = v / angleRad;
            float angleDeg = angleRad * Mathf.Rad2Deg;
            return Quaternion.AngleAxis(angleDeg, axis);
        }
    }
}

