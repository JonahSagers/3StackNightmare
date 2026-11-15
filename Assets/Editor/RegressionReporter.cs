using System.IO;
using UnityEditor;

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

    static RegressionReporter()
    {
        EditorApplication.update += Update;
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
        }
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
