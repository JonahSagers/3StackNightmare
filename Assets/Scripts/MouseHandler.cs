using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.Networking;

/// <summary>
/// Raycasts from the mouse position every frame and stores the GameObject under the cursor.
/// Left-clicking a hovered object opens a squircle menu with one segment per component on it.
/// </summary>
public class MouseHandler : MonoBehaviour
{
    [Tooltip("The GameObject currently under the mouse cursor (null if none).")]
    public GameObject hovered;

    [Tooltip("Camera used for raycasting. Defaults to Camera.main.")]
    public Camera cam;

    [Tooltip("Layers that can be hovered.")]
    public LayerMask hoverMask = ~0;

    public float maxDistance = 1000f;

    [Tooltip("Outline color shown on the hovered object (objects need an Outline component).")]
    public Color hoverOutlineColor = new Color(0.6f, 0f, 1f, 1f);

    Outline hoveredOutline;
    PauseToggle pauseToggle;

    [Header("Component Menu")]
    [Tooltip("Components currently selected via the menu. Click a segment to toggle it.")]
    public List<Component> selectedComponents = new List<Component>();
    [Tooltip("Invoked when a component is added to the selection.")]
    public UnityEvent<Component> onComponentSelected = new UnityEvent<Component>();
    [Tooltip("Invoked when a component is removed from the selection.")]
    public UnityEvent<Component> onComponentDeselected = new UnityEvent<Component>();
    [Tooltip("Log selection changes to the Console.")]
    public bool logSelection = true;

    public enum MenuLayout { Stacked, SideBySide }

    [Header("Menu Style")]
    [Tooltip("Stacked = segments top-to-bottom, SideBySide = segments left-to-right.")]
    public MenuLayout layout = MenuLayout.Stacked;
    public float segmentWidth = 170f;
    public float segmentHeight = 40f;
    [Tooltip("Corner radius in pixels.")]
    public float cornerRadius = 18f;
    [Tooltip("Superellipse exponent. 2 = circle corners, 4-5 = squircle.")]
    [Range(2f, 10f)] public float squircleExponent = 4.5f;
    [Tooltip("Offset from the cursor to the menu's top-left corner.")]
    public Vector2 cursorOffset = new Vector2(12f, 12f);
    public int fontSize = 15;

    public Color backgroundColor = new Color(0.11f, 0.11f, 0.13f, 0.96f);
    public Color hoverColor = new Color(0.22f, 0.47f, 0.96f, 1f);
    public Color pressedColor = new Color(0.16f, 0.36f, 0.80f, 1f);
    public Color selectedColor = new Color(0.20f, 0.62f, 0.42f, 1f);
    public Color selectedHoverColor = new Color(0.26f, 0.72f, 0.50f, 1f);
    public Color dividerColor = new Color(1f, 1f, 1f, 0.08f);
    public Color textColor = new Color(0.93f, 0.93f, 0.95f, 1f);
    public Color shadowColor = new Color(0f, 0f, 0f, 0.35f);

    [Header("Prompt Box")]
    [Tooltip("Current contents of the prompt box.")]
    public string prompt = "";
    public string promptPlaceholder = "Type a prompt...";
    public float promptHeight = 40f;
    [Tooltip("Vertical gap between the menu and the prompt box.")]
    public float promptGap = 8f;
    public Color promptBackgroundColor = new Color(0.15f, 0.15f, 0.18f, 0.96f);
    public Color promptPlaceholderColor = new Color(1f, 1f, 1f, 0.35f);

    // Menu state
    bool menuOpen;
    GameObject menuTarget;
    Vector2 menuPos; // GUI space (top-left origin)
    int pressedIndex = -1;

    const string PromptControl = "MouseHandler_Prompt";
    bool focusPromptNext;

    // Rendering resources
    readonly SquircleMask menuMask = new SquircleMask();
    readonly SquircleMask promptMask = new SquircleMask();
    GUIStyle labelStyle, promptStyle, placeholderStyle;

    struct MenuOption
    {
        public string label;
        public Component component; // null for missing scripts
        public MenuOption(string label, Component component) { this.label = label; this.component = component; }
    }

    readonly List<MenuOption> options = new List<MenuOption>();

    void Awake()
    {
        if (cam == null) cam = Camera.main;
    }

