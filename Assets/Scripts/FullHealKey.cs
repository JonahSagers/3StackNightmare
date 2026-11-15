using UnityEngine;
using UnityEngine.InputSystem;

// Lives on the Player (needs Health); attaches itself automatically. Press the key to restore full health. Handy for testing.
// Shows up on the ability bar at the bottom of the screen as "H - Full Heal".
[RequireComponent(typeof(Health))]
public class FullHealKey : MonoBehaviour
{
    public Key key = Key.H;

    [Tooltip("Seconds before it can be used again. 0 = no cooldown.")]
    public float cooldown = 0f;

    public float NextReadyTime => nextReadyTime;

    Health health;
    float nextReadyTime;

    // Adds itself to the player automatically when the game starts, so the H slot always shows up
    // on the ability bar (no need to add the component by hand).
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoAttach()
    {
        var player = FindAnyObjectByType<ClickToMove>();
        if (player != null && player.GetComponent<Health>() != null && player.GetComponent<FullHealKey>() == null)
            player.gameObject.AddComponent<FullHealKey>();
    }

    void Start()
    {
        health = GetComponent<Health>();
    }

    void Update()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null || !keyboard[key].wasPressedThisFrame) return;
        if (health.IsDead || Time.time < nextReadyTime) return; // dead players wait for the respawn

        health.Heal(health.maxHealth); // Heal caps at max, so this tops you off
        nextReadyTime = Time.time + cooldown;
    }
}
