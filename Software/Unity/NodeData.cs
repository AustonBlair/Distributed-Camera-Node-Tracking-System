using UnityEngine;

public enum NodeMode
{
    Disabled,  // 'D'
    Searching, // 'S'
    Tracking   // 'T'
}

public class NodeData : MonoBehaviour
{
    public int id;

    [Header("State")]
    public NodeMode currentMode;

    [Header("GPS")]
    public double lat;
    public double lon;

    [Header("Orientation")]
    // Raw values from ESP32
    public float rawPitch;
    public float rawYaw;

    // Values actually applied to Unity transform (after sign flips)
    public float appliedPitch;
    public float appliedYaw;

    public float Heading360 => Normalize360(transform.eulerAngles.y);

    private float Normalize360(float a)
    {
        a %= 360f;
        if (a < 0f) a += 360f;
        return a;
    }
}