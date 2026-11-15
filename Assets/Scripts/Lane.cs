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

    [Header("Alcoves")]
    [Tooltip("Carve a small pocket into both side walls (ARAM-style hiding spots).")]
    public bool alcoves = true;
    [Tooltip("Position of the alcoves along the lane (0 = middle).")]
    public float alcoveCenterZ = 0f;
    [Tooltip("Opening size along the lane.")]
    public float alcoveLength = 14f;
    [Tooltip("How far each alcove goes back into the wall.")]
    public float alcoveDepth = 7.5f;
    [Tooltip("Length of the wall piece left in the middle of each alcove opening (in line with the main wall). 0 = none.")]
    public float alcoveMiddleWall = 5f;

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

        // Main lane rectangle.
        Vector3 inLane = local;
        inLane.x = Mathf.Clamp(local.x, -HalfWidth + pad, HalfWidth - pad);
        inLane.z = Mathf.Clamp(local.z, -HalfLength + pad, HalfLength - pad);
        local = inLane;

        // Alcoves widen the lane locally: walkable area is the union of the lane and the alcove strip.
        float alcHalf = alcoveLength * 0.5f - pad;
        if (alcoves && alcHalf > 0f)
        {
            Vector3 raw = transform.InverseTransformPoint(worldPos);
            Vector3 inAlcove = raw;
            float reach = HalfWidth + alcoveDepth - pad;
            inAlcove.x = Mathf.Clamp(raw.x, -reach, reach);
            inAlcove.z = Mathf.Clamp(raw.z, alcoveCenterZ - alcHalf, alcoveCenterZ + alcHalf);
            float dLane = (new Vector2(inLane.x - raw.x, inLane.z - raw.z)).sqrMagnitude;
            float dAlc = (new Vector2(inAlcove.x - raw.x, inAlcove.z - raw.z)).sqrMagnitude;
            if (dAlc < dLane) local = inAlcove;

            // Middle wall piece in each opening: push out along the shallowest axis.
            if (alcoveMiddleWall > 0f)
            {
                float zHalf = alcoveMiddleWall * 0.5f + pad;
                float dz = local.z - alcoveCenterZ;
                float ax = Mathf.Abs(local.x);
                float inner = HalfWidth - pad, outer = HalfWidth + wallThickness + pad;
                if (Mathf.Abs(dz) < zHalf && ax > inner && ax < outer)
                {
                    float sx = Mathf.Sign(local.x);
                    float toInner = ax - inner, toOuter = outer - ax, toEdge = zHalf - Mathf.Abs(dz);
                    if (toEdge <= toInner && toEdge <= toOuter)
                        local.z = alcoveCenterZ + (dz >= 0f ? zHalf : -zHalf);
                    else if (toInner <= toOuter) local.x = sx * inner;
                    else local.x = sx * outer;
                }
            }
        }

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
        // With alcoves on, each side wall is split around a pocket carved into it.
        float totalLength = length + wallThickness * 2f;
        float zMin = -totalLength * 0.5f, zMax = totalLength * 0.5f;
        float gapA = alcoveCenterZ - alcoveLength * 0.5f, gapB = alcoveCenterZ + alcoveLength * 0.5f;
        for (int side = -1; side <= 1; side += 2)
        {
            var wall = new GameObject(side < 0 ? "Wall_Left" : "Wall_Right").transform;
            wall.SetParent(transform, false);
            float x = side * (HalfWidth + wallThickness * 0.5f);

            if (alcoves)
            {
                WallBase(wall, x, zMin, gapA);
                WallBase(wall, x, gapB, zMax);
            }
            else WallBase(wall, x, zMin, zMax);

            for (float z = zMin + rockSpacing * 0.5f; z < zMax; z += rockSpacing)
            {
                if (alcoves && z > gapA - 0.6f && z < gapB + 0.6f) continue; // keep the opening clear
                Rock(wall, new Vector3(x + side * R(0f, 0.6f), 0f, z + R(-0.4f, 0.4f)), R);
            }

            if (alcoves) BuildAlcove(side, R);
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
        // Blue fountain heals the player back to full; red fountain hurts them.
        Pad("BlueBase", new Vector3(0f, 0.03f, -HalfLength + 8f), blueBaseMaterial).AddComponent<Fountain>().mode = Fountain.Mode.Heal;
        Pad("RedBase", new Vector3(0f, 0.03f, HalfLength - 8f), redBaseMaterial).AddComponent<Fountain>().mode = Fountain.Mode.Damage;
    }

    void WallBase(Transform parent, float x, float z0, float z1)
    {
        if (z1 - z0 <= 0.01f) return;
        Block("Base", parent, new Vector3(x, wallHeightRange.x * 0.5f, (z0 + z1) * 0.5f),
            new Vector3(wallThickness, wallHeightRange.x, z1 - z0), Quaternion.identity, wallMaterial);
    }

    // Small pocket behind the side wall: two short side walls plus a back wall.
    void BuildAlcove(int side, System.Func<float, float, float> R)
    {
        var root = new GameObject(side < 0 ? "Alcove_Left" : "Alcove_Right").transform;
        root.SetParent(transform, false);

        float h = wallHeightRange.x;
        float t = wallThickness;
        float half = alcoveLength * 0.5f;
        float backX = side * (HalfWidth + alcoveDepth + t * 0.5f);

        // Floor of the pocket (matches the lane floor).
        Block("Floor", root, new Vector3(side * (HalfWidth + alcoveDepth * 0.5f), 0.01f, alcoveCenterZ),
            new Vector3(alcoveDepth, 0.02f, alcoveLength), Quaternion.identity, floorMaterial);

        // Back wall.
        Block("Base", root, new Vector3(backX, h * 0.5f, alcoveCenterZ),
            new Vector3(t, h, alcoveLength + t * 2f), Quaternion.identity, wallMaterial);
        for (float z = -half + rockSpacing * 0.5f; z < half + t; z += rockSpacing)
            Rock(root, new Vector3(backX + side * R(0f, 0.5f), 0f, alcoveCenterZ + z + R(-0.3f, 0.3f)), R);

        // Middle wall piece, in line with the main wall, leaving an entrance on each side.
        if (alcoveMiddleWall > 0f)
        {
            float mx = side * (HalfWidth + t * 0.5f);
            float mh = alcoveMiddleWall * 0.5f;
            Block("Base", root, new Vector3(mx, h * 0.5f, alcoveCenterZ),
                new Vector3(t, h, alcoveMiddleWall), Quaternion.identity, wallMaterial);
            int n = Mathf.Max(1, Mathf.RoundToInt(alcoveMiddleWall / rockSpacing));
            float step = alcoveMiddleWall / n;
            for (int i = 0; i < n; i++)
            {
                float z = -mh + step * (i + 0.5f);
                Rock(root, new Vector3(mx + side * R(0f, 0.3f), 0f, alcoveCenterZ + z + R(-0.2f, 0.2f)), R, 0f, step / rockSpacing);
            }
        }

        // Side walls of the pocket (run across the lane direction).
        for (int end = -1; end <= 1; end += 2)
        {
            float z = alcoveCenterZ + end * (half + t * 0.5f);
            float xMid = side * (HalfWidth + (alcoveDepth + t) * 0.5f);
            Block("Base", root, new Vector3(xMid, h * 0.5f, z),
                new Vector3(alcoveDepth + t, h, t), Quaternion.identity, wallMaterial);
            for (float d = rockSpacing * 0.5f; d < alcoveDepth + t; d += rockSpacing)
                Rock(root, new Vector3(side * (HalfWidth + d + R(-0.3f, 0.3f)), 0f, z + end * R(0f, 0.5f)), R, 90f);
        }
    }

    void Rock(Transform parent, Vector3 pos, System.Func<float, float, float> R, float yaw = 0f, float lengthScale = 0f)
    {
        float h = R(wallHeightRange.x, wallHeightRange.y);
        // lengthScale > 0: tighter rocks that stay within their footprint (used for the alcove middle wall).
        bool tight = lengthScale > 0f;
        var size = tight
            ? new Vector3(wallThickness * R(0.9f, 1.1f), h, rockSpacing * lengthScale * R(0.95f, 1.1f))
            : new Vector3(wallThickness * R(0.9f, 1.3f), h, rockSpacing * R(1.1f, 1.6f));
        pos.y = h * 0.5f;
        float yawJitter = tight ? 8f : 25f;
        var rot = Quaternion.Euler(R(-6f, 6f), yaw + R(-yawJitter, yawJitter), R(-6f, 6f));
        Block("Rock", parent, pos, size, rot, wallMaterial);
    }

    GameObject Pad(string name, Vector3 pos, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = name;
        RemoveCollider(go);
        go.transform.SetParent(transform, false);
        go.transform.localPosition = pos;
        go.transform.localScale = new Vector3(12f, 0.02f, 12f);
        if (mat != null) go.GetComponent<Renderer>().sharedMaterial = mat;
        return go;
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
        if (alcoves)
            Gizmos.DrawWireCube(new Vector3(0f, 0f, alcoveCenterZ), new Vector3(width + alcoveDepth * 2f, 0.1f, alcoveLength));
    }
}
