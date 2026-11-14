using UnityEngine;

// Attach to an enemy (your EnemyShooter). Turns to face the player and fires small projectiles at it.
// Projectiles fly straight toward where the player WAS, so you can dodge them by moving.
public class EnemyShooter : MonoBehaviour
{
    [Tooltip("Who to shoot at. Defaults to the object named \"Player\".")]
    public Transform target;

    public float damage = 15f;

    [Tooltip("Seconds between shots.")]
    public float fireInterval = 0.5f;

    [Tooltip("Only shoots when the target is this close.")]
    public float range = 15f;

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
        if (target == null) return;

        // Don't shoot at a dead (hidden) player.
        Health targetHealth = target.GetComponent<Health>();
        if (targetHealth != null && targetHealth.IsDead) return;

        Vector3 toTarget = target.position - transform.position;
        toTarget.y = 0f;
        if (toTarget.magnitude > range) return;

        transform.rotation = Quaternion.LookRotation(toTarget.normalized, Vector3.up);

        if (Time.time >= nextShotTime)
        {
            Shoot(toTarget.normalized);
            nextShotTime = Time.time + fireInterval;
        }
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
        Gizmos.DrawWireSphere(transform.position, range);
    }
}
