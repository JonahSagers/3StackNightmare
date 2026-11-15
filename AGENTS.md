# Project notes for agents

Unity 6 project (`SampleScene`), new Input System only, URP. Unity is controlled through the **Unity CLI pipeline** (`com.unity.pipeline` package + `unity` CLI). Use the `mcp__unity__*` tools from codemode, or `unity cmd <group> <name>` from bash. The user often sends prompts from inside the game via `.pi/extensions/unity-bridge.ts` — those arrive as `[Unity] ...` messages with editor context.

## Unity CLI / MCP quick reference
- **Run C# in the editor:** `mcp__unity__eval({ code })` (Roslyn script mode; `return` a value to get it back). Use `"auto-import": true` for missing usings, or `file` to run a `.cs` file.
- **Console logs:** there is no MCP tool for reading the console, so use bash: `unity cmd console get --tail 50 [--level warn|error] [--json]`. Follow new entries with `--since <cursor> --since-session <session>`. `mcp__unity__clear` clears the console, and `unity cmd console status` returns counts plus the compile-failure flag.
- **Play mode:** `mcp__unity__start` / `stop` / `pause` (they return `null` on success). Entering Play mode reloads the domain, so the pipeline server is unreachable for a few seconds ("Cannot connect to Unity Editor Pipeline server"). Wait/retry, or use `mcp__unity__wait` on `UnityEditor.EditorApplication.isPlaying`.
- **Scene/objects:** `get_hierarchy`, `find_4492fb9e` (gameobject find), `get_component` / `set_component`, `set_transform`, `create_many`, `batch` (transactional, one Undo step), `save` / `save_all`.
- **Screenshots:** `game_view` (works in Play mode and includes overlay UI), `scene_view`, `editor_window`.
- **Discover commands:** `mcp__unity__list_commands({ query, detail: "compact" })` or `unity cmd <group> --help`. Many MCP tool names have hash suffixes (e.g. `run_c7e8a472` = test run), so check `describeTool` before calling one.
- **Editor status:** `unity status` (port, state, PID).
- **Don't use input-simulation commands for testing** (`simulate_key`, `simulate_pointer`, or other keystroke/mouse MCP commands). They drive the user's live game session. Test through `eval` instead (call methods and set state directly), or ask the user to try it in-game.
- **`eval` can't see Hot Reload changes in Play mode:** it compiles against the assembly loaded when Play mode started, so methods/overloads/fields added by Hot Reload don't exist for it (CS1501 / CS1061 errors). Test new code through existing entry points, or after exiting Play mode.

## Hot Reload workflow (com.singularitygroup.hotreload)

Script edits must take effect immediately, including during Play mode. Hot Reload patches code when `.cs` files are saved to disk.

### Do
- **Just save the file.** Writing/editing a `.cs` file is enough; Hot Reload detects the change on disk.
- **Verify via the Console** (`unity cmd console get`): look for Hot Reload logs and "Unsupported change" warnings. `EditorUtility.scriptCompilationFailed` does not reflect hot patches.
- **Check `EditorApplication.isPlaying`** (via `eval`) before editing a script, and keep Play-mode edits within what Hot Reload supports:
  - OK: method body edits, adding methods, adding/changing fields and field initializers.
  - Not OK: adding new classes/structs/enums — make those changes only outside Play mode (or tell the user it applies after exiting Play mode).
  - No multidimensional arrays (`[,]`) in methods; use jagged arrays (`[][]`).
  - `Awake`/`Start` do not re-run on existing objects; put new initialization logic somewhere lazy (e.g. first use in `Update`) or note that it only affects new instances.
- **When the pipeline server is unreachable**, Unity is reloading (domain reload / entering Play mode); wait a few seconds and retry instead of reporting failure.

### Don't
- **Never force a recompile** after editing scripts: no `mcp__unity__recompile`, `unity recompile`, `reload_editor`, `AssetDatabase.Refresh()`, or `CompilationPipeline.RequestScriptCompilation()` (including inside `eval`). This causes full domain reloads and is unnecessary with Hot Reload. (`mcp__unity__reload` is the pipeline's own `[MethodReload]` patcher; it isn't needed here because Hot Reload handles patching.)
- **Don't change these editor settings** (Hot Reload manages them): Unity Auto Refresh = off, Script Changes While Playing = Recompile After Finished Playing, Hot Reload "Auto recompile unsupported changes" = on exit Play mode.

### Scene/object changes vs. code changes
Hot Reload only patches code. Changes to GameObjects, components, or serialized values made during Play mode (scale, inspector values, etc.) are lost when Play mode ends. When making such changes in Play mode, tell the user they are temporary and offer to re-apply them in edit mode (then save the scene) once Play mode has stopped.

## Verified behavior
- `eval` and `get_hierarchy` still respond (in under 1s) in Play mode after a `.cs` file has changed. The old AI Assistant `Unity_RunCommand` timeout problem is gone.
- `unity cmd list` is not a valid command (it's ambiguous). Use `unity cmd <group> --help` or `list_commands`.
