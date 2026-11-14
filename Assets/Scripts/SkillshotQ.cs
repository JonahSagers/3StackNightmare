using UnityEngine;
using UnityEngine.InputSystem;

// Attach to the Player. Press Q to fire a sphere toward the mouse cursor, League-style.
public class SkillshotQ : MonoBehaviour
{
    [Header("Projectile")]
    public float speed = 15f;
    public float range = 10f;
    public float size = 0.5f;
    public Color color = new Color(0.3f, 0.7f, 1f);

    [Header("Timing")]
    [Tooltip("Seconds before Q can be used again.")]
    public float cooldown = 1f;

    [Tooltip("Height above the player's pivot the sphere spawns at.")]
    public float spawnHeight = 0f;

    [Tooltip("Camera used to aim with the mouse. Defaults to Main Camera.")]
    public Camera cam;

    float nextReadyTime;

    void Start()
    {
        if (cam == null) cam = Camera.main;
    }

    void Update()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null || !keyboard.qKey.wasPressedThisFrame) return;
        if (Time.time < nextReadyTime) return;

        Vector3 direction = GetAimDirection();
        if (direction == Vector3.zero) return;

        Fire(direction);
        nextReadyTime = Time.time + cooldown;
    }

    Vector3 GetAimDirection()
    {
        Vector2 mousePos = Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
        Ray ray = cam.ScreenPointToRay(mousePos);

        // Aim on a flat plane at the player's height so the shot always travels level.
        Plane aimPlane = new Plane(Vector3.up, transform.position);
        if (!aimPlane.Raycast(ray, out float dist)) return Vector3.zero;

        Vector3 toMouse = ray.GetPoint(dist) - transform.position;
        toMouse.y = 0f;
        return toMouse.sqrMagnitude < 0.0001f ? transform.forward : toMouse.normalized;
    }

    void Fire(Vector3 direction)
    {
        GameObject ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        ball.name = "Q Skillshot";
        ball.transform.position = transform.position + Vector3.up * spawnHeight + direction * 0.75f;
        ball.transform.localScale = Vector3.one * size;

        // Tint it so it stands out (works with URP's default material).
        var renderer = ball.GetComponent<Renderer>();
        renderer.material.color = color;

        // Make it a trigger so it doesn't shove the player or the ground around.
        ball.GetComponent<Collider>().isTrigger = true;

        // Trigger events need a Rigidbody on at least one side. Kinematic = we move it ourselves.
        var body = ball.AddComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;

        var projectile = ball.AddComponent<SkillshotProjectile>();
        projectile.Launch(direction, speed, range, gameObject);

        // Face the shot direction too, like casting a spell.
        transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
    }
}
