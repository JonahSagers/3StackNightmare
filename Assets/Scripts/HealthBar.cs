using UnityEngine;
using UnityEngine.UI;

// Added automatically by Health. Builds a small world-space bar above the object
// and keeps it facing the camera.
public class HealthBar : MonoBehaviour
{
    [Tooltip("How high above the object's pivot the bar floats.")]
    public float heightOffset = 1.6f;

    [Tooltip("Bar size in world units (width, height).")]
    public Vector2 size = new Vector2(1.2f, 0.15f);

    public Color fillColor = new Color(0.85f, 0.1f, 0.1f);
    public Color backgroundColor = new Color(0f, 0f, 0f, 0.7f);

    [Tooltip("Thin bar above the health bar, only visible while a shield is up.")]
    public Color shieldColor = new Color(0.85f, 0.95f, 1f);

    Health health;
    Transform bar;
    RectTransform fill;
    RectTransform shieldBack;
    RectTransform shieldFill;
    Camera cam;

    void Start()
    {
        health = GetComponent<Health>();
        cam = Camera.main;
        Build();
    }

    void Build()
    {
        // A tiny Canvas living in the 3D world (not on screen).
        var canvasGO = new GameObject("HealthBar", typeof(Canvas));
        canvasGO.transform.SetParent(transform, false);
        canvasGO.transform.localPosition = Vector3.up * heightOffset;
        canvasGO.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;

        var canvasRect = canvasGO.GetComponent<RectTransform>();
        canvasRect.sizeDelta = size * 100f;         // work in "pixels"...
        canvasRect.localScale = Vector3.one * 0.01f; // ...then shrink to world units
        bar = canvasGO.transform;

        CreateImage("Background", canvasGO.transform, backgroundColor);

        // The fill stretches from the left edge; we shrink its right anchor as health drops.
        fill = CreateImage("Fill", canvasGO.transform, fillColor);
        fill.offsetMin = new Vector2(4f, 4f);   // small inner border
        fill.offsetMax = new Vector2(-4f, -4f);

        // Shield bar: a thinner strip sitting just above the health bar.
        shieldBack = CreateImage("ShieldBackground", canvasGO.transform, backgroundColor);
        PlaceAboveBar(shieldBack);
        shieldFill = CreateImage("ShieldFill", shieldBack, shieldColor);
        shieldFill.offsetMin = new Vector2(3f, 2f);
        shieldFill.offsetMax = new Vector2(-3f, -2f);
        shieldBack.gameObject.SetActive(false);
    }

    void PlaceAboveBar(RectTransform rect)
    {
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 0f);
        rect.sizeDelta = new Vector2(0f, size.y * 100f * 0.6f); // 60% as tall as the health bar
        rect.anchoredPosition = new Vector2(0f, 3f);             // small gap above it
    }

    RectTransform CreateImage(string name, Transform parent, Color color)
    {
        var go = new GameObject(name, typeof(Image));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = color;

        var rect = go.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        return rect;
    }

    void LateUpdate()
    {
        if (bar == null) return;

        // Shrink the fill from right to left.
        fill.anchorMax = new Vector2(health.Fraction, 1f);

        // Show the shield strip only while there's a shield, shrinking as it's used up.
        bool shielded = health.Shield > 0f;
        if (shieldBack.gameObject.activeSelf != shielded) shieldBack.gameObject.SetActive(shielded);
        if (shielded) shieldFill.anchorMax = new Vector2(health.ShieldFraction, 1f);

        // Stay upright and face the same way as the camera so it's always readable.
        if (cam != null) bar.rotation = cam.transform.rotation;
    }
}
