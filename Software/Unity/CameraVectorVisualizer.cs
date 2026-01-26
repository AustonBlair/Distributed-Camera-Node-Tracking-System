using UnityEngine;

public class CameraVectorVisualizer : MonoBehaviour
{
    [Header("Ground plane")]
    public float groundY = 0f;

    [Header("Ray drawing")]
    public float rayLength = 200f;
    public float lineWidth = 0.05f;
    public Material lineMaterial;

    [Header("Impact marker")]
    public float markerScale = 0.5f;

    private LineRenderer _rayLine;
    private GameObject _marker;

    // We store an override point. If set, the line stops here.
    private Vector3? _overrideTarget = null;

    void Start()
    {
        if (lineMaterial == null)
            lineMaterial = new Material(Shader.Find("Sprites/Default"));

        // Line showing forward direction
        _rayLine = gameObject.AddComponent<LineRenderer>();
        _rayLine.positionCount = 2;
        _rayLine.startWidth = lineWidth;
        _rayLine.endWidth = lineWidth;
        _rayLine.material = lineMaterial;
        _rayLine.useWorldSpace = true;

        // --- 50% Faded White ---
        Color fadedWhite = new Color(1f, 1f, 1f, 0.5f);
        _rayLine.startColor = fadedWhite;
        _rayLine.endColor = fadedWhite;

        // Small sphere at ground intersection
        _marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        _marker.name = "RayHitMarker";
        Destroy(_marker.GetComponent<Collider>());
        _marker.transform.localScale = Vector3.one * markerScale;
    }

    void OnDestroy()
    {
        if (_marker != null) Destroy(_marker);
    }

    // Called by MultiNodeTriangulation to set the "Cut Off" point
    public void SetTargetPoint(Vector3? target)
    {
        _overrideTarget = target;
    }

    void Update()
    {
        Vector3 origin = transform.position;
        Vector3 dir = transform.forward; 

        _rayLine.SetPosition(0, origin);

        // PRIORITY 1: Triangulation Override
        if (_overrideTarget.HasValue)
        {
            // Draw exactly to the projected point on the line
            _rayLine.SetPosition(1, _overrideTarget.Value);
            
            // Hide the ground marker (we are tracking a target in air)
            _marker.SetActive(false);
            return;
        }

        // PRIORITY 2: Standard Ground/Infinite Ray
        _marker.SetActive(true);

        if (Mathf.Abs(dir.y) < 1e-5f)
        {
            Vector3 farPoint = origin + dir * rayLength;
            _rayLine.SetPosition(1, farPoint);
            _marker.transform.position = farPoint;
            return;
        }

        float t = (groundY - origin.y) / dir.y;

        if (t > 0f)
        {
            Vector3 hit = origin + dir * t;
            
            if (t > rayLength) hit = origin + dir * rayLength;

            _rayLine.SetPosition(1, hit);
            _marker.transform.position = hit;
        }
        else
        {
            Vector3 endPoint = origin + dir * rayLength;
            _rayLine.SetPosition(1, endPoint);
            _marker.transform.position = endPoint;
        }
    }
}