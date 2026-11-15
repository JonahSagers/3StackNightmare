using UnityEngine;

// Health pack pickup (like ARAM health relics). When the player or a teammate walks over it, it heals
// them by a fraction of their max health, disappears, and comes back after a delay.
// Works by distance on the ground (X/Z), so it needs no collider. Lane.Rebuild places one in each alcove.
public class HealthPack : MonoBehaviour
{
    [Tooltip("Fraction of max health restored on pickup (0.333 = a third).")]
    public float healFraction = 1f / 3f;

    [Tooltip("How close (on the ground) a character has to get to pick it up.")]
    public float pickupRadius = 1.3f;

    [Tooltip("Seconds before the pack comes back after being picked up.")]
    public float respawnSeconds = 30f;

    [Tooltip("Characters already at full health walk over it without using it.")]
    public bool ignoreWhenFull = true;

    [Header("Look")]
    public float spinSpeed = 90f;
    public float bobHeight = 0.15f;
    public float bobSpeed = 2f;

    readonly System.Collections.Generic.List<Health> candidates = new System.Collections.Generic.List<Health>();
    float nextCandidateRefresh;
    Transform visual;
    float visualBaseY;
    float readyTime;

    public bool IsReady => Time.time >= readyTime;

    // Team rule: only living Ally-team characters (the player and teammates) can pick it up.
    public static bool CanPickUp(Health h) => h != null && !h.IsDead && h.team == Team.Ally;

    void Update()
    {
        if (visual == null && transform.childCount > 0)
        {
            visual = transform.GetChild(0);
            visualBaseY = visual.localPosition.y;
        }

        bool ready = IsReady;
        if (visual != null)
        {
            if (visual.gameObject.activeSelf != ready) visual.gameObject.SetActive(ready);
            if (ready)
            {
                visual.Rotate(0f, spinSpeed * Time.deltaTime, 0f, Space.World);
                var p = visual.localPosition;
                p.y = visualBaseY + Mathf.Sin(Time.time * bobSpeed) * bobHeight;
                visual.localPosition = p;
            }
        }
        if (!ready) return;

        // Refresh the list of characters now and then (cheap, and picks up spawned teammates).
        if (Time.time >= nextCandidateRefresh)
        {
            nextCandidateRefresh = Time.time + 1f;
            candidates.Clear();
            foreach (var h in FindObjectsByType<Health>())
                if (CanPickUp(h)) candidates.Add(h);
        }

        foreach (var h in candidates)
        {
            if (!CanPickUp(h)) continue;
            if (ignoreWhenFull && h.Current >= h.maxHealth) continue;

            Vector3 d = h.transform.position - transform.position;
            d.y = 0f;
            if (d.sqrMagnitude > pickupRadius * pickupRadius) continue;

            h.Heal(h.maxHealth * healFraction);
            readyTime = Time.time + respawnSeconds;
            Debug.Log($"{h.name} picked up {name} (back in {respawnSeconds}s)");
            return;
        }
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(transform.position, pickupRadius);
    }
}
