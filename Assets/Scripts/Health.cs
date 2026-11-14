using System.Collections;
using UnityEngine;

// Attach to anything that can take damage (your Enemy). Adds a floating health bar automatically.
[RequireComponent(typeof(HealthBar))]
public class Health : MonoBehaviour
{
    public float maxHealth = 100f;

    [Header("Death")]
    [Tooltip("Come back after dying instead of being destroyed.")]
    public bool respawn = true;

    [Tooltip("Seconds to wait before respawning.")]
    public float respawnDelay = 3f;

    public float Current { get; private set; }
    public float Fraction => maxHealth <= 0f ? 0f : Current / maxHealth;
    public bool IsDead => Current <= 0f;

    Vector3 spawnPosition;
    Quaternion spawnRotation;

    void Awake()
    {
        Current = maxHealth;
        spawnPosition = transform.position;   // remember where we started
        spawnRotation = transform.rotation;
    }

    public void TakeDamage(float amount)
    {
        if (IsDead) return;

        Current = Mathf.Max(0f, Current - amount);
        Debug.Log($"{name} took {amount} damage ({Current}/{maxHealth})");

        if (IsDead) Die();
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
        StartCoroutine(RespawnAfterDelay());
    }

    IEnumerator RespawnAfterDelay()
    {
        yield return new WaitForSeconds(respawnDelay);

        transform.SetPositionAndRotation(spawnPosition, spawnRotation);
        Current = maxHealth;
        SetVisible(true);
        Debug.Log($"{name} respawned");
    }

    // Turns every renderer, collider and UI canvas on this object (and its children) on or off.
    void SetVisible(bool visible)
    {
        foreach (var r in GetComponentsInChildren<Renderer>(true)) r.enabled = visible;
        foreach (var c in GetComponentsInChildren<Collider>(true)) c.enabled = visible;
        foreach (var c in GetComponentsInChildren<Canvas>(true)) c.enabled = visible;
    }
}
