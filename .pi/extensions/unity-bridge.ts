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

import * as http from "node:http";
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
}

interface OutputChunk {
	seq: number;
	kind: "text" | "tool" | "user" | "status";
	text: string;
}

function formatPrompt(p: PromptPayload): string {
	const lines = [`[Unity] ${p.prompt!.trim()}`];
	const ctx: string[] = [];
	if (p.scene) ctx.push(`Scene: ${p.scene}`);
	if (p.target) ctx.push(`Clicked GameObject: ${p.targetPath || p.target}`);
	if (p.components?.length) ctx.push(`Its components: ${p.components.join(", ")}`);
	if (p.selectedComponents?.length) ctx.push(`Selected components: ${p.selectedComponents.join(", ")}`);
	if (ctx.length) lines.push("", "Context from the Unity Editor:", ...ctx.map((c) => `- ${c}`));
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

			const text = formatPrompt(payload);
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
