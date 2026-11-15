using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// In-game regression panel (editor only; spawns itself in Play mode, top-right).
//
//   AGENT REVIEW       the reviewer agent's progress through its 6 steps, with a progress bar and the verdict
//   REGRESSION CHECKS  which check is running right now, a progress bar, and every check with a pass/fail icon
//   LIVE RULES         sanity checks on the running game every half second (bad values, duplicate keybinds...)
//   BALANCE            values changed since the baseline
// Problems also get a floating label over the object they belong to.
//
// Keys: F1 collapse/expand · F2 show/hide the list of checks · F5 run the checks now.
public class RegressionHUD : MonoBehaviour
{
    enum Severity { Ok, Warning, Error }
    enum Icon { None, Pass, Fail, Running, Pending }

    struct Issue { public Severity severity; public string text; public GameObject target; }

    struct Row
    {
        public string text;
        public Icon icon;
        public float bar;      // >= 0: draw a progress bar on this row (0..1)
        public Color barColor;
        public float indent;
    }

    const float ShowListAfterRunSeconds = 8f;

    RegressionStatusFiles.Snapshot snap = new RegressionStatusFiles.Snapshot();
    readonly List<Issue> liveIssues = new List<Issue>();
    readonly List<Issue> balanceIssues = new List<Issue>();
    int liveChecksRun;

    bool expanded = true;
    bool listOpen;
    bool wasRunning;
    float listUntil;
    float nextRefresh;

    GUIStyle labelStyle, smallStyle, headerStyle;

