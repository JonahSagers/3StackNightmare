using UnityEngine;

// A glowing ring that spreads out across the ground and fades away.
// Created from code with GroundRipple.Spawn(...); uses Assets/Resources/RippleMaterial.
public class GroundRipple : MonoBehaviour
{
    const int Segments = 64;

    float maxRadius;
    float duration = 0.5f;
    float startWidth = 0.35f;
    float delay;
    float age;
    Color color;
    LineRenderer line;

    public static void Spawn(Vector3 center, float radius, Color color, float delay = 0f)
    {
        var go = new GameObject("Ground Ripple");
        go.transform.position = center;

        var ripple = go.AddComponent<GroundRipple>();
        ripple.maxRadius = radius;
        ripple.color = color;
        ripple.delay = delay;
    }

    void Start()
    {
        line = gameObject.AddComponent<LineRenderer>();
        line.loop = true;
        line.useWorldSpace = false;
        line.positionCount = Segments;
        line.alignment = LineAlignment.TransformZ;              // lie flat...
        transform.rotation = Quaternion.Euler(90f, 0f, 0f);     // ...facing up
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.sharedMaterial = Resources.Load<Material>("RippleMaterial");
        line.enabled = delay <= 0f;
        UpdateRing(0f);
    }

    void Update()
    {
        if (delay > 0f)
        {
            delay -= Time.deltaTime;
            if (delay > 0f) return;
            line.enabled = true;
        }

        age += Time.deltaTime;
        float t = age / duration;
        if (t >= 1f) { Destroy(gameObject); return; }
        UpdateRing(t);
    }

    void UpdateRing(float t)
    {
        float eased = 1f - (1f - t) * (1f - t);   // fast burst outward, slowing down
        float r = Mathf.Lerp(0.2f, maxRadius, eased);

        for (int i = 0; i < Segments; i++)
        {
            float a = i / (float)Segments * Mathf.PI * 2f;
            line.SetPosition(i, new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0f));
        }

        line.widthMultiplier = Mathf.Lerp(startWidth, 0.02f, t);   // thins out
        Color c = color;
        c.a = 1f - t;                                              // and fades
        line.startColor = line.endColor = c;
    }
}
