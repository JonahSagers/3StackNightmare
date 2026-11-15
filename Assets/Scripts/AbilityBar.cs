using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// Screen-space ability bar showing the Q / W / E / R keys (plus H, full heal) at the bottom of the screen.
// - Slot presses in while its key is held.
// - Flashes gold when the ability actually activates, red when pressed on cooldown.
// - Shows a radial cooldown sweep with the seconds remaining.
// Spawns itself automatically after the scene loads; no setup needed.
public class AbilityBar : MonoBehaviour
{
    public float slotSize = 72f;
    public float spacing = 12f;
    public float bottomMargin = 28f;

    public Color frameColor = new Color(0.08f, 0.09f, 0.12f, 0.92f);
    public Color borderColor = new Color(0.55f, 0.6f, 0.7f, 1f);
    public Color pressedBorderColor = new Color(1f, 0.85f, 0.35f, 1f);
    public Color activateFlashColor = new Color(1f, 0.85f, 0.35f, 0.75f);
    public Color deniedFlashColor = new Color(1f, 0.2f, 0.2f, 0.55f);
    public Color cooldownColor = new Color(0f, 0f, 0f, 0.7f);

    class Slot
    {
        public Key key;
        public Func<float> nextReady;
        public Func<float> cooldown;
        public RectTransform root;
        public Image border, cooldownFill, flash;
        public Text cooldownText;
        public float lastNextReady;
        public float flashTime = -10f;
        public Color flashColor;
    }

    readonly List<Slot> slots = new List<Slot>();
    static readonly Key[] order = { Key.Q, Key.W, Key.E, Key.R, Key.H };

    [Tooltip("Extra gap before the H (utility) slot, to set it apart from the abilities.")]
    public float utilityGap = 24f;
    const float FlashDuration = 0.35f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoSpawn()
    {
        if (FindAnyObjectByType<AbilityBar>() == null)
            new GameObject("AbilityBar").AddComponent<AbilityBar>();
    }

    void Start()
    {
        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 50;
        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;

        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var sprite = MakeRoundedSprite(10);

        foreach (var key in order)
        {
            var bound = BindAbility(key);
            if (bound == null && key == Key.H) continue; // no full-heal on the player: skip that slot
            slots.Add(bound ?? new Slot { key = key });
        }

        bool hasUtility = slots.Exists(sl => sl.key == Key.H);
        float total = slots.Count * slotSize + (slots.Count - 1) * spacing + (hasUtility ? utilityGap : 0f);
        for (int i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            string label = Label(s.key);
            var root = new GameObject($"Slot_{s.key}", typeof(RectTransform)).GetComponent<RectTransform>();
            root.SetParent(transform, false);
            root.anchorMin = root.anchorMax = new Vector2(0.5f, 0f);
            root.pivot = new Vector2(0.5f, 0f);
            root.sizeDelta = new Vector2(slotSize, slotSize);
            float extra = s.key == Key.H ? utilityGap : 0f;
            root.anchoredPosition = new Vector2(-total / 2f + slotSize / 2f + i * (slotSize + spacing) + extra, bottomMargin);
            s.root = root;

            s.border = MakeImage("Border", root, sprite, borderColor, 0f);
            MakeImage("Frame", root, sprite, frameColor, 3f);

            var keyText = MakeText("Key", root, font, s.key.ToString(), 34, Color.white, FontStyle.Bold);
            keyText.rectTransform.offsetMin = new Vector2(0, 14);

            var nameText = MakeText("Name", root, font, label, 13, new Color(0.8f, 0.85f, 0.95f), FontStyle.Normal);
            nameText.alignment = TextAnchor.LowerCenter;
            nameText.rectTransform.offsetMin = new Vector2(0, 5);

            s.cooldownFill = MakeImage("Cooldown", root, sprite, cooldownColor, 3f);
            s.cooldownFill.type = Image.Type.Filled;
            s.cooldownFill.fillMethod = Image.FillMethod.Radial360;
            s.cooldownFill.fillOrigin = (int)Image.Origin360.Top;
            s.cooldownFill.fillClockwise = false;
            s.cooldownFill.fillAmount = 0f;

            s.cooldownText = MakeText("CooldownText", root, font, "", 26, Color.white, FontStyle.Bold);
            s.cooldownText.gameObject.AddComponent<UnityEngine.UI.Outline>(); // not the project's 3D Outline

            s.flash = MakeImage("Flash", root, sprite, Color.clear, 0f);

            if (s.nextReady != null) s.lastNextReady = s.nextReady();
        }
    }

