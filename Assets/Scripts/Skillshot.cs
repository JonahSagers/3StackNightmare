using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

public enum SkillEffect { Damage, Heal }

// Attach to the Player, once per ability. Press the key to fire an orb toward the mouse cursor, League-style.
// Damage orbs only hit Enemy-team targets; Heal orbs only hit Ally-team targets. Everything else they fly through.
public class Skillshot : MonoBehaviour
{
    [Header("Ability")]
    public Key key = Key.Q;
    public SkillEffect effect = SkillEffect.Damage;

    [Tooltip("Damage dealt, or health restored.")]
    [FormerlySerializedAs("damage")]
    public float amount = 25f;

    [Header("Projectile")]
    [Tooltip("Your Q_Projectile prefab. Leave empty to fall back to a plain sphere.")]
    public GameObject projectilePrefab;

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

    /// <summary>Time.time at which the ability can be used again (read by AbilityBar).</summary>
    public float NextReadyTime => nextReadyTime;

    void Start()
    {
        if (cam == null) cam = Camera.main;
    }

    void Update()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null || !keyboard[key].wasPressedThisFrame) return;
        if (PiUISprites.IsTyping) return; // key went to a text box
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
        Vector3 spawnPos = transform.position + Vector3.up * spawnHeight + direction * 0.75f;
        Quaternion spawnRot = Quaternion.LookRotation(direction, Vector3.up); // so trails/particles point the right way

        GameObject ball;
        if (projectilePrefab != null)
        {
            ball = Instantiate(projectilePrefab, spawnPos, spawnRot);
            ball.name = key + " Skillshot";
        }
        else
        {
            // No prefab assigned: plain tinted sphere, like before.
            ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ball.name = key + " Skillshot";
            ball.transform.SetPositionAndRotation(spawnPos, spawnRot);
            ball.transform.localScale = Vector3.one * size;
            ball.GetComponent<Renderer>().material.color = color;
        }

        // Make sure it can detect hits, whether or not the prefab already has these.
        Collider col = ball.GetComponent<Collider>();
        if (col == null) col = ball.AddComponent<SphereCollider>();
        col.isTrigger = true; // don't shove the player or ground around

        // Trigger events need a Rigidbody on at least one side. Kinematic = we move it ourselves.
        Rigidbody body = ball.GetComponent<Rigidbody>();
        if (body == null) body = ball.AddComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;

        SkillshotProjectile projectile = ball.GetComponent<SkillshotProjectile>();
        if (projectile == null) projectile = ball.AddComponent<SkillshotProjectile>();
        projectile.Launch(direction, speed, range, gameObject, amount, effect);

        // Face the shot direction too, like casting a spell.
        transform.rotation = spawnRot;
    }
}
