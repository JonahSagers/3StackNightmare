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
    [Tooltip("Height of the empty (single-line) prompt box.")]
    public float promptHeight = 40f;
    [Tooltip("Vertical gap between the prompt box and the menu below it.")]
    public float promptGap = 8f;
    [Tooltip("Max height the prompt box grows to as wrapped text adds lines.")]
    public float promptMaxHeight = 160f;
    [Tooltip("How quickly the prompt box animates to its new size (higher = snappier).")]
    public float promptExpandSpeed = 14f;
    public Color promptBackgroundColor = new Color(0.15f, 0.15f, 0.18f, 0.96f);
    public Color promptPlaceholderColor = new Color(1f, 1f, 1f, 0.35f);

    [Header("Context Points")]
    [Tooltip("While paused with the prompt open, left-clicking the ground adds a world point that is sent with the prompt.")]
    public List<Vector3> contextPoints = new List<Vector3>();
    [Tooltip("Max number of ground points per prompt.")]
    public int maxContextPoints = 9;
    [Tooltip("Clicking within this many GUI pixels of a pin removes it.")]
    public float contextPinRadius = 13f;
    public Color contextPinColor = new Color(1f, 0.78f, 0.15f, 1f);

    // Menu state
    bool menuOpen;
    GameObject menuTarget;
    Vector2 menuPos; // GUI space (top-left origin)
    int pressedIndex = -1;

    const string PromptControl = "MouseHandler_Prompt";
    bool focusPromptNext;
    Vector2 promptSize; // animated size of the prompt box; zero = not yet initialised
    int promptAnimFrame = -1;

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
            if (menuOpen && PanelContains(guiMouse))
                return;

            // Clicks on the pi output column shouldn't select scene objects behind it.
            if (PiOutputPanel.IsPointerOver(guiMouse))
                return;

            // With the prompt open, clicks on the ground add/remove context points instead of closing it.
            if (menuOpen && (hovered == null || hovered.GetComponentInParent<Outline>() == null))
            {
                HandleContextClick(ray, guiMouse);
                return;
            }

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

    /// <summary>Click on the ground while the prompt is open: remove a pin under the cursor, or add a new point.</summary>
    void HandleContextClick(Ray ray, Vector2 guiMouse)
    {
        for (int i = contextPoints.Count - 1; i >= 0; i--)
        {
            if (TryPinGuiPos(contextPoints[i], out Vector2 pin) && (pin - guiMouse).sqrMagnitude <= contextPinRadius * contextPinRadius)
            {
                contextPoints.RemoveAt(i);
                focusPromptNext = true;
                return;
            }
        }
        if (contextPoints.Count >= maxContextPoints) { focusPromptNext = true; return; }
        if (TryGetGroundPoint(ray, out Vector3 point))
        {
            contextPoints.Add(point);
            if (logSelection) Debug.Log($"[MouseHandler] Context point #{contextPoints.Count} at {FormatPoint(point)}");
        }
        focusPromptNext = true; // keep typing after the click
    }

    /// <summary>Nearest ground hit (Ground / Background-tagged / non-character collider), else the y = 0 plane.</summary>
    bool TryGetGroundPoint(Ray ray, out Vector3 point)
    {
        RaycastHit[] hits = Physics.RaycastAll(ray, maxDistance, hoverMask, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        point = default;
        foreach (RaycastHit h in hits)
        {
            if (h.distance >= best || h.collider.GetComponentInParent<Outline>() != null) continue;
            best = h.distance;
            point = h.point;
        }
        if (best < float.MaxValue) return true;
        if (new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float dist))
        {
            point = ray.GetPoint(dist);
            return true;
        }
        return false;
    }

    /// <summary>GUI (reference-pixel) position of a world point; false if it is behind the camera.</summary>
    bool TryPinGuiPos(Vector3 world, out Vector2 gui)
    {
        gui = default;
        if (cam == null) return false;
        Vector3 sp = cam.WorldToScreenPoint(world);
        if (sp.z <= 0f) return false;
        gui = PiUISprites.ScreenToGUI(sp);
        return true;
    }

    static string FormatPoint(Vector3 p)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return $"({p.x.ToString("0.##", inv)}, {p.y.ToString("0.##", inv)}, {p.z.ToString("0.##", inv)})";
    }

    /// <summary>"#1 (x, y, z), nearest: Enemy 1.2m away" for each context point.</summary>
    string[] DescribeContextPoints()
    {
        var result = new string[contextPoints.Count];
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        Outline[] chars = FindObjectsByType<Outline>(FindObjectsSortMode.None);
        for (int i = 0; i < contextPoints.Count; i++)
        {
            Vector3 p = contextPoints[i];
            string s = $"#{i + 1} world {FormatPoint(p)}";
            Outline nearest = null;
            float nd = float.MaxValue;
            foreach (Outline o in chars)
            {
                if (o == null) continue;
                Vector3 d = o.transform.position - p;
                d.y = 0f;
                if (d.magnitude < nd) { nd = d.magnitude; nearest = o; }
            }
            if (nearest != null) s += $", nearest character: {nearest.name} {nd.ToString("0.#", inv)}m away at {FormatPoint(nearest.transform.position)}";
            result[i] = s;
        }
        return result;
    }

    void OpenMenu(GameObject target, Vector2 guiPos)
    {
        menuTarget = target;
        BuildOptions(target);
        Vector2 size = MenuSize();
        Vector2 p = guiPos + cursorOffset;
        // Keep the menu on screen, leaving room above it for the (collapsed) prompt box.
        float top = promptHeight + promptGap;
        menuPos = new Vector2(
            Mathf.Clamp(p.x, 0f, Mathf.Max(0f, PiUISprites.GuiWidth - size.x)),
            Mathf.Clamp(p.y, top, Mathf.Max(top, PiUISprites.GuiHeight - size.y)));
        pressedIndex = -1;
        promptSize = Vector2.zero; // start collapsed
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
        public string[] selectedComponentFields; // parallel to selectedComponents: "field=value, ..."
        public bool isPlaying;
        public bool editorPaused;
        public bool gamePaused;
        public string[] contextPoints; // ground points clicked while the prompt was open
    }

    /// <summary>Fills the parts of the payload that must be current at send time (fields, play state).</summary>
    void CaptureLiveState(PromptPayload payload, List<Component> comps)
    {
        payload.isPlaying = Application.isPlaying;
#if UNITY_EDITOR
        payload.editorPaused = UnityEditor.EditorApplication.isPaused;
#endif
        payload.gamePaused = IsGamePaused();
        payload.selectedComponentFields = comps.ConvertAll(c => c != null ? DescribeFields(c) : "(destroyed)").ToArray();
    }

    /// <summary>Finds a component again from its "Type on 'Hierarchy/Path'" label (used for follow-ups).</summary>
    static Component ResolveComponent(string label)
    {
        int on = label.IndexOf(" on '");
        if (on < 0 || !label.EndsWith("'")) return null;
        string type = label.Substring(0, on);
        string path = label.Substring(on + 5, label.Length - on - 6);
        GameObject go = GameObject.Find(path);
        return go != null ? go.GetComponent(type) : null;
    }

    /// <summary>Current serialized field values of a component, as "field=value, ..." (property paths usable with set_field).</summary>
    static string DescribeFields(Component c)
    {
#if UNITY_EDITOR
        var so = new UnityEditor.SerializedObject(c);
        var sb = new System.Text.StringBuilder();
        UnityEditor.SerializedProperty it = so.GetIterator();
        bool enter = true;
        while (it.NextVisible(enter))
        {
            enter = false;
            if (it.propertyPath == "m_Script") continue;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(it.propertyPath).Append('=').Append(FormatProperty(it));
            if (sb.Length > 3000) { sb.Append(", ..."); break; }
        }
        return sb.ToString();
#else
        return c is MonoBehaviour ? JsonUtility.ToJson(c) : "";
#endif
    }

