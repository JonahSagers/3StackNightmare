/**
 * Regression Gate: a separate reviewer agent checks every agent edit
 *
 * The agent that makes an edit never grades its own work:
 *
 *   Step 1  When a request starts, a balance baseline is saved in Unity (BalanceSnapshot.SaveBaseline()).
 *   Step 2  While the editing agent works, every game file is backed up right before it's changed
 *           (.pi/regression-backups/<run>/manifest.txt), so this request's edits can be undone exactly.
 *   Step 3  When the editing agent finishes, a SEPARATE pi process (the reviewer) starts with a fresh context.
 *   Step 4  The reviewer runs the regression checks (RegressionReporter.RunAll(), one check at a time).
 *   Step 5  It compares balance values against the baseline and judges the change against the request,
 *           reverting from the backups if a rule broke.
 *   Step 6  Verdict: PASS / FAIL / REVERTED (or ERROR).
 *
 * Progress is written to Library/RegressionStatus/review.txt; the in-game panel and the
 * Regression Monitor window show it as a step counter + progress bar. The reviewer's full output goes
 * to Library/RegressionStatus/reviewer-log.txt.
 *
 * The reviewer runs with PI_REGRESSION_REVIEWER=1, which turns this gate off inside it (no loops) and
 * stops the Unity bridge from opening a second server. Override the command with PI_REVIEWER_CMD
 * (default "pi"); it's called as `<cmd> -p "<review prompt>"`.
 */

import { spawn } from "node:child_process";
import * as fs from "node:fs";
import * as path from "node:path";
import type { ExtensionAPI, ExtensionContext } from "@earendil-works/pi-coding-agent";

const IS_REVIEWER = !!process.env.PI_REGRESSION_REVIEWER;
const REVIEWER_CMD = process.env.PI_REVIEWER_CMD ?? "pi";
const REVIEW_TIMEOUT_MS = 6 * 60 * 1000;

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
			child.kill();
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
		const out = await runProcess("unity", ["cmd", "script", "eval", "--code", "return BalanceSnapshot.SaveBaseline();"], cwd(), process.env, 20000);
		if (!/baseline saved/i.test(out)) notify(`Regression gate: couldn't save balance baseline (${out.trim().slice(0, 200)})`, "warning");

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

			// Step 3: reviewer starts. Steps 4-5 are detected from the checks/balance status files.
			writeReview("RUNNING", "Reviewer agent started");
			notify("Regression reviewer started (separate agent).", "info");

			const prompt = [
				"You are the REGRESSION REVIEWER for this Unity project. Follow AGENTS.md.",
				`A different agent just made changes for this user request: <<< ${request} >>>`,
				`Files it changed: ${files.replace(/\n/g, " ")}`,
				backedUp.size > 0
					? `Originals were backed up before editing. ${manifest} maps each changed file to its backup (tab-separated; NEW = the file didn't exist before).`
					: "Only scene objects changed; there are no file backups, so you can report but not revert.",
				"Do NOT add features or adjust the change. Only verify it, in this order:",
				"(1) Script eval `return RegressionReporter.RunAll();`. It runs every regression check one at a time (works in Play and Edit mode) and then compares balance values. Wait until Library/RegressionStatus/tests.txt has a summary timestamp newer than when you started, then read it, and read Library/RegressionStatus/balance.txt for the value changes.",
				"(2) Judge it: PASS if every check passed and every changed value is something the request asked for. FAIL if a check failed or values changed that the request didn't ask for.",
				"(3) If it FAILS because the change broke a rule (not because the user deliberately asked to change that rule), revert: copy each backup over its original and delete files marked NEW, then run RegressionReporter.RunAll() again to confirm everything passes.",
				"(4) If a check fails because the user deliberately changed that rule, don't revert: report FAIL and name the check in Assets/Scripts/Regression/RegressionSuite.cs that needs updating.",
				"End your reply with exactly one line in this format: VERDICT: PASS|FAIL|REVERTED - <one short sentence: check results + what changed>",
			].join("\n");

			const env = { ...process.env, PI_REGRESSION_REVIEWER: "1" };
			const output = await runProcess(REVIEWER_CMD, ["-p", prompt], cwd(), env, REVIEW_TIMEOUT_MS);

			try {
				fs.mkdirSync(statusDir(), { recursive: true });
				fs.writeFileSync(
					path.join(statusDir(), "reviewer-log.txt"),
					`${new Date().toISOString()}\n\n--- PROMPT ---\n${prompt}\n\n--- OUTPUT ---\n${output}\n`,
				);
			} catch {
				/* log is best effort */
			}

			// Step 6: verdict.
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
