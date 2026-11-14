using System.Collections;
using UnityEngine;

public enum Team { Ally, Enemy }

// Attach to anything that can be damaged or healed. Adds a floating health bar automatically.
[RequireComponent(typeof(HealthBar))]
public class Health : MonoBehaviour
{
    [Tooltip("Ally: can be healed by W, ignored by Q. Enemy: damaged by Q, ignored by W.")]
    public Team team = Team.Enemy;

    public float maxHealth = 100f;

    [Tooltip("Health when the game starts. Respawning always restores full health.")]
    public float startingHealth = 100f;

    [Header("Decay")]
    [Tooltip("Allies only: when health is above Starting Health, drain back down to it after a pause.")]
    public bool decayToStartingHealth = true;

    [Tooltip("Seconds after the last heal before draining starts.")]
    public float decayDelaySeconds = 2f;

    [Tooltip("Health lost per second while draining.")]
    public float decayPerSecond = 15f;

    [Header("Death")]
    [Tooltip("Come back after dying instead of being destroyed.")]
    public bool respawn = true;

    [Tooltip("Seconds to wait before respawning.")]
    public float respawnDelay = 3f;

    public float Current { get; private set; }
    public float Fraction => maxHealth <= 0f ? 0f : Current / maxHealth;
    public bool IsDead => Current <= 0f;

    float lastHealTime;
    Vector3 spawnPosition;
    Quaternion spawnRotation;

    void Awake()
    {
        Current = Mathf.Clamp(startingHealth, 0f, maxHealth);
        spawnPosition = transform.position;   // remember where we started
        spawnRotation = transform.rotation;
    }

    void Update()
    {
        if (team != Team.Ally || !decayToStartingHealth || IsDead) return; // only friendlies decay
        if (Current <= startingHealth) return;                 // nothing to drain
        if (Time.time - lastHealTime < decayDelaySeconds) return;     // still in the pause after a heal

        // Slide smoothly toward Starting Health, a little each frame.
        Current = Mathf.MoveTowards(Current, startingHealth, decayPerSecond * Time.deltaTime);
    }

    public void TakeDamage(float amount)
    {
        if (IsDead) return;

        Current = Mathf.Max(0f, Current - amount);
        Debug.Log($"{name} took {amount} damage ({Current}/{maxHealth})");

        if (IsDead) Die();
    }

    public void Heal(float amount)
    {
        if (IsDead) return;

        Current = Mathf.Min(maxHealth, Current + amount);
        lastHealTime = Time.time; // restart the decay countdown
        Debug.Log($"{name} healed {amount} ({Current}/{maxHealth})");
    }

    void Die()
    {
        Debug.Log($"{name} died");

        if (!respawn)
        {
            Destroy(gameObject);
            return;
        }

        // Hide instead of destroying, so this script keeps running and can bring us back.
        SetVisible(false);
        SetControlsEnabled(false); // e.g. a dead player can't walk or cast
        StartCoroutine(RespawnAfterDelay());
    }

    IEnumerator RespawnAfterDelay()
    {
        yield return new WaitForSeconds(respawnDelay);

        transform.SetPositionAndRotation(spawnPosition, spawnRotation);
        Current = maxHealth;
        lastHealTime = Time.time; // full health after respawn, then decay as usual
        SetVisible(true);
        SetControlsEnabled(true);
        Debug.Log($"{name} respawned");
    }

    // Pauses this object's other scripts (movement, abilities, shooting) while dead.
    readonly System.Collections.Generic.List<MonoBehaviour> pausedWhileDead = new System.Collections.Generic.List<MonoBehaviour>();

    void SetControlsEnabled(bool on)
    {
        if (!on)
        {
            pausedWhileDead.Clear();
            foreach (var mb in GetComponents<MonoBehaviour>())
            {
                if (mb == this || mb is HealthBar || mb is Outline || !mb.enabled) continue;
                mb.enabled = false;
                pausedWhileDead.Add(mb);
            }
        }
        else
        {
            foreach (var mb in pausedWhileDead) if (mb != null) mb.enabled = true;
            pausedWhileDead.Clear();
        }
    }

    // Turns every renderer, collider and UI canvas on this object (and its children) on or off.
    void SetVisible(bool visible)
    {
        foreach (var r in GetComponentsInChildren<Renderer>(true)) r.enabled = visible;
        foreach (var c in GetComponentsInChildren<Collider>(true)) c.enabled = visible;
        foreach (var c in GetComponentsInChildren<Canvas>(true)) c.enabled = visible;
    }
}
