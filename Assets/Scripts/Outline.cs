using UnityEngine;

// Attach to any object with a MeshFilter (your Enemy) to give it a solid-color outline.
// Uses the Custom/Outline shader via Assets/Resources/OutlineMaterial.
public class Outline : MonoBehaviour
{
    public Color color = Color.red;

    [Range(0f, 0.3f)]
    public float width = 0.04f;

    Material material;

    void Start()
    {
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
        var renderer = shell.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        Apply();
    }

    void OnValidate() => Apply(); // live-update when you tweak it in the Inspector

    void Apply()
    {
        if (material == null) return;
        material.SetColor("_OutlineColor", color);
        material.SetFloat("_OutlineWidth", width);
    }

    void OnDestroy()
    {
        if (material != null) Destroy(material);
    }
}
