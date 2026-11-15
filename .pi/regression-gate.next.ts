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
 * Unity prompts ("[Unity] ..." messages) also get a BEFORE run: the checks run (like pressing F5) as soon as
 * the request arrives, and the agent's first edit waits for those results. After the change, any check that
 * passed before and fails now is a regression: the editing agent gets the failure output as a follow-up
 * and reworks its solution (up to MAX_REWORKS times) before the reviewer agent is involved. Checks that
 * were already failing before the change don't count against it.
 *
 * The gate talks to Unity through Library/RegressionStatus/request.txt ("baseline" / "run"), which
 * Assets/Editor/RegressionReporter.cs watches, so it doesn't depend on the unity command line.
 * If Unity doesn't answer (closed, compiling, busy), the reviewer agent does the whole check instead.
 *
 * Progress goes to Library/RegressionStatus/review.txt (in-game panel + Window > Regression Monitor).
 * The reviewer runs with PI_REGRESSION_REVIEWER=1 (turns this gate off inside it, and keeps the
 * Unity bridge from opening a second server). It's launched with the same node + pi script as the running pi; PI_REVIEWER_CMD overrides that.
 */

import { spawn } from "node:child_process";
import * as fs from "node:fs";
import * as path from "node:path";
import type { ExtensionAPI, ExtensionContext } from "@earendil-works/pi-coding-agent";

const IS_REVIEWER = !!process.env.PI_REGRESSION_REVIEWER;
const REVIEWER_CMD = process.env.PI_REVIEWER_CMD ?? "pi";
// The reviewer is a full agent run (reading files, calling Unity, thinking), which can take several minutes.
const REVIEW_TIMEOUT_MS = 15 * 60 * 1000;
// After a fast PASS, the reviewer agent still double-checks the diff against the Game rules in AGENTS.md
// (catches rules no check covers). Set PI_REGRESSION_AI_DOUBLECHECK=0 to skip it.
const AI_DOUBLECHECK = process.env.PI_REGRESSION_AI_DOUBLECHECK !== "0";

// How many times the editing agent gets to rework a Unity solution that broke previously-passing checks.
const MAX_REWORKS = 2;

const GAME_FILE = /\.(cs|unity|prefab|mat|asset|shader)$/i;

// The regression checks and tools. The editing agent may not change these: an agent that can rewrite
// the tests can make any change "pass". (The reviewer runs with this gate off, so it can still fix them.)
const PROTECTED_PATH =
	/(Assets[\\/]Scripts[\\/]Regression[\\/])|(Assets[\\/]Tests[\\/])|(Assets[\\/]Editor[\\/](RegressionReporter|BalanceSnapshot|RegressionWindow)\.cs)|(\.pi[\\/]extensions[\\/]regression-gate\.ts)/i;
const PROTECTED_NAME = /(RegressionSuite|RegressionStatusFiles|RegressionHUD|GameRulesTests|RegressionReporter|BalanceSnapshot|RegressionWindow|regression-gate)/i;
const WRITES_FILES = /(>|\bsed\s+-i|\bperl\s+-i|\bmv\b|\bcp\b|\brm\b|\bdel\b|Set-Content|Add-Content|Out-File|Remove-Item|Move-Item|Copy-Item|\btee\b|\bpython|\bnode\b|git\s+(checkout|restore|reset))/i;
/** True if a shell command looks like it writes files (ignores harmless redirects like 2>&1 or >/dev/null). */
function writesFiles(command: string): boolean {
	const cleaned = command.replace(/\d?>&\d/g, " ").replace(/\d?>\s*\/dev\/null/g, " ").replace(/[-=]>/g, " ").replace(/\$null/gi, " ");
	return WRITES_FILES.test(cleaned);
}

const PROTECTED_REASON =
	"regression-gate: the regression checks and tools are protected, so the editing agent can't change them. " +
	"Make only the gameplay change the user asked for. If it breaks a check, the regression gate will flag it " +
	"and the user (or the reviewer agent) decides whether that rule should change.";
