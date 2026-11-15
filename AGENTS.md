# Project notes for agents

Unity 6 project (`SampleScene`), new Input System only, URP. Unity is controlled through the **Unity CLI pipeline** (`com.unity.pipeline` package + `unity` CLI). Use the `mcp__unity__*` tools from codemode, or `unity cmd <group> <name>` from bash. The user often sends prompts from inside the game via `.pi/extensions/unity-bridge.ts` — those arrive as `[Unity] ...` messages with editor context.

## Unity CLI / MCP quick reference
MCP tool names follow `mcp__unity__<group>_<command>` (no hash suffixes). They mirror `unity cmd <group> <command>`.
- **Run C# in the editor:** `mcp__unity__script_eval({ code })` (Roslyn script mode; `return` a value to get it back). Use `"auto-import": true` for missing usings, or `file` to run a `.cs` file.
- **Console logs:** `mcp__unity__console_get({ tail, level: "log"|"warn"|"error", since, "since-session" })` (or bash: `unity cmd console get --tail 50 [--level warn|error] [--json]`). To follow new entries, pass the `cursor`/`session` from the previous response as `since`/`since-session`. Use `console_clear` to clear and `console_status` for counts plus the compile-failure flag.
- **Play mode:** `mcp__unity__editor_playmode_start` / `_stop` / `_pause`. Entering Play mode reloads the domain, so the pipeline server is unreachable for a few seconds ("Cannot connect to Unity Editor Pipeline server"). Wait/retry, then poll `script_eval` on `UnityEditor.EditorApplication.isPlaying` (or `editor_status`).
- **Scene/objects:** `scene_get_hierarchy`, `gameobject_find`, `component_get_properties` / `component_set_properties` (also `component_get_fields` / `component_set_field`), `gameobject_set_transform`, `gameobject_create_many`, `job_batch` (transactional, one Undo step), `scene_save` / `editor_save_all`.
- **Screenshots:** `capture_game_view` (works in Play mode and includes overlay UI), `capture_scene_view`, `capture_editor_window`.
- **Recompile / tests:** `script_recompile` (+ `script_recompile_status`), `test_run` / `test_status`.
- **Discover commands:** `mcp__unity__pipeline_list_commands`, `searchTools("...")` in codemode, or `unity cmd <group> --help`. Check `describeTool` for parameter names before calling a tool.
- **Editor status:** `unity status` (port, state, PID) or `mcp__unity__editor_status`.
- **Don't use input-simulation commands for testing** (`runtime_simulate_key`, `runtime_simulate_pointer`, or other keystroke/mouse MCP commands). They drive the user's live game session. Test through `script_eval` instead (call methods and set state directly), or ask the user to try it in-game.
- **`script_eval` can't see Hot Reload changes in Play mode:** it compiles against the assembly loaded when Play mode started, so methods/overloads/fields added by Hot Reload don't exist for it (CS1501 / CS1061 errors). Test new code through existing entry points (see "Play mode first" below). Don't exit Play mode just to test.

## Play mode first: don't leave Play mode, test inside it
The user works in a live Play session. **Never leave Play mode on your own**, and never do anything that implicitly ends or restarts it. Keeping the session alive takes priority over convenience.
- **Don't call** `editor_playmode_stop`, `test_run` (the Test Runner exits and re-enters Play mode, even for PlayMode tests), `scene_open` / `scene_create`, `build_run`, `build_target_switch`, package add/remove, or anything else that ends Play mode or reloads the scene while `EditorApplication.isPlaying` is true.
- **Domain reloads are fine.** The editor is configured so `script_recompile` / `unity recompile` reloads the domain without exiting Play mode.
- If something truly can't be done without leaving Play mode, **stop and ask the user first**. Explain what would be lost (runtime state, temporary scene changes).
- **Run all testing inside the running game:**
  - Use `script_eval` to call existing methods, read and set runtime state, find objects, and assert on results (return a summary string, e.g. `"PASS: hp=90"` / `"FAIL: ..."`).
  - Verify behavior over time by setting up state with `script_eval`, letting the game run, then calling `script_eval` again (or reading `console_get`).
  - Hot-reloaded code that `script_eval` can't call directly: route the test through an existing method that Hot Reload has patched, or add temporary `Debug.Log` output and read it with `console_get`. Remove temporary test hooks when done.
  - Use `capture_game_view` for visual checks.
  - Don't use `test_run` or input simulation (see above). If a check needs real player input, ask the user to try it in-game.
