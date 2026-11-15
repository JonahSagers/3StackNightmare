/**
 * Regression Gate: every agent edit is checked, fast
 *
 *   Step 1  When a request starts, Unity saves a balance baseline (every tunable value in the scene).
 *   Step 2  While the editing agent works, every game file is backed up right before it changes
 *           (.pi/regression-backups/<run>/manifest.txt), so this request's edits can be undone exactly.
 *   Step 3-4  When the editing agent finishes, Unity runs every regression check (no AI needed; a few seconds).
 *   Step 5  Unity compares balance values against the baseline.
 *   Step 6  Verdict. All checks passed -> PASS right away, with the list of changed values.
 *           Any check failed -> a SEPARATE reviewer agent (second pi process, fresh context) decides
 *           whether the change broke a rule (revert from backups -> REVERTED) or the user deliberately
 *           changed that rule (FAIL + which check to update).
 *
 * The gate talks to Unity through Library/RegressionStatus/request.txt ("baseline" / "run"), which
 * Assets/Editor/RegressionReporter.cs watches, so it doesn't depend on the unity command line.
 * If Unity doesn't answer (closed, compiling, busy), the reviewer agent does the whole check instead.
 *
 * Progress goes to Library/RegressionStatus/review.txt (in-game panel + Window > Regression Monitor).
 * The reviewer runs with PI_REGRESSION_REVIEWER=1 (turns this gate off inside it, and keeps the
 * Unity bridge from opening a second server). Override its command with PI_REVIEWER_CMD (default "pi").
 */

import { spawn } from "node:child_process";
import * as fs from "node:fs";
import * as path from "node:path";
import type { ExtensionAPI, ExtensionContext } from "@earendil-works/pi-coding-agent";

const IS_REVIEWER = !!process.env.PI_REGRESSION_REVIEWER;
const REVIEWER_CMD = process.env.PI_REVIEWER_CMD ?? "pi";
// The reviewer is a full agent run (reading files, calling Unity, thinking), which can take several minutes.
const REVIEW_TIMEOUT_MS = 15 * 60 * 1000;

const GAME_FILE = /\.(cs|unity|prefab|mat|asset|shader)$/i;
const UNITY_MUTATING_TOOL = /^mcp__unity__.*(set_|create|delete|destroy|batch|add_|remove_|save|instantiate)/i;

function textOf(content: unknown): string {
	if (typeof content === "string") return content;
	if (Array.isArray(content))
		return content
			.filter((c: any) => c?.type === "text" && typeof c.text === "string")
			.map((c: any) => c.text)
			.join("\n");
	return "";
}

// On Windows, `pi` and `unity` are .cmd shims that only start through the shell, and the shell splits
// unquoted arguments at spaces and stops at the first newline. So every argument is quoted, kept on one
// line, and stripped of characters cmd.exe would reinterpret (" and %).
function winQuote(arg: string): string {
	return `"${arg.replace(/[\r\n]+/g, " ").replace(/"/g, "'").replace(/%/g, " percent")}"`;
}

function runProcess(cmd: string, args: string[], cwd: string, env: NodeJS.ProcessEnv, timeoutMs: number): Promise<string> {
	return new Promise((resolve) => {
		let out = "";
		let child: ReturnType<typeof spawn>;
		try {
			child =
				process.platform === "win32"
					? spawn([cmd, ...args].map(winQuote).join(" "), { cwd, env, shell: true })
					: spawn(cmd, args, { cwd, env });
		} catch (err) {
			return resolve(`VERDICT: ERROR - couldn't start ${cmd} (${String(err)})`);
		}
		const timer = setTimeout(() => {
			out += `\nVERDICT: ERROR - ${cmd} timed out after ${Math.round(timeoutMs / 1000)}s`;
			// On Windows, kill() only stops the cmd.exe wrapper; taskkill /T stops the whole process tree.
			if (process.platform === "win32" && child.pid) spawn("taskkill", ["/pid", String(child.pid), "/T", "/F"]);
			else child.kill();
		}, timeoutMs);
		child.stdout?.on("data", (d) => (out += d));
		child.stderr?.on("data", (d) => (out += d));
		child.on("error", (err) => {
			clearTimeout(timer);
			resolve(`${out}\nVERDICT: ERROR - couldn't start ${cmd} (${err.message})`);
		});
		child.on("close", () => {
			clearTimeout(timer);
			resolve(out);
		});
	});
}

