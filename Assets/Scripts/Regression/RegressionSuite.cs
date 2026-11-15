using System;
using System.Collections.Generic;
using UnityEngine;

// The game's regression checks: small experiments on throwaway characters that confirm the core
// rules still hold (damage, healing, shields, ally decay, who each attack can hit).
//
// They're plain C#, so they run in Play mode AND Edit mode (Unity's NUnit tests can't run while the
// game is playing). A run goes one check at a time with a short pause, so the in-game panel and the
// Regression Monitor window can show which check is running and a progress bar.
//
//   Start a run:  RegressionSuite.StartRun()   (or F5 in game, or RegressionReporter.RunAll() from the agent)
//   One check:    RegressionSuite.Run("Shield_AbsorbsDamageBeforeHealth")  -> null if it passed
//
// The editor pumps runs forward every frame (Assets/Editor/RegressionReporter.cs).
public static class RegressionSuite
{
    public enum State { Pending, Running, Passed, Failed }

    public class Result
    {
        public string name;
        public State state;
        public string message;
    }

    struct Check
    {
        public string name;
        public Func<string> run; // returns null on pass, or what went wrong
    }

    /// <summary>Seconds each check stays on screen as "running", so people can follow along.</summary>
    public static float StepDelay = 0.12f;

    public static readonly List<Result> Results = new List<Result>();
    public static bool IsRunning { get; private set; }
    public static int Done { get; private set; }
    public static int Total => Checks.Count;

    /// <summary>Called when a run finishes (the editor uses it to run the balance check right after).</summary>
    public static event Action Finished;

    static List<Check> checks;
    static List<Check> Checks => checks ??= Build();

    static int index;
    static bool executeNext;
    static double nextStepTime;
    static long runStart;
    static readonly List<GameObject> spawned = new List<GameObject>();

    public static List<string> Names()
    {
        var names = new List<string>();
        foreach (Check c in Checks) names.Add(c.name);
        return names;
    }

    /// <summary>Runs one check right away. Returns null if it passed, otherwise the failure message.</summary>
    public static string Run(string name)
    {
        foreach (Check c in Checks)
            if (c.name == name) return Execute(c);
        return $"No check named {name}";
    }

    // ---- Step-by-step runs -----------------------------------------------------------

    public static void StartRun()
    {
        Results.Clear();
        foreach (Check c in Checks) Results.Add(new Result { name = c.name, state = State.Pending });
        index = 0;
        Done = 0;
        executeNext = false;
        nextStepTime = 0;
        IsRunning = true;
        runStart = RegressionStatusFiles.Now();
        RegressionStatusFiles.WriteProgress(runStart, Results, true);
    }

    /// <summary>Advances a run. Called every frame with the current time (seconds).</summary>
    public static void Step(double now)
    {
        if (!IsRunning || now < nextStepTime) return;

        if (index >= Checks.Count)
        {
            FinishRun();
            return;
        }

        Result r = Results[index];
        if (!executeNext)
        {
            // Phase 1: show it as running for a moment.
            r.state = State.Running;
            executeNext = true;
            nextStepTime = now + StepDelay;
        }
        else
        {
            // Phase 2: run it and record the result.
            r.message = Execute(Checks[index]);
            r.state = r.message == null ? State.Passed : State.Failed;
            Done++;
            index++;
            executeNext = false;
            nextStepTime = now + StepDelay * 0.25f;
        }
        RegressionStatusFiles.WriteProgress(runStart, Results, true);
    }

    /// <summary>Runs every check immediately (no animation).</summary>
    public static void RunAllNow()
    {
        StartRun();
        for (int i = 0; i < Checks.Count; i++)
        {
            Results[i].message = Execute(Checks[i]);
            Results[i].state = Results[i].message == null ? State.Passed : State.Failed;
            Done++;
        }
        index = Checks.Count;
        FinishRun();
    }