- Write code so it can be applied and tested in the current session (see Hot Reload rules below): no new types, lazy init instead of `Awake`/`Start`, and defaults that work on live objects.

## Hot Reload workflow (com.singularitygroup.hotreload)

Script edits must take effect immediately, including during Play mode. Hot Reload patches code when `.cs` files are saved to disk.

### Prioritize changes that apply immediately
- **If Play mode is active, prefer an implementation that Hot Reload can apply right away** over one that needs a domain reload. Add a method or field to an existing class instead of creating a new class, and use lazy init instead of `Awake`/`Start`.
- **Make the change take effect in the running game too, not just in code.** New serialized fields don't reach existing instances, and `script_eval` can't set fields added by Hot Reload. So when a feature needs a value on a live object, make the code's default work without setup (e.g. pick a sensible default in the code path), or use a value the running game already has.
- **Before relying on Hot Reload, check the Console for `[HotReload]` compile errors.** Errors anywhere block all patches. If errors exist, tell the user immediately and explain that nothing will apply until they're fixed or Play mode exits.
- Changes Hot Reload can't apply get picked up by the domain reload at the end of the task. That reload doesn't exit Play mode, but it may reset runtime state, so mention it when a change depends on it.

### Do
- **Just save the file.** Writing/editing a `.cs` file is enough; Hot Reload detects the change on disk.
- **Verify via the Console** (`unity cmd console get`): look for Hot Reload logs and "Unsupported change" warnings. `EditorUtility.scriptCompilationFailed` does not reflect hot patches.
- **Check `EditorApplication.isPlaying`** (via `script_eval`) before editing a script, and keep Play-mode edits within what Hot Reload supports:
  - OK: method body edits, adding methods, adding/changing fields and field initializers.
  - Not OK: adding new classes/structs/enums — these only apply after the end-of-task domain reload.
  - No multidimensional arrays (`[,]`) in methods; use jagged arrays (`[][]`).
  - `Awake`/`Start` do not re-run on existing objects; put new initialization logic somewhere lazy (e.g. first use in `Update`) or note that it only affects new instances.
- **When the pipeline server is unreachable**, Unity is reloading (domain reload / entering Play mode); wait a few seconds and retry instead of reporting failure.

### Don't
- **Don't force a recompile in the middle of a task.** Don't recompile after each script edit; let Hot Reload patch while you work.
- **Always force a domain reload at the end of every task**, in Play mode or not (it doesn't exit Play mode). Once you've finished all changes, run `unity recompile` (or `mcp__unity__script_recompile`). This guarantees every change is actually loaded, including ones Hot Reload couldn't apply (new classes, blocked patches). Then wait for the pipeline server to come back ("Cannot connect" means it's still reloading, so retry). Check `console_get` with `level: "error"` (or `unity cmd console get --level error`) for compile errors and fix them before reporting done.
- **Don't change these editor settings** (Hot Reload manages them): Unity Auto Refresh = off, Script Changes While Playing = Recompile After Finished Playing, Hot Reload "Auto recompile unsupported changes" = on exit Play mode.

### Scene/object changes vs. code changes
Hot Reload only patches code. Changes to GameObjects, components, or serialized values made during Play mode (scale, inspector values, etc.) are lost when Play mode ends. When making such changes in Play mode, tell the user they are temporary and offer to re-apply them in edit mode (then save the scene) once Play mode has stopped.

## Verified behavior
- `script_eval` and `scene_get_hierarchy` still respond (in under 1s) in Play mode after a `.cs` file has changed. The old AI Assistant `Unity_RunCommand` timeout problem is gone.
- `unity cmd list` is not a valid command (it's ambiguous). Use `unity cmd <group> --help` or `list_commands`.