    static readonly Color ColOk = new Color(0.35f, 0.85f, 0.45f);
    static readonly Color ColWarn = new Color(1f, 0.75f, 0.25f);
    static readonly Color ColError = new Color(1f, 0.35f, 0.35f);
    static readonly Color ColInfo = new Color(0.45f, 0.75f, 1f);
    static readonly Color ColPanel = new Color(0.08f, 0.09f, 0.12f, 0.92f);
    static readonly Color ColTrack = new Color(1f, 1f, 1f, 0.1f);
    static readonly Color ColDim = new Color(0.7f, 0.75f, 0.85f);

#if UNITY_EDITOR
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoSpawn()
    {
        if (FindAnyObjectByType<RegressionHUD>() == null)
            new GameObject("RegressionHUD").AddComponent<RegressionHUD>();
    }
#endif

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && !PiUISprites.IsTyping)
        {
            if (kb.f1Key.wasPressedThisFrame) expanded = !expanded;
            if (kb.f2Key.wasPressedThisFrame) listOpen = !listOpen;
            if (kb.f5Key.wasPressedThisFrame && !RegressionSuite.IsRunning) RegressionSuite.StartRun();
        }

        // Refresh fast while something is moving, slower otherwise.
        bool busy = snap.runActive || (snap.reviewState != "" && !snap.ReviewFinal);
        if (Time.unscaledTime < nextRefresh) return;
        nextRefresh = Time.unscaledTime + (busy ? 0.1f : 0.5f);

        snap = RegressionStatusFiles.Load();
        if (wasRunning && !snap.runActive) listUntil = Time.unscaledTime + ShowListAfterRunSeconds; // keep the list up briefly after a run
        wasRunning = snap.runActive;

        balanceIssues.Clear();
        foreach (RegressionStatusFiles.Change c in snap.changes)
            balanceIssues.Add(new Issue
            {
                severity = Severity.Warning,
                text = $"{c.field}: {c.oldValue} -> {c.newValue}",
                target = c.objectPath.Length > 0 ? GameObject.Find(c.objectPath) : null,
            });

        RunLiveChecks();
    }

    // ---- Live rule checks ------------------------------------------------------------

    void Check(bool broken, Severity severity, string text, GameObject target)
    {
        liveChecksRun++;
        if (broken) liveIssues.Add(new Issue { severity = severity, text = text, target = target });
    }

    void RunLiveChecks()
    {
        liveIssues.Clear();
        liveChecksRun = 0;

        foreach (Health h in FindObjectsByType<Health>())
        {
            if ((h.hideFlags & HideFlags.HideAndDontSave) != 0) continue; // regression check characters
            GameObject g = h.gameObject;
            Check(float.IsNaN(h.Current) || h.Current < 0f || h.Current > h.maxHealth + 0.01f, Severity.Error,
                $"{g.name}: health {h.Current:0}/{h.maxHealth:0} out of range", g);
            Check(h.maxHealth <= 0f, Severity.Error, $"{g.name}: max health is {h.maxHealth}", g);
            Check(h.startingHealth > h.maxHealth, Severity.Warning, $"{g.name}: starting health above max", g);
            Check(h.Shield < 0f || float.IsNaN(h.Shield), Severity.Error, $"{g.name}: shield is {h.Shield}", g);
        }

        foreach (PlayerMove player in FindObjectsByType<PlayerMove>())
        {
            Health h = player.GetComponent<Health>();
            Check(h != null && h.team != Team.Ally, Severity.Warning, $"{player.name} is on the Enemy team", player.gameObject);
            CheckKeybinds(player.gameObject);
        }

        foreach (Skillshot s in FindObjectsByType<Skillshot>())
        {
            string who = $"{s.name} {s.key}";
            Check(s.speed <= 0f || s.range <= 0f, Severity.Error, $"{who}: speed/range must be above 0", s.gameObject);
            Check(s.amount < 0f, Severity.Error, $"{who}: negative {(s.effect == SkillEffect.Heal ? "heal" : "damage")}", s.gameObject);
            Check(s.cooldown < 0f, Severity.Error, $"{who}: negative cooldown", s.gameObject);
            Check(s.projectilePrefab == null, Severity.Warning, $"{who}: no projectile prefab (plain sphere)", s.gameObject);
        }

        foreach (LeapSlam l in FindObjectsByType<LeapSlam>())
            Check(l.damage < 0f || l.radius <= 0f || l.airTime <= 0f, Severity.Error, $"{l.name} {l.key}: leap damage/radius/air time invalid", l.gameObject);

        foreach (AutoAttack a in FindObjectsByType<AutoAttack>())
            Check(a.range <= 0f || a.attackInterval <= 0f || a.damage < 0f, Severity.Error, $"{a.name}: auto attack range/interval/damage invalid", a.gameObject);

        foreach (EnemyShooter e in FindObjectsByType<EnemyShooter>())
        {
            Check(e.fireInterval <= 0f, Severity.Error, $"{e.name}: fire interval must be above 0", e.gameObject);
            Check(e.damage < 0f, Severity.Error, $"{e.name}: negative damage", e.gameObject);
            Check(e.target == null, Severity.Warning, $"{e.name}: has no target", e.gameObject);
        }
    }

    // Two abilities on the same key (the bug where Q fired the heal orb too).
    void CheckKeybinds(GameObject player)
    {
        var seen = new Dictionary<Key, string>();
        void Add(Key key, string ability)
        {
            if (seen.TryGetValue(key, out string other)) Check(true, Severity.Error, $"{key} is bound to both {other} and {ability}", player);
            else { seen[key] = ability; liveChecksRun++; }
        }
        foreach (Skillshot s in player.GetComponents<Skillshot>()) Add(s.key, s.effect == SkillEffect.Heal ? "Heal" : "Shot");
        foreach (LeapSlam l in player.GetComponents<LeapSlam>()) Add(l.key, "Leap");
        foreach (ShieldAbility a in player.GetComponents<ShieldAbility>()) Add(a.key, "Shield");
        foreach (FullHealKey f in player.GetComponents<FullHealKey>()) Add(f.key, "Full Heal");
    }

    // ---- Status ----------------------------------------------------------------------

    Severity ReviewSeverity() =>
        snap.reviewState == "FAIL" || snap.reviewState == "ERROR" ? Severity.Error :
        snap.reviewState == "REVERTED" ? Severity.Warning : Severity.Ok;

    Severity Overall()
    {
        if (snap.testsFailed > 0 || snap.checksFailed > 0 || ReviewSeverity() == Severity.Error) return Severity.Error;
        foreach (Issue i in liveIssues) if (i.severity == Severity.Error) return Severity.Error;
        if (liveIssues.Count > 0 || balanceIssues.Count > 0 || ReviewSeverity() == Severity.Warning) return Severity.Warning;
        return Severity.Ok;
    }

    bool Working => snap.runActive || (snap.reviewState != "" && !snap.ReviewFinal);

    static Color ColorOf(Severity s) => s == Severity.Error ? ColError : s == Severity.Warning ? ColWarn : ColOk;
    static string Colored(string text, Color c) => $"<color=#{ColorUtility.ToHtmlStringRGB(c)}>{text}</color>";
    static string Trim(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "...";

    static string Ago(long unixTime)
    {
        if (unixTime <= 0) return "never";
        long s = RegressionStatusFiles.Now() - unixTime;
        return s < 60 ? "just now" : s < 3600 ? $"{s / 60}m ago" : $"{s / 3600}h ago";
    }

    static Severity WorstOf(List<Issue> issues)
    {
        Severity worst = Severity.Ok;
        foreach (Issue i in issues) if (i.severity > worst) worst = i.severity;
        return worst;
    }

    // ---- Building the panel ----------------------------------------------------------

    static Row Text(string text, float indent = 0f) => new Row { text = text, icon = Icon.None, bar = -1f, indent = indent };
    static Row Bar(float fraction, Color color) => new Row { text = null, bar = Mathf.Clamp01(fraction), barColor = color };

    List<Row> BuildRows()
    {
        var rows = new List<Row>();

        // AGENT REVIEW: the reviewer pipeline, step by step
        RegressionStatusFiles.Pipeline(snap, out int step, out string stepLabel, out float pipelineProgress);
        if (step > 0)
        {
            if (!snap.ReviewFinal)
            {
                rows.Add(Text($"{Colored("AGENT REVIEW", ColDim)}  {Colored($"Step {step}/{RegressionStatusFiles.PipelineSteps}", ColInfo)}  {stepLabel}"));
                rows.Add(Bar(pipelineProgress, ColInfo));
            }
            else
            {
                Color vc = ColorOf(ReviewSeverity());
                rows.Add(Text($"{Colored("AGENT REVIEW", ColDim)}  {Colored(snap.reviewState, vc)}  {Colored(Ago(snap.reviewTime), ColDim)}"));
                rows.Add(Bar(1f, vc));
                if (snap.reviewText.Length > 0) rows.Add(Text(Colored(Trim(snap.reviewText, 62), vc), 6f));
            }
        }

        // REGRESSION CHECKS: progress + which check is running
        int total = snap.checks.Count;
        if (snap.runActive && total > 0)
        {
            rows.Add(Text($"{Colored("REGRESSION CHECKS", ColDim)}  {Colored($"running {snap.checksDone}/{total}", ColInfo)}" +
                          (snap.checksFailed > 0 ? $"  {Colored($"{snap.checksFailed} failed", ColError)}" : "")));
            rows.Add(Bar((float)snap.checksDone / total, snap.checksFailed > 0 ? ColError : ColInfo));
            if (snap.currentCheck.Length > 0) rows.Add(Text($"{Colored("now:", ColDim)} {snap.currentCheck}", 6f));
        }
        else if (snap.testsTime > 0)
        {
            string result = snap.testsFailed > 0
                ? $"{Colored($"{snap.testsFailed} failed", ColError)}, {snap.testsPassed} passed"
                : Colored($"{snap.testsPassed}/{snap.testsPassed} passed", ColOk);
            rows.Add(Text($"{Colored("REGRESSION CHECKS", ColDim)}  {result}  {Colored(Ago(snap.testsTime), ColDim)}"));
        }
        else rows.Add(Text($"{Colored("REGRESSION CHECKS", ColDim)}  {Colored("not run yet: press F5", ColWarn)}"));

        // The list of every check with its state
        bool showList = total > 0 && (snap.runActive || listOpen || Time.unscaledTime < listUntil);
        if (showList)
        {
            // Too many to show at once: a window around the check that's running (or the last ones),
            // and failures always stay visible.
            const int MaxListRows = 14;
            int focus = snap.checks.Count - 1;
            for (int i = 0; i < snap.checks.Count; i++)
                if (snap.checks[i].state == RegressionSuite.State.Running || snap.checks[i].state == RegressionSuite.State.Pending) { focus = i; break; }
            int first = Mathf.Clamp(focus - MaxListRows / 2, 0, Mathf.Max(0, snap.checks.Count - MaxListRows));
            int last = Mathf.Min(snap.checks.Count, first + MaxListRows);
            if (first > 0) rows.Add(Text(Colored($"... {first} earlier check{(first == 1 ? "" : "s")}", ColDim), 28f));

            for (int index = 0; index < snap.checks.Count; index++)
            {
                RegressionSuite.Result r = snap.checks[index];
                bool inWindow = index >= first && index < last;
                if (!inWindow && r.state != RegressionSuite.State.Failed) continue;
                Row row = Text(r.name, 6f);
                switch (r.state)
                {
                    case RegressionSuite.State.Passed: row.icon = Icon.Pass; row.text = Colored(r.name, ColOk); break;
                    case RegressionSuite.State.Failed: row.icon = Icon.Fail; row.text = Colored(Trim($"{r.name}: {r.message}", 60), ColError); break;
                    case RegressionSuite.State.Running: row.icon = Icon.Running; row.text = Colored(r.name, ColInfo); break;
                    default: row.icon = Icon.Pending; row.text = Colored(r.name, new Color(1f, 1f, 1f, 0.35f)); break;
                }
                rows.Add(row);
            }
            if (last < snap.checks.Count) rows.Add(Text(Colored($"... {snap.checks.Count - last} more", ColDim), 28f));
        }
        else
        {
            // Not showing the full list: still call out failures.
            int shown = 0;
            foreach (RegressionSuite.Result r in snap.checks)
                if (r.state == RegressionSuite.State.Failed && shown++ < 4)
                    rows.Add(new Row { text = Colored(Trim($"{r.name}: {r.message}", 60), ColError), icon = Icon.Fail, bar = -1f, indent = 6f });
        }

        // LIVE RULES
        if (liveIssues.Count == 0) rows.Add(Text($"{Colored("LIVE RULES", ColDim)}  {Colored($"{liveChecksRun} checks OK", ColOk)}"));
        else
        {
            rows.Add(Text($"{Colored("LIVE RULES", ColDim)}  {Colored($"{liveIssues.Count} issue{(liveIssues.Count == 1 ? "" : "s")}", ColorOf(WorstOf(liveIssues)))}"));
            for (int i = 0; i < liveIssues.Count && i < 4; i++)
                rows.Add(new Row { text = Colored(Trim(liveIssues[i].text, 60), ColorOf(liveIssues[i].severity)), icon = liveIssues[i].severity == Severity.Error ? Icon.Fail : Icon.Running, bar = -1f, indent = 6f });
        }

        // BALANCE
        if (snap.balanceTime <= 0 || snap.noBaseline) rows.Add(Text($"{Colored("BALANCE", ColDim)}  {Colored("no baseline yet", ColDim)}"));
        else if (balanceIssues.Count == 0) rows.Add(Text($"{Colored("BALANCE", ColDim)}  {Colored("no values changed", ColOk)}  {Colored(Ago(snap.balanceTime), ColDim)}"));
        else
        {
            rows.Add(Text($"{Colored("BALANCE", ColDim)}  {Colored($"{balanceIssues.Count} value{(balanceIssues.Count == 1 ? "" : "s")} changed", ColWarn)}  {Colored(Ago(snap.balanceTime), ColDim)}"));
            for (int i = 0; i < balanceIssues.Count && i < 4; i++)
            {
                string who = balanceIssues[i].target != null ? balanceIssues[i].target.name + " " : "";
                rows.Add(Text(Colored("~ " + Trim(who + balanceIssues[i].text, 58), ColWarn), 6f));
            }
        }

        return rows;
    }

    // ---- Drawing ---------------------------------------------------------------------

    void MakeStyles()
    {
        if (labelStyle != null) return;
        labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 17, richText = true, wordWrap = false, clipping = TextClipping.Clip };
        labelStyle.normal.textColor = Color.white;
        smallStyle = new GUIStyle(labelStyle) { fontSize = 15 };
        headerStyle = new GUIStyle(labelStyle) { fontSize = 19, fontStyle = FontStyle.Bold };
    }

    void OnGUI()
    {
        MakeStyles();
        GUI.matrix = PiUISprites.GuiMatrix;
        GUI.depth = -500;

        DrawObjectLabels();
        DrawPanel();
    }

    void DrawPanel()
    {
        Severity overall = Overall();
        int problems = snap.checksFailed > 0 ? snap.checksFailed : snap.testsFailed;
        if (ReviewSeverity() == Severity.Error) problems++;
        foreach (Issue i in liveIssues) if (i.severity == Severity.Error) problems++;

        string title = Working ? "Checking..." :
            overall == Severity.Ok ? "All checks passing" :
            problems > 0 ? $"{problems} problem{(problems == 1 ? "" : "s")} found" : "Needs attention";
        Color headColor = Working ? ColInfo : ColorOf(overall);

        List<Row> rows = expanded ? BuildRows() : new List<Row>();

        const float width = 500f, pad = 14f, headerH = 30f, lineH = 22f, barH = 12f, footerH = 22f;
        float contentH = 0f;
        foreach (Row r in rows) contentH += r.text == null ? barH + 6f : lineH;
        float height = pad * 2f + headerH + (expanded ? contentH + 8f + footerH : 0f);
        var panel = new Rect(PiUISprites.GuiWidth - width - 20f, 20f, width, height);

        PiUISprites.DrawShadow(panel, 12, 10, new Color(0, 0, 0, 0.35f));
        PiUISprites.DrawRounded(panel, 12, ColPanel);
        PiUISprites.DrawRounded(new Rect(panel.x, panel.y, 6f, panel.height), 3, headColor); // status stripe

        // Header
        var icon = new Rect(panel.x + pad + 4f, panel.y + pad + 3f, 24f, 24f);
        if (Working) DrawSpinner(icon, ColInfo);
        else
        {
            PiUISprites.DrawTinted(icon, PiUISprites.Circle(48), headColor);
            PiUISprites.DrawTinted(icon, overall == Severity.Error ? PiUISprites.IconCross(48) : PiUISprites.IconCheck(48), new Color(0.05f, 0.05f, 0.08f));
        }
        GUI.Label(new Rect(icon.xMax + 10f, panel.y + pad, width - 140f, headerH), title, headerStyle);
        GUI.Label(new Rect(panel.xMax - 90f, panel.y + pad + 3f, 80f, headerH), Colored(expanded ? "F1 hide" : "F1 show", ColDim), smallStyle);
        if (!expanded) return;

        float x = panel.x + pad + 6f, y = panel.y + pad + headerH + 8f, inner = width - pad * 2f - 6f;
        foreach (Row r in rows)
        {
            if (r.text == null)
            {
                // progress bar row
                var track = new Rect(x, y + 2f, inner, barH);
                PiUISprites.DrawRounded(track, 5, ColTrack);
                if (r.bar > 0f) PiUISprites.DrawRounded(new Rect(track.x, track.y, Mathf.Max(barH, track.width * r.bar), barH), 5, r.barColor);
                y += barH + 6f;
                continue;
            }
            float tx = x + r.indent;
            if (r.icon != Icon.None)
            {
                DrawRowIcon(new Rect(tx, y + 3f, 16f, 16f), r.icon);
                tx += 22f;
            }
            GUI.Label(new Rect(tx, y, inner - (tx - x), lineH), r.text, r.icon != Icon.None ? smallStyle : labelStyle);
            y += lineH;
        }
        GUI.Label(new Rect(x, y + 2f, inner, footerH), Colored("F2 list of checks   F5 run checks", ColDim), smallStyle);
    }

    void DrawRowIcon(Rect r, Icon icon)
    {
        switch (icon)
        {
            case Icon.Pass:
                PiUISprites.DrawTinted(r, PiUISprites.Circle(32), ColOk);
                PiUISprites.DrawTinted(r, PiUISprites.IconCheck(32), new Color(0.05f, 0.05f, 0.08f));
                break;
            case Icon.Fail:
                PiUISprites.DrawTinted(r, PiUISprites.Circle(32), ColError);
                PiUISprites.DrawTinted(r, PiUISprites.IconCross(32), new Color(0.05f, 0.05f, 0.08f));
                break;
            case Icon.Running:
                DrawSpinner(r, ColInfo);
                break;
            case Icon.Pending:
                PiUISprites.DrawTinted(new Rect(r.x + 4f, r.y + 4f, r.width - 8f, r.height - 8f), PiUISprites.Circle(32), new Color(1f, 1f, 1f, 0.25f));
                break;
        }
    }

    // A pulsing dot: cheap "working" indicator.
    static void DrawSpinner(Rect r, Color c)
    {
        float pulse = 0.55f + 0.45f * Mathf.Sin(Time.unscaledTime * 8f);
        float inset = r.width * 0.5f * (1f - pulse) * 0.6f;
        PiUISprites.DrawTinted(new Rect(r.x + inset, r.y + inset, r.width - inset * 2f, r.height - inset * 2f), PiUISprites.Circle(48), c);
    }

    // Floating label above each object with a live issue or a changed value.
    void DrawObjectLabels()
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        var byObject = new Dictionary<GameObject, List<Issue>>();
        foreach (Issue i in liveIssues) AddLabel(byObject, i);
        foreach (Issue i in balanceIssues) AddLabel(byObject, i);

        foreach (KeyValuePair<GameObject, List<Issue>> kv in byObject)
        {
            Vector3 screen = cam.WorldToScreenPoint(kv.Key.transform.position + Vector3.up * 2.9f);
            if (screen.z < 0f) continue;
            Vector2 gui = PiUISprites.ScreenToGUI(screen);

            float h = 10f + kv.Value.Count * 21f, w = 0f;
            foreach (Issue i in kv.Value) w = Mathf.Max(w, smallStyle.CalcSize(new GUIContent(Trim(i.text, 48))).x);
            w += 34f;
            var box = new Rect(gui.x - w / 2f, gui.y - h, w, h);

            PiUISprites.DrawRounded(box, 8, new Color(0.08f, 0.09f, 0.12f, 0.9f));
            PiUISprites.DrawRounded(new Rect(box.x, box.y, box.width, 3f), 1, ColorOf(WorstOf(kv.Value)));
            float y = box.y + 5f;
            foreach (Issue i in kv.Value)
            {
                string mark = i.severity == Severity.Error ? "x" : "!";
                GUI.Label(new Rect(box.x + 10f, y, w, 21f), Colored(mark + " " + Trim(i.text, 48), ColorOf(i.severity)), smallStyle);
                y += 21f;
            }
        }
    }

    static void AddLabel(Dictionary<GameObject, List<Issue>> map, Issue issue)
    {
        if (issue.target == null) return;
        if (!map.TryGetValue(issue.target, out List<Issue> list)) map[issue.target] = list = new List<Issue>();
        if (list.Count < 3) list.Add(issue);
    }
}
