using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Space toggles Time.timeScale between 0 and 1. While paused, a thin white
/// border creeps in from the screen edges and objects tagged "Background" are
/// slightly desaturated and darkened so foreground objects stand out. Spawns itself automatically on scene load.
/// </summary>
public class PauseToggle : MonoBehaviour
{
    [Tooltip("Border thickness in pixels.")]
    public float borderThickness = 12f;
    public Color borderColor = Color.white;
    [Tooltip("Creep-in duration in seconds (unscaled time).")]
    public float fadeDuration = 0.25f;

    [Header("Background Desaturation")]
    public string backgroundTag = "Background";
    [Tooltip("How much saturation to remove from Background objects when paused (0 = none, 1 = grayscale).")]
    [Range(0f, 1f)] public float backgroundDesaturation = 0.15f;
    [Tooltip("How much brightness to remove from Background objects when paused (0 = none, 1 = black).")]
    [Range(0f, 1f)] public float backgroundDarkening = 0.35f;

    public bool IsPaused { get; private set; }

    float alpha;

    class DesatTexture { public RenderTexture rt; public float amount = -1f; public float brightness = -1f; }
    static readonly string[] TexProps = { "_BaseMap", "_MainTex" };
    static readonly string[] ColorProps = { "_BaseColor", "_Color" };
    readonly Dictionary<Texture, DesatTexture> desatTextures = new Dictionary<Texture, DesatTexture>();
    readonly List<Renderer> bgRenderers = new List<Renderer>();
    MaterialPropertyBlock mpb;
    Material desatMat;
    float appliedDesat;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindAnyObjectByType<PauseToggle>() != null) return;
        new GameObject("Pause Toggle").AddComponent<PauseToggle>();
    }

    void Update()
    {
        var kb = Keyboard.current;
        // Ignore space while typing into an IMGUI text field (e.g. the prompt box).
        if (kb != null && kb.spaceKey.wasPressedThisFrame && !PiUISprites.IsTyping)
        {
            IsPaused = !IsPaused;
            Time.timeScale = IsPaused ? 0f : 1f;
            if (IsPaused) RefreshBackgroundRenderers();
        }

        float target = IsPaused ? 1f : 0f;
        float step = fadeDuration > 0f ? Time.unscaledDeltaTime / fadeDuration : 1f;
        alpha = Mathf.MoveTowards(alpha, target, step);

        ApplyBackgroundDesaturation(Ease(alpha));
    }

    static float Ease(float x) => 1f - (1f - x) * (1f - x);

    void RefreshBackgroundRenderers()
    {
        ClearBackgroundOverrides();
        bgRenderers.Clear();
        GameObject[] tagged;
        try { tagged = GameObject.FindGameObjectsWithTag(backgroundTag); }
        catch (UnityException) { return; } // tag not defined
        foreach (var go in tagged)
            bgRenderers.AddRange(go.GetComponentsInChildren<Renderer>(true));
    }

    /// <param name="t">Eased pause progress, 0..1.</param>
    void ApplyBackgroundDesaturation(float t)
    {
        if (Mathf.Approximately(t, appliedDesat)) return;
        appliedDesat = t;

        float amount = t * backgroundDesaturation;
        float brightness = 1f - t * backgroundDarkening;
        if (t <= 0f || (amount <= 0f && brightness >= 1f)) { ClearBackgroundOverrides(); return; }

        if (desatMat == null)
        {
            var shader = Shader.Find("Hidden/PauseDesaturate");
            if (shader == null) return;
            desatMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }
        if (mpb == null) mpb = new MaterialPropertyBlock();

        foreach (var r in bgRenderers)
        {
            if (r == null) continue;
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null) continue;
                mpb.Clear();

                // Darken via the tint color when the material has one (cheap, no re-blit);
                // otherwise bake the darkening into the desaturated texture.
                bool hasColor = false;
                foreach (var p in ColorProps) if (m.HasProperty(p)) { hasColor = true; break; }
                float texBrightness = hasColor ? 1f : brightness;

                foreach (var p in TexProps)
                {
                    if (!m.HasProperty(p)) continue;
                    var tex = m.GetTexture(p) as Texture2D;
                    if (tex != null) mpb.SetTexture(p, GetDesaturated(tex, amount, texBrightness));
                }
                foreach (var p in ColorProps)
                {
                    if (!m.HasProperty(p)) continue;
                    Color c = m.GetColor(p);
                    float lum = c.r * 0.2126f + c.g * 0.7152f + c.b * 0.0722f;
                    Color d = Color.Lerp(c, new Color(lum, lum, lum, c.a), amount);
                    mpb.SetColor(p, new Color(d.r * brightness, d.g * brightness, d.b * brightness, c.a));
                }
                r.SetPropertyBlock(mpb, i);
            }
        }
    }

    RenderTexture GetDesaturated(Texture2D src, float amount, float brightness)
    {
        if (!desatTextures.TryGetValue(src, out var d))
        {
            var rt = new RenderTexture(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                useMipMap = true,
                autoGenerateMips = true,
                wrapMode = src.wrapMode,
                filterMode = src.filterMode,
                anisoLevel = src.anisoLevel,
                hideFlags = HideFlags.HideAndDontSave,
            };
            d = new DesatTexture { rt = rt };
            desatTextures[src] = d;
        }
        if (!Mathf.Approximately(d.amount, amount) || !Mathf.Approximately(d.brightness, brightness))
        {
            desatMat.SetFloat("_Amount", amount);
            desatMat.SetFloat("_Brightness", brightness);
            Graphics.Blit(src, d.rt, desatMat);
            d.amount = amount;
            d.brightness = brightness;
        }
        return d.rt;
    }

    void ClearBackgroundOverrides()
    {
        foreach (var r in bgRenderers)
        {
            if (r == null) continue;
            int n = r.sharedMaterials.Length;
            for (int i = 0; i < n; i++) r.SetPropertyBlock(null, i);
        }
    }

    void OnGUI()
    {
        if (alpha <= 0f || Event.current.type != EventType.Repaint) return;

        GUI.depth = -1000; // draw on top of other IMGUI
        Color prev = GUI.color;
        GUI.color = borderColor;

        // Ease-out so the border slides in quickly and settles.
        float eased = 1f - (1f - alpha) * (1f - alpha);
        float t = borderThickness * PiUISprites.GuiScale * eased, w = Screen.width, h = Screen.height;
        Texture tex = Texture2D.whiteTexture;
        GUI.DrawTexture(new Rect(0, 0, w, t), tex);         // top
        GUI.DrawTexture(new Rect(0, h - t, w, t), tex);     // bottom
        GUI.DrawTexture(new Rect(0, t, t, h - 2 * t), tex); // left
        GUI.DrawTexture(new Rect(w - t, t, t, h - 2 * t), tex); // right

        GUI.color = prev;
    }

    void OnDisable()
    {
        if (IsPaused) Time.timeScale = 1f;
        ClearBackgroundOverrides();
        appliedDesat = 0f;
    }

    void OnDestroy()
    {
        foreach (var d in desatTextures.Values) if (d.rt != null) Destroy(d.rt);
        desatTextures.Clear();
        if (desatMat != null) Destroy(desatMat);
    }
}
