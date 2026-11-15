using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Procedurally generated, anti-aliased UI sprites (rounded rects, circles, soft shadows, line icons).
/// Textures are white so they can be tinted with GUI.color. Cached per shape/size.
/// </summary>
public static class PiUISprites
{
    static readonly Dictionary<string, Texture2D> cache = new Dictionary<string, Texture2D>();
    static readonly Dictionary<string, GUIStyle> nineSlices = new Dictionary<string, GUIStyle>();

    // ---- Resolution-independent scaling ----------------------------------------------
    // All OnGUI UI is authored in "reference pixels" for a GuiReferenceHeight-tall screen and scaled uniformly
    // with screen height, so elements stay the same percentage of the screen at any resolution
    // (like a CanvasScaler with Match = Height).

    /// <summary>Screen height the UI pixel sizes were designed for.</summary>
    public static float GuiReferenceHeight = 1440f;

    /// <summary>Real pixels per reference pixel.</summary>
    public static float GuiScale => Mathf.Max(0.01f, Screen.height / GuiReferenceHeight);

    static int lastTypingFrame = -10;

    /// <summary>
    /// Called from OnGUI by text boxes while they have keyboard focus. GUIUtility.keyboardControl
    /// isn't reliable outside OnGUI (in the editor it can report another GUI view's state), so
    /// focus is recorded here and read by game code in Update.
    /// </summary>
    public static void MarkTyping() => lastTypingFrame = Time.frameCount;

    /// <summary>
    /// True while an IMGUI text box (prompt box, pi follow-up box) has keyboard focus.
    /// Game hotkeys (abilities, pause, ability-bar highlights) should ignore key presses while this is true.
    /// Update runs before OnGUI, so last frame's mark counts.
    /// </summary>
    public static bool IsTyping => Time.frameCount - lastTypingFrame <= 1;

    /// <summary>Screen size in reference pixels (the GUI coordinate space while <see cref="GuiMatrix"/> is applied).</summary>
    public static float GuiWidth => Screen.width / GuiScale;
    public static float GuiHeight => Screen.height / GuiScale;

    /// <summary>Assign to GUI.matrix at the start of OnGUI.</summary>
    public static Matrix4x4 GuiMatrix => Matrix4x4.Scale(new Vector3(GuiScale, GuiScale, 1f));

    /// <summary>Converts an Input System screen position (bottom-left origin, real pixels) to scaled GUI space.</summary>
    public static Vector2 ScreenToGUI(Vector2 screenPos) =>
        new Vector2(screenPos.x, Screen.height - screenPos.y) / GuiScale;

    // ---- Shapes --------------------------------------------------------------------

    /// <summary>White rounded rectangle meant for 9-slicing with a border of <paramref name="radius"/> + 1.</summary>
    public static Texture2D RoundedRect(int radius)
    {
        int size = radius * 2 + 4;
        return Get($"rr{radius}", size, size, (x, y) => RoundedBoxSdf(x, y, size, size, radius));
    }

    /// <summary>White filled circle.</summary>
    public static Texture2D Circle(int diameter)
    {
        float r = diameter * 0.5f;
        return Get($"circle{diameter}", diameter, diameter, (x, y) => Mathf.Sqrt((x - r) * (x - r) + (y - r) * (y - r)) - (r - 0.5f));
    }

