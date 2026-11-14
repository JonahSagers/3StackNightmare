# Project notes for agents

Unity 6 project (`SampleScene`), new Input System only, URP. Unity is controlled through the `mcp__unity_mcp` tools (use `Unity_RunCommand` from codemode). The user often sends prompts from inside the game via `.pi/extensions/unity-bridge.ts` — those arrive as `[Unity] ...` messages with editor context.

## Hot Reload workflow (com.singularitygroup.hotreload)

Script edits must take effect immediately, including during Play mode. Hot Reload patches code when `.cs` files are saved to disk.

### Do
- **Just save the file.** Writing/editing a `.cs` file is enough; Hot Reload detects the change on disk.
- **Verify via the Console** (`Unity_GetConsoleLogs`): look for Hot Reload logs and "Unsupported change" warnings. `EditorUtility.scriptCompilationFailed` does not reflect hot patches.
- **Check `EditorApplication.isPlaying`** before editing a script, and keep Play-mode edits within what Hot Reload supports:
  - OK: method body edits, adding methods, adding/changing fields and field initializers.
  - Not OK: adding new classes/structs/enums — make those changes only outside Play mode (or tell the user it applies after exiting Play mode).
  - No multidimensional arrays (`[,]`) in methods; use jagged arrays (`[][]`).
  - `Awake`/`Start` do not re-run on existing objects; put new initialization logic somewhere lazy (e.g. first use in `Update`) or note that it only affects new instances.
- **When MCP returns "Unity not detected"**, Unity is reloading; wait a few seconds and retry instead of reporting failure.

### Don't
- **Never call `AssetDatabase.Refresh()`, `CompilationPipeline.RequestScriptCompilation()`, or otherwise force a recompile** after editing scripts. This causes full domain reloads and MCP timeouts, and is unnecessary with Hot Reload.
- **Don't change these editor settings** (Hot Reload manages them): Unity Auto Refresh = off, Script Changes While Playing = Recompile After Finished Playing, Hot Reload "Auto recompile unsupported changes" = on exit Play mode.

### Scene/object changes vs. code changes
Hot Reload only patches code. Changes to GameObjects, components, or serialized values made during Play mode (scale, inspector values, etc.) are lost when Play mode ends. When making such changes in Play mode, tell the user they are temporary and offer to re-apply them in edit mode (then mark the scene dirty) once Play mode has stopped.

### Prerequisites the user handles
- Hot Reload must be running (Window → Hot Reload → Start) before entering Play mode.
- No debugger attached unless Hot Reload's debugger compatibility is enabled.

## MCP commands that time out
- **`Unity_RunCommand`**: times out (60s client limit) in Play mode once a `.cs` file has changed since entering Play mode (even a trivial `result.Log("hi")` hangs). Cause: the tool always calls `AssetDatabase.Refresh()` and then waits up to 120s for `!EditorApplication.isCompiling && !isUpdating` (`EditorReadyHelper.RefreshAndWaitForReady`). With "Recompile After Finished Playing", the recompile is deferred, so Unity reports compiling for the rest of the session. The server later logs `COMPILATION_IN_PROGRESS`, but only after the client has already timed out. No workaround found from MCP. Have the user exit Play mode (or don't edit scripts mid-play if RunCommand is still needed), then it works again. Retrying doesn't help. Other tools (`GetConsoleLogs`, `Capture2DScene`, `AssetGeneration_GetModels`) still respond in under 1s while this happens.

## Other MCP tool notes (not timeouts)
- `Unity_Camera_Capture` (with no camera ID) and `Unity_SceneView_CaptureMultiAngleSceneView` fail right away if no Scene View window is open. Pass a `cameraInstanceID` to `Camera_Capture`, or ask the user to open a Scene View.
- `Unity_AssetGeneration_GetModels` returns an empty list, and the console shows `NoSubscription`. That means `Unity_AssetGeneration_GenerateAsset` has no models to use. It also requires asking the user for consent once per conversation before the first call.

## MCP sandbox limits
- `System.Reflection` is not allowed in `Unity_RunCommand` scripts.
- APIs that show dialogs (e.g. `result.DestroyObject`, `AssetDatabase.DeleteAsset` in some cases) fail with "User interactions are not supported" — use `Undo.DestroyObjectImmediate` for objects and delete files via bash.
