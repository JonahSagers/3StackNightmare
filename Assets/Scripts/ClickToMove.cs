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

    [Header("Bounds")]
    [Tooltip("The player can't leave this collider's area. Defaults to the object named \"Ground\".")]
    public Collider ground;

    [Tooltip("How far from the edge the player stops. ~ the player's radius keeps it fully on the floor.")]
    public float edgePadding = 0.5f;

    Vector3 targetPosition;
    bool hasTarget;
    AutoAttack autoAttack;

    void Start()
    {
        if (cam == null) cam = Camera.main;
        autoAttack = GetComponent<AutoAttack>();

        if (ground == null)
        {
            GameObject g = GameObject.Find("Ground");
            if (g != null) ground = g.GetComponent<Collider>();
        }
    }

    void Update()
    {
        var mouse = Mouse.current;
        bool rightClicked = mouse != null && mouse.rightButton.wasPressedThisFrame;

        // Right-clicked an enemy? Let AutoAttack handle it (walk into range, then attack).
        if (rightClicked && autoAttack != null && autoAttack.TryTargetUnderMouse(cam.ScreenPointToRay(mouse.position.ReadValue())))
            rightClicked = false;
        else if (rightClicked && autoAttack != null)
            autoAttack.ClearTarget(); // clicked the ground: stop attacking and just walk

        if (rightClicked && TryGetClickPoint(mouse.position.ReadValue(), out Vector3 point))
        {
            // Keep the player's own height so it doesn't sink into the floor.
            targetPosition = ClampToGround(new Vector3(point.x, transform.position.y, point.z));
            hasTarget = true;
        }

        if (!hasTarget) return;

        Vector3 toTarget = targetPosition - transform.position;
        if (toTarget.magnitude <= stopDistance)
        {
            hasTarget = false;
            return;
        }

        transform.position = ClampToGround(Vector3.MoveTowards(transform.position, targetPosition, moveSpeed * Time.deltaTime));

        Quaternion look = Quaternion.LookRotation(toTarget.normalized, Vector3.up);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, look, turnSpeed * Time.deltaTime);
    }

    // Cancel the current walk (used by abilities like the leap so we don't keep walking afterward).
    public void Stop() => hasTarget = false;

    // Walk toward a point (used by AutoAttack to chase a target into range).
    public void MoveTo(Vector3 point)
    {
        targetPosition = ClampToGround(new Vector3(point.x, transform.position.y, point.z));
        hasTarget = true;
    }

    // Keeps a position inside the ground's area (X and Z only; height is left alone).
    public Vector3 ClampToGround(Vector3 pos)
    {
        // Stay between the lane walls when there's a lane.
        if (Lane.Active != null) pos = Lane.Active.Clamp(pos, edgePadding);
        if (ground == null) return pos;

        Bounds b = ground.bounds;
        float padX = Mathf.Min(edgePadding, b.extents.x);
        float padZ = Mathf.Min(edgePadding, b.extents.z);
        pos.x = Mathf.Clamp(pos.x, b.min.x + padX, b.max.x - padX);
        pos.z = Mathf.Clamp(pos.z, b.min.z + padZ, b.max.z - padZ);
        return pos;
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
