using UnityEngine;

// Attach to any object with a MeshFilter (your Enemy) to give it a solid-color outline.
// Uses the Custom/Outline shader via Assets/Resources/OutlineMaterial.
public class Outline : MonoBehaviour
{
    public Color color = Color.red;

    [Range(0f, 0.3f)]
    public float width = 0.04f;

    [Tooltip("Whether the outline is drawn at start. MouseHandler shows it purple while hovered, then restores this.")]
    public bool visible = false;

    Material material;
    MeshRenderer shellRenderer;

    // The color/visibility the object started with, restored by Hide() (e.g. Enemy red, Friendly green).
    Color startColor;
    bool startVisible;

    void Start()
    {
        startColor = color;
        startVisible = visible;

        var meshFilter = GetComponent<MeshFilter>();
        var baseMaterial = Resources.Load<Material>("OutlineMaterial");
        if (meshFilter == null || baseMaterial == null)
        {
            Debug.LogWarning($"Outline on {name}: needs a MeshFilter and Assets/Resources/OutlineMaterial.");
            return;
        }

        // Our own copy, so each object can have its own color/width.
        material = new Material(baseMaterial);

        // A child that draws the same mesh, inflated, with only the back faces showing.
        var shell = new GameObject("Outline");
        shell.transform.SetParent(transform, false);
        shell.AddComponent<MeshFilter>().sharedMesh = meshFilter.sharedMesh;
        shellRenderer = shell.AddComponent<MeshRenderer>();
        shellRenderer.sharedMaterial = material;
        shellRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        shellRenderer.receiveShadows = false;

        Apply();
    }

    void OnValidate() => Apply(); // live-update when you tweak it in the Inspector

    public void Show(Color c)
    {
        color = c;
        visible = true;
        Apply();
    }

    // Back to the starting state: the original outline if it had one, otherwise invisible.
    public void Hide()
    {
        color = startColor;
        visible = startVisible;
        Apply();
    }

    void Apply()
    {
        if (shellRenderer != null) shellRenderer.enabled = visible;
        if (material == null) return;
        material.SetColor("_OutlineColor", color);
        material.SetFloat("_OutlineWidth", width);
    }

    void OnDestroy()
    {
        if (material != null) Destroy(material);
    }
}
