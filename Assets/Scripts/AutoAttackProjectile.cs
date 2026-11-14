using UnityEngine;

// Added automatically by AutoAttack. Homes in on its target and deals damage on arrival.
public class AutoAttackProjectile : MonoBehaviour
{
    Health target;
    float damage;
    float speed;

    public void Launch(Health target, float damage, float speed)
    {
        this.target = target;
        this.damage = damage;
        this.speed = speed;
    }

    void Update()
    {
        // Target died or respawned out from under us? Fizzle out.
        if (target == null || target.IsDead) { Destroy(gameObject); return; }

        Vector3 aim = target.transform.position;
        transform.position = Vector3.MoveTowards(transform.position, aim, speed * Time.deltaTime);

        if ((transform.position - aim).sqrMagnitude < 0.3f * 0.3f)
        {
            target.TakeDamage(damage);
            Destroy(gameObject);
        }
    }
}
