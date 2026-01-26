using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

public class NodeManager_Ascii : MonoBehaviour
{
    [Header("UDP")]
    public int port = 4444;

    [Header("Prefab")]
    public GameObject nodePrefab;

    [Header("Map origin (Unity 0,0,0)")]
    public double originLat = 34.1467868;
    public double originLon = -118.3885814;

    [Header("Orientation flips")]
    public bool invertPitch = true;
    public bool invertYaw = false;

    private UdpClient udp;
    private IPEndPoint ep;

    private readonly Dictionary<int, NodeController> nodes = new Dictionary<int, NodeController>();
    private readonly Dictionary<int, NodeData> nodeDatas = new Dictionary<int, NodeData>();

    private readonly object _queueLock = new object();
    private readonly List<string> _queue = new List<string>();

    private const double EarthRadius = 6378137.0;

    void Start()
    {
        if (nodePrefab == null)
        {
            Debug.LogError("NodeManager_Ascii: nodePrefab not assigned!");
            enabled = false;
            return;
        }

        try
        {
            ep = new IPEndPoint(IPAddress.Any, port);
            udp = new UdpClient(port);
            udp.BeginReceive(OnReceive, null);
            Debug.Log($"<color=green>UDP Listener started on port {port}. Waiting for ESP32...</color>");
        }
        catch (Exception e)
        {
            Debug.LogError("Failed to start UDP: " + e.Message);
        }
    }

    void OnDestroy()
    {
        try { udp?.Close(); } catch { }
    }

    private void OnReceive(IAsyncResult ar)
    {
        try
        {
            byte[] data = udp.EndReceive(ar, ref ep);
            string msg = Encoding.ASCII.GetString(data);

            lock (_queueLock)
            {
                _queue.Add(msg);
            }
        }
        catch { }
        finally
        {
            try { udp.BeginReceive(OnReceive, null); } catch { }
        }
    }

    void Update()
    {
        List<string> copy = null;

        lock (_queueLock)
        {
            if (_queue.Count > 0)
            {
                copy = new List<string>(_queue);
                _queue.Clear();
            }
        }

        if (copy == null) return;

        foreach (var raw in copy)
        {
            if (!TryParseYourFormat(raw, out int id, out NodeMode mode, out float pitch, out float yaw, out double lat, out double lon))
            {
                Debug.LogWarning($"Parse Failed! String: '{raw}'");
                continue;
            }

            EnsureNodeExists(id);

            float appliedPitch = invertPitch ? -pitch : pitch;
            float appliedYaw = invertYaw ? -yaw : yaw;

            Vector3 pos = GpsToUnity(lat, lon);
            Quaternion rot = Quaternion.Euler(appliedPitch, appliedYaw, 0f);

            if (nodeDatas.TryGetValue(id, out var nd))
            {
                nd.id = id;
                nd.currentMode = mode; // Updated Enum assignment
                nd.lat = lat;
                nd.lon = lon;
                nd.rawPitch = pitch;
                nd.rawYaw = yaw;
                nd.appliedPitch = appliedPitch;
                nd.appliedYaw = appliedYaw;
            }

            // Optional: If mode is Disabled, you might choose NOT to move the node visually:
            // if (mode != NodeMode.Disabled) 
            nodes[id].Apply(pos, rot);
        }
    }

    private void EnsureNodeExists(int id)
    {
        if (nodes.ContainsKey(id) && nodes[id] != null) return;

        GameObject obj = Instantiate(nodePrefab);
        obj.name = "Node_" + id;
        Debug.Log($"<color=cyan>Spawned new node: {obj.name}</color>");

        var controller = obj.GetComponent<NodeController>();
        if (controller == null) controller = obj.AddComponent<NodeController>();
        nodes[id] = controller;

        var data = obj.GetComponent<NodeData>();
        if (data == null) data = obj.AddComponent<NodeData>();
        data.id = id;
        nodeDatas[id] = data;
    }

    private bool TryParseYourFormat(string s, out int id, out NodeMode mode, out float pitch, out float yaw, out double lat, out double lon)
    {
        id = 0; 
        mode = NodeMode.Searching; 
        pitch = 0; 
        yaw = 0; 
        lat = 0; 
        lon = 0;

        string[] tokens = s.Trim().Split(',');
        if (tokens.Length < 6) return false;

        bool okId = false, okMode = false, okP = false, okY = false, okLat = false, okLon = false;

        foreach (var t in tokens)
        {
            string token = t.Trim();

            if (token.StartsWith("ID:"))
                okId = int.TryParse(token.Substring(3), out id);

            else if (token.StartsWith("M:"))
            {
                string modeStr = token.Substring(2).Trim().ToUpper();
                if (modeStr.StartsWith("T")) mode = NodeMode.Tracking;
                else if (modeStr.StartsWith("D")) mode = NodeMode.Disabled;
                else mode = NodeMode.Searching;
                
                okMode = true;
            }

            else if (token.StartsWith("P:"))
                okP = float.TryParse(token.Substring(2), NumberStyles.Float, CultureInfo.InvariantCulture, out pitch);

            else if (token.StartsWith("MAG:")) 
                okY = float.TryParse(token.Substring(4), NumberStyles.Float, CultureInfo.InvariantCulture, out yaw);

            else if (token.StartsWith("LAT:"))
                okLat = double.TryParse(token.Substring(4), NumberStyles.Float, CultureInfo.InvariantCulture, out lat);

            else if (token.StartsWith("LON:"))
                okLon = double.TryParse(token.Substring(4), NumberStyles.Float, CultureInfo.InvariantCulture, out lon);
        }

        return okId && okMode && okP && okY && okLat && okLon;
    }

    private Vector3 GpsToUnity(double lat, double lon)
    {
        double lat0Rad = originLat * Math.PI / 180.0;
        double dLat = (lat - originLat) * Math.PI / 180.0;
        double dLon = (lon - originLon) * Math.PI / 180.0;

        double north = dLat * EarthRadius;
        double east = dLon * EarthRadius * Math.Cos(lat0Rad);

        return new Vector3((float)east, 0f, (float)north);
    }
}