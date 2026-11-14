/**
 * Unity Bridge
 *
 * Runs a tiny HTTP server on localhost so the Unity Editor (MouseHandler.SendPrompt)
 * can inject prompts into this pi session.
 *
 *   GET  http://127.0.0.1:7777/health   -> { ok: true }
 *   POST http://127.0.0.1:7777/prompt   body: { prompt, target?, targetPath?, components?, selectedComponents? }
 *
 * Port can be overridden with PI_UNITY_BRIDGE_PORT.
 */

import * as http from "node:http";
import type { ExtensionAPI, ExtensionContext } from "@earendil-works/pi-coding-agent";

const HOST = "127.0.0.1";
const PORT = Number(process.env.PI_UNITY_BRIDGE_PORT ?? 7777);
const MAX_BODY = 64 * 1024;

interface PromptPayload {
	prompt?: string;
	scene?: string;
	target?: string;
	targetPath?: string;
	components?: string[];
	selectedComponents?: string[];
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

export default function (pi: ExtensionAPI) {
	let server: http.Server | undefined;
	let ctxRef: ExtensionContext | undefined;

	const reply = (res: http.ServerResponse, status: number, body: object) => {
		res.writeHead(status, { "Content-Type": "application/json" });
		res.end(JSON.stringify(body));
	};

	pi.on("session_start", async (_event, ctx) => {
		ctxRef = ctx;
		if (server) return;

		server = http.createServer((req, res) => {
			if (req.method === "GET" && req.url === "/health") return reply(res, 200, { ok: true });
			if (req.method !== "POST" || req.url !== "/prompt") return reply(res, 404, { ok: false, error: "not found" });

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
				try {
					// When pi is busy, queue it until the current run finishes.
					if (ctxRef && !ctxRef.isIdle()) pi.sendUserMessage(text, { deliverAs: "followUp" });
					else pi.sendUserMessage(text);
					reply(res, 202, { ok: true });
				} catch (err) {
					reply(res, 500, { ok: false, error: String(err) });
				}
			});
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
