using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Left-quarter column that streams pi's output (from .pi/extensions/unity-bridge.ts) after a
/// prompt is sent. Opened by MouseHandler.SendPrompt via <see cref="Show"/>.
/// </summary>
public class PiOutputPanel : MonoBehaviour
{
    public static PiOutputPanel Instance { get; private set; }

    [Tooltip("Base URL of the pi unity-bridge extension.")]
    public string bridgeBaseUrl = "http://127.0.0.1:7777";

    [Tooltip("Seconds between output polls while the panel is open.")]
    public float pollInterval = 0.25f;

    [Tooltip("Fraction of the screen width the column spans.")]
    [Range(0.1f, 0.6f)] public float widthFraction = 0.25f;

    [Tooltip("Gap between the panel and the screen edges.")]
    public float margin = 12f;

    public float cornerRadius = 18f;
    [Range(2f, 10f)] public float squircleExponent = 4.5f;
    public int fontSize = 28;

    [Tooltip("Oldest output is dropped once the text exceeds this many characters (IMGUI limit).")]
    public int maxChars = 12000;

    [Header("Palette")]
    public Color backgroundColor = new Color(0.075f, 0.078f, 0.094f, 0.96f);
    public Color borderColor = new Color(1f, 1f, 1f, 0.07f);
    public Color shadowColor = new Color(0f, 0f, 0f, 0.55f);
    public Color accentColor = new Color(0.42f, 0.47f, 1f, 1f);
    public Color textColor = new Color(0.86f, 0.87f, 0.90f, 1f);
    public Color mutedColor = new Color(0.60f, 0.62f, 0.68f, 1f);
    public Color surfaceColor = new Color(1f, 1f, 1f, 0.045f);
    public Color successColor = new Color(0.30f, 0.82f, 0.55f, 1f);
    public Color warningColor = new Color(0.98f, 0.72f, 0.30f, 1f);
    public Color errorColor = new Color(0.96f, 0.38f, 0.40f, 1f);

    public bool IsOpen => open;

    [Header("Follow-up Box")]
    public string followUpPlaceholder = "Ask a follow-up...";
    public float followUpHeight = 64f;

    /// <summary>Payload JSON of the last prompt sent from MouseHandler; follow-ups reuse its editor context.</summary>
    [HideInInspector] public string contextJson;

    string followUp = "";
    const string FollowUpControl = "PiOutputPanel_FollowUp";
    MouseHandler.SquircleMask followUpMask; // created lazily
    GUIStyle followUpStyle, followUpPlaceholderStyle;

    // ---- JSON shapes returned by the bridge ---------------------------------------
    [System.Serializable] class OutputChunk { public int seq; public string kind; public string text; }
    [System.Serializable] class OutputResponse { public bool ok; public int seq; public bool busy; public OutputChunk[] chunks; }
    [System.Serializable] class PromptResponse { public bool ok; public int seq; }

    enum BlockKind { Prompt, Text, Tool }

    class Block
    {
        public BlockKind kind;
        public bool failed; // tool blocks only
        public readonly StringBuilder text = new StringBuilder();
        string cached;
        public string Text => cached ?? (cached = text.ToString());
        public void Changed() => cached = null;
    }

    readonly List<Block> blocks = new List<Block>();
    bool open;
    bool busy;
    string status = "";
    int lastSeq;
    Vector2 scroll;
    bool stickToBottom = true;
    Coroutine pollRoutine;

    readonly MouseHandler.SquircleMask borderMask = new MouseHandler.SquircleMask();
    readonly MouseHandler.SquircleMask mask = new MouseHandler.SquircleMask();
    GUIStyle titleStyle, textStyle, toolStyle, promptStyle, promptNoWrapStyle, toolNoWrapStyle, statusStyle, emptyStyle;
    int stylesFontSize = -1;
    readonly List<Rect> layout = new List<Rect>();

    // ---- Public API ---------------------------------------------------------------

    /// <summary>Parses the seq returned by POST /prompt (0 if missing).</summary>
    public static int ParseSeq(string json)
    {
        if (string.IsNullOrEmpty(json)) return 0;
        try { return JsonUtility.FromJson<PromptResponse>(json)?.seq ?? 0; }
        catch { return 0; }
    }

