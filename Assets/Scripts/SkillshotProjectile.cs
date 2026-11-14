using UnityEngine;

// Added automatically by SkillshotQ. Flies in a straight line, then disappears at max range.
public class SkillshotProjectile : MonoBehaviour
{
    Vector3 direction;
    float speed;
    float range;
    float travelled;
    GameObject owner;

    public void Launch(Vector3 dir, float spd, float maxRange, GameObject caster)
    {
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

        Debug.Log($"Q hit {other.name}");
        Destroy(gameObject);
    }
}
