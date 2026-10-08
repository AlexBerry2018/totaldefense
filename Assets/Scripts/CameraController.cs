using UnityEngine;
using UnityEngine.InputSystem;

public class CameraController : MonoBehaviour
{
    public float moveSpeed = 40f;
    public float zoomStep = 6f;
    public float minHeight = 8f;
    public float maxHeight = 160f;

    void Update()
    {
        Keyboard kb = Keyboard.current;
        Mouse mouse = Mouse.current;
        if (kb == null) return;

        Vector3 move = Vector3.zero;
        if (kb.wKey.isPressed) move.z += 1f;
        if (kb.sKey.isPressed) move.z -= 1f;
        if (kb.dKey.isPressed) move.x += 1f;
        if (kb.aKey.isPressed) move.x -= 1f;

        if (move != Vector3.zero)
        {
            float heightFactor = transform.position.y / 40f;
            transform.position += move.normalized * (moveSpeed * heightFactor * Time.unscaledDeltaTime);
        }

        if (mouse == null) return;
        float scroll = mouse.scroll.ReadValue().y;
        if (scroll != 0f)
        {
            Vector3 next = transform.position + transform.forward * (Mathf.Sign(scroll) * zoomStep);
            if (next.y >= minHeight && next.y <= maxHeight) transform.position = next;
        }
    }
}
