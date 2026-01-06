using UnityEngine;

/// <summary>
/// Top-down orthographic follow camera (Y-up).
/// Keeps target centered by following with an offset.
/// </summary>
[RequireComponent(typeof(Camera))]
public class TopDownCameraController : MonoBehaviour
{
    [SerializeField] private Transform target;
    [SerializeField] private Vector3 offset = new Vector3(0f, 10f, 0f);
    [SerializeField] private float followSpeed = 10f;

    [Header("Orthographic")]
    [SerializeField] private bool forceOrthographic = true;
    [SerializeField] private float orthographicSize = 5f;

    private Camera cam;

    void Awake()
    {
        cam = GetComponent<Camera>();
    }

    void Start()
    {
        if (forceOrthographic && cam != null)
        {
            cam.orthographic = true;
            cam.orthographicSize = orthographicSize;
        }

        // Strongly recommended: no roll for top-down control
        Vector3 e = transform.eulerAngles;
        transform.eulerAngles = new Vector3(e.x, e.y, 0f);
    }

    void LateUpdate()
    {
        if (target == null) return;

        Vector3 desired = target.position + offset;
        transform.position = Vector3.Lerp(transform.position, desired, followSpeed * Time.deltaTime);
    }
}
