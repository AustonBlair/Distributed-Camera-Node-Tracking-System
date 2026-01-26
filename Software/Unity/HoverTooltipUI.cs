using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.InputSystem;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Linq;

[System.Serializable]
public class MotorConfig
{
    public int id;
    public string motor_ip;
    public int motor_port;
}

public class HoverTooltipUI : MonoBehaviour
{
    public Camera targetCamera;
    public float hoverPixelRadius = 25f; 
    public Vector2 offsetPixels = new Vector2(20f, -20f);

    [Header("Motor Configuration")]
    public List<MotorConfig> systemConfig = new List<MotorConfig>()
    {
        new MotorConfig { id = 0, motor_ip = "192.168.5.183", motor_port = 3333 },
        new MotorConfig { id = 1, motor_ip = "192.168.5.172", motor_port = 3333 }
    };

    private RectTransform tooltipRect;
    private TextMeshProUGUI tooltipText;
    private Button actionButton;
    private TextMeshProUGUI buttonText;
    private NodeData currentHoveredNode;
    
    // Timer to handle visual feedback on click
    private float feedbackTimer = 0f;

    void Start()
    {
        if (targetCamera == null) targetCamera = Camera.main;

        // --- Canvas ---
        var canvasGO = new GameObject("TooltipCanvas");
        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvasGO.AddComponent<CanvasScaler>();
        canvasGO.AddComponent<GraphicRaycaster>();

        // --- Panel ---
        var panelGO = new GameObject("TooltipPanel");
        panelGO.transform.SetParent(canvas.transform, false);
        var img = panelGO.AddComponent<Image>();
        img.color = new Color(0f, 0f, 0f, 0.8f);

        tooltipRect = panelGO.GetComponent<RectTransform>();
        tooltipRect.pivot = new Vector2(0f, 1f);
        tooltipRect.sizeDelta = new Vector2(250f, 180f);
        
        // SCALING: Set to 2.0 to make everything larger
        tooltipRect.localScale = Vector3.one * 2.0f; 
        
        panelGO.SetActive(false);

        // --- Info Text ---
        var textGO = new GameObject("TooltipText");
        textGO.transform.SetParent(panelGO.transform, false);
        tooltipText = textGO.AddComponent<TextMeshProUGUI>();
        tooltipText.fontSize = 16;
        tooltipText.color = Color.white;
        
        var tr = tooltipText.GetComponent<RectTransform>();
        tr.anchorMin = new Vector2(0, 0.3f); 
        tr.anchorMax = new Vector2(1, 1);
        tr.offsetMin = new Vector2(10, 0);
        tr.offsetMax = new Vector2(-10, -10);

        // --- Action Button ---
        var btnGO = new GameObject("ActionButton");
        btnGO.transform.SetParent(panelGO.transform, false);
        // Add an Image component so we can change color
        var btnImg = btnGO.AddComponent<Image>();
        btnImg.color = Color.gray;
        
        actionButton = btnGO.AddComponent<Button>();
        // IMPORTANT: Disable the default ColorTint transition so we can control colors manually
        actionButton.transition = Selectable.Transition.None; 
        actionButton.onClick.AddListener(OnButtonClick);

        var btnRect = btnGO.GetComponent<RectTransform>();
        btnRect.anchorMin = new Vector2(0.1f, 0.05f); 
        btnRect.anchorMax = new Vector2(0.9f, 0.25f);
        btnRect.offsetMin = Vector2.zero;
        btnRect.offsetMax = Vector2.zero;

        // --- Button Text ---
        var btnTextGO = new GameObject("ButtonText");
        btnTextGO.transform.SetParent(btnGO.transform, false);
        buttonText = btnTextGO.AddComponent<TextMeshProUGUI>();
        buttonText.fontSize = 14;
        buttonText.color = Color.black;
        buttonText.alignment = TextAlignmentOptions.Center;
        buttonText.enableWordWrapping = false;
        
        var btnTextRect = btnTextGO.GetComponent<RectTransform>();
        btnTextRect.anchorMin = Vector2.zero;
        btnTextRect.anchorMax = Vector2.one;
    }

    void Update()
    {
        // Ensure Input System is active
        if (Mouse.current == null) return;
        Vector2 mousePos = Mouse.current.position.ReadValue();

        // Update feedback timer
        if (feedbackTimer > 0f) feedbackTimer -= Time.deltaTime;

        NodeData hovered = FindHoveredNode(mousePos);
        bool hoveringUI = RectTransformUtility.RectangleContainsScreenPoint(tooltipRect, mousePos, null);

        // --- MANUAL CLICK CHECK START ---
        // Since we created the UI programmatically, we might not have an EventSystem.
        // We manually check if the left mouse was clicked while inside the button rect.
        if (Mouse.current.leftButton.wasPressedThisFrame && tooltipRect.gameObject.activeSelf)
        {
            if (RectTransformUtility.RectangleContainsScreenPoint(actionButton.GetComponent<RectTransform>(), mousePos, null))
            {
                OnButtonClick();
            }
        }
        // --- MANUAL CLICK CHECK END ---

        if (hovered != null)
        {
            currentHoveredNode = hovered;
            ShowTooltip(mousePos);
        }
        else if (hoveringUI && currentHoveredNode != null)
        {
            UpdateContent(); 
        }
        else
        {
            currentHoveredNode = null;
            tooltipRect.gameObject.SetActive(false);
        }
    }

