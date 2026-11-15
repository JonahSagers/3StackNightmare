using UnityEngine;

// Put on a base pad. While the player stands on it, it either heals them back to full (Heal)
// or hurts them (Damage). Works by distance on the ground (X/Z), so the pad needs no collider.
public class Fountain : MonoBehaviour
{
    public enum Mode { Heal, Damage }

    public Mode mode = Mode.Heal;

    [Tooltip("Radius of the effect area. 0 = use the pad's own size.")]
    public float radius = 0f;

    [Tooltip("Heal mode: fraction of max health restored per second (0.25 = full in 4s from empty).")]
    public float healFractionPerSecond = 0.25f;

    [Tooltip("Damage mode: damage per second to the player.")]
    public float damagePerSecond = 20f;

    [Tooltip("Seconds between ticks (keeps the console from being spammed every frame).")]
    public float tickInterval = 0.25f;

    Health player;
    float nextTick;

    float Radius => radius > 0f ? radius : transform.lossyScale.x * 0.5f;

    // Adds itself to the existing base pads when the game starts, so the scene needs no manual setup.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoAttach()
    {
        Attach("BlueBase", Mode.Heal);
        Attach("RedBase", Mode.Damage);
    }

    static void Attach(string padName, Mode mode)
    {
        var pad = GameObject.Find(padName);
        if (pad == null || pad.GetComponent<Fountain>() != null) return;
        pad.AddComponent<Fountain>().mode = mode;
    }

    void Update()
    {
        if (player == null)
        {
            var mover = FindAnyObjectByType<PlayerMove>();
            if (mover == null) return;
            player = mover.GetComponent<Health>();
            if (player == null) return;
        }

        if (player.IsDead || Time.time < nextTick) return;

        Vector3 d = player.transform.position - transform.position;
        d.y = 0f;
        if (d.sqrMagnitude > Radius * Radius) return;

        nextTick = Time.time + tickInterval;

        if (mode == Mode.Heal)
        {
            float missing = player.maxHealth - player.Current;
            if (missing > 0f)
                player.Heal(Mathf.Min(missing, player.maxHealth * healFractionPerSecond * tickInterval));
        }
        else
        {
            player.TakeDamage(damagePerSecond * tickInterval);
        }
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = mode == Mode.Heal ? Color.cyan : Color.red;
        Gizmos.DrawWireSphere(transform.position, Radius);
    }
}