function sleep(ms: number) {
	return new Promise((r) => setTimeout(r, ms));
}

/** Unix time on the "summary" line of a status file, or 0. */
function summaryTime(file: string): number {
	try {
		const first = fs.readFileSync(file, "utf8").split(/\r?\n/)[0].split("\t");
		return first[0] === "summary" ? Number(first[1]) || 0 : 0;
	} catch {
		return 0;
	}
}

async function waitFor(check: () => boolean, timeoutMs: number): Promise<boolean> {
	const end = Date.now() + timeoutMs;
	while (Date.now() < end) {
		if (check()) return true;
		await sleep(200);
	}
	return check();
}

export default function (pi: ExtensionAPI) {
	if (IS_REVIEWER) return; // we ARE the reviewer: don't gate ourselves

	let ctxRef: ExtensionContext | undefined;
	let request = "";
	let runDir = "";
	let backedUp = new Map<string, string>(); // original path -> backup path ("NEW" if it didn't exist)
	let changedScene = false;
	let reviewing = false;
	let reviewStart = 0;

	const cwd = () => ctxRef?.cwd ?? process.cwd();
	const statusDir = () => path.join(cwd(), "Library", "RegressionStatus");
	const now = () => Math.floor(Date.now() / 1000);

	// review.txt: summary <time> <state> <text> <reviewStart>
	// state: BASELINE | EDITING | RUNNING | PASS | FAIL | REVERTED | ERROR
	const writeReview = (state: string, text: string) => {
		try {
			fs.mkdirSync(statusDir(), { recursive: true });
			const clean = text.replace(/[\t\r\n]+/g, " ").trim();
			fs.writeFileSync(path.join(statusDir(), "review.txt"), `summary\t${now()}\t${state}\t${clean}\t${reviewStart}\n`);
		} catch {
			/* status display only; never break the agent over it */
		}
	};

	const notify = (msg: string, level: "info" | "warning" | "error") => {
		if (ctxRef?.hasUI) ctxRef.ui.notify(msg, level);
	};

	const statusFile = (name: string) => path.join(statusDir(), name);

	/** Asks Unity (RegressionReporter) to do something, by appending to request.txt. */
	const requestUnity = (what: "baseline" | "run") => {
		fs.mkdirSync(statusDir(), { recursive: true });
		fs.appendFileSync(statusFile("request.txt"), what + "\n");
	};

	const readTests = () => {
		let passed = 0;
		const failures: string[] = [];
		try {
			for (const line of fs.readFileSync(statusFile("tests.txt"), "utf8").split(/\r?\n/)) {
				const f = line.split("\t");
				if (f[0] === "summary") passed = Number(f[2]) || 0;
				else if (f[0] === "fail") failures.push(`${f[1]}: ${f[2] ?? ""}`.trim());
			}
		} catch {
			/* missing file -> treated as no data */
		}
		return { passed, failures };
	};

	const readChanges = () => {
		const changes: string[] = [];
		let hasBaseline = false;
		try {
			for (const line of fs.readFileSync(statusFile("balance.txt"), "utf8").split(/\r?\n/)) {
				const f = line.split("\t");
				if (f[0] === "summary") hasBaseline = f[3] !== "nobaseline";
				else if (f[0] === "change") changes.push(`${f[1]} ${f[2]} ${f[3]} -> ${f[4]}`);
			}
		} catch {
			/* no file */
		}
		return { changes, hasBaseline };
	};

	/** Runs the separate reviewer agent and records its verdict. */
	const runReviewer = async (prompt: string) => {
		const env = { ...process.env, PI_REGRESSION_REVIEWER: "1" };
		const output = await runProcess(REVIEWER_CMD, ["-p", prompt], cwd(), env, REVIEW_TIMEOUT_MS);
		try {
			fs.writeFileSync(statusFile("reviewer-log.txt"), `${new Date().toISOString()}\n\n--- PROMPT ---\n${prompt}\n\n--- OUTPUT ---\n${output}\n`);
		} catch {
			/* log is best effort */
		}
		let verdict = "ERROR";
		let summary = "Reviewer finished without a verdict line (see Library/RegressionStatus/reviewer-log.txt).";
		const lines = output.trim().split(/\r?\n/);
		for (let i = lines.length - 1; i >= 0; i--) {
			const m = lines[i].match(/VERDICT:\s*(PASS|FAIL|REVERTED|ERROR)\s*[-:]?\s*(.*)$/i);
			if (m) {
				verdict = m[1].toUpperCase();
				summary = m[2] || summary;
				break;
			}
		}
		writeReview(verdict, summary);
		notify(`Regression reviewer: ${verdict} - ${summary}`, verdict === "PASS" ? "info" : verdict === "REVERTED" ? "warning" : "error");
	};

	pi.on("session_start", async (_event, ctx) => {
		ctxRef = ctx;
	});

	pi.on("message_start", async (event) => {
		const m: any = event.message;
		if (m?.role === "user") request = textOf(m.content).slice(0, 4000);
	});

	pi.on("agent_start", async () => {
		if (reviewing) return;
		backedUp = new Map();
		changedScene = false;
		reviewStart = now();
		runDir = path.join(cwd(), ".pi", "regression-backups", String(Date.now()));

		// Step 1: "before" picture for the balance diff.
		writeReview("BASELINE", "Saving balance baseline...");
		const asked = now();
		requestUnity("baseline");
		const saved = await waitFor(() => summaryTime(statusFile("balance.txt")) >= asked, 10000);
		if (!saved) notify("Regression gate: Unity didn't save a balance baseline (is the Editor open and not compiling?)", "warning");

		// Step 2: the editing agent works.
		writeReview("EDITING", "Editing agent is making the change...");
	});

	pi.on("tool_call", async (event) => {
		const input = event.input as Record<string, unknown>;
		const name = event.toolName;

		if ((name === "write" || name === "edit") && typeof input.path === "string" && GAME_FILE.test(input.path)) {
			const original = path.resolve(cwd(), input.path);
			if (!backedUp.has(original)) {
				// Back up BEFORE the edit runs (tool_call fires before execution).
				fs.mkdirSync(runDir, { recursive: true });
				if (fs.existsSync(original)) {
					const backup = path.join(runDir, path.relative(cwd(), original));
					fs.mkdirSync(path.dirname(backup), { recursive: true });
					fs.copyFileSync(original, backup);
					backedUp.set(original, backup);
				} else backedUp.set(original, "NEW");
				const manifest = [...backedUp]
					.map(([o, b]) => `${path.relative(cwd(), o)}\t${b === "NEW" ? "NEW" : path.relative(cwd(), b)}`)
					.join("\n");
				fs.writeFileSync(path.join(runDir, "manifest.txt"), manifest + "\n");
			}
		}
		if (UNITY_MUTATING_TOOL.test(name)) changedScene = true;
	});

	pi.on("agent_settled", async (event) => {
		if (reviewing) return;
		if (event.aborted || (backedUp.size === 0 && !changedScene)) {
			writeReview("", ""); // nothing to review: clear the pipeline display
			return;
		}

		reviewing = true;
		try {
			const files = [...backedUp.keys()].map((f) => `- ${path.relative(cwd(), f)}`).join("\n") || "- (scene objects only, no file backups)";
			const manifest = path.relative(cwd(), path.join(runDir, "manifest.txt"));

			// Steps 3-5: Unity runs the checks, then the balance comparison. No AI needed.
			writeReview("RUNNING", "Running regression checks");
			const asked = now();
			requestUnity("run");
			const answered = await waitFor(
				() => summaryTime(statusFile("tests.txt")) >= asked && summaryTime(statusFile("balance.txt")) >= asked,
				30000,
			);

			if (answered) {
				const { passed, failures } = readTests();
				const { changes, hasBaseline } = readChanges();
				const changeText = !hasBaseline ? "no balance baseline, so value changes weren't compared" :
					changes.length === 0 ? "no tunable values changed" : `changed: ${changes.slice(0, 3).join(", ")}${changes.length > 3 ? ` (+${changes.length - 3} more)` : ""}`;

				if (failures.length === 0) {
					// Step 6, fast path.
					const summary = `${passed}/${passed} checks passed; ${changeText}`;
					writeReview("PASS", summary);
					notify(`Regression check: PASS - ${summary}`, "info");
					return;
				}

				// A check failed: the reviewer agent decides what to do about it.
				writeReview("REVIEWING", `${failures.length} check(s) failed`);
				notify(`Regression check: ${failures.length} failed - starting reviewer agent`, "warning");
				const prompt = [
					"You are the REGRESSION REVIEWER for this Unity project. Follow AGENTS.md.",
					`A different agent just made changes for this user request: <<< ${request} >>>`,
					`Files it changed: ${files.replace(/\n/g, " ")}`,
					backedUp.size > 0
						? `Originals were backed up before editing. ${manifest} maps each changed file to its backup (tab-separated; NEW = the file didn't exist before).`
						: "Only scene objects changed; there are no file backups, so you can report but not revert.",
					`The regression checks were already run. These FAILED: ${failures.join(" | ")}.`,
					`Balance values ${changeText}.`,
					"Do NOT add features or adjust the change. Decide:",
					"(a) If the change broke a rule the user did not ask to change: revert (copy each backup over its original, delete files marked NEW), then script eval `return RegressionReporter.RunAll();`, wait for Library/RegressionStatus/tests.txt to update, and confirm every check passes. Verdict REVERTED.",
					"(b) If the user deliberately asked to change that rule: don't revert. Verdict FAIL, and name the check in Assets/Scripts/Regression/RegressionSuite.cs that needs updating.",
					"End your reply with exactly one line in this format: VERDICT: FAIL|REVERTED - <one short sentence>",
				].join("\n");
				await runReviewer(prompt);
				return;
			}

			// Unity didn't answer: let the reviewer agent do the whole check with its own tools.
			writeReview("REVIEWING", "Unity didn't respond; reviewer agent is running the checks");
			notify("Regression check: Unity didn't respond, starting reviewer agent", "warning");
			const prompt = [
				"You are the REGRESSION REVIEWER for this Unity project. Follow AGENTS.md.",
				`A different agent just made changes for this user request: <<< ${request} >>>`,
				`Files it changed: ${files.replace(/\n/g, " ")}`,
				backedUp.size > 0
					? `Originals were backed up before editing. ${manifest} maps each changed file to its backup (tab-separated; NEW = the file didn't exist before).`
					: "Only scene objects changed; there are no file backups, so you can report but not revert.",
				"Do NOT add features or adjust the change. Only verify it:",
				"(1) Script eval `return RegressionReporter.RunAll();`, wait until Library/RegressionStatus/tests.txt has a newer summary timestamp, then read it and Library/RegressionStatus/balance.txt.",
				"(2) PASS if every check passed and every changed value is something the request asked for; otherwise FAIL.",
				"(3) If it fails because the change broke a rule the user didn't ask to change, revert from the backups and re-run RunAll() to confirm. Verdict REVERTED.",
				"End your reply with exactly one line in this format: VERDICT: PASS|FAIL|REVERTED - <one short sentence>",
			].join("\n");
			await runReviewer(prompt);
		} catch (err) {
			writeReview("ERROR", `Regression gate crashed: ${String(err)}`);
		} finally {
			reviewing = false;
		}
	});

	pi.on("session_shutdown", async () => {
		ctxRef = undefined;
	});
}
