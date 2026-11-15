/**
 * Unity Eval Guard
 *
 * Blocks agent-run C# (the Unity "agent sandbox") that uses LINQ or Reflection.
 *
 * Covered entry points:
 *   - mcp__unity__script_eval / mcp__unity__eval   (inline `code` or `file`)
 *   - mcp__unity__script_run                       (`file`)
 *   - bash: `unity cmd|command script eval|run ...` (inline --code or --file)
 *   - write / edit of `.cs` files: blocked only if the change ADDS LINQ/Reflection usages
 *     (pre-existing usages in a file don't block unrelated edits)
 *
 * MCP calls made from codemode scripts go through `tool_call` too, so they are covered.
 * Detection is static (comments and plain string literals are ignored), so it can have
 * false positives on user-defined methods with LINQ names (e.g. a custom `.Where(`).
 */

import * as fs from "node:fs";
import * as path from "node:path";
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";

interface Rule {
	re: RegExp;
	label: string;
}

const LINQ_METHODS = [
	"Where", "SelectMany", "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending",
	"GroupBy", "GroupJoin", "First", "FirstOrDefault", "Last", "LastOrDefault", "Single",
	"SingleOrDefault", "Any", "All", "Count", "LongCount", "Sum", "Average", "Distinct", "DistinctBy",
	"MinBy", "MaxBy", "ToList", "ToDictionary", "ToHashSet", "ToLookup", "Skip", "SkipWhile", "SkipLast",
	"Take", "TakeWhile", "TakeLast", "Aggregate", "Zip", "Except", "ExceptBy", "Intersect",
	"IntersectBy", "Union", "UnionBy", "AsEnumerable", "AsQueryable", "ElementAt", "ElementAtOrDefault",
	"DefaultIfEmpty", "SequenceEqual", "Chunk",
];

