#!/usr/bin/env python3
"""Dump the built server's own ``tools/list`` as canonical JSON.

The tool-name registry rewrites 279 shipped literals into constant references.
Nothing a caller sees may move by a character.  The oracle for that is not a
sample of source sites -- it is the wire: names, descriptions, parameter
descriptions and input schemas exactly as a client receives them.  Dump it
before the sweep and after, and diff.

Drives the REAL ``housecarl-mcp.exe`` over stdio, the same way
``BindingShimProbe`` does: a fresh empty ``HOUSECARL_DATA_DIR`` so the server
boots unconfigured and deterministically, even on a dev box with a real user
config beside the exe.

    python scripts/tool-names/dump-tools-list.py --out before.json
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import tempfile
import uuid

PROTOCOL = "2025-06-18"


def rpc(proc, ident, method, params):
    proc.stdin.write(json.dumps({
        "jsonrpc": "2.0", "id": ident, "method": method, "params": params,
    }) + "\n")
    proc.stdin.flush()
    while True:
        line = proc.stdout.readline()
        if not line:
            raise SystemExit(f"server closed stdout waiting for id {ident}")
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except json.JSONDecodeError:
            continue
        if msg.get("id") == ident:
            if "error" in msg:
                raise SystemExit(f"{method} failed: {msg['error']}")
            return msg["result"]


def notify(proc, method):
    proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": method}) + "\n")
    proc.stdin.flush()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default=".")
    ap.add_argument("--config", default="Release")
    ap.add_argument("--out", required=True)
    args = ap.parse_args()

    exe = os.path.join(args.root, "src", "housecarl-mcp", "bin", args.config,
                       "net9.0", "housecarl-mcp.exe")
    if not os.path.exists(exe):
        raise SystemExit(f"housecarl-mcp.exe not found at '{exe}' -- build the solution first")

    data_dir = os.path.join(tempfile.gettempdir(), "housecarl-toolslist-" + uuid.uuid4().hex)
    os.makedirs(data_dir, exist_ok=True)
    env = dict(os.environ, HOUSECARL_DATA_DIR=data_dir)

    proc = subprocess.Popen(
        [exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
        text=True, encoding="utf-8", env=env, cwd=os.path.abspath(args.root))
    try:
        rpc(proc, 1, "initialize", {
            "protocolVersion": PROTOCOL,
            "capabilities": {},
            "clientInfo": {"name": "tool-name-registry-render-proof", "version": "0"},
        })
        notify(proc, "notifications/initialized")

        tools, cursor, ident = [], None, 2
        while True:
            params = {} if cursor is None else {"cursor": cursor}
            result = rpc(proc, ident, "tools/list", params)
            tools.extend(result.get("tools", []))
            cursor = result.get("nextCursor")
            ident += 1
            if not cursor:
                break
    finally:
        try:
            proc.stdin.close()
        except Exception:
            pass
        try:
            proc.wait(timeout=10)
        except Exception:
            proc.kill()

    tools.sort(key=lambda t: t.get("name", ""))
    with open(args.out, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(tools, fh, indent=1, sort_keys=True, ensure_ascii=False)
        fh.write("\n")

    chars = len(json.dumps(tools, ensure_ascii=False))
    print(f"tools published: {len(tools)}")
    print(f"canonical size : {chars} chars -> {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
