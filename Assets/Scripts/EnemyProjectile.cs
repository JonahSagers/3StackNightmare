using UnityEngine;

// Added automatically by EnemyShooter. Flies straight and damages the first Ally-team target it touches
// (the player, or the Friendly if it's in the way). Disappears after travelling its max distance.
public class EnemyProjectile : MonoBehaviour
{
    Vector3 direction;
    float speed;
    float maxDistance;
    float damage;
    float radius;
    float travelled;
    Transform target;

    public void Launch(Vector3 direction, float speed, float maxDistance, float damage, float radius, Transform target = null)
    {
        this.target = target;
        this.direction = direction;
        this.speed = speed;
        this.maxDistance = maxDistance;
        this.damage = damage;
        this.radius = radius;
    }

    void Update()
    {
        float step = speed * Time.deltaTime;
        transform.position += direction * step;
        travelled += step;

        foreach (Collider hit in Physics.OverlapSphere(transform.position, radius))
        {
            Health health = hit.GetComponentInParent<Health>();
            if (health == null || health.IsDead) continue;

            // Hit the shooter's target no matter what team it's set to, plus any ally in the way.
            bool isTarget = target != null && health.transform == target;
            if (!isTarget && health.team != Team.Ally) continue;

            health.TakeDamage(damage);
            Destroy(gameObject);
            return;
        }

        if (travelled >= maxDistance) Destroy(gameObject);
    }
}