const LINQ_RULES: Rule[] = [
	{ re: /\bSystem\s*\.\s*Linq\b/, label: "System.Linq" },
	{ re: /\bEnumerable\s*\./, label: "Enumerable.*" },
	{ re: /\bfrom\s+[\w<>,\s]*?\w+\s+in\s+[^;]*?\b(select|group)\b/s, label: "LINQ query syntax" },
	{ re: new RegExp(`\\.\\s*(${LINQ_METHODS.join("|")})\\s*(<[^>()]*>)?\\s*\\(`), label: "LINQ extension method" },
	{ re: /\.\s*(Cast|OfType)\s*<[^>]*>\s*\(/, label: "LINQ Cast/OfType" },
	// Min/Max/Concat/Reverse/Contains collide with Mathf/string/List APIs, so only flag common LINQ shapes.
	{ re: /(?<!\b(Mathf|Math|Vector2|Vector3|Vector4|Vector2Int|Vector3Int|Color|Color32|string|String|Bounds|int|float|double)\s*)\.\s*(Min|Max)\s*\(\s*(\w+\s*=>|\(|\))/, label: "LINQ Min/Max" },
	{ re: /(?<!\b(string|String|Path)\s*)\.\s*Concat\s*\(/, label: "LINQ Concat" },
	{ re: /(?<!\b(string|String)\s*)\.\s*Join\s*\(/, label: "LINQ Join" },
	// UI Selectable.Select() takes no args; LINQ Select always takes a selector.
	{ re: /\.\s*Select\s*(<[^>()]*>)?\s*\(\s*[^)\s]/, label: "LINQ Select" },
];

const REFLECTION_RULES: Rule[] = [
	{ re: /\bSystem\s*\.\s*Reflection\b/, label: "System.Reflection" },
	{ re: /\b(BindingFlags|MethodInfo|FieldInfo|PropertyInfo|MemberInfo|ConstructorInfo|ParameterInfo|EventInfo|MethodBase|TypeInfo|Assembly|AppDomain|Activator|RuntimeHelpers|ILGenerator|DynamicMethod)\b/, label: "reflection type" },
	{ re: /\.\s*(GetMethods?|GetFields?|GetPropert(y|ies)|GetMembers?|GetConstructors?|GetEvents?|GetNestedTypes?|GetInterfaces?|GetCustomAttributes?|GetTypeInfo|InvokeMember|GetRuntimeMethods?|GetRuntimeFields?|GetRuntimeProperties|GetRuntimeProperty|DynamicInvoke|CreateDelegate|GetTypes|GetExportedTypes|MakeGenericType|MakeGenericMethod)\s*(<[^>()]*>)?\s*\(/, label: "reflection member lookup" },
	{ re: /\bType\s*\.\s*GetType\s*\(/, label: "Type.GetType(string)" },
	{ re: /\bTypeCache\b/, label: "UnityEditor.TypeCache" },
	{ re: /\bdynamic\b/, label: "dynamic (late binding)" },
];

/**
 * Remove comments and the contents of plain string/char literals so that text like
 * "Select an object" doesn't trigger. Interpolated strings keep their content because
 * `{...}` holes can contain code.
 */
function stripNonCode(src: string): string {
	let out = "";
	let i = 0;
	const n = src.length;
	while (i < n) {
		const c = src[i];
		const next = src[i + 1];
		if (c === "/" && next === "/") {
			while (i < n && src[i] !== "\n") i++;
			continue;
		}
		if (c === "/" && next === "*") {
			const end = src.indexOf("*/", i + 2);
			i = end < 0 ? n : end + 2;
			out += " ";
			continue;
		}
		if (c === '"' || c === "'") {
			// Look back for $ / @ prefixes.
			const prefix = src.slice(Math.max(0, i - 2), i);
			const interpolated = prefix.includes("$");
			const verbatim = prefix.includes("@");
			let j = i + 1;
			let body = "";
			while (j < n) {
				const d = src[j];
				if (verbatim && d === '"' && src[j + 1] === '"') {
					body += '""';
					j += 2;
					continue;
				}
				if (!verbatim && d === "\\") {
					body += src.slice(j, j + 2);
					j += 2;
					continue;
				}
				if (d === c) break;
				if (!verbatim && d === "\n") break;
				body += d;
				j++;
			}
			out += c + (interpolated ? body : "") + c;
			i = j + 1;
			continue;
		}
		out += c;
		i++;
	}
	return out;
}

function findViolations(src: string, strip = true): string[] {
	// Shell commands use quotes for quoting, not C# literals, so they are scanned raw.
	const code = strip ? stripNonCode(src) : src;
	const hits: string[] = [];
	for (const [kind, rules] of [["LINQ", LINQ_RULES], ["Reflection", REFLECTION_RULES]] as const) {
		for (const r of rules) {
			const m = code.match(r.re);
			if (m) hits.push(`${kind}: ${r.label} (\`${m[0].replace(/\s+/g, " ").trim().slice(0, 60)}\`)`);
		}
	}
	return hits;
}

function countMatches(code: string, re: RegExp): number {
	const g = new RegExp(re.source, re.flags.includes("g") ? re.flags : re.flags + "g");
	return [...code.matchAll(g)].length;
}

/** Violations whose match count increases from `before` to `after`. */
function findAddedViolations(before: string, after: string): string[] {
	const a = stripNonCode(before);
	const b = stripNonCode(after);
	const hits: string[] = [];
	for (const [kind, rules] of [["LINQ", LINQ_RULES], ["Reflection", REFLECTION_RULES]] as const) {
		for (const r of rules) {
			const added = countMatches(b, r.re) - countMatches(a, r.re);
			if (added > 0) {
				const m = b.match(r.re);
				hits.push(`${kind}: ${r.label} (+${added}, e.g. \`${(m?.[0] ?? "").replace(/\s+/g, " ").trim().slice(0, 60)}\`)`);
			}
		}
	}
	return hits;
}

function applyEdits(original: string, edits: Array<{ oldText?: unknown; newText?: unknown }>): string {
	let out = original;
	for (const e of edits) {
		if (typeof e.oldText !== "string" || typeof e.newText !== "string") continue;
		const idx = out.indexOf(e.oldText);
		if (idx < 0) continue; // edit tool will reject it anyway
		out = out.slice(0, idx) + e.newText + out.slice(idx + e.oldText.length);
	}
	return out;
}

function readSource(file: string, cwd: string): string | undefined {
	try {
		return fs.readFileSync(path.isAbsolute(file) ? file : path.join(cwd, file), "utf8");
	} catch {
		return undefined;
	}
}

function blockReason(where: string, hits: string[]): string {
	return [
		`Blocked by unity-eval-guard: LINQ and Reflection are not allowed in agent-run Unity C# (${where}).`,
		...hits.map((h) => `- ${h}`),
		"Rewrite with plain loops / direct API calls (e.g. for/foreach, List<T> methods, FindObjectsByType, GetComponent).",
	].join("\n");
}

const EVAL_TOOL = /^mcp__unity__(script_)?(eval|run)$/;
const BASH_UNITY_SCRIPT = /\bunity\s+(cmd|command)\s+script\s+(eval|run)\b/;

export default function (pi: ExtensionAPI) {
	pi.on("tool_call", async (event, ctx) => {
		const cwd = ctx.cwd ?? process.cwd();
		const input = event.input as Record<string, unknown>;

		if ((event.toolName === "write" || event.toolName === "edit") && typeof input.path === "string" && /\.cs$/i.test(input.path)) {
			const before = readSource(input.path, cwd) ?? "";
			let after: string | undefined;
			if (event.toolName === "write") {
				if (typeof input.content === "string") after = input.content;
			} else {
				const edits = Array.isArray(input.edits)
					? (input.edits as Array<{ oldText?: unknown; newText?: unknown }>)
					: [{ oldText: input.oldText, newText: input.newText }];
				after = applyEdits(before, edits);
			}
			if (after === undefined) return;
			const hits = findAddedViolations(before, after);
			if (hits.length) return { block: true, reason: blockReason(`${event.toolName} ${input.path}`, hits) };
			return;
		}

		if (EVAL_TOOL.test(event.toolName)) {
			const sources: Array<[string, string]> = [];
			if (typeof input.code === "string") sources.push(["code", input.code]);
			if (typeof input.file === "string") {
				const text = readSource(input.file, cwd);
				if (text === undefined)
					return { block: true, reason: `unity-eval-guard: can't read ${input.file} to check it for LINQ/Reflection.` };
				sources.push([input.file, text]);
			}
			for (const [where, text] of sources) {
				const hits = findViolations(text);
				if (hits.length) return { block: true, reason: blockReason(where, hits) };
			}
			return;
		}

		if (event.toolName === "bash" && typeof input.command === "string" && BASH_UNITY_SCRIPT.test(input.command)) {
			const cmd = input.command;
			const hits = findViolations(cmd, false);
			if (hits.length) return { block: true, reason: blockReason("bash unity script command", hits) };
			for (const m of cmd.matchAll(/--file[=\s]+("([^"]+)"|'([^']+)'|(\S+))/g)) {
				const file = m[2] ?? m[3] ?? m[4];
				const text = readSource(file, cwd);
				if (text === undefined)
					return { block: true, reason: `unity-eval-guard: can't read ${file} to check it for LINQ/Reflection.` };
				const fileHits = findViolations(text);
				if (fileHits.length) return { block: true, reason: blockReason(file, fileHits) };
			}
		}
	});
}
