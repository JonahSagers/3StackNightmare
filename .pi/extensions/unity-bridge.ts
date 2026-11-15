/**
 * Unity Bridge
 *
 * Runs a tiny HTTP server on localhost so the Unity Editor can inject prompts into this
 * pi session and read pi's output back.
 *
 *   GET  /health               -> { ok: true }
 *   POST /prompt               body: { prompt, scene?, target?, targetPath?, components?, selectedComponents? }
 *                              -> { ok: true, seq }   (seq = output position to read from)
 *   GET  /output?since=<seq>   -> { ok: true, seq, busy, chunks: [{ seq, kind, text }] }
 *
 * Output chunk kinds: "text" (assistant text delta), "tool" (tool call started/finished),
 * "user" (prompt that started a run), "status" (run started/finished).
 *
 * Port can be overridden with PI_UNITY_BRIDGE_PORT.
 */

import * as fs from "node:fs";
import * as http from "node:http";
import * as path from "node:path";
import type { ExtensionAPI, ExtensionContext } from "@earendil-works/pi-coding-agent";

const HOST = "127.0.0.1";
const PORT = Number(process.env.PI_UNITY_BRIDGE_PORT ?? 7777);
const MAX_BODY = 64 * 1024;
const MAX_CHUNKS = 5000;

interface PromptPayload {
	prompt?: string;
	scene?: string;
	target?: string;
	targetPath?: string;
	components?: string[];
	selectedComponents?: string[];
	selectedComponentFields?: string[];
	isPlaying?: boolean;
	editorPaused?: boolean;
	gamePaused?: boolean;
}

/** Condensed AGENTS.md rules, tailored to whether the game is running. */
function rulesFor(p: PromptPayload): string[] {
	const rules = [
		"No LINQ or Reflection in agent-run C# (script eval/run) or in new .cs code (blocked by unity-eval-guard); use loops, List<T>, GetComponent, FindObjectsByType, SerializedObject.",
		"Don't use input simulation (runtime_simulate_key / runtime_simulate_pointer); test via script eval or ask the user.",
		"Never recompile (no `unity recompile` / script_recompile); just save .cs files and Hot Reload patches them.",
		"After editing scripts, check `unity cmd console get --level error` for [HotReload] errors / \"Unsupported change\" warnings.",
	];
	if (p.isPlaying)
		rules.push(
			"Play mode is active: no new classes/structs/enums (they only apply after exiting Play mode); method/field edits are fine. Awake/Start won't re-run, so use lazy init.",
			"Play mode: script eval can't see members added by Hot Reload; scene/component changes are temporary (offer to re-apply in edit mode).",
		);
	else rules.push("Edit mode: scene/component changes persist only after saving the scene (scene_save).");
	return rules;
}

interface OutputChunk {
	seq: number;
	kind: "text" | "tool" | "user" | "status";
	text: string;
}

/** Map of class/struct name -> .cs files (relative to cwd) that declare it, scanned from Assets/. */
function indexScripts(cwd: string): Map<string, string[]> {
	const index = new Map<string, string[]>();
	const walk = (dir: string) => {
		let items: fs.Dirent[];
		try {
			items = fs.readdirSync(dir, { withFileTypes: true });
		} catch {
			return;
		}
		for (const it of items) {
			const p = path.join(dir, it.name);
			if (it.isDirectory()) walk(p);
			else if (it.name.endsWith(".cs")) {
				let src: string;
				try {
					src = fs.readFileSync(p, "utf8");
				} catch {
					continue;
				}
				src = src.replace(/\/\*[\s\S]*?\*\/|\/\/.*$/gm, "");
				const rel = path.relative(cwd, p);
				for (const m of src.matchAll(/\b(?:class|struct)\s+(\w+)/g)) {
					const list = index.get(m[1]) ?? [];
					if (!list.includes(rel)) list.push(rel);
					index.set(m[1], list);
				}
			}
		}
	};
	walk(path.join(cwd, "Assets"));
	return index;
}

/** "Type -> path" lines for the project scripts behind the selected/clicked components. */
function scriptPaths(p: PromptPayload, cwd: string): string[] {
	const types: string[] = [];
	const add = (t: string) => {
		const name = t.trim().split(".").pop()!;
		if (/^\w+$/.test(name) && !types.includes(name)) types.push(name);
	};
	for (const s of p.selectedComponents ?? []) add(s.replace(/ on '.*'$/s, ""));
	for (const c of p.components ?? []) add(c);
	if (!types.length) return [];
	const index = indexScripts(cwd);
	const out: string[] = [];
	for (const t of types) {
		const files = index.get(t);
		if (!files?.length) continue; // built-in Unity component or missing script
		// Prefer the file named after the type (Unity requires it for MonoBehaviours).
		files.sort((a, b) => Number(path.basename(b, ".cs") === t) - Number(path.basename(a, ".cs") === t));
		out.push(`${t} -> ${files.join(", ")}`);
	}
	return out;
}

function formatPrompt(p: PromptPayload, cwd = process.cwd()): string {
	const lines = [`[Unity] ${p.prompt!.trim()}`];
	const ctx: string[] = [];
	if (p.scene) ctx.push(`Scene: ${p.scene}`);
	if (p.target) ctx.push(`Clicked GameObject: ${p.targetPath || p.target}`);
	if (p.components?.length) ctx.push(`Its components: ${p.components.join(", ")}`);
	if (typeof p.isPlaying === "boolean") {
		const state = p.isPlaying ? "Play mode" : "Edit mode";
		const extras: string[] = [];
		if (p.editorPaused) extras.push("editor paused");
		if (p.gamePaused) extras.push("game paused via in-game pause menu");
		ctx.push(`Editor state: ${state}${extras.length ? ` (${extras.join(", ")})` : ""}`);
	}
	if (p.selectedComponents?.length) {
		ctx.push(`Selected components: ${p.selectedComponents.join(", ")}`);
		p.selectedComponents.forEach((c, i) => {
			const fields = p.selectedComponentFields?.[i];
			if (fields) ctx.push(`Current fields of ${c}: ${fields}`);
		});
	}
	const scripts = scriptPaths(p, cwd);
	if (scripts.length) ctx.push(`Script files: ${scripts.join("; ")}`);
	if (ctx.length) lines.push("", "Context from the Unity Editor:", ...ctx.map((c) => `- ${c}`));
	lines.push("", "Rules for this project:", ...rulesFor(p).map((r) => `- ${r}`));
	return lines.join("\n");
}

