/**
 * Test Source Guard
 *
 * While pi is working on a prompt sent from Unity (user message starting with "[Unity]"), it may not
 * look at (or change) the test cases, so solutions are written against the game's behavior instead of
 * being fitted to the tests. Test OUTPUT is fine: running tests (RegressionSuite.Run / StartRun via
 * script_eval, mcp__unity__test_run / test_status, `unity cmd test ...`), the console, and the status
 * files in Library/RegressionStatus stay readable.
 *
 * What it does during Unity runs:
 *   - blocks read/edit/write (and grep/find/ls path args) on test sources
 *   - blocks bash commands and Unity MCP calls (asset_read_text, script_eval `file`/code, ...) that name test sources
 *   - redacts test-source lines from search output (grep/rg "path:line:" lines, `head`/`tail` "==> path <==" sections)
 *
 * Prompts typed directly into pi (not from Unity) are not restricted.
 */

import * as path from "node:path";
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";

/** Test sources, relative to the project root (forward slashes). Directories end with "/". */
const TEST_SOURCES = [
	"Assets/Tests/",
	"Assets/Scripts/Regression/",
	"Assets/Editor/RegressionWindow.cs",
	"Assets/Editor/RegressionReporter.cs",
];

/** Fragments that identify a test source when they appear anywhere in a command or tool input. */
const TEST_FRAGMENTS = [
	"Assets/Tests",
	"Tests/Editor",
	"Scripts/Regression",
	"Regression/Regression",
	"GameRulesTests",
	"RegressionSuite.cs",
	"RegressionHUD.cs",
	"RegressionStatusFiles.cs",
	"RegressionWindow.cs",
	"RegressionReporter.cs",
];

const REASON =
	"Blocked by test-source-guard: test cases are hidden while working on a prompt from Unity, so the fix " +
	"must come from the game's behavior, not the tests. Test output is allowed: run the checks " +
	"(e.g. script_eval `RegressionSuite.Run(name)` over `RegressionSuite.Names()`, or mcp__unity__test_run) " +
	"and read the results / console / Library/RegressionStatus.";

function normalize(p: string, cwd: string): string {
	const abs = path.resolve(cwd, p.trim().replace(/^@/, ""));
	return path.relative(cwd, abs).split(path.sep).join("/");
}

function isTestPath(p: unknown, cwd: string): boolean {
	if (typeof p !== "string" || !p.trim()) return false;
	const rel = normalize(p, cwd);
	if (rel.endsWith(".meta")) return false; // .meta files hold no test logic
	for (const t of TEST_SOURCES) {
		if (t.endsWith("/") ? rel === t.slice(0, -1) || rel.startsWith(t) : rel === t) return true;
	}
	return false;
}

function mentionsTests(text: string): boolean {
	for (const f of TEST_FRAGMENTS) if (text.includes(f)) return true;
	return false;
}

/** Drops test-source lines from search/listing output. */
function redact(text: string, cwd: string): { text: string; removed: number } {
	let removed = 0;
	let skipping = false;
	const out: string[] = [];
	for (const line of text.split("\n")) {
		const header = line.match(/^==> (.+) <==$/); // head/tail multi-file headers
		if (header) skipping = isTestPath(header[1], cwd);
		// grep/rg: "path:12:text", "path-12-text" (context), "path:text", or a bare path
		const m = line.match(/^(.+?\.cs)(?:[:-]\d+[:-]|:|$)/);
		if (skipping || (m && (isTestPath(m[1], cwd) || mentionsTests(m[1])))) {
			removed++;
			continue;
		}
		out.push(line);
	}
	return { text: out.join("\n"), removed };
}

export default function (pi: ExtensionAPI) {
	let unityRun = false;

	pi.on("message_start", async (event) => {
		const m: any = event.message;
		if (m?.role !== "user") return;
		const content = m.content;
		const text =
			typeof content === "string"
				? content
				: Array.isArray(content)
					? content.filter((c: any) => c?.type === "text").map((c: any) => c.text).join("\n")
					: "";
		unityRun = text.trimStart().startsWith("[Unity]");
	});

	pi.on("agent_settled", async () => {
		unityRun = false;
	});

	pi.on("tool_call", async (event, ctx) => {
		if (!unityRun) return;
		const cwd = ctx.cwd ?? process.cwd();
		const input = (event.input ?? {}) as Record<string, unknown>;
		const tool = event.toolName;

		if (["read", "edit", "write", "grep", "find", "ls"].includes(tool)) {
			if (isTestPath(input.path, cwd)) return { block: true, reason: REASON };
			if (tool === "grep" || tool === "find") {
				if (typeof input.glob === "string" && mentionsTests(input.glob)) return { block: true, reason: REASON };
			}
			return;
		}

		if (tool === "bash" && typeof input.command === "string") {
			if (mentionsTests(input.command)) return { block: true, reason: REASON };
			return;
		}

		if (tool.startsWith("mcp__unity__")) {
			if (mentionsTests(JSON.stringify(input))) return { block: true, reason: REASON };
			for (const key of ["path", "file", "asset", "asset-path"]) {
				if (isTestPath(input[key], cwd)) return { block: true, reason: REASON };
			}
		}
	});

	// Broad searches (e.g. `rg Health Assets`) can still sweep up test files; strip those lines.
	pi.on("tool_result", async (event, ctx) => {
		if (!unityRun) return;
		if (!["bash", "grep", "find", "ls"].includes(event.toolName) && !event.toolName.startsWith("mcp__unity__asset_")) return;
		const cwd = ctx.cwd ?? process.cwd();
		let removed = 0;
		const content = (event.content ?? []).map((c: any) => {
			if (c?.type !== "text" || typeof c.text !== "string") return c;
			const r = redact(c.text, cwd);
			removed += r.removed;
			return { ...c, text: r.text };
		});
		if (!removed) return;
		content.push({ type: "text", text: `[test-source-guard: ${removed} line(s) from test sources hidden]` });
		return { content };
	});
}