    /// <summary>Soft, blurred rounded rect used for drop shadows. 9-slice border = radius + blur + 1.</summary>
    public static Texture2D SoftShadow(int radius, int blur)
    {
        int size = (radius + blur) * 2 + 4;
        string key = $"shadow{radius}_{blur}";
        if (cache.TryGetValue(key, out var tex) && tex != null) return tex;

        tex = NewTexture(size, size);
        var px = new Color32[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            // Distance outside a box inset by the blur radius.
            float d = RoundedBoxSdf(x + 0.5f, y + 0.5f, size, size, radius, blur + 2f);
            float a = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-blur * 0.35f, blur, d));
            px[y * size + x] = new Color32(255, 255, 255, (byte)(a * a * 255f));
        }
        tex.SetPixels32(px);
        tex.Apply(false, false);
        cache[key] = tex;
        return tex;
    }

    // ---- Icons (line art, drawn in GUI space: y down) ------------------------------

    public static Texture2D IconClose(int size) =>
        Icon($"close{size}", size, 0.085f, new[] { V(0.30f, 0.30f), V(0.70f, 0.70f) }, new[] { V(0.70f, 0.30f), V(0.30f, 0.70f) });

    public static Texture2D IconChevron(int size) =>
        Icon($"chev{size}", size, 0.09f, new[] { V(0.38f, 0.24f), V(0.64f, 0.50f), V(0.38f, 0.76f) });

    public static Texture2D IconCheck(int size) =>
        Icon($"check{size}", size, 0.09f, new[] { V(0.24f, 0.52f), V(0.43f, 0.70f), V(0.77f, 0.31f) });

    public static Texture2D IconCross(int size) =>
        Icon($"cross{size}", size, 0.09f, new[] { V(0.32f, 0.32f), V(0.68f, 0.68f) }, new[] { V(0.68f, 0.32f), V(0.32f, 0.68f) });

    // ---- Drawing helpers -------------------------------------------------------------

    /// <summary>Draws a tinted, 9-sliced rounded rect (repaint only).</summary>
    public static void DrawRounded(Rect r, int radius, Color tint)
    {
        if (Event.current.type != EventType.Repaint) return;
        Draw9(r, $"rr{radius}", RoundedRect(radius), radius + 1, tint);
    }

    /// <summary>Draws a soft shadow around <paramref name="r"/> (repaint only).</summary>
    public static void DrawShadow(Rect r, int radius, int blur, Color tint, float offsetY = 4f)
    {
        if (Event.current.type != EventType.Repaint) return;
        float grow = blur + 2f;
        var outer = new Rect(r.x - grow, r.y - grow + offsetY, r.width + grow * 2f, r.height + grow * 2f);
        Draw9(outer, $"shadow{radius}_{blur}", SoftShadow(radius, blur), radius + blur + 2, tint);
    }

    /// <summary>Draws a tinted texture (any event).</summary>
    public static void DrawTinted(Rect r, Texture2D tex, Color tint)
    {
        Color prev = GUI.color;
        GUI.color = prev * tint;
        GUI.DrawTexture(r, tex, ScaleMode.StretchToFill, true);
        GUI.color = prev;
    }

    static void Draw9(Rect r, string key, Texture2D tex, int border, Color tint)
    {
        if (!nineSlices.TryGetValue(key, out var style) || style.normal.background == null)
        {
            style = new GUIStyle { border = new RectOffset(border, border, border, border) };
            style.normal.background = tex;
            nineSlices[key] = style;
        }
        Color prev = GUI.color;
        GUI.color = prev * tint;
        style.Draw(r, GUIContent.none, false, false, false, false);
        GUI.color = prev;
    }

    // ---- Internals -------------------------------------------------------------------

    static Vector2 V(float x, float y) => new Vector2(x, y);

    /// <summary>Signed distance to a rounded box filling a w*h canvas (negative inside), optionally inset.</summary>
    static float RoundedBoxSdf(float x, float y, float w, float h, float radius, float inset = 1f)
    {
        float hx = w * 0.5f - inset, hy = h * 0.5f - inset;
        float qx = Mathf.Abs(x - w * 0.5f) - (hx - radius);
        float qy = Mathf.Abs(y - h * 0.5f) - (hy - radius);
        float outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude;
        float inside = Mathf.Min(Mathf.Max(qx, qy), 0f);
        return outside + inside - radius;
    }

    static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-6f, ab.sqrMagnitude));
        return (p - (a + ab * t)).magnitude;
    }

    /// <summary>Icon made of one or more polylines in normalized GUI coords (y down), with round caps.</summary>
    static Texture2D Icon(string key, int size, float thickness, params Vector2[][] polylines)
    {
        float halfT = thickness * size * 0.5f;
        return Get(key, size, size, (x, yUp) =>
        {
            var p = new Vector2(x, size - yUp); // texture rows are bottom-up
            float d = float.MaxValue;
            foreach (var line in polylines)
                for (int i = 0; i < line.Length - 1; i++)
                    d = Mathf.Min(d, SegmentDistance(p, line[i] * size, line[i + 1] * size));
            return d - halfT;
        });
    }

    /// <summary>Builds (or fetches) a white texture whose alpha comes from a signed distance function.</summary>
    static Texture2D Get(string key, int w, int h, Func<float, float, float> sdf)
    {
        if (cache.TryGetValue(key, out var tex) && tex != null) return tex;

        tex = NewTexture(w, h);
        var px = new Color32[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float d = sdf(x + 0.5f, y + 0.5f);
            float a = Mathf.Clamp01(0.5f - d); // 1px anti-aliased edge
            px[y * w + x] = new Color32(255, 255, 255, (byte)(a * 255f));
        }
        tex.SetPixels32(px);
        tex.Apply(false, false);
        cache[key] = tex;
        return tex;
    }

    static Texture2D NewTexture(int w, int h) => new Texture2D(w, h, TextureFormat.RGBA32, false)
    {
        wrapMode = TextureWrapMode.Clamp,
        filterMode = FilterMode.Bilinear,
        hideFlags = HideFlags.HideAndDontSave,
    };
}
