using System.IO;
using UnityEditor;
using UnityEngine;

// Drives the regression checks from the editor.
//   - Pumps RegressionSuite runs forward every editor frame (works in Play mode and Edit mode).
//   - RunAll(): runs every check step by step, then the balance comparison right after.
//   - Watches Library/RegressionStatus/request.txt so the pi regression gate can ask for a
//     "baseline" or a "run" without going through the command line.
//
//   Menu:  Tools > Regression > Run All Checks
//   Agent: unity cmd script eval --code "return RegressionReporter.RunAll();"
[InitializeOnLoad]
public static class RegressionReporter
{
    static bool compareAfterRun;
    static double nextPoll;
    static double rerunAt; // re-run the checks shortly after an undo (gives Hot Reload time to patch)

    static double autoRunAt; // fresh results after every script reload / entering Play mode

    static RegressionReporter()
    {
        EditorApplication.update += Update;
        autoRunAt = EditorApplication.timeSinceStartup + 3.0; // let things settle first
        RegressionSuite.Finished += () =>
        {
            if (!compareAfterRun) return;
            compareAfterRun = false;
            BalanceSnapshot.Compare();
        };
    }

    static void Update()
    {
        double now = EditorApplication.timeSinceStartup;
        RegressionSuite.Step(now);

        // Re-check automatically after a reload, so the panel never shows results from older code.
        if (autoRunAt > 0 && now >= autoRunAt)
        {
            autoRunAt = 0;
            if (!RegressionSuite.IsRunning) RegressionSuite.StartRun(); // checks only (no balance compare)
        }

        if (rerunAt > 0 && now >= rerunAt)
        {
            rerunAt = 0;
            RunAll();
        }

        if (now < nextPoll) return;
        nextPoll = now + 0.2;
        HandleRequests();
    }

    // request.txt holds one word per line: "baseline" and/or "run".
    static void HandleRequests()
    {
        string path = Path.Combine(RegressionStatusFiles.Folder, "request.txt");
        if (!File.Exists(path)) return;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
            File.Delete(path);
        }
        catch (IOException) { return; } // still being written; try again next poll

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line == "baseline") BalanceSnapshot.SaveBaseline();
            else if (line == "run") RunAll();
            else if (line == "undo") Debug.Log(UndoLastAgentChange());
        }
    }

    [MenuItem("Tools/Regression/Undo Last Agent Change")]
    static void UndoMenu() => Debug.Log(UndoLastAgentChange());

    /// <summary>Puts back the files the last agent request changed, from the backups the pi regression gate
    /// made right before each edit, then re-runs the checks.</summary>
    public static string UndoLastAgentChange()
    {
        RegressionStatusFiles.Snapshot snap = RegressionStatusFiles.Load();
        if (!snap.CanUndo) return "Nothing to undo (no finished agent change with backups, or it was already reverted).";

        string root = Path.GetDirectoryName(Application.dataPath);
        string manifest = Path.Combine(root, snap.reviewManifest);
        if (!File.Exists(manifest)) return $"Can't undo: backup list not found ({snap.reviewManifest}).";

        int restored = 0, removed = 0;
        bool needsRecompile = false;
        foreach (string line in File.ReadAllLines(manifest))
        {
            string[] f = line.Split('\t');
            if (f.Length < 2 || f[0].Trim().Length == 0) continue;
            string relative = f[0].Trim().Replace('\\', '/');
            string original = Path.Combine(root, relative);

            if (f[1].Trim() == "NEW")
            {
                // The agent created this file: remove it (and its .meta).
                if (File.Exists(original)) { File.Delete(original); removed++; }
                if (File.Exists(original + ".meta")) File.Delete(original + ".meta");
                if (relative.EndsWith(".cs")) needsRecompile = true; // removing a class needs a full recompile
            }
            else
            {
                string backup = Path.Combine(root, f[1].Trim());
                if (!File.Exists(backup)) continue;
                File.Copy(backup, original, true);
                restored++;
            }

            // Scripts are picked up by Hot Reload; other assets need re-importing.
            if (!relative.EndsWith(".cs") && relative.StartsWith("Assets/") && File.Exists(original))
                AssetDatabase.ImportAsset(relative);
        }

        string summary = $"Undone by you: restored {restored} file(s)" + (removed > 0 ? $", removed {removed} new file(s)" : "") +
                         (needsRecompile ? ". Stop Play mode and press Ctrl+R to finish" : "");
        RegressionStatusFiles.WriteReview("REVERTED", summary, snap.reviewStart);
        rerunAt = EditorApplication.timeSinceStartup + 2.5; // let Hot Reload patch the restored scripts, then re-check
        return summary + ". Re-running the regression checks...";
    }

    [MenuItem("Tools/Regression/Run All Checks")]
    static void RunAllMenu() => UnityEngine.Debug.Log(RunAll());

    /// <summary>Starts all regression checks (one by one, visible in the in-game panel and the
    /// Regression Monitor window), then compares balance values. Results land in
    /// Library/RegressionStatus/tests.txt and balance.txt a few seconds later.</summary>
    public static string RunAll()
    {
        if (RegressionSuite.IsRunning) return "Regression checks are already running.";
        compareAfterRun = true;
        RegressionSuite.StartRun();
        float seconds = RegressionSuite.Total * RegressionSuite.StepDelay * 1.25f + 1f;
        return $"Started {RegressionSuite.Total} regression checks; results in Library/RegressionStatus/tests.txt in about {seconds:0} seconds, then balance.txt.";
    }
}