    void BuildOptions(GameObject target)
    {
        options.Clear();
        foreach (Component c in target.GetComponents<Component>())
            options.Add(new MenuOption(c != null ? c.GetType().Name : "Missing Script", c));
    }

    public bool IsSelected(Component c) => c != null && selectedComponents.Contains(c);

    /// <summary>Adds the component to the selection, or removes it if already selected.</summary>
    public void ToggleComponent(Component c)
    {
        if (c == null) return;
        if (selectedComponents.Remove(c))
        {
            if (logSelection) Debug.Log($"[MouseHandler] Deselected {c.GetType().Name} on '{c.gameObject.name}'", c);
            onComponentDeselected?.Invoke(c);
        }
        else
        {
            selectedComponents.Add(c);
            if (logSelection) Debug.Log($"[MouseHandler] Selected {c.GetType().Name} on '{c.gameObject.name}'", c);
            onComponentSelected?.Invoke(c);
        }
    }

    public void ClearSelection()
    {
        for (int i = selectedComponents.Count - 1; i >= 0; i--)
        {
            Component c = selectedComponents[i];
            selectedComponents.RemoveAt(i);
            if (c != null) onComponentDeselected?.Invoke(c);
        }
    }

    void OnDestroy()
    {
        menuMask.Dispose();
        promptMask.Dispose();
    }

    void Update()
    {
        if (cam == null) cam = Camera.main;
        selectedComponents.RemoveAll(c => c == null); // drop destroyed components
        if (cam == null || Mouse.current == null)
        {
            hovered = null;
            UpdateHoverOutline();
            return;
        }

        Vector2 mousePos = Mouse.current.position.ReadValue();
        Ray ray = cam.ScreenPointToRay(mousePos);

        // Collide with triggers too: projectiles use trigger colliders.
        hovered = Physics.Raycast(ray, out RaycastHit hit, maxDistance, hoverMask, QueryTriggerInteraction.Collide)
            ? hit.collider.gameObject
            : null;

        UpdateHoverOutline();

        // The component menu is only available while paused; unpausing closes it.
        if (!IsGamePaused())
        {
            if (menuOpen) CloseMenu();
            return;
        }

        if (Mouse.current.leftButton.wasPressedThisFrame)
        {
            Vector2 guiMouse = PiUISprites.ScreenToGUI(mousePos);

            // Clicks on the open menu are handled by OnGUI.
            if (menuOpen && PanelRect().Contains(guiMouse))
                return;

            // Clicks on the pi output column shouldn't select scene objects behind it.
            if (PiOutputPanel.IsPointerOver(guiMouse))
                return;

            if (hovered != null)
                OpenMenu(hovered, guiMouse);
            else
                CloseMenu();
        }

        if (menuOpen && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            CloseMenu();
        }
        if (menuOpen && Keyboard.current != null && prompt.Length > 0 && Keyboard.current.enterKey.wasPressedThisFrame)
        {
            SendPrompt();
            CloseMenu();
        }
    }

    bool IsGamePaused()
    {
        if (pauseToggle == null) pauseToggle = FindAnyObjectByType<PauseToggle>();
        return pauseToggle != null && pauseToggle.IsPaused;
    }

    // While paused, shows the purple outline on whatever is under the cursor and restores the previous object.
    void UpdateHoverOutline()
    {
        Outline next = IsGamePaused() && hovered != null ? hovered.GetComponentInParent<Outline>() : null;
        if (next == hoveredOutline) return;
        if (hoveredOutline != null) hoveredOutline.Hide();
        hoveredOutline = next;
        if (hoveredOutline != null) hoveredOutline.Show(hoverOutlineColor);
    }

    void OpenMenu(GameObject target, Vector2 guiPos)
    {
        menuTarget = target;
        BuildOptions(target);
        Vector2 size = PanelSize();
        Vector2 p = guiPos + cursorOffset;
        // Keep the menu on screen.
        menuPos = new Vector2(
            Mathf.Clamp(p.x, 0f, Mathf.Max(0f, PiUISprites.GuiWidth - size.x)),
            Mathf.Clamp(p.y, 0f, Mathf.Max(0f, PiUISprites.GuiHeight - size.y)));
        pressedIndex = -1;
        menuOpen = true;
        focusPromptNext = true;
    }