    private void ShowTooltip(Vector2 mousePos)
    {
        tooltipRect.gameObject.SetActive(true);

        Vector3 sp = targetCamera.WorldToScreenPoint(currentHoveredNode.transform.position);
        tooltipRect.position = (Vector2)sp + offsetPixels;

        UpdateContent();
    }

    private void UpdateContent()
    {
        if (currentHoveredNode == null) return;

        // Update Text
        string modeStr = "";
        string modeColor = "white";
        
        // This relies on your REAL NodeData.cs having these enum values
        switch (currentHoveredNode.currentMode)
        {
            case NodeMode.Disabled: modeStr = "DISABLED"; modeColor = "red"; break;
            case NodeMode.Searching: modeStr = "SEARCHING"; modeColor = "orange"; break;
            case NodeMode.Tracking: modeStr = "TRACKING"; modeColor = "green"; break;
        }

        tooltipText.text =
            $"<b>ID: {currentHoveredNode.id}</b>\n" +
            $"Mode: <color={modeColor}>{modeStr}</color>\n" +
            $"Lat: {currentHoveredNode.lat:F6}\n" +
            $"Lon: {currentHoveredNode.lon:F6}\n" +
            $"Pitch: {currentHoveredNode.rawPitch:0.0}°";

        // Update Button Appearance
        // Only update color if we are NOT currently showing click feedback
        if (feedbackTimer <= 0f)
        {
            if (currentHoveredNode.currentMode == NodeMode.Disabled)
            {
                buttonText.text = "ENABLE";
                actionButton.image.color = new Color(0.2f, 0.8f, 0.2f); // Green
            }
            else
            {
                buttonText.text = "DISABLE";
                actionButton.image.color = new Color(0.8f, 0.2f, 0.2f); // Red
            }
        }
        else
        {
            // FEEDBACK STATE: Significantly darker to show click
            Color baseColor = (currentHoveredNode.currentMode == NodeMode.Disabled) ? 
                              new Color(0.2f, 0.8f, 0.2f) : 
                              new Color(0.8f, 0.2f, 0.2f);
            
            // Darken by mixing with black
            actionButton.image.color = Color.Lerp(baseColor, Color.black, 0.5f);
        }
    }

    private void OnButtonClick()
    {
        if (currentHoveredNode == null) return;

        // Trigger visual feedback for 0.25 seconds
        feedbackTimer = 0.25f;
        UpdateContent(); // Force update immediately to show change

        bool shouldEnable = (currentHoveredNode.currentMode == NodeMode.Disabled);
        string command = shouldEnable ? "active" : "disabled";

        SendUdpCommand(currentHoveredNode.id, command);
    }

    private void SendUdpCommand(int id, string message)
    {
        var config = systemConfig.FirstOrDefault(c => c.id == id);
        if (config == null)
        {
            Debug.LogError($"No configuration found for ID {id}. Add it to the list in HoverTooltipUI.");
            return;
        }

        try
        {
            using (var client = new UdpClient())
            {
                // Sends exactly "active" or "disabled" in ASCII
                byte[] data = Encoding.ASCII.GetBytes(message);
                client.Send(data, data.Length, config.motor_ip, config.motor_port);
                Debug.Log($"Sent '{message}' to ID {id} ({config.motor_ip}:{config.motor_port})");
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"UDP Send Error: {ex.Message}");
        }
    }

    private NodeData FindHoveredNode(Vector2 mouse)
    {
        // Use FindObjectsOfType (standard) to locate your existing NodeData scripts
        var all = Object.FindObjectsOfType<NodeData>();
        
        if (all.Length == 0) return null;

        // Adjust hit radius based on scale so it feels natural with the larger UI
        float r2 = (hoverPixelRadius * hoverPixelRadius);

        NodeData best = null;
        float bestD2 = float.MaxValue;

        foreach (var n in all)
        {
            Vector3 sp = targetCamera.WorldToScreenPoint(n.transform.position);
            if (sp.z <= 0f) continue; 

            float dx = sp.x - mouse.x;
            float dy = sp.y - mouse.y;
            float d2 = dx * dx + dy * dy;

            if (d2 <= r2 && d2 < bestD2)
            {
                bestD2 = d2;
                best = n;
            }
        }

        return best;
    }
}