#if UNITY_EDITOR
    static string FormatProperty(UnityEditor.SerializedProperty p)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        switch (p.propertyType)
        {
            case UnityEditor.SerializedPropertyType.Integer: return p.longValue.ToString(inv);
            case UnityEditor.SerializedPropertyType.Boolean: return p.boolValue ? "true" : "false";
            case UnityEditor.SerializedPropertyType.Float: return p.floatValue.ToString("0.###", inv);
            case UnityEditor.SerializedPropertyType.String:
                string s = p.stringValue ?? "";
                return "\"" + (s.Length > 80 ? s.Substring(0, 77) + "..." : s) + "\"";
            case UnityEditor.SerializedPropertyType.Color: return p.colorValue.ToString();
            case UnityEditor.SerializedPropertyType.LayerMask: return p.intValue.ToString(inv);
            case UnityEditor.SerializedPropertyType.Enum:
                string[] names = p.enumDisplayNames;
                return p.enumValueIndex >= 0 && p.enumValueIndex < names.Length ? names[p.enumValueIndex] : p.intValue.ToString(inv);
            case UnityEditor.SerializedPropertyType.Vector2: return p.vector2Value.ToString();
            case UnityEditor.SerializedPropertyType.Vector3: return p.vector3Value.ToString();
            case UnityEditor.SerializedPropertyType.Vector4: return p.vector4Value.ToString();
            case UnityEditor.SerializedPropertyType.Vector2Int: return p.vector2IntValue.ToString();
            case UnityEditor.SerializedPropertyType.Vector3Int: return p.vector3IntValue.ToString();
            case UnityEditor.SerializedPropertyType.Rect: return p.rectValue.ToString();
            case UnityEditor.SerializedPropertyType.Bounds: return p.boundsValue.ToString();
            case UnityEditor.SerializedPropertyType.Quaternion: return "euler" + p.quaternionValue.eulerAngles.ToString();
            case UnityEditor.SerializedPropertyType.ObjectReference:
                Object o = p.objectReferenceValue;
                return o != null ? $"{o.name} ({o.GetType().Name})" : "null";
            case UnityEditor.SerializedPropertyType.ArraySize: return p.intValue.ToString(inv);
            default:
                return p.isArray ? $"[{p.arraySize} items]" : "{...}";
        }
    }
