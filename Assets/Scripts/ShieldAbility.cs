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

    [Header("Sparkles")]
    [Tooltip("Tiny glints that orbit around the bubble.")]
    public bool orbitingSparkles = true;
    public Color sparkleColor = new Color(0.85f, 0.97f, 1f, 1f);
    [Tooltip("Sparkles spawned per second.")]
    public float sparkleRate = 30f;
    [Tooltip("How fast the sparkles circle the player.")]
    public float sparkleOrbitSpeed = 2.5f;

    Health health;
    float nextReadyTime;

    /// <summary>Time.time at which the ability can be used again (read by AbilityBar).</summary>
    public float NextReadyTime => nextReadyTime;
    GameObject bubble;
    Material bubbleMaterial;
    GameObject sparkles;

    void Start()
    {
        health = GetComponent<Health>();
    }

    void Update()
    {
        var keyboard = Keyboard.current;
        if (keyboard != null && keyboard[key].wasPressedThisFrame && GUIUtility.keyboardControl == 0 && Time.time >= nextReadyTime && !health.IsDead)
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

        if (orbitingSparkles && sparkles == null) sparkles = CreateSparkles();
        if (sparkles != null) sparkles.SetActive(true);
    }

    // A particle system of small glints spawning on the bubble's surface and swirling around it.
    GameObject CreateSparkles()
    {
        var go = new GameObject("Shield Sparkles");
        go.transform.SetParent(transform, false);

        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); // configure before it plays

        var main = ps.main;
        main.loop = true;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 1f);
        main.startSpeed = 0f;
        main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.14f);
        main.startColor = sparkleColor;
        main.simulationSpace = ParticleSystemSimulationSpace.Local; // follow the player
        main.maxParticles = 200;

        var emission = ps.emission;
        emission.rateOverTime = sparkleRate;

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = bubbleSize * 0.5f;
        shape.radiusThickness = 0f; // spawn on the surface, not inside

        // Swirl: orbit around the vertical axis, with a slight upward drift.
        var velocity = ps.velocityOverLifetime;
        velocity.enabled = true;
        velocity.space = ParticleSystemSimulationSpace.Local;
        velocity.orbitalY = sparkleOrbitSpeed;
        velocity.y = 0.3f;

        // Twinkle: pop in, then shrink away.
        var size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
            new Keyframe(0f, 0f), new Keyframe(0.15f, 1f), new Keyframe(1f, 0f)));

        var renderer = go.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = Resources.Load<Material>("RippleMaterial"); // additive glow
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        ps.Play();
        return go;
    }

    void UpdateBubble()
    {
        if (bubble == null) return;

        bool up = health.Shield > 0f;
        if (bubble.activeSelf != up) bubble.SetActive(up);
        if (sparkles != null && sparkles.activeSelf != up) sparkles.SetActive(up);
        if (!up || bubbleMaterial == null) return;

        // Fade the bubble as the shield gets used up.
        Color c = bubbleColor;
        c.a *= Mathf.Lerp(0.3f, 1f, health.ShieldFraction);
        bubbleMaterial.SetColor("_BaseColor", c);
    }

    void OnDisable()
    {
        if (bubble != null) bubble.SetActive(false); // e.g. while dead
        if (sparkles != null) sparkles.SetActive(false);
    }

    void OnDestroy()
    {
        if (bubbleMaterial != null) Destroy(bubbleMaterial);
    }
}
