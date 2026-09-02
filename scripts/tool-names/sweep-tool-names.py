#!/usr/bin/env python3
"""Emit the tool-name registry, and rewrite every shipped tool-name literal into it.

The sweep is a SCRIPT and the compiler is the verifier (#475, RUN_ORDER
amendment 2026-09-02 base (i)).  Two modes:

  --emit-registry   generate ``src/housecarl-core/ToolNames.cs`` from the
                    DECLARED tool set, derived by reflection over the
                    ``[McpServerTool(Name = ...)]`` attributes in source.  Never
                    a hand list.
  --rewrite         rewrite every class (a) / (b) / (c) site to reference a
                    constant.  Class (d) -- inside a deletion-flagged 1.x tool
                    body -- is skipped by rule 10(a) and the skip list printed.

Two independent render checks run on every ``--rewrite``:

  * TEXTUAL, over every plain-literal site: the rewritten ``+``-expression is
    re-rendered by unescaping each fragment and substituting each constant's
    value, and compared to the unescaped original literal.  Not a sample --
    every site.  A 50-site before/after sample is printed for reading.
  * WIRE, out of band: ``dump-tools-list.py`` before and after.  If a
    caller-facing sentence moved by a character the diff says so.

Per RUN_ORDER boot rule 6 this prints one line per file plus a summary; a
mismatch prints in full.
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location(
    "derive_tool_name_sites", os.path.join(HERE, "derive-tool-name-sites.py"))
derive = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(derive)

REGISTRY_PATH = "src/housecarl-core/ToolNames.cs"
PREFIX = "housecarl_"

# C# simple escapes that may appear in these literals.
ESCAPES = {
    "'": "'", '"': '"', "\\": "\\", "0": "\0", "a": "\a", "b": "\b",
    "f": "\f", "n": "\n", "r": "\r", "t": "\t", "v": "\v",
}


def const_name(tool: str) -> str:
    """`housecarl_copy_npc_appearance` -> `CopyNpcAppearance`.  Mechanical."""
    assert tool.startswith(PREFIX), tool
    return "".join(p[:1].upper() + p[1:] for p in tool[len(PREFIX):].split("_") if p)


def unescape(s: str) -> str:
    """Render a regular C# string literal's body."""
    out, i = [], 0
    while i < len(s):
        c = s[i]
        if c != "\\":
            out.append(c)
            i += 1
            continue
        nxt = s[i + 1] if i + 1 < len(s) else ""
        if nxt in ESCAPES:
            out.append(ESCAPES[nxt])
            i += 2
        elif nxt == "u":
            out.append(chr(int(s[i + 2:i + 6], 16)))
            i += 6
        elif nxt == "x":
            j = i + 2
            while j < len(s) and j < i + 6 and s[j] in "0123456789abcdefABCDEF":
                j += 1
            out.append(chr(int(s[i + 2:j], 16)))
            i = j
        elif nxt == "U":
            out.append(chr(int(s[i + 2:i + 10], 16)))
            i = 10 + i
        else:
            raise ValueError(f"unknown escape \\{nxt} in {s!r}")
    return "".join(out)


# ---------------------------------------------------------------- registry ----

REGISTRY_HEADER = '''namespace HousecarlCore;

/// <summary>
/// Every tool name the shipped surface uses, as compile-time constants -- the one home for the
/// <c>housecarl_</c> spellings (RUN_ORDER amendment 2026-09-02 base (i), #475).
///
/// <para><b>What this buys.</b> Deleting a tool means deleting its constant, and the compiler then
/// names every surviving site that still refers to it. The 1.x cut's checklist becomes a build-error
/// list rather than a population somebody noticed by hand -- which is the root both of PR #474's
/// class-stops shared.</para>
///
/// <para><b><c>const</c>, not <c>static readonly</c>.</b> These are spliced into
/// <c>[McpServerTool(Name = ...)]</c> and <c>[Description(...)]</c> arguments, which must be constant
/// expressions. <c>"..." + ToolNames.Records + "..."</c> is one; a <c>static readonly</c> field is not.</para>
///
/// <para><b>Why <c>housecarl-core</c> and not <c>housecarl-mcp</c>.</b> 25 of the 279 shipped sites are in
/// <c>housecarl-core</c>, and the project reference runs mcp -> core, so a registry in mcp could not reach
/// them. Placement follows the derived population, not the other way round.</para>
///
/// <para><b>The population is DECLARED, not registered.</b> One constant per <c>[McpServerTool].Name</c> on
/// the surface. That is deliberately not the set the SDK scans, which also needs <c>[McpServerToolType]</c>
/// on the declaring type: <c>housecarl_check</c> is declared and has never been registered (#470), its
/// attribute still names it in source, and it is owed a constant all the same.</para>
///
/// <para><b>Retired spellings are absent on purpose.</b> A name that no longer names a tool has no constant
/// whose deletion should break anything, and a second, hand-kept population is the hazard this file exists to
/// remove. The six retired 1.x write names stay literals in <c>AliasTable</c>'s retired rows.</para>
///
/// <para>GENERATED by <c>scripts/tool-names/sweep-tool-names.py --emit-registry</c>. Re-run it rather than
/// editing by hand; a new tool's constant arrives with its attribute.</para>
/// </summary>
public static class ToolNames
{
'''


