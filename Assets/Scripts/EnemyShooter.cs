using UnityEngine;

// Attach to an enemy (your EnemyShooter). Fires small projectiles straight ahead (the way the
// shooter is facing, its blue Z arrow) on a timer. It doesn't aim; rotate the shooter to change direction.
public class EnemyShooter : MonoBehaviour
{
    [Tooltip("Always damaged by these shots, whatever its team. Defaults to the object named \"Player\".")]
    public Transform target;

    public float damage = 15f;

    [Tooltip("Seconds between shots.")]
    public float fireInterval = 0.5f;

    [Tooltip("How far each shot travels before disappearing.")]
    public float range = 15f;

    [Tooltip("Turns the firing direction around the shooter, in degrees. 0 = straight ahead (blue Z arrow), 180 = backward.")]
    public float fireAngle = 180f;

    [Tooltip("Only fire while the player is within Range. Off = fire nonstop.")]
    public bool onlyWhenPlayerInRange = false;

    [Header("Projectile")]
    public float projectileSpeed = 12f;
    public float projectileSize = 0.25f;
    public Color projectileColor = new Color(1f, 0.25f, 0.1f);

    [Tooltip("Height above this object's pivot the shots come from.")]
    public float spawnHeight = 0f;

    float nextShotTime;

    void Start()
    {
        if (target == null)
        {
            GameObject player = GameObject.Find("Player");
            if (player != null) target = player.transform;
        }
    }

    void Update()
    {
        if (onlyWhenPlayerInRange)
        {
            if (target == null) return;
            Health targetHealth = target.GetComponent<Health>();
            if (targetHealth != null && targetHealth.IsDead) return;
            if (Vector3.Distance(target.position, transform.position) > range) return;
        }

        if (Time.time >= nextShotTime)
        {
            Vector3 dir = FireDirection();
            Shoot(dir);
            nextShotTime = Time.time + fireInterval;
        }
    }

    Vector3 FireDirection()
    {
        Vector3 dir = Quaternion.AngleAxis(fireAngle, Vector3.up) * transform.forward;
        dir.y = 0f; // keep shots level even if the shooter is tilted
        return dir.sqrMagnitude > 0.001f ? dir.normalized : Vector3.forward;
    }

    void Shoot(Vector3 direction)
    {
        GameObject ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        ball.name = "Enemy Shot";
        Destroy(ball.GetComponent<Collider>()); // the projectile checks for hits itself
        ball.transform.position = transform.position + Vector3.up * spawnHeight + direction * 0.7f;
        ball.transform.localScale = Vector3.one * projectileSize;
        ball.GetComponent<Renderer>().material.color = projectileColor;

        ball.AddComponent<EnemyProjectile>().Launch(direction, projectileSpeed, range + 2f, damage, projectileSize * 0.5f, target);
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.25f, 0.1f, 0.5f);
        Gizmos.DrawLine(transform.position, transform.position + FireDirection() * range); // firing line
    }
}
