using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

// Records every tunable value on the game's scripts in the open scene (damage, cooldowns, speeds,
// health, colors...) and compares against that record later, so you can see exactly what an edit changed.
//
//   Menu:  Tools > Balance Snapshot > Save Baseline / Compare To Baseline
//   Agent: unity cmd script eval --code "return BalanceSnapshot.SaveBaseline();"
//          unity cmd script eval --code "return BalanceSnapshot.Compare();"
//
// The baseline lives in Library/BalanceSnapshots (not in Assets, not in git). Each compare also writes
// Library/RegressionStatus/balance.txt for the in-game panel and the Regression Monitor window.
public static class BalanceSnapshot
{
    const string Folder = "Library/BalanceSnapshots";

    // UI, camera and editor-tool scripts: their values aren't gameplay balance, and some (like the pi chat
    // panel) only exist while open, which made them show up as "removed" after every request.
    static readonly HashSet<string> NotBalance = new HashSet<string>
    {
        "PiOutputPanel", "MouseHandler", "PiUISprites", "PauseToggle", "AbilityBar",
        "CameraFollow", "HealthBar", "Outline", "GroundRipple", "Knockback",
    };
    static string BaselinePath => Path.Combine(Folder, "baseline.txt");

    [MenuItem("Tools/Balance Snapshot/Save Baseline")]
    static void SaveBaselineMenu() => Debug.Log(SaveBaseline());

    [MenuItem("Tools/Balance Snapshot/Compare To Baseline")]
    static void CompareMenu() => Debug.Log(Compare());