    static void FinishRun()
    {
        IsRunning = false;
        int passed = 0, failed = 0;
        foreach (Result r in Results) if (r.state == State.Passed) passed++; else if (r.state == State.Failed) failed++;

        RegressionStatusFiles.WriteProgress(runStart, Results, false);
        RegressionStatusFiles.WriteTests(passed, failed, Results);

        if (failed > 0) Debug.LogWarning($"Regression checks: {failed} failed, {passed} passed.");
        else Debug.Log($"Regression checks: all {passed} passed.");
        Finished?.Invoke();
    }

    static string Execute(Check c)
    {
        try { return c.run(); }
        catch (Exception e) { return $"{e.GetType().Name}: {e.Message}"; }
        finally { CleanUp(); }
    }

    // ---- Helpers ---------------------------------------------------------------------

    // A fresh hidden character that's never saved, so checks never touch your scene.
    static Health MakeCharacter(Team team, float maxHealth = 100f, float startingHealth = 100f)
    {
        var go = new GameObject("Regression Check Character");
        go.hideFlags = HideFlags.HideAndDontSave;
        spawned.Add(go);
        var h = go.AddComponent<Health>();
        h.team = team;
        h.maxHealth = maxHealth;
        h.startingHealth = startingHealth;
        h.respawn = true;
        h.ResetToStart(); // what Awake does when the game starts
        return h;
    }

    static void CleanUp()
    {
        foreach (GameObject go in spawned) if (go != null) UnityEngine.Object.DestroyImmediate(go);
        spawned.Clear();
    }

    static string Expect(float expected, float actual, string what = "value") =>
        Mathf.Abs(expected - actual) <= 0.001f ? null : $"expected {what} {expected:0.##}, got {actual:0.##}";

    static string Expect(bool condition, string failure) => condition ? null : failure;

    static string All(params string[] results)
    {
        foreach (string r in results) if (r != null) return r;
        return null;
    }

    static void Add(List<Check> list, string name, Func<string> run) => list.Add(new Check { name = name, run = run });

    // ---- The checks ------------------------------------------------------------------

