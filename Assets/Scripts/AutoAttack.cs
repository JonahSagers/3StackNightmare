using UnityEngine;

// Attach to the Player (next to PlayerMove). Right-click an enemy to auto attack it, League-style:
// if it's out of range the player walks toward it, then fires small homing spheres until the enemy
// dies or you right-click the ground.
public class AutoAttack : MonoBehaviour
{
    public float damage = 5f;

    [Tooltip("How close the player must be to start attacking.")]
    public float range = 4f;

    [Tooltip("Seconds between attacks.")]
    public float attackInterval = 0.8f;

    [Header("Projectile")]
    public float projectileSpeed = 20f;
    public float projectileSize = 0.2f;
    public Color projectileColor = new Color(1f, 0.85f, 0.4f);

    PlayerMove mover;
    Health target;
    float nextAttackTime;

    void Start()
    {
        mover = GetComponent<PlayerMove>();
    }

    // Called by PlayerMove on right-click. Returns true if the click landed on an enemy.
    public bool TryTargetUnderMouse(Ray ray)
    {
        RaycastHit[] hits = Physics.RaycastAll(ray, 500f);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance)); // nearest first

        foreach (RaycastHit hit in hits)
        {
            if (hit.transform.IsChildOf(transform)) continue; // ignore ourselves

            Health health = hit.collider.GetComponentInParent<Health>();
            if (health != null && health.team == Team.Enemy && !health.IsDead)
            {
                target = health;
                return true;
            }
            return false; // nearest thing under the mouse isn't an enemy
        }
        return false;
    }

    public void ClearTarget() => target = null;

    void Update()
    {
        if (target == null) return;
        if (target.IsDead) { target = null; return; }

        Vector3 toTarget = target.transform.position - transform.position;
        toTarget.y = 0f;

        if (toTarget.magnitude > range)
        {
            // Out of range: keep walking toward it (it may be moving or knocked back).
            if (mover != null) mover.MoveTo(target.transform.position);
            return;
        }

        // In range: stand still, face the enemy, and attack on a timer.
        if (mover != null) mover.Stop();
        if (toTarget.sqrMagnitude > 0.001f) transform.rotation = Quaternion.LookRotation(toTarget.normalized, Vector3.up);

        if (Time.time >= nextAttackTime)
        {
            Fire();
            nextAttackTime = Time.time + attackInterval;
        }
    }

    void Fire()
    {
        GameObject ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        ball.name = "Auto Attack";
        Destroy(ball.GetComponent<Collider>()); // homing, so it doesn't need physics
        ball.transform.position = transform.position + transform.forward * 0.6f;
        ball.transform.localScale = Vector3.one * projectileSize;
        ball.GetComponent<Renderer>().material.color = projectileColor;

        ball.AddComponent<AutoAttackProjectile>().Launch(target, damage, projectileSpeed);
    }

    // Shows the attack range in the Scene view when the Player is selected.
    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.85f, 0.4f, 0.6f);
        Gizmos.DrawWireSphere(transform.position, range);
    }
}