#endif

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
        CaptureLiveState(payload, selectedComponents.FindAll(c => c != null));
        payload.contextPoints = DescribeContextPoints();

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
        // The saved context is from the earlier prompt; refresh field values and play state.
        var comps = new List<Component>();
        if (payload.selectedComponents != null)
            foreach (string label in payload.selectedComponents) comps.Add(ResolveComponent(label));
        CaptureLiveState(payload, comps);
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
        // The prompt box stops being drawn, but IMGUI keeps its keyboard focus; drop it so
        // ability hotkeys don't stay blocked by an invisible text box.
        if (menuOpen) GUIUtility.keyboardControl = 0;
        menuOpen = false;
        menuTarget = null;
        pressedIndex = -1;
        prompt = "";
        contextPoints.Clear();
    }

    Vector2 MenuSize()
    {
        int n = Mathf.Max(1, options.Count);
        return layout == MenuLayout.Stacked
            ? new Vector2(segmentWidth, segmentHeight * n)
            : new Vector2(segmentWidth * n, segmentHeight);
    }

    Rect MenuRect() => new Rect(menuPos, MenuSize());

    Vector2 CollapsedPromptSize() => new Vector2(MenuSize().x, promptHeight);

    /// <summary>Size the prompt box is animating toward: menu width, height fits the wrapped text.</summary>
    Vector2 TargetPromptSize()
    {
        Vector2 collapsed = CollapsedPromptSize();
        if (string.IsNullOrEmpty(prompt) || promptStyle == null) return collapsed;
        float w = collapsed.x; // width stays matched to the menu; only height grows
        float h = promptStyle.CalcHeight(new GUIContent(prompt), w);
        return new Vector2(w, Mathf.Clamp(h, promptHeight, Mathf.Max(promptHeight, promptMaxHeight)));
    }

    /// <summary>Eases promptSize toward its target. Called once per frame (unscaled time: the game is paused).</summary>
    void StepPromptSize()
    {
        Vector2 target = TargetPromptSize();
        if (promptSize.x <= 0f) promptSize = CollapsedPromptSize();
        float k = 1f - Mathf.Exp(-promptExpandSpeed * Time.unscaledDeltaTime);
        promptSize = Vector2.Lerp(promptSize, target, k);
        if ((promptSize - target).sqrMagnitude < 0.01f) promptSize = target;
    }

    /// <summary>Prompt box sits above the menu; its bottom edge is anchored so it grows upward.</summary>
    Rect PromptRect()
    {
        Rect menu = MenuRect();
        Vector2 s = promptSize.x > 0f ? promptSize : CollapsedPromptSize();
        float x = Mathf.Clamp(menu.x, 0f, Mathf.Max(0f, PiUISprites.GuiWidth - s.x));
        float y = Mathf.Max(0f, menu.y - promptGap - s.y);
        return new Rect(x, y, s.x, s.y);
    }

    /// <summary>True if the point is over the menu or the prompt box.</summary>
    bool PanelContains(Vector2 p) => MenuRect().Contains(p) || PromptRect().Contains(p);

    /// <summary>
    /// Draws a squircle of any size by 9-slicing a fixed corner mask, so an animating
    /// size doesn't regenerate the mask texture every frame.
    /// </summary>
    void DrawSquircle(Rect rect, Color color)
    {
        promptMask.Ensure(new Vector2(cornerRadius * 2f, cornerRadius * 2f), cornerRadius, squircleExponent);
        Texture tex = promptMask.Texture;
        float r = Mathf.Min(cornerRadius, rect.width * 0.5f, rect.height * 0.5f);
        float[] xs = { rect.x, rect.x + r, rect.xMax - r };
        float[] ws = { r, rect.width - 2f * r, r };
        float[] us = { 0f, 0.5f, 0.5f };
        float[] uw = { 0.5f, 0f, 0.5f };
        float[] ys = { rect.y, rect.y + r, rect.yMax - r };
        float[] hs = { r, rect.height - 2f * r, r };
        float[] vs = { 0.5f, 0.5f, 0f }; // texture UVs are bottom-up
        float[] vh = { 0.5f, 0f, 0.5f };
        GUI.color = color;
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
            {
                if (ws[col] <= 0f || hs[row] <= 0f) continue;
                GUI.DrawTextureWithTexCoords(new Rect(xs[col], ys[row], ws[col], hs[row]), tex,
                    new Rect(us[col], vs[row], uw[col], vh[row]));
            }
    }

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

        EnsureStyle();
        Event e = Event.current;
        if (e.type == EventType.Layout && promptAnimFrame != Time.frameCount)
        {
            promptAnimFrame = Time.frameCount;
            StepPromptSize();
        }

        Rect menu = MenuRect();
        Rect promptRect = PromptRect();
        menuMask.Ensure(menu.size, cornerRadius, squircleExponent);

        Vector2 mouse = e.mousePosition;
        Color prevColor = GUI.color;

        // Soft drop shadow.
        GUI.color = shadowColor;
        GUI.DrawTexture(new Rect(menu.x, menu.y + 3f, menu.width, menu.height), menuMask.Texture);
        DrawSquircle(new Rect(promptRect.x, promptRect.y + 3f, promptRect.width, promptRect.height), shadowColor);

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
        DrawContextPoints(menu);

        if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag) e.Use(); // repaint hover

        GUI.color = prevColor;
        GUI.matrix = prevMatrix;
    }

    void DrawPrompt(Rect rect, Event e)
    {
        DrawSquircle(rect, promptBackgroundColor);
        GUI.color = Color.white;

        // Block game hotkeys while the prompt has focus (or is about to get it on open).
        if (focusPromptNext || GUI.GetNameOfFocusedControl() == PromptControl) PiUISprites.MarkTyping();

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
        // TextArea so long prompts wrap onto new lines; Enter is intercepted above, so no newlines get in.
        prompt = GUI.TextArea(rect, prompt ?? "", promptStyle);

        GUI.skin.settings.cursorColor = prevCursor;

        if (string.IsNullOrEmpty(prompt))
            GUI.Label(rect, promptPlaceholder, placeholderStyle);

        if (focusPromptNext && e.type == EventType.Repaint)
        {
            GUI.FocusControl(PromptControl);
            focusPromptNext = false;
        }
    }

    /// <summary>Numbered pins on the clicked ground points, plus a hint under the menu.</summary>
    void DrawContextPoints(Rect menu)
    {
        if (Event.current.type != EventType.Repaint) return;
        EnsurePinStyle();
        float d = contextPinRadius * 2f;
        for (int i = 0; i < contextPoints.Count; i++)
        {
            if (!TryPinGuiPos(contextPoints[i], out Vector2 p)) continue;
            Rect r = new Rect(p.x - contextPinRadius, p.y - contextPinRadius, d, d);
            GUI.color = shadowColor;
            GUI.DrawTexture(new Rect(r.x, r.y + 2f, d, d), PinTexture());
            GUI.color = contextPinColor;
            GUI.DrawTexture(r, PinTexture());
            GUI.color = Color.white;
            GUI.Label(r, (i + 1).ToString(), pinStyle);
        }

        string hint = contextPoints.Count == 0
            ? "Click the ground to add points"
            : $"{contextPoints.Count} point{(contextPoints.Count == 1 ? "" : "s")} \u00b7 click a pin to remove";
        GUI.color = Color.white;
        Rect hr = new Rect(menu.x, menu.yMax + 4f, Mathf.Max(menu.width, 260f), 20f);
        GUI.Label(hr, hint, hintStyle);
    }

    GUIStyle pinStyle, hintStyle;
    Texture2D pinTex;

    Texture2D PinTexture()
    {
        if (pinTex == null) pinTex = PiUISprites.Circle(64);
        return pinTex;
    }

    void EnsurePinStyle()
    {
        if (pinStyle != null) return;
        pinStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = Mathf.Max(10, fontSize - 2),
            fontStyle = FontStyle.Bold,
            padding = new RectOffset(0, 0, 0, 0),
        };
        pinStyle.normal.textColor = new Color(0.1f, 0.08f, 0.02f, 1f);
        hintStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleLeft,
            fontSize = Mathf.Max(10, fontSize - 3),
            padding = new RectOffset(6, 0, 0, 0),
        };
        hintStyle.normal.textColor = new Color(1f, 1f, 1f, 0.6f);
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
        promptStyle = new GUIStyle(GUI.skin.textArea)
        {
            alignment = TextAnchor.UpperLeft,
            fontSize = fontSize,
            wordWrap = true,
            clipping = TextClipping.Clip,
            border = new RectOffset(0, 0, 0, 0),
            margin = new RectOffset(0, 0, 0, 0),
        };
        // Vertical padding centres a single line in the collapsed box; wrapped lines make the box taller.
        int vPad = Mathf.Max(0, Mathf.RoundToInt((promptHeight - promptStyle.lineHeight) * 0.5f));
        promptStyle.padding = new RectOffset(16, 16, vPad, vPad);
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
