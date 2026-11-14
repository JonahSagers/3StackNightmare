using UnityEngine;

// Added automatically by Skillshot. Flies in a straight line, then disappears at max range.
public class SkillshotProjectile : MonoBehaviour
{
    Vector3 direction;
    float speed;
    float range;
    float travelled;
    GameObject owner;
    float amount;
    SkillEffect effect;

    public void Launch(Vector3 dir, float spd, float maxRange, GameObject caster, float amt, SkillEffect fx)
    {
        amount = amt;
        effect = fx;
        direction = dir;
        speed = spd;
        range = maxRange;
        owner = caster;
    }

    void Update()
    {
        float step = speed * Time.deltaTime;
        transform.position += direction * step;
        travelled += step;

        if (travelled >= range) Destroy(gameObject);
    }

    void OnTriggerEnter(Collider other)
    {
        if (owner != null && other.transform.IsChildOf(owner.transform)) return; // don't hit yourself
        if (other.name == "Ground") return; // skim along the floor
        if (other.GetComponent<SkillshotProjectile>() != null) return; // orbs pass through each other

        Health health = other.GetComponentInParent<Health>();
        if (health != null)
        {
            // Damage only hurts enemies, heals only help allies. Wrong team? Fly straight through.
            if (effect == SkillEffect.Damage && health.team == Team.Enemy) health.TakeDamage(amount);
            else if (effect == SkillEffect.Heal && health.team == Team.Ally) health.Heal(amount);
            else return;
        }

        Debug.Log($"{name} hit {other.name}");

        Destroy(gameObject);
    }
}
