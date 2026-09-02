#!/usr/bin/env python3
"""Derive every shipped tool-name site under the C# sources, classified.

Population, per the tool-name registry charter (RUN_ORDER amendment 2026-09-02
base, part (i)): every occurrence of ``housecarl_[a-z0-9_]+`` in
``src/housecarl-mcp``, ``src/housecarl-core`` and ``src/housecarl-mcp-tests``
(the last does not exist before #470 lands).

Classes, exactly as the charter names them:

  a  the tool's own ``[McpServerTool(Name = ...)]`` attribute argument
  b  a description / refusal / remedy sentence naming a tool (the literal
     carries text besides the name)
  c  a code-path literal (the literal IS the name: alias rows, redirect
     tables, census lists)
  d  inside a deletion-flagged 1.x tool body -- SKIPPED by rule 10(a)
  e  a name that is not a DECLARED tool at all (a retired spelling)
  r  inside the registry itself -- where, after the sweep, the name literals live

Non-literal occurrences (comments, identifiers) are counted separately: they
are not rewritten.

DECLARED, not REGISTERED.  The constant population is every ``[McpServerTool].Name``
in the shipped assembly (46 on ``8f520b9``).  That is deliberately not the set the
SDK scans, which also needs ``[McpServerToolType]`` on the declaring type (45 --
``housecarl_check`` is declared and has never been registered, #470).  A constant is
owed to whatever the source attribute names, so the oracle here is the declared set.

Nothing is written by this script.  ``--json`` emits the machine-readable site
list the sweep consumes.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys

NAME_RE = re.compile(r"housecarl_[a-z0-9_]+")

# The eleven deletion-flagged 1.x tools (RUN_ORDER rule 10(a); the read family
# and the check family named in the registry charter).  Their bodies are not
# touched by this engagement -- they die at the cut.
DELETION_FLAGGED = {
    "housecarl_read_record",
    "housecarl_batch_record_detail",
    "housecarl_resolve",
    "housecarl_read_plugin_file",
    "housecarl_cross_plugin_query",
    "housecarl_diff_record",
    "housecarl_effect_chain",
    "housecarl_skypatcher_read",
    "housecarl_check_errors",
    "housecarl_validate_scripts",
    "housecarl_validate_dialogue",
}

SOURCE_DIRS = [
    "src/housecarl-mcp",
    "src/housecarl-core",
    "src/housecarl-mcp-tests",
]

# The registry itself (#475).  After the sweep this is the ONE shipped file that
# still spells a tool name as a literal, outside the two by-rule residues.
REGISTRY_FILE = "src/housecarl-core/ToolNames.cs"


class Span:
    __slots__ = ("start", "end", "cstart", "cend", "kind")

    def __init__(self, start, end, cstart, cend, kind):
        self.start = start
        self.end = end
        self.cstart = cstart
        self.cend = cend
        self.kind = kind


def lex(text):
    """Return (literal spans, comment spans, masked text).

    Masked text has every literal/comment interior replaced by spaces so that
    brace matching over it is safe.
    """
    literals = []
    comments = []
    mask = list(text)
    i = 0
    n = len(text)
    while i < n:
        ch = text[i]
        nxt = text[i + 1] if i + 1 < n else ""
        if ch == "/" and nxt == "/":
            j = text.find("\n", i)
            j = n if j < 0 else j
            comments.append(Span(i, j, i + 2, j, "line"))
            for k in range(i, j):
                mask[k] = " "
            i = j
            continue
        if ch == "/" and nxt == "*":
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            comments.append(Span(i, j, i + 2, j - 2, "block"))
            for k in range(i, j):
                mask[k] = " "
            i = j
            continue
        if ch == "'":
            j = i + 1
            while j < n:
                if text[j] == "\\":
                    j += 2
                    continue
                if text[j] == "'":
                    j += 1
                    break
                j += 1
            for k in range(i, min(j, n)):
                mask[k] = " "
            i = j
            continue
        # string-literal openers, longest prefix first
        prefix_start = i
        pi = i
        verbatim = False
        while pi < n and text[pi] in "$@":
            if text[pi] == "@":
                verbatim = True
            pi += 1
        if pi < n and text[pi] == '"':
            # raw string?  three or more quotes and not verbatim
            q = pi
            while q < n and text[q] == '"':
                q += 1
            qcount = q - pi
            if qcount >= 3 and not verbatim:
                close = text.find('"' * qcount, q)
                end = (close + qcount) if close >= 0 else n
                literals.append(Span(prefix_start, end, q, close if close >= 0 else n, "raw"))
                for k in range(prefix_start, end):
                    mask[k] = " "
                i = end
                continue
            if verbatim:
                j = pi + 1
                while j < n:
                    if text[j] == '"':
                        if j + 1 < n and text[j + 1] == '"':
                            j += 2
                            continue
                        break
                    j += 1
                end = min(j + 1, n)
                literals.append(Span(prefix_start, end, pi + 1, j, "verbatim"))
                for k in range(prefix_start, end):
                    mask[k] = " "
                i = end
                continue
            j = pi + 1
            while j < n:
                if text[j] == "\\":
                    j += 2
                    continue
                if text[j] == '"' or text[j] == "\n":
                    break
                j += 1
            end = min(j + 1, n)
            literals.append(Span(prefix_start, end, pi + 1, j, "regular"))
            for k in range(prefix_start, end):
                mask[k] = " "
            i = end
            continue
        i += 1
    return literals, comments, "".join(mask)


def match_bracket(masked, start, opener, closer):
    depth = 0
    for k in range(start, len(masked)):
        if masked[k] == opener:
            depth += 1
        elif masked[k] == closer:
            depth -= 1
            if depth == 0:
                return k
    return -1


def tool_regions(text, masked):
    """Attribute-declared tools in this file: name -> (attr_start, body_end)."""
    out = []
    for m in re.finditer(r"\[McpServerTool\(", masked):
        attr_open = masked.rfind("[", 0, m.end())
        attr_close = match_bracket(masked, attr_open, "[", "]")
        if attr_close < 0:
            continue
        nm = re.search(r'Name\s*=\s*"([^"]+)"', text[attr_open:attr_close + 1])
        if not nm:
            nm2 = re.search(r"Name\s*=\s*([A-Za-z_][\w.]*)", text[attr_open:attr_close + 1])
            name = nm2.group(1) if nm2 else "<unknown>"
        else:
            name = nm.group(1)
        brace = masked.find("{", attr_close)
        semi = masked.find(";", attr_close)
        if brace >= 0 and (semi < 0 or brace < semi):
            end = match_bracket(masked, brace, "{", "}")
            end = end if end >= 0 else len(masked)
        else:
            end = semi if semi >= 0 else len(masked)
        out.append((name, attr_open, attr_close, end))
    return out


def line_of(text, idx):
    return text.count("\n", 0, idx) + 1


def collect(root):
    files = []
    for d in SOURCE_DIRS:
        base = os.path.join(root, d)
        if not os.path.isdir(base):
            continue
        for dirpath, dirnames, filenames in os.walk(base):
            dirnames[:] = [x for x in dirnames if x not in ("bin", "obj")]
            for f in sorted(filenames):
                if f.endswith(".cs"):
                    files.append(os.path.join(dirpath, f).replace("\\", "/"))
    return sorted(files)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default=".")
    ap.add_argument("--json", help="write the site list here")
    args = ap.parse_args()
    root = os.path.abspath(args.root)

    files = collect(root)
    registered = {}
    per_file = {}

    for path in files:
        with open(path, "r", encoding="utf-8-sig") as fh:
            text = fh.read()
        literals, comments, masked = lex(text)
        regions = tool_regions(text, masked)
        per_file[path] = (text, literals, comments, masked, regions)
        for name, a, b, e in regions:
            registered.setdefault(name, []).append(path)

    reg_names = set(registered)

    sites = []
    for path in files:
        text, literals, comments, masked, regions = per_file[path]
        lit_index = sorted(literals, key=lambda s: s.start)
        com_index = sorted(comments, key=lambda s: s.start)
        flagged_regions = [(a, e) for (name, a, b, e) in regions if name in DELETION_FLAGGED]
        attr_ranges = [(a, b, name) for (name, a, b, e) in regions]

        for m in NAME_RE.finditer(text):
            s, e = m.start(), m.end()
            lit = next((x for x in lit_index if x.cstart <= s and e <= x.cend), None)
            com = next((x for x in com_index if x.start <= s and e <= x.end), None)
            where = "literal" if lit else ("comment" if com else "code")
            name = m.group(0)
            in_flagged = any(a <= s <= f_e for (a, f_e) in flagged_regions)
            in_attr = next((nm for (a, b, nm) in attr_ranges if a <= s <= b), None)

            rel = os.path.relpath(path, root).replace("\\", "/")
            if where != "literal":
                cls = "non-literal"
            elif rel == REGISTRY_FILE:
                cls = "r"
            elif in_flagged:
                cls = "d"
            elif in_attr is not None and name == in_attr:
                cls = "a"
            elif name not in reg_names:
                cls = "e"
            elif text[lit.cstart:lit.cend].strip() == name:
                cls = "c"
            else:
                cls = "b"

            sites.append({
                "file": os.path.relpath(path, root).replace("\\", "/"),
                "line": line_of(text, s),
                "col": s - (text.rfind("\n", 0, s) + 1),
                "offset": s,
                "name": name,
                "class": cls,
                "where": where,
                "literal_kind": lit.kind if lit else None,
                "literal_start": lit.start if lit else None,
                "literal_cstart": lit.cstart if lit else None,
                "literal_cend": lit.cend if lit else None,
                "literal_end": lit.end if lit else None,
                "exact": bool(lit and text[lit.cstart:lit.cend].strip() == name),
            })

    # ---- summary ------------------------------------------------------
    by_class = {}
    for s in sites:
        by_class[s["class"]] = by_class.get(s["class"], 0) + 1
    distinct_names = sorted({s["name"] for s in sites})
    unregistered = sorted({s["name"] for s in sites if s["name"] not in reg_names})
    files_with = sorted({s["file"] for s in sites})

    print("== tool-name site derivation ==")
    print(f"source dirs present : {[d for d in SOURCE_DIRS if os.path.isdir(os.path.join(root, d))]}")
    print(f"files scanned       : {len(files)}")
    print(f"files with a site   : {len(files_with)}")
    print(f"declared tools      : {len(reg_names)}  (from [McpServerTool(Name = ...)])")
    print(f"  of which flagged  : {len(reg_names & DELETION_FLAGGED)} / {len(DELETION_FLAGGED)} expected")
    print(f"name occurrences    : {len(sites)}")
    print(f"distinct names seen : {len(distinct_names)}")
    print(f"undeclared names    : {len(unregistered)} -> {unregistered}")
    print("classes:")
    for k in ("a", "b", "c", "d", "e", "r", "non-literal"):
        print(f"  {k:<11} {by_class.get(k, 0)}")
    rewritable = by_class.get("a", 0) + by_class.get("b", 0) + by_class.get("c", 0)
    print(f"  REWRITABLE (a+b+c) {rewritable}")

    print()
    print("-- per file (rewritable sites, then d / e / non-literal) --")
    rows = []
    for f in files_with:
        fs = [s for s in sites if s["file"] == f]
        cnt = {k: sum(1 for s in fs if s["class"] == k) for k in ("a", "b", "c", "d", "e", "r", "non-literal")}
        rows.append((cnt["a"] + cnt["b"] + cnt["c"], f, cnt, len(fs)))
    for rw, f, cnt, total in sorted(rows, key=lambda r: (-r[0], r[1])):
        print(f"  {rw:>4} rw  (a{cnt['a']} b{cnt['b']} c{cnt['c']} | d{cnt['d']} e{cnt['e']} r{cnt['r']} n{cnt['non-literal']}) "
              f"tot {total:>4}  {f}")

    print()
    print("-- class (d) skip list, by tool (rule 10(a)) --")
    dsites = [s for s in sites if s["class"] == "d"]
    dfiles = sorted({s["file"] for s in dsites})
    for f in dfiles:
        print(f"  {sum(1 for s in dsites if s['file'] == f):>4}  {f}")
    print(f"  total skipped: {len(dsites)}")

    print()
    print("-- declared tool names (the constant population) --")
    for i, nm in enumerate(sorted(reg_names), 1):
        flag = "  [deletion-flagged]" if nm in DELETION_FLAGGED else ""
        print(f"  {i:>3}. {nm}{flag}")

    if args.json:
        with open(args.json, "w", encoding="utf-8") as fh:
            json.dump({
                "registered": sorted(reg_names),
                "deletion_flagged": sorted(DELETION_FLAGGED),
                "sites": sites,
            }, fh, indent=1)
        print(f"\nwrote {args.json}")

    missing = DELETION_FLAGGED - reg_names
    if missing:
        print(f"\n!! deletion-flagged names not found as declared tools: {sorted(missing)}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