    static List<Check> Build()
    {
        var c = new List<Check>();

        // Damage, healing, death
        Add(c, "Damage_ReducesHealth", () =>
        {
            var h = MakeCharacter(Team.Enemy);
            h.TakeDamage(25f);
            return Expect(75f, h.Current, "health");
        });
        Add(c, "Damage_NeverGoesBelowZero_AndKills", () =>
        {
            var h = MakeCharacter(Team.Enemy);
            h.TakeDamage(500f);
            return All(Expect(0f, h.Current, "health"), Expect(h.IsDead, "a character at 0 health should be dead"));
        });
        Add(c, "Heal_NeverGoesAboveMax", () =>
        {
            var h = MakeCharacter(Team.Ally, 100f, 25f);
            h.Heal(1000f);
            return Expect(100f, h.Current, "health");
        });
        Add(c, "Dead_CannotBeHealed", () =>
        {
            var h = MakeCharacter(Team.Ally);
            h.TakeDamage(500f);
            h.Heal(50f);
            return Expect(0f, h.Current, "health (healing shouldn't revive)");
        });
        Add(c, "StartingHealth_IsUsedAtStart", () =>
        {
            var h = MakeCharacter(Team.Ally, 100f, 25f);
            return Expect(25f, h.Current, "health");
        });

        // Shield (R)
        Add(c, "Shield_AbsorbsDamageBeforeHealth", () =>
        {
            var h = MakeCharacter(Team.Ally);
            h.AddShield(50f, 4f);
            h.TakeDamage(30f);
            return All(Expect(100f, h.Current, "health"), Expect(20f, h.Shield, "shield"));
        });
        Add(c, "Shield_LeftoverDamageReachesHealth", () =>
        {
            var h = MakeCharacter(Team.Ally);
            h.AddShield(50f, 4f);
            h.TakeDamage(70f);
            return All(Expect(0f, h.Shield, "shield"), Expect(80f, h.Current, "health"));
        });
        Add(c, "Shield_ExpiresAfterItsDuration", () =>
        {
            var h = MakeCharacter(Team.Ally);
            h.AddShield(50f, 4f);
            h.Tick(Time.time + 4.1f, 0.016f);
            return Expect(0f, h.Shield, "shield");
        });
        Add(c, "Shield_LastsUntilItsDuration", () =>
        {
            var h = MakeCharacter(Team.Ally);
            h.AddShield(50f, 4f);
            h.Tick(Time.time + 3.9f, 0.016f);
            return Expect(50f, h.Shield, "shield");
        });

        // Decay back to Starting Health (Friendly)
        Add(c, "Ally_NoDecay_DuringPauseAfterHeal", () =>
        {
            var h = MakeCharacter(Team.Ally, 100f, 25f);
            h.Heal(75f);
            h.Tick(Time.time + h.decayDelaySeconds * 0.5f, 1f);
            return Expect(100f, h.Current, "health");
        });
        Add(c, "Ally_DecaysTowardStartingHealth", () =>
        {
            var h = MakeCharacter(Team.Ally, 100f, 25f);
            h.Heal(75f);
            h.Tick(Time.time + h.decayDelaySeconds + 1f, 1f);
            return Expect(100f - h.decayPerSecond, h.Current, "health after 1s of decay");
        });
        Add(c, "Ally_DecayStopsAtStartingHealth", () =>
        {
            var h = MakeCharacter(Team.Ally, 100f, 25f);
            h.Heal(75f);
            h.Tick(Time.time + h.decayDelaySeconds + 1f, 100f);
            return Expect(25f, h.Current, "health");
        });
        Add(c, "Enemy_NeverDecays", () =>
        {
            var h = MakeCharacter(Team.Enemy, 100f, 25f);
            h.Heal(75f);
            h.Tick(Time.time + 10f, 5f);
            return Expect(100f, h.Current, "health (only allies decay)");
        });

        // Team rules: who each attack can hit
        Add(c, "Q_Damage_HitsEnemies", () => Expect(SkillshotProjectile.Affects(SkillEffect.Damage, Team.Enemy), "Q damage should hit enemies"));
        Add(c, "Q_Damage_PassesThroughAllies", () => Expect(!SkillshotProjectile.Affects(SkillEffect.Damage, Team.Ally), "Q damage should not hit allies"));
        Add(c, "W_Heal_HealsAllies", () => Expect(SkillshotProjectile.Affects(SkillEffect.Heal, Team.Ally), "W should heal allies"));
        Add(c, "W_Heal_PassesThroughEnemies", () => Expect(!SkillshotProjectile.Affects(SkillEffect.Heal, Team.Enemy), "W should not heal enemies"));
        Add(c, "EnemyShot_HitsItsTarget_AnyTeam", () =>
        {
            var player = MakeCharacter(Team.Enemy); // even if the player's team was set wrong
            return Expect(EnemyProjectile.ShouldHit(player, player.transform), "enemy shots should hit their target");
        });
        Add(c, "EnemyShot_HitsAlliesInTheWay", () =>
        {
            var friendly = MakeCharacter(Team.Ally);
            return Expect(EnemyProjectile.ShouldHit(friendly, null), "enemy shots should hit allies");
        });
        Add(c, "EnemyShot_IgnoresOtherEnemies", () =>
        {
            var enemy = MakeCharacter(Team.Enemy);
            return Expect(!EnemyProjectile.ShouldHit(enemy, null), "enemy shots should not hit enemies");
        });
        Add(c, "EnemyShot_IgnoresTheDead", () =>
        {
            var friendly = MakeCharacter(Team.Ally);
            friendly.TakeDamage(500f);
            return Expect(!EnemyProjectile.ShouldHit(friendly, null), "enemy shots should not hit the dead");
        });

        return c;
    }
}
