using UnityEngine;

/// <summary>
/// World-space dual ring UI + direction arrow.
/// Rings follow otter position in world.
/// Arrow:
/// - starts at otter position
/// - points in provided world direction (XZ)
/// - length clamped to outer ring radius
/// </summary>
public class DualRingUIController : MonoBehaviour
{
    [Header("Ring Settings (World Units)")]
    [SerializeField] private float innerRingRadius = 1f;
    [SerializeField] private float outerRingRadius = 3f;
    [SerializeField] private int ringSegments = 64;

    [Header("Ring Visual")]
    [SerializeField] private Color innerRingColor = new Color(1f, 1f, 1f, 0.3f);
    [SerializeField] private Color outerRingColor = new Color(1f, 1f, 1f, 0.5f);
    [SerializeField] private float ringWidth = 0.05f;

    [Header("Arrow Visual")]
    [SerializeField] private Color arrowColor = Color.yellow;
    [SerializeField] private float arrowWidth = 0.08f;
    [SerializeField] private float arrowYOffset = 0.08f;

    private MovementController movement;

    private GameObject ringContainer;
    private LineRenderer innerRing;
    private LineRenderer outerRing;

    private GameObject arrowObject;
    private LineRenderer arrow;

    private Vector3 centerWorld;

    void Start()
    {
        movement = FindObjectOfType<MovementController>();
        if (movement != null)
        {
            innerRingRadius = movement.GetInnerRingRadius();
            outerRingRadius = movement.GetOuterRingRadius();
            centerWorld = movement.transform.position;
        }

        CreateRings();
        CreateArrow();
    }

    void Update()
    {
        if (movement == null)
            return;

        // Follow otter in world
        centerWorld = movement.transform.position;
        if (ringContainer != null)
            ringContainer.transform.position = centerWorld;

        // Optional: hide arrow when not dragging
        if (!movement.IsDragging())
            HideArrow();
    }

    public void SetCenter(Vector3 worldCenter)
    {
        centerWorld = worldCenter;
        if (ringContainer != null)
            ringContainer.transform.position = centerWorld;
    }

    private void CreateRings()
    {
        ringContainer = new GameObject("RingContainer");
        ringContainer.transform.SetParent(transform, worldPositionStays: true);
        ringContainer.transform.position = centerWorld;

        GameObject innerGO = new GameObject("InnerRing");
        innerGO.transform.SetParent(ringContainer.transform, worldPositionStays: false);
        innerRing = innerGO.AddComponent<LineRenderer>();
        SetupRing(innerRing, innerRingRadius, innerRingColor);

        GameObject outerGO = new GameObject("OuterRing");
        outerGO.transform.SetParent(ringContainer.transform, worldPositionStays: false);
        outerRing = outerGO.AddComponent<LineRenderer>();
        SetupRing(outerRing, outerRingRadius, outerRingColor);
    }

    private void SetupRing(LineRenderer lr, float radius, Color color)
    {
        Shader shader = Shader.Find("Unlit/Color");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        lr.material = new Material(shader);

        lr.startColor = color;
        lr.endColor = color;
        lr.startWidth = ringWidth;
        lr.endWidth = ringWidth;

        lr.useWorldSpace = false;
        lr.loop = true;

        int seg = Mathf.Max(8, ringSegments);
        lr.positionCount = seg;

        for (int i = 0; i < seg; i++)
        {
            float angle = 2f * Mathf.PI * i / seg;
            float x = Mathf.Cos(angle) * radius;
            float z = Mathf.Sin(angle) * radius;
            lr.SetPosition(i, new Vector3(x, 0f, z));
        }
    }

    private void CreateArrow()
    {
        arrowObject = new GameObject("DirectionArrow");
        arrowObject.transform.SetParent(transform, worldPositionStays: true);

        arrow = arrowObject.AddComponent<LineRenderer>();
        Shader shader = Shader.Find("Unlit/Color");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        arrow.material = new Material(shader);

        arrow.startColor = arrowColor;
        arrow.endColor = arrowColor;
        arrow.startWidth = arrowWidth;
        arrow.endWidth = arrowWidth;

        arrow.useWorldSpace = true;
        arrow.positionCount = 2;
        arrow.enabled = false;
    }

    public void UpdateArrow(Vector3 startPosition, Vector3 direction, float distanceWorld)
    {
        if (arrow == null) return;

        Vector3 dir = new Vector3(direction.x, 0f, direction.z);
        if (dir.sqrMagnitude < 0.0001f)
        {
            arrow.enabled = false;
            return;
        }
        dir.Normalize();

        float length = Mathf.Clamp(distanceWorld, 0f, outerRingRadius);

        Vector3 p0 = new Vector3(startPosition.x, startPosition.y + arrowYOffset, startPosition.z);
        Vector3 p1 = p0 + dir * length;

        arrow.enabled = true;
        arrow.SetPosition(0, p0);
        arrow.SetPosition(1, p1);
    }

    public void HideArrow()
    {
        if (arrow != null) arrow.enabled = false;
    }
}