// Unity tools that change the scene. Script eval/run counts too: agent C# can change anything.
const UNITY_MUTATING_TOOL = /^mcp__unity__.*(set_|create|delete|destroy|batch|add_|remove_|save|instantiate|eval|script_run)/i;
// Game files named in a shell command (so shell edits get backed up too).
const GAME_FILE_IN_TEXT = /[\w.\/\\-]+\.(cs|unity|prefab|mat|asset|shader)\b/gi;

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

function runProcess(cmd: string, args: string[], cwd: string, env: NodeJS.ProcessEnv, timeoutMs: number, direct = false): Promise<string> {
	return new Promise((resolve) => {
		let out = "";
		let child: ReturnType<typeof spawn>;
		try {
			child =
				process.platform === "win32" && !direct
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

	// Before/after checks for Unity prompts.
	let unityTask = false; // the current request came from Unity
	let originalRequest = ""; // the Unity prompt that started this request (reworks keep pointing at it)
	let beforeChecks: Promise<void> | null = null; // the BEFORE run, started when the Unity prompt arrives
	let beforeResults: Map<string, string> | null = null; // check name -> "Passed" | "Failed" before the change
	let firstMessageSeen = false; // only the first user message of a run can start a BEFORE run
	let reworkPending = false; // the next agent run is a rework of the current request
	let reworks = 0;

	const cwd = () => ctxRef?.cwd ?? process.cwd();
	const statusDir = () => path.join(cwd(), "Library", "RegressionStatus");
	const now = () => Math.floor(Date.now() / 1000);

	// review.txt: summary <time> <state> <text> <reviewStart> <manifest>
	// state: BASELINE | EDITING | RUNNING | REVIEWING | PASS | FAIL | REVERTED | ERROR | INTERRUPTED
	// manifest: this request's backup list, so the panel's "Undo agent change" button can restore it.
	const writeReview = (state: string, text: string) => {
		try {
			fs.mkdirSync(statusDir(), { recursive: true });
			const clean = text.replace(/[\t\r\n]+/g, " ").trim();
			const manifest = backedUp.size > 0 ? path.relative(cwd(), path.join(runDir, "manifest.txt")) : "";
			fs.writeFileSync(path.join(statusDir(), "review.txt"), `summary\t${now()}\t${state}\t${clean}\t${reviewStart}\t${manifest}\n`);
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

	/** Per-check results of the last run, from progress.txt: name -> { state, message }. */
	const readProgress = () => {
		const results = new Map<string, { state: string; message: string }>();
		try {
			for (const line of fs.readFileSync(statusFile("progress.txt"), "utf8").split(/\r?\n/)) {
				const f = line.split("\t");
				if (f[0] === "test" && f[1]) results.set(f[1], { state: f[2] ?? "", message: f[3] ?? "" });
			}
		} catch {
			/* no file */
		}
		return results;
	};

	/** Runs every check the way F5 does (RegressionSuite.StartRun via RegressionReporter) and waits for the results. */
	const runChecksNow = async (): Promise<Map<string, { state: string; message: string }> | null> => {
		const asked = now();
		requestUnity("run");
		const done = await waitFor(() => summaryTime(statusFile("tests.txt")) >= asked, 30000);
		return done ? readProgress() : null;
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

	/** Backs up a game file before it changes (once per request). */
	const backUp = (filePath: string) => {
		const original = path.resolve(cwd(), filePath);
		if (backedUp.has(original)) return;
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
	};

	/** Runs the separate reviewer agent and records its verdict. */
	const runReviewer = async (prompt: string, keepPassIfNoVerdict?: string, forRequest?: number) => {
		const env = { ...process.env, PI_REGRESSION_REVIEWER: "1" };
		// The full instructions go in a file: Windows caps a command line at ~8000 characters.
		const promptFile = statusFile(`reviewer-prompt-${forRequest ?? reviewStart}.txt`);
		fs.mkdirSync(statusDir(), { recursive: true });
		fs.writeFileSync(promptFile, prompt);
		const shortPrompt = `You are the regression reviewer. Read your full instructions in ${path.relative(cwd(), promptFile)} and follow them exactly, including the VERDICT line at the end.`;
		// Start the reviewer exactly the way this pi was started (same node, same pi script), so it works
		// no matter how pi is installed or which folder `pi` resolves from. PI_REVIEWER_CMD overrides it.
		const piScript = process.argv[1];
		const output =
			!process.env.PI_REVIEWER_CMD && piScript && fs.existsSync(piScript)
				? await runProcess(process.execPath, [piScript, "-p", shortPrompt], cwd(), env, REVIEW_TIMEOUT_MS, true)
				: await runProcess(REVIEWER_CMD, ["-p", shortPrompt], cwd(), env, REVIEW_TIMEOUT_MS);
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
		// The person already pressed "Undo agent change": keep that, don't overwrite it with a late verdict.
		try {
			const current = fs.readFileSync(statusFile("review.txt"), "utf8").split("\t");
			if (current[2] === "REVERTED" && (current[3] ?? "").startsWith("Undone by you")) {
				notify(`Regression reviewer: ${verdict} - ${summary} (you already undid this change)`, "info");
				return;
			}
		} catch {
			/* no status yet */
		}
		// A background double-check that finishes after a newer request started: don't overwrite that request's status.
		if (forRequest !== undefined && forRequest !== reviewStart) {
			notify(`Regression reviewer (earlier request): ${verdict} - ${summary}`, verdict === "PASS" ? "info" : "warning");
			return;
		}
		if (verdict === "ERROR" && keepPassIfNoVerdict) {
			writeReview("PASS", `${keepPassIfNoVerdict}; AI double-check didn't finish (see reviewer-log.txt)`);
			return;
		}
		if (verdict === "PASS" && keepPassIfNoVerdict) summary = `${keepPassIfNoVerdict}; AI reviewer agrees: ${summary}`;
		writeReview(verdict, summary);
		notify(`Regression reviewer: ${verdict} - ${summary}`, verdict === "PASS" ? "info" : verdict === "REVERTED" ? "warning" : "error");
	};

	pi.on("session_start", async (_event, ctx) => {
		ctxRef = ctx;

		// A review that was still in progress when pi last closed will never finish: say so instead of
		// leaving the panel on "Checking..." forever. Its backups stay listed, so Undo still works.
		try {
			const f = fs.readFileSync(statusFile("review.txt"), "utf8").replace(/\r?\n$/, "").split("\t");
			if (["BASELINE", "EDITING", "RUNNING", "REVIEWING"].includes(f[2] ?? "")) {
				// INTERRUPTED is neutral: not a problem with the game (the checks re-run on their own), just no verdict.
				const text = "Last review didn't finish (pi was closed or restarted)";
				fs.writeFileSync(statusFile("review.txt"), `summary\t${now()}\tINTERRUPTED\t${text}\t${f[4] ?? 0}\t${f[5] ?? ""}\n`);
			}
		} catch {
			/* no status yet */
		}
	});

	pi.on("message_start", async (event) => {
		const m: any = event.message;
		if (m?.role !== "user") return;
		request = textOf(m.content).slice(0, 4000);
		if (firstMessageSeen) return;
		firstMessageSeen = true;

		// A new Unity prompt (not a rework): run the checks BEFORE anything changes.
		if (request.trimStart().startsWith("[Unity]") && !beforeChecks && !reviewing) {
			unityTask = true;
			originalRequest = request;
			writeReview("BASELINE", "Running checks before the change...");
			beforeChecks = runChecksNow().then((results) => {
				beforeResults = results && results.size > 0 ? results : null;
				if (!beforeResults) notify("Regression gate: couldn't get check results before the change; regressions can't be told apart from old failures.", "warning");
				else if (!reviewing) writeReview("EDITING", "Editing agent is making the change...");
			});
		}
	});

	pi.on("agent_start", async () => {
		firstMessageSeen = false;
		if (reworkPending) {
			// Same request, second try: keep the backups (Undo restores the original), baseline and BEFORE results.
			reworkPending = false;
			writeReview("EDITING", `Reworking the change (attempt ${reworks} of ${MAX_REWORKS})...`);
			return;
		}
		unityTask = false;
		originalRequest = "";
		beforeChecks = null;
		beforeResults = null;
		reworks = 0;
		if (reviewing) {
			notify("Regression gate: the previous change is still being reviewed, so this request won't be checked. Press F5 afterward to re-check.", "warning");
			return;
		}
		backedUp = new Map();
		changedScene = false;
		reviewStart = now();
		runDir = path.join(cwd(), ".pi", "regression-backups", String(Date.now()));

		// Step 1: "before" picture for the balance diff.
		writeReview("BASELINE", "Saving balance baseline...");
		const asked = now();
		const requestId = reviewStart;
		requestUnity("baseline");
		// Don't make pi wait: confirm the baseline in the background.
		void waitFor(() => summaryTime(statusFile("balance.txt")) >= asked, 10000).then((saved) => {
			if (!saved) notify("Regression gate: Unity didn't save a balance baseline (is the Editor open and not compiling?)", "warning");
			if (reviewStart === requestId && !reviewing) writeReview("EDITING", "Editing agent is making the change...");
		});
	});

	pi.on("tool_call", async (event) => {
		const input = event.input as Record<string, unknown>;
		const name = event.toolName;

		// Keep the editing agent's hands off the tests.
		if ((name === "write" || name === "edit") && typeof input.path === "string" && PROTECTED_PATH.test(input.path))
			return { block: true, reason: PROTECTED_REASON };
		if (name === "bash" && typeof input.command === "string" && PROTECTED_NAME.test(input.command) && writesFiles(input.command))
			return { block: true, reason: PROTECTED_REASON };

		// Unity prompts: don't let anything change until the BEFORE run has its results (max 30s).
		const changes =
			((name === "write" || name === "edit") && typeof input.path === "string" && GAME_FILE.test(input.path)) ||
			(name === "bash" && typeof input.command === "string" && writesFiles(input.command));
		if (changes && beforeChecks) await Promise.race([beforeChecks, sleep(30000)]);

		// Back up BEFORE the edit runs (tool_call fires before execution).
		if ((name === "write" || name === "edit") && typeof input.path === "string" && GAME_FILE.test(input.path)) backUp(input.path);

		// Shell commands that write files: back up any game files they name, and review afterward either way.
		if (name === "bash" && typeof input.command === "string" && writesFiles(input.command)) {
			for (const m of input.command.match(GAME_FILE_IN_TEXT) ?? []) {
				try {
					backUp(m);
				} catch {
					/* not a real path */
				}
			}
			changedScene = true;
		}
		if (UNITY_MUTATING_TOOL.test(name)) changedScene = true;
	});

	pi.on("agent_settled", async (event) => {
		if (reviewing) return;
		if (event.aborted || (backedUp.size === 0 && !changedScene)) {
			writeReview("", ""); // nothing to review: clear the pipeline display
			return;
		}
		// Run the review in the background: pi must never wait on the gate (otherwise new prompts queue up
		// behind a multi-minute reviewer run and the Unity panel sits on "Waiting for pi...").
		reviewing = true;
		void review();
	});

	const review = async () => {
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
				const tests = readTests();
				const passed = tests.passed;
				let failures = tests.failures;
				let preexisting = 0;

				// Unity prompts: only checks that passed BEFORE the change count against it.
				if (unityTask && beforeResults) {
					const regressions: string[] = [];
					for (const [checkName, r] of readProgress()) {
						if (r.state !== "Failed") continue;
						if (beforeResults.get(checkName) === "Passed") regressions.push(`${checkName}: ${r.message}`.trim());
						else preexisting++;
					}
					if (regressions.length > 0 && reworks < MAX_REWORKS) {
						reworks++;
						writeReview("EDITING", `${regressions.length} check(s) broke; editing agent is reworking (attempt ${reworks} of ${MAX_REWORKS})`);
						notify(`Regression check: ${regressions.length} check(s) passed before and fail now - sending them back to the agent`, "warning");
						const asked = originalRequest.replace(/^\s*\[Unity\]\s*/, "");
						const text = [
							"[Unity] Automatic regression check: your change made checks fail that passed before you started.",
							"Rework your solution so it still does what was asked AND these checks pass again. Work from this output only (the test sources stay hidden). Don't undo the requested feature unless there's truly no other way, and say so if that happens.",
							"",
							"Failed checks (passed before your change):",
							...regressions.map((r) => `- ${r}`),
							"",
							`Original request: <<< ${asked} >>>`,
							"",
							`The checks run again automatically when you finish (rework ${reworks} of ${MAX_REWORKS}).`,
						].join("\n");
						reworkPending = true;
						if (ctxRef && !ctxRef.isIdle()) pi.sendUserMessage(text, { deliverAs: "followUp" });
						else pi.sendUserMessage(text);
						return;
					}
					failures = regressions; // old failures aren't this change's fault
				}
				const { changes, hasBaseline } = readChanges();
				const changeText = !hasBaseline ? "no balance baseline, so value changes weren't compared" :
					changes.length === 0 ? "no tunable values changed" : `changed: ${changes.slice(0, 3).join(", ")}${changes.length > 3 ? ` (+${changes.length - 3} more)` : ""}`;

				if (failures.length === 0) {
					// Step 6, fast path: instant PASS...
					const total = passed + preexisting;
					const summary = preexisting > 0
						? `${passed}/${total} checks passed (${preexisting} already failing before this change); ${changeText}`
						: `${passed}/${passed} checks passed; ${changeText}`;
					const doubleCheck = AI_DOUBLECHECK && backedUp.size > 0;
					writeReview("PASS", doubleCheck ? `${summary}; AI reviewer double-checking...` : summary);
					notify(`Regression check: PASS - ${summary}`, "info");
					if (!doubleCheck) return;

					// ...then the reviewer agent looks for rule breaks no check covers (verdict may change).
					const prompt = [
						"You are the REGRESSION REVIEWER for this Unity project. Follow AGENTS.md, especially its Game rules section.",
						`A different agent just made changes for this user request: <<< ${originalRequest || request} >>>`,
						`Files it changed: ${files.replace(/\n/g, " ")}`,
						`Originals were backed up before editing. ${manifest} maps each changed file to its backup (tab-separated; NEW = the file didn't exist before).`,
						`All ${passed} automated regression checks PASSED, and balance values: ${changeText}.`,
						"Your job is to catch what the checks might miss. Compare each changed file with its backup and look for anything that breaks a Game rule in AGENTS.md (friendly fire, healing enemies, abilities ignoring teams/cooldowns, shields or heals that never end, values the user didn't ask to change, and so on).",
						"Do NOT add features or adjust the change.",
						"(a) No rule broken: verdict PASS.",
						"(b) A rule is broken that the user did NOT ask to change: revert (copy each backup over its original, delete files marked NEW), then script eval `return RegressionReporter.RunAll();`, wait for Library/RegressionStatus/tests.txt to update, confirm it passes. Verdict REVERTED, naming the rule.",
						"(c) The user deliberately asked to break a rule: don't revert. Verdict FAIL, naming the rule so the user knows.",
						"End your reply with exactly one line in this format: VERDICT: PASS|FAIL|REVERTED - <one short sentence>",
					].join("\n");
					void runReviewer(prompt, summary, reviewStart); // runs in the background; new requests aren't blocked
					return;
				}

				// A check failed: the reviewer agent decides what to do about it.
				writeReview("REVIEWING", `${failures.length} check(s) failed`);
				notify(`Regression check: ${failures.length} failed - starting reviewer agent`, "warning");
				const prompt = [
					"You are the REGRESSION REVIEWER for this Unity project. Follow AGENTS.md.",
					`A different agent just made changes for this user request: <<< ${originalRequest || request} >>>`,
					`Files it changed: ${files.replace(/\n/g, " ")}`,
					backedUp.size > 0
						? `Originals were backed up before editing. ${manifest} maps each changed file to its backup (tab-separated; NEW = the file didn't exist before).`
						: "Only scene objects changed; there are no file backups, so you can report but not revert.",
					`The regression checks were already run. These FAILED: ${failures.join(" | ")}.`,
					reworks > 0 ? `(They passed before the change; the editing agent already tried to rework its solution ${reworks} time(s) and they still fail.)` : "",
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
				`A different agent just made changes for this user request: <<< ${originalRequest || request} >>>`,
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
	};

	pi.on("session_shutdown", async () => {
		ctxRef = undefined;
	});
}
