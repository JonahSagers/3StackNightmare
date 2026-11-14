using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

// Attach to the Player. Press the key to leap toward the mouse cursor. On landing:
// damages and knocks back every Enemy-team target in a radius, and sends ripples across the ground.
public class LeapSlam : MonoBehaviour
{
    [Header("Ability")]
    public Key key = Key.E;
    public float cooldown = 4f;

    [Header("Leap")]
    [Tooltip("Farthest the leap can travel.")]
    public float maxRange = 6f;
    [Tooltip("Seconds spent in the air.")]
    public float airTime = 0.45f;
    [Tooltip("Peak height of the jump arc.")]
    public float arcHeight = 2f;

    [Header("Impact")]
    public float damage = 40f;
    public float radius = 3f;
    [Tooltip("How far enemies get pushed away from the landing point.")]
    public float knockbackDistance = 3f;
    public float knockbackDuration = 0.25f;

    [Header("Ripple")]
    public Color rippleColor = new Color(1f, 0.55f, 0.15f);
    [Tooltip("How many rings spread out from the landing point.")]
    public int rippleCount = 2;

    [Tooltip("Camera used to aim with the mouse. Defaults to Main Camera.")]
    public Camera cam;

    ClickToMove mover;
    float nextReadyTime;

    /// <summary>Time.time at which the ability can be used again (read by AbilityBar).</summary>
    public float NextReadyTime => nextReadyTime;
    bool leaping;

    void Start()
    {
        if (cam == null) cam = Camera.main;
        mover = GetComponent<ClickToMove>();
    }

    void Update()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null || !keyboard[key].wasPressedThisFrame) return;
        if (leaping || Time.time < nextReadyTime) return;
        if (!TryGetLandingPoint(out Vector3 landing)) return;

        nextReadyTime = Time.time + cooldown;
        StartCoroutine(Leap(landing));
    }

    bool TryGetLandingPoint(out Vector3 landing)
    {
        landing = transform.position;
        if (Mouse.current == null || cam == null) return false;

        Ray ray = cam.ScreenPointToRay(Mouse.current.position.ReadValue());
        Plane floor = new Plane(Vector3.up, transform.position);
        if (!floor.Raycast(ray, out float dist)) return false;

        // Toward the mouse, but no farther than maxRange.
        Vector3 offset = ray.GetPoint(dist) - transform.position;
        offset.y = 0f;
        landing = transform.position + Vector3.ClampMagnitude(offset, maxRange);

        // Don't leap off the map.
        if (mover != null) landing = mover.ClampToGround(landing);
        return true;
    }

    IEnumerator Leap(Vector3 landing)
    {
        leaping = true;

        // Take over from click-to-move while we're in the air.
        if (mover != null) { mover.Stop(); mover.enabled = false; }

        Vector3 start = transform.position;
        Vector3 flat = landing - start;
        if (flat.sqrMagnitude > 0.001f) transform.rotation = Quaternion.LookRotation(flat.normalized, Vector3.up);

        for (float t = 0f; t < 1f; t += Time.deltaTime / airTime)
        {
            Vector3 pos = Vector3.Lerp(start, landing, t);
            pos.y += 4f * arcHeight * t * (1f - t); // parabola: 0 at start/end, arcHeight in the middle
            transform.position = pos;
            yield return null;
        }
        transform.position = landing;

        Impact(landing);

        if (mover != null) mover.enabled = true;
        leaping = false;
    }

    void Impact(Vector3 center)
    {
        foreach (Collider hit in Physics.OverlapSphere(center, radius))
        {
            Health health = hit.GetComponentInParent<Health>();
            if (health == null || health.team != Team.Enemy || health.IsDead) continue;

            health.TakeDamage(damage);

            // Push away from the landing point (flat on the ground).
            Vector3 away = health.transform.position - center;
            away.y = 0f;
            if (away.sqrMagnitude < 0.001f) away = transform.forward;

            var knockback = health.GetComponent<Knockback>();
            if (knockback == null) knockback = health.gameObject.AddComponent<Knockback>();
            knockback.Push(away.normalized, knockbackDistance, knockbackDuration);
        }

        // Ripples sit just above the floor, under the player's feet.
        Vector3 floorPoint = center;
        if (Physics.Raycast(center + Vector3.up * 0.5f, Vector3.down, out RaycastHit floorHit, 5f))
            floorPoint = floorHit.point;

        for (int i = 0; i < rippleCount; i++)
            GroundRipple.Spawn(floorPoint + Vector3.up * 0.02f, radius, rippleColor, delay: i * 0.12f);
    }

    // Shows the impact radius in the Scene view when the Player is selected.
    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.5f, 0f, 0.6f);
        Gizmos.DrawWireSphere(transform.position, radius);
    }
}
