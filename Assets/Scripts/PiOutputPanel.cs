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
    public int fontSize = 14;

    [Tooltip("Oldest output is dropped once the text exceeds this many characters (IMGUI limit).")]
    public int maxChars = 12000;

    public Color backgroundColor = new Color(0.11f, 0.11f, 0.13f, 0.94f);
    public Color headerColor = new Color(0.93f, 0.93f, 0.95f, 1f);
    public Color textColor = new Color(0.88f, 0.88f, 0.90f, 1f);
    public Color toolColor = new Color(0.55f, 0.70f, 1f, 1f);
    public Color promptColor = new Color(0.45f, 0.85f, 0.60f, 1f);
    public Color dimColor = new Color(1f, 1f, 1f, 0.45f);
    public Color shadowColor = new Color(0f, 0f, 0f, 0.35f);

    public bool IsOpen => open;

    // ---- JSON shapes returned by the bridge ---------------------------------------
    [System.Serializable] class OutputChunk { public int seq; public string kind; public string text; }
    [System.Serializable] class OutputResponse { public bool ok; public int seq; public bool busy; public OutputChunk[] chunks; }
    [System.Serializable] class PromptResponse { public bool ok; public int seq; }

    enum BlockKind { Prompt, Text, Tool }

    class Block
    {
        public BlockKind kind;
        public readonly StringBuilder text = new StringBuilder();
    }

    readonly List<Block> blocks = new List<Block>();
    bool open;
    bool busy;
    string status = "";
    int lastSeq;
    Vector2 scroll;
    bool stickToBottom = true;
    Coroutine pollRoutine;

    readonly MouseHandler.SquircleMask mask = new MouseHandler.SquircleMask();
    GUIStyle headerStyle, textStyle, toolStyle, promptStyle, closeStyle, statusStyle;
    int stylesFontSize = -1;

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
    }

    void AddBlock(BlockKind kind, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var b = new Block { kind = kind };
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
            blocks[0].text.Remove(0, total - maxChars);
    }

    // ---- Drawing ------------------------------------------------------------------

    Rect PanelRect()
    {
        float w = Screen.width * widthFraction;
        return new Rect(margin, margin, Mathf.Max(120f, w - margin * 1.5f), Screen.height - margin * 2f);
    }

    void OnGUI()
    {
        if (!open) return;
        EnsureStyles();

        Rect r = PanelRect();
        mask.Ensure(r.size, cornerRadius, squircleExponent);

        Color prev = GUI.color;
        GUI.color = shadowColor;
        GUI.DrawTexture(new Rect(r.x, r.y + 3f, r.width, r.height), mask.Texture);
        GUI.color = backgroundColor;
        GUI.DrawTexture(r, mask.Texture);
        GUI.color = Color.white;

        const float pad = 16f;
        const float headerH = 30f;

        // Header: title, status, close button.
        Rect header = new Rect(r.x + pad, r.y + 10f, r.width - pad * 2f, headerH);
        GUI.Label(new Rect(header.x, header.y, 40f, headerH), "pi", headerStyle);
        string statusText = busy ? "working" + new string('.', 1 + (int)(Time.realtimeSinceStartup * 2f) % 3) : status;
        GUI.Label(new Rect(header.x + 32f, header.y, header.width - 70f, headerH), statusText, statusStyle);
        if (GUI.Button(new Rect(header.xMax - 26f, header.y + 2f, 26f, 26f), "x", closeStyle))
        {
            Close();
            GUI.color = prev;
            return;
        }

        // Divider.
        GUI.color = new Color(1f, 1f, 1f, 0.08f);
        GUI.DrawTexture(new Rect(r.x + pad, header.yMax + 6f, r.width - pad * 2f, 1f), Texture2D.whiteTexture);
        GUI.color = Color.white;

        // Body: scrollable output.
        Rect body = new Rect(r.x + pad * 0.5f, header.yMax + 12f, r.width - pad, r.yMax - header.yMax - 12f - pad * 0.75f);
        float contentW = body.width - 18f; // leave room for scrollbar
        float contentH = 0f;
        var heights = new float[blocks.Count];
        for (int i = 0; i < blocks.Count; i++)
        {
            heights[i] = StyleFor(blocks[i].kind).CalcHeight(new GUIContent(blocks[i].text.ToString()), contentW);
            contentH += heights[i] + 6f;
        }

        float maxScroll = Mathf.Max(0f, contentH - body.height);
        if (stickToBottom) scroll.y = maxScroll;

        Vector2 newScroll = GUI.BeginScrollView(body, scroll, new Rect(0f, 0f, contentW, Mathf.Max(contentH, body.height)),
                                                false, false, GUIStyle.none, GUI.skin.verticalScrollbar);
        float y = 0f;
        for (int i = 0; i < blocks.Count; i++)
        {
            GUI.Label(new Rect(0f, y, contentW, heights[i]), blocks[i].text.ToString(), StyleFor(blocks[i].kind));
            y += heights[i] + 6f;
        }
        GUI.EndScrollView();

        // Re-stick when the user scrolls back to the bottom; unstick when they scroll up.
        if (!Mathf.Approximately(newScroll.y, scroll.y))
            stickToBottom = newScroll.y >= maxScroll - 4f;
        scroll = newScroll;

        GUI.color = prev;
    }

    GUIStyle StyleFor(BlockKind k) => k == BlockKind.Tool ? toolStyle : k == BlockKind.Prompt ? promptStyle : textStyle;

    void EnsureStyles()
    {
        if (textStyle != null && stylesFontSize == fontSize) return;
        stylesFontSize = fontSize;

        GUIStyle Make(Color c, int size, FontStyle fs = FontStyle.Normal, bool wrap = true)
        {
            var s = new GUIStyle(GUI.skin.label)
            {
                fontSize = size,
                fontStyle = fs,
                wordWrap = wrap,
                richText = false,
                alignment = TextAnchor.UpperLeft,
                padding = new RectOffset(8, 8, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
            };
            s.normal.textColor = c;
            return s;
        }

        textStyle = Make(textColor, fontSize);
        toolStyle = Make(toolColor, fontSize - 1);
        promptStyle = Make(promptColor, fontSize, FontStyle.Bold);
        headerStyle = Make(headerColor, fontSize + 3, FontStyle.Bold, false);
        headerStyle.alignment = TextAnchor.MiddleLeft;
        headerStyle.padding = new RectOffset(0, 0, 0, 0);
        statusStyle = Make(dimColor, fontSize - 1, FontStyle.Normal, false);
        statusStyle.alignment = TextAnchor.MiddleLeft;

        closeStyle = Make(dimColor, fontSize + 2, FontStyle.Bold, false);
        closeStyle.alignment = TextAnchor.MiddleCenter;
        closeStyle.padding = new RectOffset(0, 0, 0, 0);
        closeStyle.hover.textColor = headerColor;
        closeStyle.active.textColor = toolColor;
    }
}
