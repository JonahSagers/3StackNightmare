using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

// Small text files in Library/RegressionStatus that connect the pieces (Unity resets its memory when
// entering Play mode, and the reviewer agent is a separate process, so files are the shared channel):
//
//   progress.txt  the current/last check run, one line per check with its state  (RegressionSuite)
//   tests.txt     summary + failures of the last finished run                    (RegressionSuite)
//   balance.txt   values changed since the baseline                              (BalanceSnapshot)
//   review.txt    reviewer agent pipeline state + verdict                        (.pi/extensions/regression-gate.ts)
public static class RegressionStatusFiles
{
    public static string Folder => Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "RegressionStatus");

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();

    static void Write(string file, string text)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, file), text);
        }
        catch (Exception e) { Debug.LogWarning($"Regression status: couldn't write {file} ({e.Message})"); }
    }

    // ---- Writing ---------------------------------------------------------------------

    public static void WriteProgress(long runStart, List<RegressionSuite.Result> results, bool running)
    {
        var sb = new StringBuilder($"run\t{runStart}\t{results.Count}\t{(running ? "running" : "done")}\n");
        foreach (RegressionSuite.Result r in results)
            sb.Append($"test\t{r.name}\t{r.state}\t{Clean(r.message)}\n");
        Write("progress.txt", sb.ToString());
    }

    public static void WriteTests(int passed, int failed, List<RegressionSuite.Result> results)
    {
        var sb = new StringBuilder($"summary\t{Now()}\t{passed}\t{failed}\t0\n");
        foreach (RegressionSuite.Result r in results)
            if (r.state == RegressionSuite.State.Failed) sb.Append($"fail\t{r.name}\t{Clean(r.message)}\n");
        Write("tests.txt", sb.ToString());
    }

    /// <summary>Asks the editor (RegressionReporter) to undo the last agent change. Used by the in-game button.</summary>
    public static void RequestUndo()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.AppendAllText(Path.Combine(Folder, "request.txt"), "undo\n");
        }
        catch (Exception e) { Debug.LogWarning($"Regression status: couldn't request undo ({e.Message})"); }
    }

    public static void WriteReview(string state, string text, long reviewStart, string manifest = "")
    {
        Write("review.txt", $"summary\t{Now()}\t{state}\t{Clean(text)}\t{reviewStart}\t{manifest}\n");
    }

    // ---- Reading ---------------------------------------------------------------------

    public class Change { public string objectPath, field, oldValue, newValue; }

    public class Snapshot
    {
        // progress.txt
        public long runStart;
        public bool runActive;
        public readonly List<RegressionSuite.Result> checks = new List<RegressionSuite.Result>();
        public int checksDone, checksFailed;
        public string currentCheck = "";
        // tests.txt
        public long testsTime;
        public int testsPassed, testsFailed;
        // balance.txt
        public long balanceTime;
        public bool noBaseline;
        public readonly List<Change> changes = new List<Change>();
        // review.txt
        public long reviewTime, reviewStart;
        public string reviewState = "", reviewText = "";
        public string reviewManifest = ""; // backups of the last agent change (for Undo)

        public bool ReviewFinal => reviewState == "PASS" || reviewState == "FAIL" || reviewState == "REVERTED" || reviewState == "ERROR" || reviewState == "INTERRUPTED";

        /// <summary>The last agent change can be undone: review finished, backups exist, not already reverted.</summary>
        public bool CanUndo => ReviewFinal && reviewState != "REVERTED" && reviewManifest.Length > 0;
    }

    static string[] Lines(string file)
    {
        string path = Path.Combine(Folder, file);
        try { return File.Exists(path) ? File.ReadAllLines(path) : new string[0]; }
        catch (IOException) { return new string[0]; } // being written right now; read again next time
    }

    public static Snapshot Load()
    {
        var s = new Snapshot();

        foreach (string line in Lines("progress.txt"))
        {
            string[] f = line.Split('\t');
            if (f[0] == "run" && f.Length >= 4)
            {
                long.TryParse(f[1], out s.runStart);
                s.runActive = f[3] == "running";
            }
            else if (f[0] == "test" && f.Length >= 3)
            {
                var r = new RegressionSuite.Result { name = f[1], message = f.Length >= 4 ? f[3] : "" };
                Enum.TryParse(f[2], out r.state);
                s.checks.Add(r);
                if (r.state == RegressionSuite.State.Passed || r.state == RegressionSuite.State.Failed) s.checksDone++;
                if (r.state == RegressionSuite.State.Failed) s.checksFailed++;
                if (r.state == RegressionSuite.State.Running) s.currentCheck = r.name;
            }
        }

        foreach (string line in Lines("tests.txt"))
        {
            string[] f = line.Split('\t');
            if (f[0] == "summary" && f.Length >= 4)
            {
                long.TryParse(f[1], out s.testsTime);
                int.TryParse(f[2], out s.testsPassed);
                int.TryParse(f[3], out s.testsFailed);
            }
        }

        foreach (string line in Lines("balance.txt"))
        {
            string[] f = line.Split('\t');
            if (f[0] == "summary" && f.Length >= 2)
            {
                long.TryParse(f[1], out s.balanceTime);
                s.noBaseline = f.Length >= 4 && f[3] == "nobaseline";
            }
            else if (f[0] == "change" && f.Length >= 5)
                s.changes.Add(new Change { objectPath = f[1], field = f[2], oldValue = f[3], newValue = f[4] });
        }

        foreach (string line in Lines("review.txt"))
        {
            string[] f = line.Split('\t');
            if (f[0] != "summary" || f.Length < 4) continue;
            long.TryParse(f[1], out s.reviewTime);
            s.reviewState = f[2];
            s.reviewText = f[3];
            if (f.Length >= 5) long.TryParse(f[4], out s.reviewStart);
            s.reviewManifest = f.Length >= 6 ? f[5].Trim() : "";
        }

        return s;
    }

    // ---- Reviewer pipeline steps (shared by the in-game panel and the editor window) ----------

    public const int PipelineSteps = 6;

    static string Elapsed(long seconds) => seconds < 60 ? $"{Math.Max(0, seconds)}s" : $"{seconds / 60}m {seconds % 60:00}s";

    /// <summary>Which step (1-6) the agent pipeline is on, its label, and overall progress 0..1.</summary>
    //  1 Saving balance baseline        (BASELINE)
    //  2 Editing agent works            (EDITING)
    //  3 Starting regression checks     (RUNNING, checks not started yet)
    //  4 Running regression checks n/N  (RUNNING, checks in progress)
    //  5 Comparing balance values       (RUNNING, checks done)  or  reviewer agent deciding (REVIEWING)
    //  6 Verdict                        (PASS / FAIL / REVERTED / ERROR)
    public static void Pipeline(Snapshot s, out int step, out string label, out float progress)
    {
        step = 0; label = ""; progress = 0f;
        bool checksThisReview = s.runStart >= s.reviewStart && s.checks.Count > 0;
        switch (s.reviewState)
        {
            case "": return;
            case "BASELINE": step = 1; label = "Saving balance baseline"; break;
            case "EDITING": step = 2; label = "Editing agent is making the change"; break;
            case "RUNNING":
                if (checksThisReview && s.runActive)
                {
                    step = 4; label = $"Running regression checks ({s.checksDone}/{s.checks.Count})";
                    progress = (3f + (float)s.checksDone / s.checks.Count) / PipelineSteps;
                    return;
                }
                if (checksThisReview) { step = 5; label = "Comparing balance values"; }
                else { step = 3; label = "Starting regression checks"; }
                break;
            case "REVIEWING":
                step = 5;
                label = "Checks failed: reviewer agent deciding whether to revert";
                label += $"  ({Elapsed(Now() - s.reviewTime)})"; // proves it's still working
                break;
            default: step = 6; label = "Done"; progress = 1f; return; // PASS / FAIL / REVERTED / ERROR
        }
        progress = (step - 1f) / PipelineSteps + 0.5f / PipelineSteps;
    }
}