/** Short one-line summary of tool arguments for the Unity panel. */
function summarizeArgs(args: unknown): string {
	if (!args || typeof args !== "object") return "";
	const a = args as Record<string, unknown>;
	const pick = a.path ?? a.command ?? a.Title ?? a.title ?? a.query ?? a.name;
	if (typeof pick !== "string") return "";
	const oneLine = pick.replace(/\s+/g, " ").trim();
	return oneLine.length > 80 ? `${oneLine.slice(0, 77)}...` : oneLine;
}

function textOf(content: unknown): string {
	if (typeof content === "string") return content;
	if (Array.isArray(content))
		return content
			.filter((c: any) => c?.type === "text" && typeof c.text === "string")
			.map((c: any) => c.text)
			.join("\n");
	return "";
}

export default function (pi: ExtensionAPI) {
	let server: http.Server | undefined;
	let ctxRef: ExtensionContext | undefined;

	// ---- Output buffer -------------------------------------------------------------
	let seq = 0;
	let busy = false;
	const chunks: OutputChunk[] = [];

	const push = (kind: OutputChunk["kind"], text: string) => {
		if (!text) return;
		chunks.push({ seq: ++seq, kind, text });
		if (chunks.length > MAX_CHUNKS) chunks.splice(0, chunks.length - MAX_CHUNKS);
	};

	pi.on("agent_start", async () => {
		busy = true;
		push("status", "working");
	});

	pi.on("agent_settled", async (event) => {
		busy = false;
		push("status", event.aborted ? "aborted" : "done");
	});

	pi.on("message_start", async (event) => {
		const m: any = event.message;
		if (m?.role === "user") push("user", textOf(m.content));
		if (m?.role === "assistant") push("text", "\n");
	});

	pi.on("message_update", async (event) => {
		const e: any = event.assistantMessageEvent;
		if (e?.type === "text_delta" && typeof e.delta === "string") push("text", e.delta);
	});

	pi.on("tool_execution_start", async (event) => {
		const summary = summarizeArgs(event.args);
		push("tool", `> ${event.toolName}${summary ? `  ${summary}` : ""}`);
	});

	pi.on("tool_execution_end", async (event) => {
		if (event.isError) push("tool", `x ${event.toolName} failed`);
	});

	// ---- HTTP server ---------------------------------------------------------------
	const reply = (res: http.ServerResponse, status: number, body: object) => {
		res.writeHead(status, { "Content-Type": "application/json" });
		res.end(JSON.stringify(body));
	};

	const handleOutput = (url: URL, res: http.ServerResponse) => {
		const since = Number(url.searchParams.get("since") ?? 0) || 0;
		const out = chunks.filter((c) => c.seq > since);
		reply(res, 200, { ok: true, seq, busy, chunks: out });
	};

	const handlePrompt = (req: http.IncomingMessage, res: http.ServerResponse) => {
		let body = "";
		req.on("data", (chunk) => {
			body += chunk;
			if (body.length > MAX_BODY) req.destroy();
		});
		req.on("end", () => {
			let payload: PromptPayload;
			try {
				payload = JSON.parse(body);
			} catch {
				return reply(res, 400, { ok: false, error: "invalid JSON" });
			}
			if (!payload.prompt?.trim()) return reply(res, 400, { ok: false, error: "empty prompt" });

			const text = formatPrompt(payload, ctxRef?.cwd);
			const startSeq = seq; // Unity reads output from here on
			try {
				// When pi is busy, queue it until the current run finishes.
				if (ctxRef && !ctxRef.isIdle()) pi.sendUserMessage(text, { deliverAs: "followUp" });
				else pi.sendUserMessage(text);
				reply(res, 202, { ok: true, seq: startSeq });
			} catch (err) {
				reply(res, 500, { ok: false, error: String(err) });
			}
		});
	};

	pi.on("session_start", async (_event, ctx) => {
		ctxRef = ctx;
		if (server) return;

		server = http.createServer((req, res) => {
			const url = new URL(req.url ?? "/", `http://${HOST}`);
			if (req.method === "GET" && url.pathname === "/health") return reply(res, 200, { ok: true });
			if (req.method === "GET" && url.pathname === "/output") return handleOutput(url, res);
			if (req.method === "POST" && url.pathname === "/prompt") return handlePrompt(req, res);
			reply(res, 404, { ok: false, error: "not found" });
		});

		server.on("error", (err: NodeJS.ErrnoException) => {
			const msg =
				err.code === "EADDRINUSE"
					? `Unity bridge: port ${PORT} is already in use (another pi session?)`
					: `Unity bridge error: ${err.message}`;
			if (ctx.hasUI) ctx.ui.notify(msg, "error");
			server = undefined;
		});

		server.listen(PORT, HOST, () => {
			if (ctx.hasUI) ctx.ui.notify(`Unity bridge listening on http://${HOST}:${PORT}`, "info");
		});
	});

	pi.on("session_shutdown", async () => {
		server?.close();
		server = undefined;
		ctxRef = undefined;
	});
}
