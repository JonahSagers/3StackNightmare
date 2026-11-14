using UnityEngine;
using UnityEngine.InputSystem;

// Attach to the Player. Right-click anywhere on the ground and the player walks there.
public class ClickToMove : MonoBehaviour
{
    [Tooltip("Units per second.")]
    public float moveSpeed = 5f;

    [Tooltip("How fast the player turns to face where it's going.")]
    public float turnSpeed = 720f;

    [Tooltip("Stop when this close to the clicked point.")]
    public float stopDistance = 0.1f;

    [Tooltip("Camera used to turn the mouse position into a world point. Defaults to Main Camera.")]
    public Camera cam;

    Vector3 targetPosition;
    bool hasTarget;

    void Start()
    {
        if (cam == null) cam = Camera.main;
    }

    void Update()
    {
        var mouse = Mouse.current;
        if (mouse != null && mouse.rightButton.wasPressedThisFrame && TryGetClickPoint(mouse.position.ReadValue(), out Vector3 point))
        {
            // Keep the player's own height so it doesn't sink into the floor.
            targetPosition = new Vector3(point.x, transform.position.y, point.z);
            hasTarget = true;
        }

        if (!hasTarget) return;

        Vector3 toTarget = targetPosition - transform.position;
        if (toTarget.magnitude <= stopDistance)
        {
            hasTarget = false;
            return;
        }

        transform.position = Vector3.MoveTowards(transform.position, targetPosition, moveSpeed * Time.deltaTime);

        Quaternion look = Quaternion.LookRotation(toTarget.normalized, Vector3.up);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, look, turnSpeed * Time.deltaTime);
    }

    bool TryGetClickPoint(Vector2 screenPos, out Vector3 point)
    {
        Ray ray = cam.ScreenPointToRay(screenPos);

        // Clicked on something with a collider (e.g. your ground)? Use that spot,
        // ignoring the player itself.
        RaycastHit[] hits = Physics.RaycastAll(ray, 500f);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance)); // nearest first
        foreach (RaycastHit hit in hits)
        {
            if (hit.transform == transform) continue;
            point = hit.point;
            return true;
        }

        // No ground object yet? Fall back to an invisible flat floor at y = 0.
        if (new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float dist))
        {
            point = ray.GetPoint(dist);
            return true;
        }

        point = default;
        return false;
    }
}
