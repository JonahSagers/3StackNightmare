using UnityEditor;

// Drives the regression checks from the editor.
//   - Pumps RegressionSuite runs forward every editor frame (works in Play mode and Edit mode).
//   - RunAll(): runs every check step by step, then the balance comparison right after.
//
//   Menu:  Tools > Regression > Run All Checks
//   Agent: unity cmd script eval --code "return RegressionReporter.RunAll();"
[InitializeOnLoad]
public static class RegressionReporter
{
    static bool compareAfterRun;

    static RegressionReporter()
    {
        EditorApplication.update += () => RegressionSuite.Step(EditorApplication.timeSinceStartup);
        RegressionSuite.Finished += () =>
        {
            if (!compareAfterRun) return;
            compareAfterRun = false;
            BalanceSnapshot.Compare();
        };
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
