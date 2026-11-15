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
    public float alcoveLength = 19f;
    [Tooltip("How far each alcove goes back into the wall.")]
    public float alcoveDepth = 8.5f;
    [Tooltip("Length of the wall piece left in the middle of each alcove opening (in line with the main wall). 0 = none.")]
    public float alcoveMiddleWall = 9f;
    [Tooltip("Rounding radius of the alcove's back corners. 0 = square.")]
    public float alcoveCornerRadius = 5f;
    [Tooltip("Put a health pack in each alcove, just behind the middle wall.")]
    public bool alcoveHealthPacks = true;

    [Header("Materials")]
    public Material floorMaterial;
    public Material wallMaterial;
    public Material blueBaseMaterial;
    public Material redBaseMaterial;
    public Material healthPackMaterial;

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

            // Rounded back corners of the alcove.
            float r = AlcoveCornerRadius;
            if (r > pad)
            {
                float u = Mathf.Abs(local.x) - HalfWidth;          // depth into the alcove
                float v = local.z - alcoveCenterZ;                 // along the lane
                float cu = alcoveDepth - r, cv = alcoveLength * 0.5f - r;
                if (u > cu && Mathf.Abs(v) > cv)
                {
                    var off = new Vector2(u - cu, Mathf.Abs(v) - cv);
                    float rr = r - pad;
                    if (off.magnitude > rr)
                    {
                        off = off.normalized * rr;
                        local.x = Mathf.Sign(local.x) * (HalfWidth + cu + off.x);
                        local.z = alcoveCenterZ + Mathf.Sign(v) * (cv + off.y);
                    }
                }
            }

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

        // Floor of the pocket (matches the lane floor).
        Block("Floor", root, new Vector3(side * (HalfWidth + alcoveDepth * 0.5f), 0.01f, alcoveCenterZ),
            new Vector3(alcoveDepth, 0.02f, alcoveLength), Quaternion.identity, floorMaterial);

        // Back + side walls follow one path around the pocket, with rounded back corners.
        BuildAlcoveShell(root, side, R);

        // Health pack tucked behind the middle wall (halfway between it and the back wall).
        if (alcoveHealthPacks)
        {
            float behind = alcoveMiddleWall > 0f ? t : 0f;
            float u = behind + (alcoveDepth - behind) * 0.5f;
            BuildHealthPack(root, new Vector3(side * (HalfWidth + u), 0f, alcoveCenterZ));
        }

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

    }

    void BuildHealthPack(Transform parent, Vector3 localPos)
    {
        var pack = new GameObject("HealthPack").transform;
        pack.SetParent(parent, false);
        pack.localPosition = localPos;
        pack.gameObject.AddComponent<HealthPack>();

        // Visual: a floating green plus sign (child 0 is spun/bobbed and hidden while respawning).
        var visual = new GameObject("Visual").transform;
        visual.SetParent(pack, false);
        visual.localPosition = new Vector3(0f, 0.9f, 0f);
        visual.localRotation = Quaternion.Euler(0f, 45f, 0f);
        Block("Bar", visual, Vector3.zero, new Vector3(0.9f, 0.3f, 0.3f), Quaternion.identity, healthPackMaterial);
        Block("Bar", visual, Vector3.zero, new Vector3(0.3f, 0.9f, 0.3f), Quaternion.identity, healthPackMaterial);
    }

    float AlcoveCornerRadius => Mathf.Clamp(alcoveCornerRadius, 0f, Mathf.Min(alcoveDepth, alcoveLength * 0.5f));

    void BuildAlcoveShell(Transform root, int side, System.Func<float, float, float> R)
    {
        float h = wallHeightRange.x;
        float t = wallThickness;
        float o = t * 0.5f;                       // wall centerline sits half a thickness outside the walkable edge
        float half = alcoveLength * 0.5f;
        float r = AlcoveCornerRadius;
        float cu = alcoveDepth - r, cv = half - r;

        // Path in alcove space: u = depth into the wall, v = along the lane.
        var path = new System.Collections.Generic.List<Vector2>();
        path.Add(new Vector2(0.5f, half + o)); // start inside the main wall
        int arcSteps = r > 0.01f ? 8 : 0;
        for (int i = 0; i <= arcSteps; i++)
        {
            float a = Mathf.Lerp(90f, 0f, i / (float)Mathf.Max(1, arcSteps)) * Mathf.Deg2Rad;
            path.Add(new Vector2(cu + (r + o) * Mathf.Cos(a), cv + (r + o) * Mathf.Sin(a)));
        }
        for (int i = 0; i <= arcSteps; i++)
        {
            float a = Mathf.Lerp(0f, -90f, i / (float)Mathf.Max(1, arcSteps)) * Mathf.Deg2Rad;
            path.Add(new Vector2(cu + (r + o) * Mathf.Cos(a), -cv + (r + o) * Mathf.Sin(a)));
        }
        path.Add(new Vector2(0.5f, -half - o));

        Vector3 ToLocal(Vector2 p) => new Vector3(side * (HalfWidth + p.x), 0f, alcoveCenterZ + p.y);
        var interior = new Vector2(alcoveDepth * 0.5f, 0f);

        // Solid base along each segment (slightly overlapped so there are no gaps on the curve).
        for (int i = 0; i < path.Count - 1; i++)
        {
            Vector3 a = ToLocal(path[i]), b = ToLocal(path[i + 1]);
            Vector3 d = b - a;
            float len = d.magnitude;
            if (len < 0.01f) continue;
            Block("Base", root, (a + b) * 0.5f + Vector3.up * h * 0.5f, new Vector3(t, h, len + t * 0.5f),
                Quaternion.LookRotation(d / len, Vector3.up), wallMaterial);
        }

        // Rocks spaced evenly along the path.
        float total = 0f;
        for (int i = 0; i < path.Count - 1; i++) total += Vector2.Distance(path[i], path[i + 1]);
        for (float dist = rockSpacing * 0.5f; dist < total; dist += rockSpacing)
        {
            float acc = dist;
            int seg = 0;
            while (seg < path.Count - 2 && acc > Vector2.Distance(path[seg], path[seg + 1]))
            {
                acc -= Vector2.Distance(path[seg], path[seg + 1]);
                seg++;
            }
            Vector2 p0 = path[seg], p1 = path[seg + 1];
            Vector2 dir = (p1 - p0).normalized;
            Vector2 p = p0 + dir * acc;
            Vector2 n = new Vector2(-dir.y, dir.x);
            if (Vector2.Dot(n, p - interior) < 0f) n = -n;
            p += n * R(0f, 0.4f);

            Vector3 dLocal = new Vector3(side * dir.x, 0f, dir.y);
            float yaw = Mathf.Atan2(dLocal.x, dLocal.z) * Mathf.Rad2Deg;
            Rock(root, ToLocal(p), R, yaw);
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