    Slot BindAbility(Key key)
    {
        foreach (var a in FindObjectsByType<Skillshot>())
            if (a.key == key) { var c = a; return new Slot { key = key, nextReady = () => c ? c.NextReadyTime : 0f, cooldown = () => c ? c.cooldown : 1f }; }
        foreach (var a in FindObjectsByType<LeapSlam>())
            if (a.key == key) { var c = a; return new Slot { key = key, nextReady = () => c ? c.NextReadyTime : 0f, cooldown = () => c ? c.cooldown : 1f }; }
        foreach (var a in FindObjectsByType<ShieldAbility>())
            if (a.key == key) { var c = a; return new Slot { key = key, nextReady = () => c ? c.NextReadyTime : 0f, cooldown = () => c ? c.cooldown : 1f }; }
        foreach (var a in FindObjectsByType<FullHealKey>())
            if (a.key == key) { var c = a; return new Slot { key = key, nextReady = () => c ? c.NextReadyTime : 0f, cooldown = () => c ? c.cooldown : 1f }; }
        return null;
    }

    static string Label(Key key)
    {
        foreach (var a in FindObjectsByType<Skillshot>())
            if (a.key == key) return a.effect == SkillEffect.Heal ? "Heal" : "Shot";
        foreach (var a in FindObjectsByType<LeapSlam>())
            if (a.key == key) return "Leap";
        foreach (var a in FindObjectsByType<ShieldAbility>())
            if (a.key == key) return "Shield";
        foreach (var a in FindObjectsByType<FullHealKey>())
            if (a.key == key) return "Full Heal";
        return "";
    }

    void Update()
    {
        var keyboard = Keyboard.current;
        float now = Time.time;

        foreach (var s in slots)
        {
            if (s.root == null) continue;
            // Keys typed into a text box don't count as ability presses (no press/denied feedback).
            bool keysLive = keyboard != null && !PiUISprites.IsTyping;
            bool held = keysLive && keyboard[s.key].isPressed;
            bool pressed = keysLive && keyboard[s.key].wasPressedThisFrame;

            float remaining = 0f, cd = 1f;
            if (s.nextReady != null)
            {
                float next = s.nextReady();
                cd = Mathf.Max(0.01f, s.cooldown());
                remaining = Mathf.Max(0f, next - now);

                // Ability fired: its ready time moved forward.
                if (next > s.lastNextReady + 0.001f) { s.flashTime = now; s.flashColor = activateFlashColor; }
                else if (pressed && remaining > 0f) { s.flashTime = now; s.flashColor = deniedFlashColor; }
                s.lastNextReady = next;
            }

            // Held key: slot pushes in and border lights up.
            float targetScale = held ? 0.9f : 1f;
            float scale = Mathf.MoveTowards(s.root.localScale.x, targetScale, Time.unscaledDeltaTime * 4f);
            s.root.localScale = new Vector3(scale, scale, 1f);
            s.border.color = held ? pressedBorderColor : borderColor;

            // Cooldown sweep + seconds left.
            s.cooldownFill.fillAmount = remaining > 0f ? Mathf.Clamp01(remaining / cd) : 0f;
            s.cooldownText.text = remaining <= 0f ? "" : remaining >= 1f ? Mathf.CeilToInt(remaining).ToString() : remaining.ToString("0.0");

            // Activation / denied flash fades out.
            float t = (now - s.flashTime) / FlashDuration;
            var fc = s.flashColor;
            fc.a *= Mathf.Clamp01(1f - t);
            s.flash.color = fc;
        }
    }

    // ---- UI helpers ------------------------------------------------------------------

    static Image MakeImage(string name, Transform parent, Sprite sprite, Color color, float inset)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(inset, inset); rt.offsetMax = new Vector2(-inset, -inset);
        var img = go.GetComponent<Image>();
        img.sprite = sprite;
        img.type = Image.Type.Sliced;
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    static Text MakeText(string name, Transform parent, Font font, string text, int size, Color color, FontStyle style)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Text));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        var t = go.GetComponent<Text>();
        t.font = font;
        t.text = text;
        t.fontSize = size;
        t.fontStyle = style;
        t.color = color;
        t.alignment = TextAnchor.MiddleCenter;
        t.raycastTarget = false;
        return t;
    }

    static Sprite MakeRoundedSprite(int radius)
    {
        var tex = PiUISprites.RoundedRect(radius);
        float b = radius + 1;
        return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f, 0,
            SpriteMeshType.FullRect, new Vector4(b, b, b, b));
    }
}