    [Header("Pi Bridge")]
    [Tooltip("Endpoint of the pi unity-bridge extension (.pi/extensions/unity-bridge.ts).")]
    public string piBridgeUrl = "http://127.0.0.1:7777/prompt";

    [System.Serializable]
    class PromptPayload
    {
        public string prompt;
        public string scene;
        public string target;
        public string targetPath;
        public string[] components;
        public string[] selectedComponents;
    }

    void SendPrompt()
    {
        Debug.Log("Prompt: " + prompt);

        // Capture everything now: CloseMenu() clears the prompt and target right after this call.
        var payload = new PromptPayload
        {
            prompt = prompt,
            scene = gameObject.scene.name,
            target = menuTarget != null ? menuTarget.name : null,
            targetPath = menuTarget != null ? HierarchyPath(menuTarget.transform) : null,
            components = options.ConvertAll(o => o.label).ToArray(),
            selectedComponents = selectedComponents.FindAll(c => c != null)
                .ConvertAll(c => $"{c.GetType().Name} on '{HierarchyPath(c.transform)}'").ToArray(),
        };

        StartCoroutine(PostToPi(JsonUtility.ToJson(payload)));
    }

    /// <summary>
    /// Sends <paramref name="text"/> to pi reusing the editor context (scene, target, components)
    /// captured in <paramref name="contextJson"/> from an earlier prompt. Used by PiOutputPanel's follow-up box.
    /// </summary>
    public void SendFollowUp(string text, string contextJson)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        PromptPayload payload = null;
        if (!string.IsNullOrEmpty(contextJson))
        {
            try { payload = JsonUtility.FromJson<PromptPayload>(contextJson); }
            catch { payload = null; }
        }
        if (payload == null) payload = new PromptPayload { scene = gameObject.scene.name };
        payload.prompt = text;
        Debug.Log("Follow-up prompt: " + text);
        StartCoroutine(PostToPi(JsonUtility.ToJson(payload)));
    }

    IEnumerator PostToPi(string json)
    {
        using (var req = new UnityWebRequest(piBridgeUrl, UnityWebRequest.kHttpVerbPOST))
        {
            req.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = 5;

            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                Debug.Log("[MouseHandler] Prompt sent to pi");
                // Stream pi's reply into the left-quarter output column.
                var panel = PiOutputPanel.Show(gameObject, PiOutputPanel.ParseSeq(req.downloadHandler.text));
                panel.contextJson = json; // follow-ups typed in the panel reuse this context
            }
            else
                Debug.LogWarning($"[MouseHandler] Could not reach pi at {piBridgeUrl}: {req.error}. " +
                                 "Is pi running with the unity-bridge extension loaded (/reload)?");
        }
    }

    static string HierarchyPath(Transform t)
    {
        string path = t.name;
        while (t.parent != null) { t = t.parent; path = t.name + "/" + path; }
        return path;
    }

    void CloseMenu()
    {
        menuOpen = false;
        menuTarget = null;
        pressedIndex = -1;
        prompt = "";
    }

    Vector2 MenuSize()
    {
        int n = Mathf.Max(1, options.Count);
        return layout == MenuLayout.Stacked
            ? new Vector2(segmentWidth, segmentHeight * n)
            : new Vector2(segmentWidth * n, segmentHeight);
    }

    Rect MenuRect() => new Rect(menuPos, MenuSize());

    Rect PromptRect()
    {
        Rect menu = MenuRect();
        return new Rect(menu.x, menu.yMax + promptGap, menu.width, promptHeight);
    }

    /// <summary>Menu + gap + prompt box.</summary>
    Vector2 PanelSize()
    {
        Vector2 m = MenuSize();
        return new Vector2(m.x, m.y + promptGap + promptHeight);
    }

    Rect PanelRect() => new Rect(menuPos, PanelSize());

    void SubmitPrompt()
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;
        Debug.Log($"[MouseHandler] Prompt: {prompt}");
    }

    Rect SegmentRect(Rect menu, int i)
    {
        return layout == MenuLayout.Stacked
            ? new Rect(menu.x, menu.y + i * segmentHeight, menu.width, segmentHeight)
            : new Rect(menu.x + i * segmentWidth, menu.y, segmentWidth, menu.height);
    }

    void OnGUI()
    {
        if (!menuOpen) return;
        if (menuTarget == null) { CloseMenu(); return; } // target was destroyed

        Matrix4x4 prevMatrix = GUI.matrix;
        GUI.matrix = PiUISprites.GuiMatrix; // sizes below are reference pixels

        Rect menu = MenuRect();
        Rect promptRect = PromptRect();
        menuMask.Ensure(menu.size, cornerRadius, squircleExponent);
        promptMask.Ensure(promptRect.size, cornerRadius, squircleExponent);
        EnsureStyle();

        Event e = Event.current;
        Vector2 mouse = e.mousePosition;
        Color prevColor = GUI.color;

        // Soft drop shadow.
        GUI.color = shadowColor;
        GUI.DrawTexture(new Rect(menu.x, menu.y + 3f, menu.width, menu.height), menuMask.Texture);
        GUI.DrawTexture(new Rect(promptRect.x, promptRect.y + 3f, promptRect.width, promptRect.height), promptMask.Texture);

        // Segments: each one is the matching slice of the single squircle mask.
        for (int i = 0; i < options.Count; i++)
        {
            Rect seg = SegmentRect(menu, i);
            bool isHover = seg.Contains(mouse);
            bool isSelected = IsSelected(options[i].component);

            GUI.color = backgroundColor;
            DrawMaskSlice(menu, seg);

            if (isHover && pressedIndex == i)
                GUI.color = pressedColor;
            else if (isSelected)
                GUI.color = isHover ? selectedHoverColor : selectedColor;
            else if (isHover)
                GUI.color = hoverColor;
            else
                GUI.color = Color.clear;

            if (GUI.color.a > 0f) DrawMaskSlice(menu, seg);

            // Divider between segments (inset so it doesn't touch the rounded edge).
            if (i > 0)
            {
                GUI.color = dividerColor;
                if (layout == MenuLayout.Stacked)
                    GUI.DrawTexture(new Rect(seg.x + 12f, seg.y - 0.5f, seg.width - 24f, 1f), Texture2D.whiteTexture);
                else
                    GUI.DrawTexture(new Rect(seg.x - 0.5f, seg.y + 8f, 1f, seg.height - 16f), Texture2D.whiteTexture);
            }

            GUI.color = Color.white;
            labelStyle.normal.textColor = textColor;
            GUI.Label(seg, options[i].label, labelStyle);

            // Input
            if (isHover && e.type == EventType.MouseDown && e.button == 0)
            {
                pressedIndex = i;
                e.Use();
            }
            else if (e.type == EventType.MouseUp && e.button == 0 && pressedIndex == i)
            {
                bool releasedOnSame = isHover;
                pressedIndex = -1;
                e.Use();
                if (releasedOnSame)
                    ToggleComponent(options[i].component); // menu stays open
            }
        }

        if (e.type == EventType.MouseUp && e.button == 0) pressedIndex = -1;

        DrawPrompt(promptRect, e);

        if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag) e.Use(); // repaint hover

        GUI.color = prevColor;
        GUI.matrix = prevMatrix;
    }

    void DrawPrompt(Rect rect, Event e)
    {
        GUI.color = promptBackgroundColor;
        GUI.DrawTexture(rect, promptMask.Texture);
        GUI.color = Color.white;

        // Enter submits. Handle before the TextField so it can't swallow the key.
        if (e.type == EventType.KeyDown && GUI.GetNameOfFocusedControl() == PromptControl)
        {
            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
            {
                SubmitPrompt();
                e.Use();
            }
            else if (e.character == '\n' || e.character == '\r')
            {
                e.Use(); // swallow the matching character event
            }
        }

        Color prevCursor = GUI.skin.settings.cursorColor;
        GUI.skin.settings.cursorColor = textColor;

        GUI.SetNextControlName(PromptControl);
        prompt = GUI.TextField(rect, prompt ?? "", promptStyle);

        GUI.skin.settings.cursorColor = prevCursor;

        if (string.IsNullOrEmpty(prompt))
            GUI.Label(rect, promptPlaceholder, placeholderStyle);

        if (focusPromptNext && e.type == EventType.Repaint)
        {
            GUI.FocusControl(PromptControl);
            focusPromptNext = false;
        }
    }

    /// <summary>Draws the part of the full-menu mask that lies under <paramref name="seg"/>.</summary>
    void DrawMaskSlice(Rect menu, Rect seg)
    {
        Rect uv = new Rect(
            (seg.x - menu.x) / menu.width,
            1f - (seg.yMax - menu.y) / menu.height, // texture UVs are bottom-up
            seg.width / menu.width,
            seg.height / menu.height);
        GUI.DrawTextureWithTexCoords(seg, menuMask.Texture, uv);
    }

    void EnsureStyle()
    {
        if (labelStyle != null && labelStyle.fontSize == fontSize) return;
        labelStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = fontSize,
            fontStyle = FontStyle.Normal,
            wordWrap = false,
            clipping = TextClipping.Clip,
            padding = new RectOffset(8, 8, 0, 0),
        };

        // Borderless text field: the squircle behind it is the background.
        promptStyle = new GUIStyle(GUI.skin.textField)
        {
            alignment = TextAnchor.MiddleLeft,
            fontSize = fontSize,
            wordWrap = false,
            clipping = TextClipping.Clip,
            padding = new RectOffset(16, 16, 0, 0),
            border = new RectOffset(0, 0, 0, 0),
            margin = new RectOffset(0, 0, 0, 0),
        };
        foreach (GUIStyleState s in new[] { promptStyle.normal, promptStyle.hover, promptStyle.active, promptStyle.focused,
                                             promptStyle.onNormal, promptStyle.onHover, promptStyle.onActive, promptStyle.onFocused })
        {
            s.background = null;
            s.scaledBackgrounds = null;
            s.textColor = textColor;
        }

        placeholderStyle = new GUIStyle(promptStyle);
        placeholderStyle.normal.textColor = promptPlaceholderColor;
    }

    /// <summary>Cached white anti-aliased squircle (superellipse-cornered rect) mask texture.</summary>
    internal class SquircleMask
    {
        public Texture2D Texture { get; private set; }
        Vector2Int size;
        float radius, exponent;

        public void Ensure(Vector2 guiSize, float cornerRadius, float squircleExponent)
        {
            // Render at UI scale so edges stay crisp on high-DPI displays.
            float scale = Mathf.Max(1f, GUI.matrix.lossyScale.x);
            Vector2Int px = new Vector2Int(
                Mathf.Max(2, Mathf.CeilToInt(guiSize.x * scale)),
                Mathf.Max(2, Mathf.CeilToInt(guiSize.y * scale)));

            if (Texture != null && px == size &&
                Mathf.Approximately(radius, cornerRadius) &&
                Mathf.Approximately(exponent, squircleExponent))
                return;

            Dispose();
            size = px;
            radius = cornerRadius;
            exponent = squircleExponent;

            Texture = new Texture2D(px.x, px.y, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };

            float r = Mathf.Min(cornerRadius * scale, px.x * 0.5f, px.y * 0.5f);
            float p = squircleExponent;
            var pixels = new Color32[px.x * px.y];

            for (int y = 0; y < px.y; y++)
            {
                for (int x = 0; x < px.x; x++)
                {
                    // Pixel center, distance into the corner zone on each axis.
                    float cx = x + 0.5f, cy = y + 0.5f;
                    float dx = Mathf.Max(0f, Mathf.Max(r - cx, cx - (px.x - r)));
                    float dy = Mathf.Max(0f, Mathf.Max(r - cy, cy - (px.y - r)));

                    float alpha;
                    if (dx <= 0f || dy <= 0f)
                    {
                        // Straight edges: anti-alias against the outer border.
                        float edge = Mathf.Min(Mathf.Min(cx, px.x - cx), Mathf.Min(cy, px.y - cy));
                        alpha = Mathf.Clamp01(edge + 0.5f);
                    }
                    else
                    {
                        float d = Mathf.Pow(Mathf.Pow(dx, p) + Mathf.Pow(dy, p), 1f / p);
                        alpha = Mathf.Clamp01(r - d + 0.5f);
                    }

                    pixels[y * px.x + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            }

            Texture.SetPixels32(pixels);
            Texture.Apply(false, false);
        }

        public void Dispose()
        {
            if (Texture != null) Object.Destroy(Texture);
            Texture = null;
        }
    }
}
