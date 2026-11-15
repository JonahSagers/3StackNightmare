using UnityEngine;

// ARAM-style single lane running diagonally across the map, with rocky walls on both sides.
// The Lane object's rotation sets the lane direction (local +Z runs along the lane).
// Movement (ClickToMove, LeapSlam landings, Knockback) is clamped to the lane via Lane.Active.
// Right-click the component header > "Rebuild Lane" after changing the size settings.
public class Lane : MonoBehaviour
{
    public static Lane Active { get; private set; }

    [Header("Size")]
    [Tooltip("Walkable width between the walls.")]
    public float width = 26f;
    [Tooltip("Walkable length end to end.")]
    public float length = 96f;

    [Header("Walls")]
    public float wallThickness = 3f;
    public Vector2 wallHeightRange = new Vector2(1.8f, 3.2f);
    [Tooltip("Spacing of the rock chunks that make up each wall.")]
    public float rockSpacing = 2.5f;
    public int seed = 7;

    [Header("Materials")]
    public Material floorMaterial;
    public Material wallMaterial;
    public Material blueBaseMaterial;
    public Material redBaseMaterial;

    [Header("Movement")]
    [Tooltip("Extra gap kept between characters and the walls.")]
    public float wallPadding = 0.6f;

    public float HalfWidth => width * 0.5f;
    public float HalfLength => length * 0.5f;

    void OnEnable() => Active = this;
    void OnDisable() { if (Active == this) Active = null; }

    /// <summary>Keeps a world position inside the lane (X/Z only; height is untouched).</summary>
    public Vector3 Clamp(Vector3 worldPos, float radius = 0.5f)
    {
        Vector3 local = transform.InverseTransformPoint(worldPos);
        float pad = radius + wallPadding;
        local.x = Mathf.Clamp(local.x, -HalfWidth + pad, HalfWidth - pad);
        local.z = Mathf.Clamp(local.z, -HalfLength + pad, HalfLength - pad);
        Vector3 result = transform.TransformPoint(local);
        result.y = worldPos.y;
        return result;
    }

    [ContextMenu("Rebuild Lane")]
    public void Rebuild()
    {
        // Clear old geometry.
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var child = transform.GetChild(i).gameObject;
            if (Application.isPlaying) Destroy(child); else DestroyImmediate(child);
        }

        var rng = new System.Random(seed);
        float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);

        // Lane floor: a thin strip just above the ground.
        Block("Floor", transform, new Vector3(0f, 0.01f, 0f), new Vector3(width, 0.02f, length), Quaternion.identity, floorMaterial);

        // Side walls: a continuous base plus irregular rock chunks on top for a rocky silhouette.
        float totalLength = length + wallThickness * 2f;
        for (int side = -1; side <= 1; side += 2)
        {
            var wall = new GameObject(side < 0 ? "Wall_Left" : "Wall_Right").transform;
            wall.SetParent(transform, false);
            float x = side * (HalfWidth + wallThickness * 0.5f);

            Block("Base", wall, new Vector3(x, wallHeightRange.x * 0.5f, 0f),
                new Vector3(wallThickness, wallHeightRange.x, totalLength), Quaternion.identity, wallMaterial);

            for (float z = -totalLength * 0.5f + rockSpacing * 0.5f; z < totalLength * 0.5f; z += rockSpacing)
                Rock(wall, new Vector3(x + side * R(0f, 0.6f), 0f, z + R(-0.4f, 0.4f)), R);
        }

        // End walls closing off each base.
        for (int end = -1; end <= 1; end += 2)
        {
            var cap = new GameObject(end < 0 ? "Wall_BlueEnd" : "Wall_RedEnd").transform;
            cap.SetParent(transform, false);
            float z = end * (HalfLength + wallThickness * 0.5f);

            Block("Base", cap, new Vector3(0f, wallHeightRange.x * 0.5f, z),
                new Vector3(width, wallHeightRange.x, wallThickness), Quaternion.identity, wallMaterial);

            for (float x = -HalfWidth + rockSpacing * 0.5f; x < HalfWidth; x += rockSpacing)
                Rock(cap, new Vector3(x + R(-0.4f, 0.4f), 0f, z + end * R(0f, 0.6f)), R);
        }

        // Base pads at each end of the lane (blue = start, red = far end).
        Pad("BlueBase", new Vector3(0f, 0.03f, -HalfLength + 8f), blueBaseMaterial);
        Pad("RedBase", new Vector3(0f, 0.03f, HalfLength - 8f), redBaseMaterial);
    }

    void Rock(Transform parent, Vector3 pos, System.Func<float, float, float> R)
    {
        float h = R(wallHeightRange.x, wallHeightRange.y);
        var size = new Vector3(wallThickness * R(0.9f, 1.3f), h, rockSpacing * R(1.1f, 1.6f));
        pos.y = h * 0.5f;
        var rot = Quaternion.Euler(R(-6f, 6f), R(-25f, 25f), R(-6f, 6f));
        Block("Rock", parent, pos, size, rot, wallMaterial);
    }

    void Pad(string name, Vector3 pos, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = name;
        RemoveCollider(go);
        go.transform.SetParent(transform, false);
        go.transform.localPosition = pos;
        go.transform.localScale = new Vector3(12f, 0.02f, 12f);
        if (mat != null) go.GetComponent<Renderer>().sharedMaterial = mat;
    }

    static void Block(string name, Transform parent, Vector3 localPos, Vector3 size, Quaternion rot, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        RemoveCollider(go); // purely visual; movement is limited by Lane.Clamp
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localRotation = rot;
        go.transform.localScale = size;
        if (mat != null) go.GetComponent<Renderer>().sharedMaterial = mat;
    }

    static void RemoveCollider(GameObject go)
    {
        var col = go.GetComponent<Collider>();
        if (col == null) return;
        if (Application.isPlaying) Destroy(col); else DestroyImmediate(col);
    }

    void OnDrawGizmos()
    {
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.8f);
        Gizmos.DrawWireCube(Vector3.zero, new Vector3(width, 0.1f, length));
    }
}
