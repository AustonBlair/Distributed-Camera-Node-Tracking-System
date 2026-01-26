using UnityEngine;
using UnityEngine.InputSystem;

public class FlyMover : MonoBehaviour
{
    public float moveSpeed = 12f;
    public float fastMultiplier = 3f;
    public float lookSensitivity = 0.15f;

    float yaw;
    float pitch;
    Camera cam;

    void Start()
    {
        cam = Camera.main;

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        yaw = transform.eulerAngles.y;
        pitch = cam != null ? cam.transform.localEulerAngles.x : 0f;
    }

    void Update()
    {
        var kb = Keyboard.current;
        var mouse = Mouse.current;
        if (kb == null) return;

        // Look (hold right mouse)
        if (mouse != null && mouse.rightButton.isPressed)
        {
            Vector2 delta = mouse.delta.ReadValue();
            yaw += delta.x * lookSensitivity;
            pitch -= delta.y * lookSensitivity;
            pitch = Mathf.Clamp(pitch, -80f, 80f);

            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            if (cam != null) cam.transform.localRotation = Quaternion.Euler(pitch, 0f, 0f);
        }

        // Movement follows view direction
        Transform basis = (cam != null) ? cam.transform : transform;

        Vector3 move = Vector3.zero;
        if (kb.wKey.isPressed) move += basis.forward;
        if (kb.sKey.isPressed) move -= basis.forward;
        if (kb.dKey.isPressed) move += basis.right;
        if (kb.aKey.isPressed) move -= basis.right;
        if (kb.eKey.isPressed) move += basis.up;
        if (kb.qKey.isPressed) move -= basis.up;

        float speed = moveSpeed;
        if (kb.leftShiftKey.isPressed) speed *= fastMultiplier;

        transform.position += move.normalized * speed * Time.deltaTime;

        // Esc unlock
        if (kb.escapeKey.wasPressedThisFrame)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }
}
