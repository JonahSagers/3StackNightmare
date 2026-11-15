using UnityEditor;
using UnityEngine;

// Window > Regression Monitor: the same progress as the in-game panel, but usable when the game
// isn't running (the in-game panel only exists in Play mode).
public class RegressionWindow : EditorWindow
{
    RegressionStatusFiles.Snapshot snap = new RegressionStatusFiles.Snapshot();
    Vector2 scroll;
    double nextRefresh;
    GUIStyle rich;

    static readonly Color ColOk = new Color(0.35f, 0.85f, 0.45f);
    static readonly Color ColError = new Color(1f, 0.4f, 0.4f);
    static readonly Color ColInfo = new Color(0.45f, 0.75f, 1f);
    static readonly Color ColWarn = new Color(1f, 0.75f, 0.25f);

    [MenuItem("Window/Regression Monitor")]
    [MenuItem("Tools/Regression/Open Monitor Window")]
    static void Open() => GetWindow<RegressionWindow>("Regression Monitor").Show();

    void Update()
    {
        bool busy = snap.runActive || (snap.reviewState != "" && !snap.ReviewFinal);
        if (EditorApplication.timeSinceStartup < nextRefresh) return;
        nextRefresh = EditorApplication.timeSinceStartup + (busy ? 0.1 : 0.5);
        snap = RegressionStatusFiles.Load();
        Repaint();
    }

    static string C(string text, Color c) => $"<color=#{ColorUtility.ToHtmlStringRGB(c)}>{text}</color>";

    void OnGUI()
    {
        rich ??= new GUIStyle(EditorStyles.label) { richText = true, wordWrap = true };

        EditorGUILayout.Space(4);
        using (new EditorGUILayout.HorizontalScope())
        {
            GUI.enabled = !RegressionSuite.IsRunning;
            if (GUILayout.Button("Run All Checks", GUILayout.Height(24))) RegressionReporter.RunAll();
            GUI.enabled = true;
            if (GUILayout.Button("Save Balance Baseline", GUILayout.Height(24))) BalanceSnapshot.SaveBaseline();
        }

        // Agent review pipeline
        RegressionStatusFiles.Pipeline(snap, out int step, out string label, out float progress);
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Agent Review", EditorStyles.boldLabel);
        if (step == 0) EditorGUILayout.LabelField("No agent review yet.");
        else
        {
            string text = snap.ReviewFinal ? $"{snap.reviewState}: {snap.reviewText}" : $"Step {step}/{RegressionStatusFiles.PipelineSteps}: {label}";
            EditorGUI.ProgressBar(EditorGUILayout.GetControlRect(false, 20), progress, text);
        }

        // Regression checks
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Regression Checks", EditorStyles.boldLabel);
        int total = snap.checks.Count;
        if (total == 0) EditorGUILayout.LabelField("Not run yet.");
        else
        {
            string barText = snap.runActive
                ? $"Running {snap.checksDone}/{total}: {snap.currentCheck}"
                : snap.checksFailed > 0 ? $"{snap.checksFailed} failed, {total - snap.checksFailed} passed" : $"All {total} passed";
            EditorGUI.ProgressBar(EditorGUILayout.GetControlRect(false, 20), (float)snap.checksDone / total, barText);

            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MinHeight(160));
            foreach (RegressionSuite.Result r in snap.checks)
            {
                string line =
                    r.state == RegressionSuite.State.Passed ? C("PASS  ", ColOk) + r.name :
                    r.state == RegressionSuite.State.Failed ? C("FAIL  ", ColError) + r.name + C($"  {r.message}", ColError) :
                    r.state == RegressionSuite.State.Running ? C("RUN   ", ColInfo) + C(r.name, ColInfo) :
                    C("....  " + r.name, new Color(1f, 1f, 1f, 0.4f));
                EditorGUILayout.LabelField(line, rich);
            }
            EditorGUILayout.EndScrollView();
        }

        // Balance
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Balance", EditorStyles.boldLabel);
        if (snap.balanceTime <= 0 || snap.noBaseline) EditorGUILayout.LabelField("No baseline yet.");
        else if (snap.changes.Count == 0) EditorGUILayout.LabelField(C("No values changed since the baseline.", ColOk), rich);
        else
            foreach (RegressionStatusFiles.Change c in snap.changes)
                EditorGUILayout.LabelField(C($"{c.objectPath} {c.field}: {c.oldValue} -> {c.newValue}", ColWarn), rich);
    }
}
