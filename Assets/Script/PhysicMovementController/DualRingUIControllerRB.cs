using UnityEngine;

/// <summary>
/// Dual ring UI drawn in WORLD SPACE (LineRenderer),
/// centered at the CHARACTER (otter) position.
/// With a following camera, rings appear at screen center.
///
/// Radii are defined in PIXELS and converted to WORLD units for ORTHOGRAPHIC cameras,
/// so the UI stays visually consistent regardless of camera size/resolution.
/// </summary>
public class DualRingUIControllerRB : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Camera uiCamera;

    [Tooltip("The character/root transform the rings should follow (otter root).")]
    [SerializeField] private Transform followTarget;

    [Header("Plane (Water Surface)")]
    [SerializeField] private bool useFixedPlaneY = true;
    [SerializeField] private float fixedPlaneY = 0f;

    [Header("Ring Radii (Pixels)")]
    [SerializeField] private float innerRadiusPx = 120f;
    [SerializeField] private float outerRadiusPx = 260f;

    [Header("Ring Geometry")]
    [SerializeField] private int ringSegments = 64;

    [Header("Ring Visual")]
    [SerializeField] private Color innerRingColor = new Color(1f, 1f, 1f, 0.30f);
    [SerializeField] private Color outerRingColor = new Color(1f, 1f, 1f, 0.50f);
    [SerializeField] private float ringWidthWorld = 0.05f;

    [Header("Arrow Visual")]
    [SerializeField] private Color arrowColor = Color.yellow;
    [SerializeField] private float arrowWidthWorld = 0.08f;
    [SerializeField] private float arrowYOffsetWorld = 0.08f;

    private GameObject ringContainer;
    private LineRenderer innerRing;
    private LineRenderer outerRing;

    private GameObject arrowObject;
    private LineRenderer arrow;

    private Vector3 centerWorld;
    private float innerRadiusWorld;
    private float outerRadiusWorld;

    private int lastScreenW, lastScreenH;
    private float lastOrthoSize;
    private float lastAspect;

    public float InnerRadiusPx => innerRadiusPx;
    public float OuterRadiusPx => outerRadiusPx;
    public Vector2 ScreenCenterPx => new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);

    private float PlaneY => useFixedPlaneY ? fixedPlaneY : centerWorld.y;



    private Vector2 _cachedDir;
    private float _cachedRadius;
    private bool _hasArrowInput;

    public void SetArrowInput(Vector2 dirScreen, float radiusPx)
    {
        _cachedDir = dirScreen;
        _cachedRadius = radiusPx;
        _hasArrowInput = true;
    }

    private void Start()
    {
        if (uiCamera == null) uiCamera = Camera.main;

        // Auto-find MovementControllerRB (preferred) or MovementController as default follow target
        if (followTarget == null)
        {
            var mvRB = FindObjectOfType<MovementControllerRB>();
            if (mvRB != null) followTarget = mvRB.transform;
            else
            {
                var mv = FindObjectOfType<MovementController>();
                if (mv != null) followTarget = mv.transform;
            }
        }

        CreateRings();
        CreateArrow();

        RecomputeWorldRadii(force: true);
        UpdateCenterFromTarget();
        ApplyCenter();
        RebuildRingGeometry();
    }

    private void LateUpdate()
    {
        if (uiCamera == null) return;

        if (followTarget == null)
        {
            var mvRB = FindObjectOfType<MovementControllerRB>();
            if (mvRB != null) followTarget = mvRB.transform;
            else
            {
                var mv = FindObjectOfType<MovementController>();
                if (mv != null) followTarget = mv.transform;
            }
        }

        RecomputeWorldRadii(force: false);
        UpdateCenterFromTarget();
        ApplyCenter();

        if (_hasArrowInput) UpdateArrowFromScreen(_cachedDir, _cachedRadius);
    }

    public Vector3 GetCenterWorldOnPlane() => centerWorld;

    public void UpdateArrowFromScreen(Vector2 dirScreen, float radiusPx)
    {
        if (arrow == null) return;

        if (dirScreen.sqrMagnitude < 1e-6f)
        {
            arrow.enabled = false;
            return;
        }

        Vector3 dirWorld = ScreenDirToWorldXZ(dirScreen);
        if (dirWorld.sqrMagnitude < 1e-6f)
        {
            arrow.enabled = false;
            return;
        }

        float clampedPx = Mathf.Clamp(radiusPx, 0f, outerRadiusPx);
        float lenWorld = (clampedPx / Mathf.Max(1f, outerRadiusPx)) * outerRadiusWorld;

        Vector3 p0 = centerWorld + Vector3.up * arrowYOffsetWorld;
        Vector3 p1 = p0 + dirWorld * lenWorld;

        arrow.enabled = true;
        arrow.positionCount = 2;
        arrow.SetPosition(0, p0);
        arrow.SetPosition(1, p1);
    }

    public void HideArrow()
    {
        if (arrow != null) arrow.enabled = false;
    }

    private void CreateRings()
    {
        ringContainer = new GameObject("RingContainer_CharacterCentered");
        ringContainer.transform.SetParent(transform, worldPositionStays: true);

        GameObject innerGO = new GameObject("InnerRing");
        innerGO.transform.SetParent(ringContainer.transform, worldPositionStays: false);
        innerRing = innerGO.AddComponent<LineRenderer>();
        SetupLine(innerRing, innerRingColor, ringWidthWorld);
        innerRing.loop = true;

        GameObject outerGO = new GameObject("OuterRing");
        outerGO.transform.SetParent(ringContainer.transform, worldPositionStays: false);
        outerRing = outerGO.AddComponent<LineRenderer>();
        SetupLine(outerRing, outerRingColor, ringWidthWorld);
        outerRing.loop = true;
    }

    private void CreateArrow()
    {
        arrowObject = new GameObject("DirectionArrow_CharacterCentered");
        arrowObject.transform.SetParent(transform, worldPositionStays: true);

        arrow = arrowObject.AddComponent<LineRenderer>();
        SetupLine(arrow, arrowColor, arrowWidthWorld);
        arrow.useWorldSpace = true;
        arrow.loop = false;
        arrow.enabled = false;
    }

    private void SetupLine(LineRenderer lr, Color color, float width)
    {
        Shader shader = Shader.Find("Unlit/Color");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        lr.material = new Material(shader);

        lr.startColor = color;
        lr.endColor = color;
        lr.startWidth = width;
        lr.endWidth = width;

        lr.useWorldSpace = false;
        lr.numCapVertices = 4;
        lr.numCornerVertices = 2;
    }

    private void UpdateCenterFromTarget()
    {
        if (followTarget == null) return;

        centerWorld = followTarget.position;
        if (useFixedPlaneY) centerWorld.y = fixedPlaneY;
    }

    private void ApplyCenter()
    {
        if (ringContainer != null)
            ringContainer.transform.position = centerWorld;
    }

    private void RecomputeWorldRadii(bool force)
    {
        if (uiCamera == null) return;

        bool changed =
            force ||
            Screen.width != lastScreenW ||
            Screen.height != lastScreenH ||
            !Mathf.Approximately(uiCamera.aspect, lastAspect) ||
            (uiCamera.orthographic && !Mathf.Approximately(uiCamera.orthographicSize, lastOrthoSize));

        if (!changed) return;

        lastScreenW = Screen.width;
        lastScreenH = Screen.height;
        lastAspect = uiCamera.aspect;
        lastOrthoSize = uiCamera.orthographic ? uiCamera.orthographicSize : lastOrthoSize;

        if (!uiCamera.orthographic)
        {
            innerRadiusWorld = innerRadiusPx * 0.01f;
            outerRadiusWorld = outerRadiusPx * 0.01f;
        }
        else
        {
            float unitsPerPixel = (2f * uiCamera.orthographicSize) / Mathf.Max(1, Screen.height);
            innerRadiusWorld = innerRadiusPx * unitsPerPixel;
            outerRadiusWorld = outerRadiusPx * unitsPerPixel;
        }

        RebuildRingGeometry();
    }

    private void RebuildRingGeometry()
    {
        int seg = Mathf.Max(12, ringSegments);

        innerRing.positionCount = seg;
        outerRing.positionCount = seg;

        for (int i = 0; i < seg; i++)
        {
            float a = 2f * Mathf.PI * i / seg;
            innerRing.SetPosition(i, new Vector3(Mathf.Cos(a) * innerRadiusWorld, 0f, Mathf.Sin(a) * innerRadiusWorld));
            outerRing.SetPosition(i, new Vector3(Mathf.Cos(a) * outerRadiusWorld, 0f, Mathf.Sin(a) * outerRadiusWorld));
        }
    }

    private Vector3 ScreenDirToWorldXZ(Vector2 dirScreen)
    {
        // For top-down cameras: use camera.up as screen-Y axis (not forward).
        Vector3 camRight = uiCamera.transform.right; camRight.y = 0f;
        Vector3 camUp = uiCamera.transform.up; camUp.y = 0f;

        if (camRight.sqrMagnitude < 1e-6f) camRight = Vector3.right;
        if (camUp.sqrMagnitude < 1e-6f) camUp = Vector3.forward;

        camRight.Normalize();
        camUp.Normalize();

        Vector3 w = camRight * dirScreen.x + camUp * dirScreen.y;
        w.y = 0f;
        return w.sqrMagnitude > 1e-6f ? w.normalized : Vector3.forward;
    }
}