    /// <summary>Opens the panel (adding it to <paramref name="host"/> if needed) and streams output after <paramref name="sinceSeq"/>.</summary>
    public static PiOutputPanel Show(GameObject host, int sinceSeq)
    {
        var panel = Instance != null ? Instance : host.GetComponent<PiOutputPanel>();
        if (panel == null) panel = host.AddComponent<PiOutputPanel>();
        panel.Open(sinceSeq);
        return panel;
    }

    /// <summary>True if the GUI-space point (top-left origin) is over the open panel.</summary>
    public static bool IsPointerOver(Vector2 guiPoint) =>
        Instance != null && Instance.open && Instance.PanelRect().Contains(guiPoint);

    public void Open(int sinceSeq)
    {
        // Keep previous output if we're continuing the same stream; otherwise start fresh.
        if (!open || sinceSeq < lastSeq) blocks.Clear();
        lastSeq = Mathf.Max(0, sinceSeq);
        status = "sending...";
        busy = true;
        open = true;
        stickToBottom = true;

        if (pollRoutine == null) pollRoutine = StartCoroutine(Poll());
    }

    public void Close()
    {
        open = false;
        if (pollRoutine != null) StopCoroutine(pollRoutine);
        pollRoutine = null;
    }

    // ---- Lifecycle ----------------------------------------------------------------

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        mask.Dispose();
        borderMask.Dispose();
        followUpMask?.Dispose();
    }

    IEnumerator Poll()
    {
        var wait = new WaitForSecondsRealtime(pollInterval);
        while (open)
        {
            using (var req = UnityWebRequest.Get($"{bridgeBaseUrl}/output?since={lastSeq}"))
            {
                req.timeout = 5;
                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.Success)
                {
                    Apply(JsonUtility.FromJson<OutputResponse>(req.downloadHandler.text));
                }
                else
                {
                    // Don't leave the header stuck on "working..." when polling fails.
                    busy = false;
                    status = req.responseCode == 404
                        ? "bridge outdated - run /reload in pi"
                        : "pi unreachable";
                }
            }
            yield return wait;
        }
        pollRoutine = null;
    }

    void Apply(OutputResponse r)
    {
        if (r == null || !r.ok) return;
        busy = r.busy;

        if (r.chunks != null)
        {
            foreach (var c in r.chunks)
            {
                if (c.seq <= lastSeq) continue;
                lastSeq = c.seq;
                switch (c.kind)
                {
                    case "text": AppendText(c.text); break;
                    case "tool": AddBlock(BlockKind.Tool, c.text); break;
                    case "user": AddBlock(BlockKind.Prompt, PromptLine(c.text)); break;
                    case "status": status = c.text; break;
                }
            }
            if (r.chunks.Length > 0) TrimToLimit();
        }

        lastSeq = Mathf.Max(lastSeq, r.seq);
        if (busy && (status == "done" || status == "aborted" || status == "sending...")) status = "working";
        if (!busy && status == "working") status = "done";
    }

    // Show just the user's typed prompt, not the editor context lines.
    static string PromptLine(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string first = text.Split('\n')[0];
        return first.StartsWith("[Unity] ") ? first.Substring(8) : first;
    }

    void AppendText(string delta)
    {
        Block last = blocks.Count > 0 ? blocks[blocks.Count - 1] : null;
        if (last == null || last.kind != BlockKind.Text)
        {
            if (string.IsNullOrWhiteSpace(delta)) return; // skip leading separators
            last = new Block { kind = BlockKind.Text };
            blocks.Add(last);
            delta = delta.TrimStart('\n');
        }
        last.text.Append(delta);
        last.Changed();
    }

    void AddBlock(BlockKind kind, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var b = new Block { kind = kind };
        if (kind == BlockKind.Tool && text.Length > 2 && (text[0] == '>' || text[0] == 'x') && text[1] == ' ')
        {
            b.failed = text[0] == 'x';
            text = text.Substring(2); // icons replace the "> " / "x " prefixes
        }
        b.text.Append(text);
        blocks.Add(b);
    }

    void TrimToLimit()
    {
        int total = 0;
        foreach (var b in blocks) total += b.text.Length;
        while (total > maxChars && blocks.Count > 1)
        {
            total -= blocks[0].text.Length;
            blocks.RemoveAt(0);
        }
        if (total > maxChars && blocks.Count == 1)
        {
            blocks[0].text.Remove(0, total - maxChars);
            blocks[0].Changed();
        }
    }

    // ---- Drawing ------------------------------------------------------------------

    Rect PanelRect()
    {
        // Reference-pixel space (see PiUISprites.GuiScale), so the panel is the same share of the screen at any resolution.
        float w = PiUISprites.GuiWidth * widthFraction;
        return new Rect(margin, margin, Mathf.Max(120f, w - margin * 1.5f), PiUISprites.GuiHeight - margin * 2f);
    }

    const float Pad = 16f;
    const float HeaderH = 52f;
    const float BlockGap = 10f;

    void OnGUI()
    {
        if (!open) return;
        EnsureStyles();

        Matrix4x4 prevMatrix = GUI.matrix;
        GUI.matrix = PiUISprites.GuiMatrix;
        Rect r = PanelRect();
        Color prev = GUI.color;

        // Shadow, 1px border, background.
        PiUISprites.DrawShadow(r, Mathf.RoundToInt(cornerRadius), 18, shadowColor, 6f);
        borderMask.Ensure(r.size, cornerRadius + 1f, squircleExponent);
        Rect inner = new Rect(r.x + 1f, r.y + 1f, r.width - 2f, r.height - 2f);
        mask.Ensure(inner.size, cornerRadius, squircleExponent);
        PiUISprites.DrawTinted(r, borderMask.Texture, borderColor);
        PiUISprites.DrawTinted(inner, mask.Texture, backgroundColor);

        if (DrawHeader(new Rect(r.x + Pad, r.y + 12f, r.width - Pad * 2f, HeaderH - 16f)))
        {
            GUI.color = prev;
            GUI.matrix = prevMatrix;
            return; // closed
        }

        // Hairline under the header.
        PiUISprites.DrawTinted(new Rect(r.x + Pad, r.y + HeaderH, r.width - Pad * 2f, 1f), Texture2D.whiteTexture, borderColor);

        // Follow-up box pinned to the bottom; the output body fills the space above it.
        Rect input = new Rect(r.x + Pad * 0.75f, r.yMax - Pad * 0.75f - followUpHeight, r.width - Pad * 1.5f, followUpHeight);
        float bodyTop = r.y + HeaderH + 10f;
        Rect body = new Rect(r.x + Pad - 4f, bodyTop, r.width - Pad * 2f + 8f, Mathf.Max(0f, input.y - 10f - bodyTop));
        DrawBody(body);
        DrawFollowUp(input);

        GUI.color = prev;
        GUI.matrix = prevMatrix;
    }

    /// <summary>Badge, status pill, close button. Returns true if the panel was closed.</summary>
    bool DrawHeader(Rect h)
    {
        // Brand badge.
        Rect badge = new Rect(h.x, h.center.y - 14f, 28f, 28f);
        PiUISprites.DrawRounded(badge, 8, accentColor);
        GUI.Label(badge, "pi", titleStyle);

        // Status pill with colored dot.
        GetStatusVisual(out string label, out Color dot, out bool pulse);
        float labelW = statusStyle.CalcSize(new GUIContent(label)).x;
        Rect pill = new Rect(badge.xMax + 10f, h.center.y - 12f, labelW + 30f, 24f);
        PiUISprites.DrawRounded(pill, 11, surfaceColor);
        float a = pulse ? 0.45f + 0.55f * (0.5f + 0.5f * Mathf.Sin(Time.realtimeSinceStartup * 5f)) : 1f;
        PiUISprites.DrawTinted(new Rect(pill.x + 10f, pill.center.y - 4f, 8f, 8f), PiUISprites.Circle(16), new Color(dot.r, dot.g, dot.b, dot.a * a));
        GUI.Label(new Rect(pill.x + 22f, pill.y, labelW + 4f, pill.height), label, statusStyle);

        // Close button: circular hover state + line icon.
        Rect close = new Rect(h.xMax - 28f, h.center.y - 14f, 28f, 28f);
        bool hover = close.Contains(Event.current.mousePosition);
        if (hover) PiUISprites.DrawTinted(close, PiUISprites.Circle(56), surfaceColor * 2f);
        PiUISprites.DrawTinted(new Rect(close.center.x - 7f, close.center.y - 7f, 14f, 14f), PiUISprites.IconClose(28), hover ? textColor : mutedColor);
        if (GUI.Button(close, GUIContent.none, GUIStyle.none))
        {
            Close();
            return true;
        }
        if (Event.current.type == EventType.MouseMove) Event.current.Use(); // keep hover responsive
        return false;
    }

    void DrawBody(Rect body)
    {
        float contentW = body.width - 10f; // room for the overlay scrollbar

        // Layout pass.
        layout.Clear();
        float y = 0f;
        for (int i = 0; i < blocks.Count; i++)
        {
            Rect br = LayoutBlock(blocks[i], contentW, y);
            layout.Add(br);
            y = br.yMax + BlockGap;
        }
        if (busy) y += 18f; // typing indicator
        float contentH = Mathf.Max(0f, y - BlockGap);

        float maxScroll = Mathf.Max(0f, contentH - body.height);
        if (stickToBottom) scroll.y = maxScroll;

        Vector2 newScroll = GUI.BeginScrollView(body, scroll, new Rect(0f, 0f, contentW, Mathf.Max(contentH, body.height)),
                                                false, false, GUIStyle.none, GUIStyle.none);
        if (blocks.Count == 0 && !busy)
            GUI.Label(new Rect(0f, 0f, contentW, body.height), "Waiting for pi...", emptyStyle);

        for (int i = 0; i < blocks.Count; i++)
        {
            Rect br = layout[i];
            if (br.yMax < scroll.y - 20f || br.y > scroll.y + body.height + 20f) continue; // cull off-screen
            DrawBlock(blocks[i], br);
        }
        if (busy) DrawTypingIndicator(new Vector2(6f, contentH - 10f));
        GUI.EndScrollView();

        // Re-stick when the user scrolls back to the bottom; unstick when they scroll up.
        if (!Mathf.Approximately(newScroll.y, scroll.y))
            stickToBottom = newScroll.y >= maxScroll - 4f;
        scroll = newScroll;

        // Thin overlay scrollbar thumb.
        if (maxScroll > 0f)
        {
            float trackH = body.height - 8f;
            float thumbH = Mathf.Max(28f, trackH * body.height / contentH);
            float thumbY = body.y + 4f + (trackH - thumbH) * (scroll.y / maxScroll);
            PiUISprites.DrawRounded(new Rect(body.xMax - 6f, thumbY, 4f, thumbH), 2, new Color(1f, 1f, 1f, 0.16f));
        }
    }

    Rect LayoutBlock(Block b, float contentW, float y)
    {
        var content = new GUIContent(b.Text);
        switch (b.kind)
        {
            case BlockKind.Prompt:
            {
                // Right-aligned chat bubble that shrinks to fit short prompts.
                float maxW = contentW * 0.85f;
                float w = Mathf.Min(maxW, promptNoWrapStyle.CalcSize(content).x + 1f);
                float h = promptStyle.CalcHeight(content, w);
                return new Rect(contentW - w, y + 2f, w, h);
            }
            case BlockKind.Tool:
            {
                float w = Mathf.Min(contentW, toolNoWrapStyle.CalcSize(content).x + 1f);
                float h = toolStyle.CalcHeight(content, w);
                return new Rect(0f, y, w, h);
            }
            default:
                return new Rect(0f, y, contentW, textStyle.CalcHeight(content, contentW));
        }
    }

    void DrawBlock(Block b, Rect br)
    {
        switch (b.kind)
        {
            case BlockKind.Prompt:
                PiUISprites.DrawRounded(br, 12, new Color(accentColor.r, accentColor.g, accentColor.b, 0.22f));
                GUI.Label(br, b.Text, promptStyle);
                break;

            case BlockKind.Tool:
            {
                Color tint = b.failed ? errorColor : accentColor;
                PiUISprites.DrawRounded(br, 8, b.failed ? new Color(errorColor.r, errorColor.g, errorColor.b, 0.10f) : surfaceColor);
                Texture2D icon = b.failed ? PiUISprites.IconCross(24) : PiUISprites.IconChevron(24);
                PiUISprites.DrawTinted(new Rect(br.x + 9f, br.y + 7f, 12f, 12f), icon, tint);
                GUI.Label(br, b.Text, toolStyle);
                break;
            }

            default:
                GUI.Label(br, b.Text, textStyle);
                break;
        }
    }

    void DrawFollowUp(Rect rect)
    {
        EnsureFollowUpStyles();
        Event e = Event.current;
        bool focused = GUI.GetNameOfFocusedControl() == FollowUpControl;

        // Background + focus ring.
        if (followUpMask == null) followUpMask = new MouseHandler.SquircleMask();
        float radius = Mathf.Min(cornerRadius - 4f, rect.height * 0.5f);
        followUpMask.Ensure(rect.size, radius, squircleExponent);
        Color ring = focused ? new Color(accentColor.r, accentColor.g, accentColor.b, 0.55f) : borderColor;
        PiUISprites.DrawTinted(new Rect(rect.x - 1f, rect.y - 1f, rect.width + 2f, rect.height + 2f), followUpMask.Texture, ring);
        PiUISprites.DrawTinted(rect, followUpMask.Texture, new Color(0.11f, 0.115f, 0.135f, 1f));

        // Send button on the right.
        Rect send = new Rect(rect.xMax - rect.height + 6f, rect.y + 6f, rect.height - 12f, rect.height - 12f);
        bool canSend = !string.IsNullOrWhiteSpace(followUp);
        bool sendHover = canSend && send.Contains(e.mousePosition);
        Color sendBg = canSend ? (sendHover ? Color.Lerp(accentColor, Color.white, 0.15f) : accentColor) : surfaceColor;
        PiUISprites.DrawTinted(send, PiUISprites.Circle(64), sendBg);
        Matrix4x4 prevMatrix = GUI.matrix;
        GUIUtility.RotateAroundPivot(-90f, send.center); // chevron points up = "send"
        PiUISprites.DrawTinted(new Rect(send.center.x - 6f, send.center.y - 6f, 12f, 12f), PiUISprites.IconChevron(24),
                               canSend ? Color.white : mutedColor);
        GUI.matrix = prevMatrix;

        bool submit = false;
        if (canSend && GUI.Button(send, GUIContent.none, GUIStyle.none)) submit = true;

        // Enter sends, Escape unfocuses. Handle before the TextField so it can't swallow the key.
        if (focused && e.type == EventType.KeyDown)
        {
            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) { submit = canSend; e.Use(); }
            else if (e.keyCode == KeyCode.Escape) { GUIUtility.keyboardControl = 0; e.Use(); }
            else if (e.character == '\n' || e.character == '\r') e.Use();
        }

        // Clicking anywhere outside the box drops focus so game hotkeys (Space to unpause) work again.
        if (focused && e.type == EventType.MouseDown && !rect.Contains(e.mousePosition))
            GUIUtility.keyboardControl = 0;

        Rect field = new Rect(rect.x, rect.y, rect.width - rect.height + 4f, rect.height);
        Color prevCursor = GUI.skin.settings.cursorColor;
        GUI.skin.settings.cursorColor = textColor;
        GUI.SetNextControlName(FollowUpControl);
        followUp = GUI.TextField(field, followUp ?? "", followUpStyle);
        GUI.skin.settings.cursorColor = prevCursor;

        if (string.IsNullOrEmpty(followUp))
            GUI.Label(field, followUpPlaceholder, followUpPlaceholderStyle);

        if (submit) SubmitFollowUp();
    }

    void SubmitFollowUp()
    {
        string text = followUp?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        var sender = GetComponent<MouseHandler>();
        if (sender == null) sender = FindAnyObjectByType<MouseHandler>();
        if (sender == null) { status = "no MouseHandler to send with"; return; }
        sender.SendFollowUp(text, contextJson);
        followUp = "";
        status = "sending...";
        busy = true;
        stickToBottom = true;
    }

    void EnsureFollowUpStyles()
    {
        if (followUpStyle != null && followUpStyle.fontSize == fontSize) return;
        followUpStyle = new GUIStyle(GUI.skin.textField)
        {
            alignment = TextAnchor.MiddleLeft,
            fontSize = fontSize,
            wordWrap = false,
            clipping = TextClipping.Clip,
            padding = new RectOffset(14, 8, 0, 0),
            border = new RectOffset(0, 0, 0, 0),
            margin = new RectOffset(0, 0, 0, 0),
        };
        foreach (GUIStyleState s in new[] { followUpStyle.normal, followUpStyle.hover, followUpStyle.active, followUpStyle.focused,
                                             followUpStyle.onNormal, followUpStyle.onHover, followUpStyle.onActive, followUpStyle.onFocused })
        {
            s.background = null;
            s.scaledBackgrounds = null;
            s.textColor = textColor;
        }
        followUpPlaceholderStyle = new GUIStyle(followUpStyle);
        followUpPlaceholderStyle.normal.textColor = new Color(mutedColor.r, mutedColor.g, mutedColor.b, 0.7f);
    }

    void DrawTypingIndicator(Vector2 origin)
    {
        float t = Time.realtimeSinceStartup * 4f;
        for (int i = 0; i < 3; i++)
        {
            float a = 0.25f + 0.75f * Mathf.Clamp01(0.5f + 0.5f * Mathf.Sin(t - i * 0.9f));
            PiUISprites.DrawTinted(new Rect(origin.x + i * 10f, origin.y - 3f, 6f, 6f), PiUISprites.Circle(12),
                                   new Color(mutedColor.r, mutedColor.g, mutedColor.b, a));
        }
    }

    void GetStatusVisual(out string label, out Color dot, out bool pulse)
    {
        pulse = false;
        if (busy || status == "working" || status == "sending...")
        {
            label = status == "sending..." ? "Sending" : "Working";
            dot = accentColor;
            pulse = true;
        }
        else if (status == "done") { label = "Done"; dot = successColor; }
        else if (status == "aborted") { label = "Stopped"; dot = warningColor; }
        else if (string.IsNullOrEmpty(status)) { label = "Idle"; dot = mutedColor; }
        else { label = char.ToUpper(status[0]) + status.Substring(1); dot = errorColor; }
    }

    void EnsureStyles()
    {
        if (textStyle != null && stylesFontSize == fontSize) return;
        stylesFontSize = fontSize;

        GUIStyle Make(Color c, int size, FontStyle fs, bool wrap, RectOffset padding, TextAnchor align = TextAnchor.UpperLeft)
        {
            var s = new GUIStyle(GUI.skin.label)
            {
                fontSize = size,
                fontStyle = fs,
                wordWrap = wrap,
                richText = false,
                alignment = align,
                padding = padding,
                margin = new RectOffset(0, 0, 0, 0),
                border = new RectOffset(0, 0, 0, 0),
            };
            s.normal.background = null;
            s.normal.textColor = c;
            return s;
        }

        textStyle = Make(textColor, fontSize, FontStyle.Normal, true, new RectOffset(6, 6, 2, 2));
        promptStyle = Make(new Color(0.95f, 0.95f, 1f, 1f), fontSize, FontStyle.Normal, true, new RectOffset(12, 12, 8, 8));
        promptNoWrapStyle = new GUIStyle(promptStyle) { wordWrap = false };
        toolStyle = Make(mutedColor, fontSize - 2, FontStyle.Normal, true, new RectOffset(28, 10, 6, 6));
        toolNoWrapStyle = new GUIStyle(toolStyle) { wordWrap = false };
        titleStyle = Make(Color.white, fontSize, FontStyle.Bold, false, new RectOffset(0, 0, 0, 1), TextAnchor.MiddleCenter);
        statusStyle = Make(textColor, fontSize - 2, FontStyle.Normal, false, new RectOffset(0, 0, 0, 0), TextAnchor.MiddleLeft);
        emptyStyle = Make(mutedColor, fontSize - 1, FontStyle.Italic, true, new RectOffset(6, 6, 6, 6), TextAnchor.UpperLeft);
    }
}
