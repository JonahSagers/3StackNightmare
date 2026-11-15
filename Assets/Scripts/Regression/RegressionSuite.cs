using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

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
    public static float StepDelay = 0.08f;

    public static readonly List<Result> Results = new List<Result>();
    public static bool IsRunning { get; private set; }
    public static int Done { get; private set; }
    public static int Total => Checks.Count;
    static bool loggedStepError;

    /// <summary>Called when a run finishes (the editor uses it to run the balance check right after).</summary>
    public static event Action Finished;

    // Built fresh every time instead of cached: Hot Reload keeps old static data alive between code
    // patches, and a stale cached list is what made runs crash and hang.
    static List<Check> Checks => Build();
    static List<Check> runChecks = new List<Check>(); // the checks of the run in progress

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
        foreach (Check c in Build())
            if (c.name == name) return Execute(c);
        return $"No check named {name}";
    }

    // ---- Step-by-step runs -----------------------------------------------------------

    public static void StartRun()
    {
        runChecks = Build();
        Results.Clear();
        foreach (Check c in runChecks) Results.Add(new Result { name = c.name, state = State.Pending });
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
        try { StepRun(now); }
        catch (Exception e)
        {
            // Never get stuck: report it once and end the run, instead of throwing every frame.
            if (!loggedStepError) Debug.LogError($"Regression checks stopped unexpectedly: {e.Message}");
            loggedStepError = true;
            FinishRun();
        }
    }

    static void StepRun(double now)
    {
        if (runChecks == null || index >= runChecks.Count || index >= Results.Count)
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
            r.message = Execute(runChecks[index]);
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
        for (int i = 0; i < runChecks.Count; i++)
        {
            Results[i].message = Execute(runChecks[i]);
            Results[i].state = Results[i].message == null ? State.Passed : State.Failed;
            Done++;
        }
        index = runChecks.Count;
        FinishRun();
    }

    static void FinishRun()
    {
        IsRunning = false;
        loggedStepError = false;
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
        go.transform.position = TestArea; // far from the real scene, so shots can't touch real characters
        spawned.Add(go);
        var h = go.AddComponent<Health>();
        h.team = team;
        h.maxHealth = maxHealth;
        h.startingHealth = startingHealth;
        h.respawn = true;
        h.ResetToStart(); // what Awake does when the game starts
        return h;
    }

    static readonly Vector3 TestArea = new Vector3(5000f, -5000f, 5000f);

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

    // Fires a real skillshot projectile into a real target (through the projectile's own collision code)
    // and returns the target's health afterward. Catches changes to the hit logic itself, not just Affects().
    static float FireShotAt(SkillEffect effect, Team team, float startingHealth, float amount = 25f)
    {
        Health target = MakeCharacter(team, 100f, startingHealth);
        Collider body = target.gameObject.AddComponent<CapsuleCollider>();

        var shot = new GameObject("Regression Check Shot");
        shot.hideFlags = HideFlags.HideAndDontSave;
        spawned.Add(shot);
        var projectile = shot.AddComponent<SkillshotProjectile>();
        projectile.Launch(Vector3.forward, 0f, 100f, null, amount, effect);

        shot.SendMessage("OnTriggerEnter", body, SendMessageOptions.RequireReceiver);
        return target.Current;
    }

    // Puts an enemy-shooter projectile right on top of a target and lets it run one frame (its own
    // overlap check decides whether it hits). Returns the target's health, or -1 if physics queries
    // can't see the test objects here.
    static float EnemyShotAt(Team team)
    {
        Health target = MakeCharacter(team);
        Collider body = target.gameObject.AddComponent<CapsuleCollider>();

        var shot = new GameObject("Regression Check Enemy Shot");
        shot.hideFlags = HideFlags.HideAndDontSave;
        spawned.Add(shot);
        shot.transform.position = target.transform.position;
        var projectile = shot.AddComponent<EnemyProjectile>();
        projectile.Launch(Vector3.forward, 0f, 10f, 15f, 0.5f, null);

        Physics.SyncTransforms();
        bool physicsSeesIt = false;
        foreach (Collider c in Physics.OverlapSphere(target.transform.position, 0.5f)) if (c == body) physicsSeesIt = true;
        if (!physicsSeesIt) return -1f;

        shot.SendMessage("Update", SendMessageOptions.RequireReceiver);
        return target.Current;
    }

    // ---- Source scan: every piece of code that deals damage or heals must apply a team rule ----------

    struct DamageSource
    {
        public string file;      // script that calls TakeDamage / Heal / AddShield
        public string ruleFile;  // script that must contain the team rule (usually the same file)
        public string rule;      // text that must appear there
        public int damage, heal, shield; // how many call sites are expected
        public string why;
    }

    static readonly DamageSource[] KnownSources =
    {
        new DamageSource { file = "SkillshotProjectile.cs", ruleFile = "SkillshotProjectile.cs", rule = "Affects(", damage = 1, heal = 1, why = "Q/W hits" },
        new DamageSource { file = "EnemyProjectile.cs", ruleFile = "EnemyProjectile.cs", rule = "ShouldHit(", damage = 1, why = "enemy shooter shots" },
        new DamageSource { file = "AutoAttackProjectile.cs", ruleFile = "AutoAttack.cs", rule = "CanTarget(", damage = 1, why = "auto attacks (target picked by AutoAttack)" },
        new DamageSource { file = "LeapSlam.cs", ruleFile = "LeapSlam.cs", rule = "ShouldHit(", damage = 1, why = "leap slam" },
        new DamageSource { file = "Fountain.cs", ruleFile = "Fountain.cs", rule = "PlayerMove", damage = 1, heal = 1, why = "base pads (player only)" },
        new DamageSource { file = "FullHealKey.cs", ruleFile = "FullHealKey.cs", rule = "GetComponent<Health>", heal = 1, why = "H key (heals yourself)" },
        new DamageSource { file = "ShieldAbility.cs", ruleFile = "ShieldAbility.cs", rule = "GetComponent<Health>", shield = 1, why = "R shield (yourself)" },
    };

    static string ScriptsFolder => Path.Combine(Application.dataPath, "Scripts");

    // Code without comments, so commented-out calls don't count.
    static string CodeOf(string path)
    {
        string text = File.ReadAllText(path);
        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/') { while (i < text.Length && text[i] != '\n') i++; }
            else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*') { i += 2; while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/')) i++; i++; }
            else sb.Append(text[i]);
        }
        return sb.ToString();
    }

    static int Count(string code, string token)
    {
        int n = 0;
        for (int i = code.IndexOf(token, StringComparison.Ordinal); i >= 0; i = code.IndexOf(token, i + token.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    static string CheckDamageSources()
    {
        if (!Directory.Exists(ScriptsFolder)) return null; // not in the editor (e.g. a build): nothing to scan
        var problems = new List<string>();

        foreach (string path in Directory.GetFiles(ScriptsFolder, "*.cs", SearchOption.AllDirectories))
        {
            string file = Path.GetFileName(path);
            if (file == "Health.cs" || path.Replace('\\', '/').Contains("/Regression/")) continue;

            string code = CodeOf(path);
            int damage = Count(code, ".TakeDamage("), heal = Count(code, ".Heal("), shield = Count(code, ".AddShield(");
            if (damage + heal + shield == 0) continue;

            bool known = false;
            foreach (DamageSource src in KnownSources)
            {
                if (src.file != file) continue;
                known = true;
                if (damage > src.damage) problems.Add($"{file} now deals damage in {damage} places (expected {src.damage}); check the new one respects teams");
                if (heal > src.heal) problems.Add($"{file} now heals in {heal} places (expected {src.heal}); check the new one respects teams");
                if (shield > src.shield) problems.Add($"{file} now gives shields in {shield} places (expected {src.shield})");
                string rulePath = Path.Combine(Path.GetDirectoryName(path), src.ruleFile);
                if (File.Exists(rulePath) && !CodeOf(rulePath).Contains(src.rule))
                    problems.Add($"{src.ruleFile} no longer uses its team rule ({src.rule}) for {src.why}");
            }
            if (!known) problems.Add($"{file} is a NEW damage/heal source with no team rule check");
        }
        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    static string CheckNoTeamChangesInCode()
    {
        if (!Directory.Exists(ScriptsFolder)) return null;
        var problems = new List<string>();
        foreach (string path in Directory.GetFiles(ScriptsFolder, "*.cs", SearchOption.AllDirectories))
        {
            string file = Path.GetFileName(path);
            if (file == "Health.cs" || path.Replace('\\', '/').Contains("/Regression/")) continue;
            string code = CodeOf(path).Replace(" ", "").Replace("\t", "");
            // ".team=" followed by anything but "=" is an assignment (".team==" is just a comparison).
            for (int i = code.IndexOf(".team=", StringComparison.Ordinal); i >= 0; i = code.IndexOf(".team=", i + 6, StringComparison.Ordinal))
            {
                if (i + 6 < code.Length && code[i + 6] == '=') continue;
                problems.Add($"{file} changes a character's team at runtime");
                break;
            }
        }
        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    // ---- Scene setup ---------------------------------------------------------------------

    static bool IsCheckObject(Component c) => (c.gameObject.hideFlags & HideFlags.HideAndDontSave) != 0;

    static string CheckSceneTeams()
    {
        var problems = new List<string>();
        foreach (Health h in UnityEngine.Object.FindObjectsByType<Health>())
        {
            if (IsCheckObject(h)) continue;
            string n = h.gameObject.name;
            bool isPlayer = h.GetComponent<PlayerMove>() != null;
            bool shouldBeAlly = isPlayer || n.IndexOf("Friendly", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Teammate", StringComparison.OrdinalIgnoreCase) >= 0;
            bool shouldBeEnemy = !isPlayer && n.IndexOf("Enemy", StringComparison.OrdinalIgnoreCase) >= 0;
            if (shouldBeAlly && h.team != Team.Ally) problems.Add($"{n} should be on the Ally team");
            if (shouldBeEnemy && h.team != Team.Enemy) problems.Add($"{n} should be on the Enemy team");
        }
        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    static string CheckPlayerAbilities()
    {
        var problems = new List<string>();
        foreach (PlayerMove player in UnityEngine.Object.FindObjectsByType<PlayerMove>())
        {
            if (IsCheckObject(player)) continue;
            var keys = new Dictionary<Key, string>();
            void Bind(Key key, string ability)
            {
                if (keys.TryGetValue(key, out string other)) problems.Add($"{key} is bound to both {other} and {ability}");
                else keys[key] = ability;
            }
            foreach (Skillshot s in player.GetComponents<Skillshot>())
            {
                Bind(s.key, s.effect == SkillEffect.Heal ? "Heal" : "Shot");
                if (s.key == Key.Q && s.effect != SkillEffect.Damage) problems.Add("Q should be the damage skillshot");
                if (s.key == Key.W && s.effect != SkillEffect.Heal) problems.Add("W should be the heal skillshot");
            }
            foreach (LeapSlam l in player.GetComponents<LeapSlam>()) Bind(l.key, "Leap");
            foreach (ShieldAbility a in player.GetComponents<ShieldAbility>()) Bind(a.key, "Shield");
            foreach (FullHealKey f in player.GetComponents<FullHealKey>()) Bind(f.key, "Full Heal");
        }
        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    static string CheckAbilityValues()
    {
        var problems = new List<string>();
        foreach (Skillshot s in UnityEngine.Object.FindObjectsByType<Skillshot>())
        {
            if (s.speed <= 0f || s.range <= 0f) problems.Add($"{s.key} skillshot speed/range must be above 0");
            if (s.amount < 0f) problems.Add($"{s.key} skillshot amount is negative");
            if (s.cooldown < 0f) problems.Add($"{s.key} skillshot cooldown is negative");
        }
        foreach (LeapSlam l in UnityEngine.Object.FindObjectsByType<LeapSlam>())
            if (l.damage < 0f || l.radius <= 0f || l.airTime <= 0f) problems.Add("leap damage/radius/air time invalid");
        foreach (AutoAttack a in UnityEngine.Object.FindObjectsByType<AutoAttack>())
            if (a.damage < 0f || a.range <= 0f || a.attackInterval <= 0f) problems.Add("auto attack damage/range/interval invalid");
        foreach (EnemyShooter e in UnityEngine.Object.FindObjectsByType<EnemyShooter>())
            if (e.damage < 0f || e.fireInterval <= 0f) problems.Add($"{e.name} damage/fire interval invalid");
        foreach (ShieldAbility sh in UnityEngine.Object.FindObjectsByType<ShieldAbility>())
            if (sh.shieldAmount < 0f || sh.duration <= 0f) problems.Add("shield amount/duration invalid");
        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

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
        // Real projectile hits: these go through SkillshotProjectile's collision code, so they catch
        // any change to how a hit is handled, not just changes to the Affects() rule.
        Add(c, "Q_Shot_DamagesEnemyOnHit", () => Expect(75f, FireShotAt(SkillEffect.Damage, Team.Enemy, 100f), "enemy health after a Q hit"));
        Add(c, "Q_Shot_DoesNotHurtAllyOnHit", () => Expect(100f, FireShotAt(SkillEffect.Damage, Team.Ally, 100f), "ally health after a Q hit (Q must not hurt teammates)"));
        Add(c, "W_Shot_HealsAllyOnHit", () => Expect(75f, FireShotAt(SkillEffect.Heal, Team.Ally, 50f), "ally health after a W hit"));
        Add(c, "W_Shot_DoesNotHealEnemyOnHit", () =>
        {
            float after = FireShotAt(SkillEffect.Heal, Team.Enemy, 100f);
            return Expect(100f, after, "enemy health after a W hit (W must not heal enemies)");
        });

        // Auto attacks and the leap slam: only living enemies.
        Add(c, "AutoAttack_TargetsEnemiesOnly", () =>
        {
            Health enemy = MakeCharacter(Team.Enemy);
            Health ally = MakeCharacter(Team.Ally);
            Health deadEnemy = MakeCharacter(Team.Enemy);
            deadEnemy.TakeDamage(500f);
            return All(Expect(AutoAttack.CanTarget(enemy), "auto attacks should target enemies"),
                       Expect(!AutoAttack.CanTarget(ally), "auto attacks must not target allies"),
                       Expect(!AutoAttack.CanTarget(deadEnemy), "auto attacks must not target the dead"));
        });
        Add(c, "AutoAttackShot_DamagesItsTarget", () =>
        {
            Health enemy = MakeCharacter(Team.Enemy);
            var shot = new GameObject("Regression Check Auto Attack");
            shot.hideFlags = HideFlags.HideAndDontSave;
            spawned.Add(shot);
            shot.transform.position = enemy.transform.position;
            shot.AddComponent<AutoAttackProjectile>().Launch(enemy, 5f, 20f);
            shot.SendMessage("Update", SendMessageOptions.RequireReceiver);
            return Expect(95f, enemy.Current, "enemy health after an auto attack");
        });
        Add(c, "LeapSlam_HitsEnemiesOnly", () =>
        {
            Health enemy = MakeCharacter(Team.Enemy);
            Health ally = MakeCharacter(Team.Ally);
            return All(Expect(LeapSlam.ShouldHit(enemy), "the slam should hit enemies"),
                       Expect(!LeapSlam.ShouldHit(ally), "the slam must not hit allies"));
        });

        // Enemy shooter shots, fired for real.
        Add(c, "EnemyShot_HurtsAllyOnContact", () =>
        {
            float after = EnemyShotAt(Team.Ally);
            return after < 0f ? null : Expect(85f, after, "ally health after an enemy shot");
        });
        Add(c, "EnemyShot_PassesThroughEnemyOnContact", () =>
        {
            float after = EnemyShotAt(Team.Enemy);
            return after < 0f ? null : Expect(100f, after, "enemy health after an enemy shot (enemies don't shoot each other)");
        });

        // Code scan: any code that deals damage or heals must go through a team rule.
        Add(c, "AllDamageSources_UseTeamRules", CheckDamageSources);
        Add(c, "NoCode_ChangesTeamsAtRuntime", CheckNoTeamChangesInCode);

        // Scene setup.
        Add(c, "Scene_TeamsAreCorrect", CheckSceneTeams);
        Add(c, "Player_AbilityKeysAreCorrect", CheckPlayerAbilities);
        Add(c, "Abilities_HaveValidValues", CheckAbilityValues);

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