    public static string SaveBaseline()
    {
        Dictionary<string, string> values = Capture();
        Directory.CreateDirectory(Folder);

        List<string> keys = new List<string>(values.Keys);
        keys.Sort(System.StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (string key in keys) sb.Append(key).Append('\t').Append(values[key]).Append('\n');
        File.WriteAllText(BaselinePath, sb.ToString());

        WriteStatus(new List<string>()); // fresh baseline: nothing changed yet
        return $"Balance baseline saved ({values.Count} values).";
    }

    // Returns a readable report of what changed since the baseline.
    public static string Compare()
    {
        if (!File.Exists(BaselinePath))
        {
            WriteStatus(new List<string>(), false); // still answer, so nobody waits for a result that never comes
            return "No balance baseline yet. Save one first.";
        }

        var before = new Dictionary<string, string>();
        foreach (string line in File.ReadAllLines(BaselinePath))
        {
            int tab = line.IndexOf('\t');
            if (tab > 0) before[line.Substring(0, tab)] = line.Substring(tab + 1);
        }
        Dictionary<string, string> after = Capture();

        var report = new List<string>();
        var status = new List<string>();
        foreach (KeyValuePair<string, string> kv in after)
        {
            if (!before.TryGetValue(kv.Key, out string old))
            {
                report.Add($"  + {kv.Key} = {kv.Value}");
                status.Add(StatusLine(kv.Key, "(new)", kv.Value));
            }
            else if (old != kv.Value)
            {
                report.Add($"  ~ {kv.Key}: {old} -> {kv.Value}");
                status.Add(StatusLine(kv.Key, old, kv.Value));
            }
        }
        foreach (KeyValuePair<string, string> kv in before)
            if (!after.ContainsKey(kv.Key))
            {
                report.Add($"  - {kv.Key} (was {kv.Value})");
                status.Add(StatusLine(kv.Key, kv.Value, "(removed)"));
            }
        WriteStatus(status);

        if (report.Count == 0) return "Balance check: no tunable values changed.";
        report.Sort(System.StringComparer.Ordinal);
        return "Balance check: values changed since the baseline\n" + string.Join("\n", report);
    }

    // "Player/Skillshot#1.speed" -> object path "Player", field "Skillshot#1.speed".
    static string StatusLine(string key, string oldValue, string newValue)
    {
        int slash = key.LastIndexOf('/');
        string objectPath = slash > 0 ? key.Substring(0, slash) : "";
        string field = slash > 0 ? key.Substring(slash + 1) : key;
        return $"change\t{objectPath}\t{field}\t{oldValue}\t{newValue}";
    }

    static void WriteStatus(List<string> changes, bool hasBaseline = true)
    {
        Directory.CreateDirectory(RegressionStatusFiles.Folder);
        var sb = new StringBuilder($"summary\t{RegressionStatusFiles.Now()}\t{changes.Count}\t{(hasBaseline ? "ok" : "nobaseline")}\n");
        foreach (string c in changes) sb.Append(c).Append('\n');
        File.WriteAllText(Path.Combine(RegressionStatusFiles.Folder, "balance.txt"), sb.ToString());
    }

    // Every visible serialized field of every script from Assets/Scripts on objects in the loaded scenes.
    static Dictionary<string, string> Capture()
    {
        var values = new Dictionary<string, string>();
        for (int s = 0; s < SceneManager.sceneCount; s++)
        {
            Scene scene = SceneManager.GetSceneAt(s);
            if (!scene.isLoaded) continue;
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (MonoBehaviour mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                    CaptureComponent(mb, values);
        }
        return values;
    }

    static void CaptureComponent(MonoBehaviour mb, Dictionary<string, string> values)
    {
        if (mb == null) return; // missing script
        if ((mb.gameObject.hideFlags & HideFlags.HideAndDontSave) != 0) return; // regression check characters
        MonoScript script = MonoScript.FromMonoBehaviour(mb);
        string scriptPath = script != null ? AssetDatabase.GetAssetPath(script) : "";
        if (!scriptPath.StartsWith("Assets/Scripts/") || scriptPath.StartsWith("Assets/Scripts/Regression/")) return;

        if (NotBalance.Contains(mb.GetType().Name)) return;

        string prefix = $"{PathOf(mb.transform)}/{ComponentLabel(mb)}";
        var so = new SerializedObject(mb);
        SerializedProperty it = so.GetIterator();
        bool enterChildren = true;
        while (it.NextVisible(enterChildren))
        {
            enterChildren = false; // top-level fields only
            if (it.name == "m_Script") continue;
            string value = Format(it);
            if (value != null) values[$"{prefix}.{it.name}"] = value;
        }
    }

    // "Player/Skillshot#2" when an object has more than one of the same script.
    static string ComponentLabel(MonoBehaviour mb)
    {
        System.Type type = mb.GetType();
        int index = 0, count = 0;
        foreach (MonoBehaviour other in mb.GetComponents<MonoBehaviour>())
        {
            if (other == null || other.GetType() != type) continue;
            count++;
            if (other == mb) index = count;
        }
        return count > 1 ? $"{type.Name}#{index}" : type.Name;
    }

    static string PathOf(Transform t)
    {
        string path = t.name;
        while (t.parent != null) { t = t.parent; path = t.name + "/" + path; }
        return path;
    }

    static string Format(SerializedProperty p)
    {
        switch (p.propertyType)
        {
            case SerializedPropertyType.Float: return p.floatValue.ToString("0.###");
            case SerializedPropertyType.Integer: return p.intValue.ToString();
            case SerializedPropertyType.Boolean: return p.boolValue ? "true" : "false";
            case SerializedPropertyType.String: return p.stringValue;
            case SerializedPropertyType.Enum:
                return p.enumValueIndex >= 0 && p.enumValueIndex < p.enumNames.Length ? p.enumNames[p.enumValueIndex] : p.intValue.ToString();
            case SerializedPropertyType.Color:
                Color c = p.colorValue; return $"({c.r:0.##}, {c.g:0.##}, {c.b:0.##}, {c.a:0.##})";
            case SerializedPropertyType.Vector2: return p.vector2Value.ToString("0.##");
            case SerializedPropertyType.Vector3: return p.vector3Value.ToString("0.##");
            case SerializedPropertyType.ObjectReference: return p.objectReferenceValue != null ? p.objectReferenceValue.name : "None";
            default: return null; // lists, structs etc. are skipped
        }
    }
}
