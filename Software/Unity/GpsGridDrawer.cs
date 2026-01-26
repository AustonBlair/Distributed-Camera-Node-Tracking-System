using System.Collections.Generic;
using UnityEngine;

public class GpsGridDrawer : MonoBehaviour
{
    [Header("Grid size")]
    public int halfSizeMeters = 2000;   // 2000 = grid spans 4km wide
    public int spacingMeters = 100;     // distance between lines
    public float y = 0f;               // ground height

    [Header("Line look")]
    public float lineWidth = 0.03f;
    public Material lineMaterial;

    private readonly List<GameObject> _lines = new List<GameObject>();

    void Start()
    {
        if (lineMaterial == null)
        {
            // Unity default material fallback
            lineMaterial = new Material(Shader.Find("Sprites/Default"));
        }

        BuildGrid();
    }

    void OnDestroy()
    {
        foreach (var go in _lines) Destroy(go);
        _lines.Clear();
    }

    void BuildGrid()
    {
        // Vertical lines (north-south): x constant, z varies
        for (int x = -halfSizeMeters; x <= halfSizeMeters; x += spacingMeters)
        {
            Vector3 a = new Vector3(x, y, -halfSizeMeters);
            Vector3 b = new Vector3(x, y,  halfSizeMeters);
            CreateLine($"Grid_X_{x}", a, b);
        }

        // Horizontal lines (east-west): z constant, x varies
        for (int z = -halfSizeMeters; z <= halfSizeMeters; z += spacingMeters)
        {
            Vector3 a = new Vector3(-halfSizeMeters, y, z);
            Vector3 b = new Vector3( halfSizeMeters, y, z);
            CreateLine($"Grid_Z_{z}", a, b);
        }

        // Optional: axis lines thicker (X and Z)
        CreateLine("Axis_X", new Vector3(-halfSizeMeters, y, 0), new Vector3(halfSizeMeters, y, 0), lineWidth * 2f);
        CreateLine("Axis_Z", new Vector3(0, y, -halfSizeMeters), new Vector3(0, y, halfSizeMeters), lineWidth * 2f);
    }

    void CreateLine(string name, Vector3 start, Vector3 end, float widthOverride = -1f)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);

        var lr = go.AddComponent<LineRenderer>();
        lr.positionCount = 2;
        lr.SetPosition(0, start);
        lr.SetPosition(1, end);
        lr.startWidth = (widthOverride > 0) ? widthOverride : lineWidth;
        lr.endWidth = (widthOverride > 0) ? widthOverride : lineWidth;
        lr.material = lineMaterial;
        lr.useWorldSpace = true;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;

        _lines.Add(go);
    }
}