def emit_registry(root, declared, flagged):
    lines = [REGISTRY_HEADER]
    for tool in sorted(declared):
        note = "  // deletion-flagged (rule 10(a)) -- dies at the 1.x cut" if tool in flagged else ""
        lines.append(f'    public const string {const_name(tool)} = "{tool}";{note}\n')
    lines.append("}\n")
    path = os.path.join(root, REGISTRY_PATH)
    with open(path, "w", encoding="utf-8", newline="\r\n") as fh:
        fh.write("".join(lines))
    return path, len(declared)


# ----------------------------------------------------------------- rewrite ----

def rewrite_file(path, text, sites, declared):
    """Return (new_text, records) where records describe each rewritten literal."""
    by_literal = {}
    for s in sites:
        by_literal.setdefault(s["literal_start"], []).append(s)

    records = []
    for lit_start in sorted(by_literal, reverse=True):
        group = sorted(by_literal[lit_start], key=lambda s: s["offset"])
        first = group[0]
        cstart, cend, lend = first["literal_cstart"], first["literal_cend"], first["literal_end"]
        prefix = text[lit_start:cstart - 1]
        body = text[cstart:cend]
        interpolated = "$" in prefix

        if interpolated:
            new_body = body
            for s in sorted(group, key=lambda s: s["offset"], reverse=True):
                a = s["offset"] - cstart
                b = a + len(s["name"])
                new_body = new_body[:a] + "{ToolNames." + const_name(s["name"]) + "}" + new_body[b:]
            replacement = prefix + '"' + new_body + '"'
            rendered_ok = None          # checked by reading, and by the wire diff
        else:
            parts, cursor = [], cstart
            for s in group:
                seg = text[cursor:s["offset"]]
                if seg:
                    if (len(seg) - len(seg.rstrip("\\"))) % 2:
                        raise ValueError(f"{path}: split would land inside an escape near line {s['line']}")
                    parts.append(("lit", seg))
                parts.append(("const", s["name"]))
                cursor = s["offset"] + len(s["name"])
            tail = text[cursor:cend]
            if tail:
                parts.append(("lit", tail))
            replacement = " + ".join(
                f'"{v}"' if kind == "lit" else f"ToolNames.{const_name(v)}"
                for kind, v in parts)
            rendered = "".join(unescape(v) if kind == "lit" else v for kind, v in parts)
            rendered_ok = (rendered == unescape(body))

        records.append({
            "file": sites[0]["file"], "line": first["line"], "interpolated": interpolated,
            "before": text[lit_start:lend], "after": replacement,
            "names": [s["name"] for s in group], "rendered_ok": rendered_ok,
        })
        text = text[:lit_start] + replacement + text[lend:]
    return text, records


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default=".")
    ap.add_argument("--emit-registry", action="store_true")
    ap.add_argument("--rewrite", action="store_true")
    ap.add_argument("--sample", type=int, default=50)
    ap.add_argument("--report", help="write the full before/after record here")
    args = ap.parse_args()
    root = os.path.abspath(args.root)

    files = derive.collect(root)
    parsed, declared = {}, set()
    for p in files:
        with open(p, "r", encoding="utf-8-sig", newline="") as fh:
            t = fh.read()
        lits, coms, masked = derive.lex(t)
        regions = derive.tool_regions(t, masked)
        parsed[p] = (t, lits, coms, masked, regions)
        declared.update(n for n, _a, _b, _e in regions)

    print(f"declared tools: {len(declared)}")

    if args.emit_registry:
        path, n = emit_registry(root, declared, derive.DELETION_FLAGGED)
        print(f"registry: {n} constant(s) -> {os.path.relpath(path, root)}")

    if not args.rewrite:
        return 0

    # Re-derive the classified sites from the CURRENT tree, then rewrite a/b/c.
    import subprocess
    tmp = os.path.join(root, "sites.tmp.json")
    subprocess.run([sys.executable, os.path.join(HERE, "derive-tool-name-sites.py"),
                    "--root", root, "--json", tmp],
                   check=True, stdout=subprocess.DEVNULL)
    with open(tmp, encoding="utf-8") as fh:
        data = json.load(fh)
    os.remove(tmp)

    per_file, skipped = {}, []
    for s in data["sites"]:
        if s["file"].replace("\\", "/") == REGISTRY_PATH:
            continue                                   # the registry is where the names now live
        if s["class"] == "d":
            skipped.append(s)
        elif s["class"] in ("a", "b", "c"):
            per_file.setdefault(s["file"], []).append(s)

    all_records, bad = [], []
    for rel in sorted(per_file):
        p = os.path.join(root, rel)
        with open(p, "r", encoding="utf-8-sig", newline="") as fh:
            text = fh.read()
        new_text, records = rewrite_file(p, text, per_file[rel], declared)
        with open(p, "w", encoding="utf-8", newline="") as fh:
            fh.write(new_text)
        mism = [r for r in records if r["rendered_ok"] is False]
        bad.extend(mism)
        all_records.extend(records)
        interp = sum(1 for r in records if r["interpolated"])
        print(f"  {len(per_file[rel]):>4} site(s) in {len(records):>3} literal(s)"
              f"{f', {interp} interpolated' if interp else '':<18}  {rel}"
              f"{'   *** RENDER MISMATCH ***' if mism else ''}")

    print()
    print("-- class (d) skipped, rule 10(a) (deletion-flagged 1.x tool bodies) --")
    for f in sorted({s["file"] for s in skipped}):
        print(f"  {sum(1 for s in skipped if s['file'] == f):>4}  {f}")
    print(f"  total skipped: {len(skipped)}")

    plain = [r for r in all_records if not r["interpolated"]]
    interp = [r for r in all_records if r["interpolated"]]
    print()
    print("== summary ==")
    print(f"files rewritten     : {len(per_file)}")
    print(f"literals rewritten  : {len(all_records)}  ({len(plain)} plain, {len(interp)} interpolated)")
    print(f"sites rewritten     : {sum(len(v) for v in per_file.values())}")
    print(f"sites skipped (d)   : {len(skipped)}")
    print(f"render check (plain): {sum(1 for r in plain if r['rendered_ok'])}/{len(plain)} identical, "
          f"{len(bad)} MISMATCH")
    print("render check (interp): each name replaced by a {ToolNames.X} hole; all listed below for reading, "
          "and the wire diff is the oracle")

    if args.report:
        with open(args.report, "w", encoding="utf-8", newline="\n") as fh:
            json.dump(all_records, fh, indent=1, ensure_ascii=False)
        print(f"full record -> {args.report}")

    print()
    print(f"-- before/after sample ({min(args.sample, len(plain))} of {len(plain)} plain literals) --")
    step = max(1, len(plain) // max(1, args.sample))
    for r in plain[::step][:args.sample]:
        print(f"  {r['file']}:{r['line']}")
        print(f"    -  {r['before'][:150]}")
        print(f"    +  {r['after'][:170]}")

    print()
    print(f"-- every interpolated rewrite ({len(interp)}) --")
    for r in interp:
        print(f"  {r['file']}:{r['line']}")
        print(f"    -  {r['before'][:150]}")
        print(f"    +  {r['after'][:170]}")

    if bad:
        print()
        print("!! RENDER MISMATCHES -- the sweep changed a rendered sentence. Nothing may ship like this.")
        for r in bad:
            print(f"  {r['file']}:{r['line']}\n    -  {r['before']}\n    +  {r['after']}")
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
