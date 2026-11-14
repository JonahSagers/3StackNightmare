using UnityEngine;
using UnityEngine.InputSystem;

// Attach to the Player (needs Health). Press the key to put up a shield that absorbs damage
// before your health does. Shows a see-through bubble while it's up.
[RequireComponent(typeof(Health))]
public class ShieldAbility : MonoBehaviour
{
    public Key key = Key.R;

    [Tooltip("How much damage the shield can absorb.")]
    public float shieldAmount = 50f;

    [Tooltip("Seconds before the shield wears off, even if it isn't used up.")]
    public float duration = 4f;

    public float cooldown = 10f;

    [Header("Bubble")]
    public Color bubbleColor = new Color(0.5f, 0.85f, 1f, 0.35f);
    public float bubbleSize = 2.4f;

    Health health;
    float nextReadyTime;
    GameObject bubble;
    Material bubbleMaterial;

    void Start()
    {
        health = GetComponent<Health>();
    }

    void Update()
    {
        var keyboard = Keyboard.current;
        if (keyboard != null && keyboard[key].wasPressedThisFrame && Time.time >= nextReadyTime && !health.IsDead)
        {
            health.AddShield(shieldAmount, duration);
            nextReadyTime = Time.time + cooldown;
            ShowBubble();
        }

        UpdateBubble();
    }

    void ShowBubble()
    {
        if (bubble == null)
        {
            bubble = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            bubble.name = "Shield Bubble";
            Destroy(bubble.GetComponent<Collider>()); // purely visual
            bubble.transform.SetParent(transform, false);
            bubble.transform.localScale = Vector3.one * bubbleSize;

            var renderer = bubble.GetComponent<Renderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var baseMaterial = Resources.Load<Material>("ShieldMaterial");
            if (baseMaterial != null)
            {
                bubbleMaterial = new Material(baseMaterial);
                renderer.sharedMaterial = bubbleMaterial;
            }
        }
        bubble.SetActive(true);
    }

    void UpdateBubble()
    {
        if (bubble == null) return;

        bool up = health.Shield > 0f;
        if (bubble.activeSelf != up) bubble.SetActive(up);
        if (!up || bubbleMaterial == null) return;

        // Fade the bubble as the shield gets used up.
        Color c = bubbleColor;
        c.a *= Mathf.Lerp(0.3f, 1f, health.ShieldFraction);
        bubbleMaterial.SetColor("_BaseColor", c);
    }

    void OnDisable()
    {
        if (bubble != null) bubble.SetActive(false); // e.g. while dead
    }

    void OnDestroy()
    {
        if (bubbleMaterial != null) Destroy(bubbleMaterial);
    }
}
