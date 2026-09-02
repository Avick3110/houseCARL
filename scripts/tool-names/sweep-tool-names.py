#!/usr/bin/env python3
"""Emit the tool-name registry, and rewrite every shipped tool-name literal into it.

The sweep is a SCRIPT and the compiler is the verifier (#475; rationale in
docs/decisions/0003-tool-names-are-compile-time-constants.md).  Two modes:

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

This prints one line per file plus a summary; a
mismatch prints in full.

ONE-SHOT MIGRATION RECORD: this script ran once, to produce the tool-name
registry and the rewrite of its call sites (#475). It is kept as the record of
how that population was derived, not as a maintenance path, and is not re-run --
a new tool's constant is added by hand and the completeness test catches a
missing one.
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import os
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
/// Every tool name the shipped surface uses, as compile-time constants.
///
/// <para>One constant per DECLARED <c>[McpServerTool].Name</c> -- declared, not registered: a tool
/// the SDK does not scan still has its name spelled here. Retired spellings have no constant.</para>
///
/// <para><c>const</c>, not <c>static readonly</c>: these are spliced into attribute arguments, which
/// must be constant expressions.</para>
///
/// <para>Produced once by <c>scripts/tool-names/</c> -- the one-shot migration record of how this file
/// and the rewritten call sites were derived. Those scripts are not a maintenance path and are not
/// re-run. A new tool's constant is added BY HAND, one line; the completeness test holds this set
/// against the declared tools and fails until it is.
/// Rationale: <c>docs/decisions/0003-tool-names-are-compile-time-constants.md</c>.</para>
/// </summary>
public static class ToolNames
{
'''


def emit_registry(root, declared, flagged):
    lines = [REGISTRY_HEADER]
    for tool in sorted(declared):
        note = "  // deletion-flagged: dies at the 1.x cut" if tool in flagged else ""
        lines.append(f'    public const string {const_name(tool)} = "{tool}";{note}\n')
    lines.append("}\n")
    path = os.path.join(root, REGISTRY_PATH)
    with open(path, "w", encoding="utf-8", newline="\r\n") as fh:
        fh.write("".join(lines))
    return path, len(declared)


# ----------------------------------------------------------------- rewrite ----

def rewrite_file(path, text, sites):
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

        # Only a regular literal round-trips through this rewriter.  The plain branch
        # below re-emits every fragment as a regular literal and drops `prefix`, so a
        # verbatim (@"...") literal would come back with its escapes re-interpreted --
        # `\r`, `\n`, `\t` becoming control characters -- and `rendered_ok` could not
        # see it, because unescape() applies the same regular-literal model to both
        # sides.  A raw ("""...""") literal loses its quotes the same way.  No site on
        # this branch is anything but regular; refuse rather than let the next run be
        # the one that finds out.
        literal_kind = first.get("literal_kind")
        if literal_kind != "regular":
            raise ValueError(
                f"{path}:{first['line']}: literal_kind={literal_kind!r} -- this rewriter only "
                f"round-trips regular literals (a verbatim or raw literal would have its "
                f"escapes silently re-interpreted). Rewrite this site by hand.")

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
            "file": first["file"], "line": first["line"], "interpolated": interpolated,
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
    derive.load_registry(root)

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
    import tempfile
    # Under the system temp dir, not the repo root: a throw between the write and the
    # remove used to leave `sites.tmp.json` untracked in the tree.
    fd, tmp = tempfile.mkstemp(prefix="tool-name-sites-", suffix=".json")
    os.close(fd)
    try:
        subprocess.run([sys.executable, os.path.join(HERE, "derive-tool-name-sites.py"),
                        "--root", root, "--json", tmp],
                       check=True, stdout=subprocess.DEVNULL)
        with open(tmp, encoding="utf-8") as fh:
            data = json.load(fh)
    finally:
        if os.path.exists(tmp):
            os.remove(tmp)

    per_file, skipped = {}, []
    for s in data["sites"]:
        if s["file"].replace("\\", "/") == REGISTRY_PATH:
            continue                                   # the registry is where the names now live
        if s["class"] == "d":
            skipped.append(s)
        elif s["class"] in ("a", "b", "c"):
            per_file.setdefault(s["file"], []).append(s)

    # Rewrite every file in memory FIRST, then write.  Writing inside the loop left a
    # half-swept tree with no rollback when a later file raised.
    all_records, bad, pending = [], [], []
    for rel in sorted(per_file):
        p = os.path.join(root, rel)
        with open(p, "r", encoding="utf-8-sig", newline="") as fh:
            text = fh.read()
        new_text, records = rewrite_file(p, text, per_file[rel])
        pending.append((p, new_text))
        mism = [r for r in records if r["rendered_ok"] is False]
        bad.extend(mism)
        all_records.extend(records)

    for p, new_text in pending:
        with open(p, "w", encoding="utf-8", newline="") as fh:
            fh.write(new_text)
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
