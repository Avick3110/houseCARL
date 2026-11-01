#!/usr/bin/env python3
"""Warn when a framework in upstream-pins.json releases past the version our skills were written against.

For each pin: fetch the latest upstream version (GitHub releases or tags, or the keyless Nexus GraphQL API),
and if it is newer than the pin, open or update one issue labelled `upstream` for that framework.
A null pin only logs the current version. A failed fetch is reported and never opens an issue.

Usage: python scripts/upstream_watch.py [--dry-run] [--pins upstream-pins.json]
Needs GITHUB_TOKEN to write issues; --dry-run only reads and prints what it would open or update.
"""
import argparse
import html
import json
import os
import re
import sys
import urllib.error
import urllib.request

GITHUB_API = "https://api.github.com"
NEXUS_GRAPHQL = "https://api.nexusmods.com/v2/graphql"
LABEL = "upstream"
NOTES_PER_RELEASE = 4000   # characters of release notes kept per version
NOTES_TOTAL = 40000        # characters of release notes kept per issue; GitHub caps a body at 65536
PRERELEASE = re.compile(r"(rc|beta|alpha|pre|dev)", re.IGNORECASE)


def version_key(text):
    """The numeric parts of a version string as a tuple, or None when it has none."""
    m = re.search(r"\d+(?:\.\d+)*", text or "")
    if not m:
        return None
    parts = [int(p) for p in m.group(0).split(".")]
    while len(parts) < 4:
        parts.append(0)
    return tuple(parts)


def http_json(url, data=None, method=None, token=None):
    headers = {"User-Agent": "houseCARL-upstream-watch", "Accept": "application/json"}
    if data is not None:
        headers["Content-Type"] = "application/json"
        data = json.dumps(data).encode()
    if token and url.startswith(GITHUB_API):
        headers["Authorization"] = "Bearer " + token
        headers["Accept"] = "application/vnd.github+json"
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    with urllib.request.urlopen(req, timeout=30) as resp:
        body = resp.read()
    return json.loads(body) if body else None


def fetch_github(source, token):
    """(latest version string, [(version, notes)] for every release) from a repo's releases, else its tags."""
    repo = source["repo"]
    releases = http_json(f"{GITHUB_API}/repos/{repo}/releases?per_page=100", token=token) or []
    found = [(r["tag_name"], r.get("body") or "") for r in releases
             if not r.get("draft") and not r.get("prerelease") and version_key(r["tag_name"])]
    if not found:
        tags = http_json(f"{GITHUB_API}/repos/{repo}/tags?per_page=100", token=token) or []
        found = [(t["name"], "") for t in tags if version_key(t["name"]) and not PRERELEASE.search(t["name"])]
    if not found:
        raise RuntimeError(f"{repo} has no versioned releases or tags")
    latest = max(found, key=lambda f: version_key(f[0]))[0]
    return latest, found, f"https://github.com/{repo}/releases"


def fetch_nexus(source, token):
    """(latest version string, [(version, changelog)] per file version) from the keyless Nexus GraphQL API."""
    game_id, mod_id = source["game_id"], source["mod_id"]
    query = ("query($ids:[CompositeIdInput!]!,$modId:ID!,$gameId:ID!){"
             "legacyMods(ids:$ids){nodes{modId name version}}"
             "modFiles(modId:$modId,gameId:$gameId){version changelogText}}")
    variables = {"ids": [{"gameId": game_id, "modId": mod_id}], "modId": str(mod_id), "gameId": str(game_id)}
    result = http_json(NEXUS_GRAPHQL, data={"query": query, "variables": variables})
    if result.get("errors"):
        raise RuntimeError("Nexus GraphQL error: " + "; ".join(e.get("message", "?") for e in result["errors"]))
    nodes = result["data"]["legacyMods"]["nodes"]
    if not nodes or not nodes[0].get("version"):
        raise RuntimeError(f"Nexus mod {mod_id} returned no version")
    notes = {}
    for f in result["data"].get("modFiles") or []:
        v = (f.get("version") or "").strip()
        if v and version_key(v):
            lines = notes.setdefault(v, [])
            for line in (html.unescape(t) for t in f.get("changelogText") or []):
                if line not in lines:
                    lines.append(line)
    found = [(v, "\n".join("- " + line for line in lines)) for v, lines in notes.items()]
    url = f"https://www.nexusmods.com/{source.get('game', 'skyrimspecialedition')}/mods/{mod_id}"
    return nodes[0]["version"].strip(), found, url


FETCHERS = {"github": fetch_github, "nexus": fetch_nexus}


def issue_title(pin, latest):
    return f"Upstream: {pin['name']} {latest} is out (pinned {pin['pinned']})"


def marker(pin):
    return f"<!-- upstream-watch:{pin['name']} -->"


def issue_body(pin, latest, found, url):
    pinned_key = version_key(pin["pinned"])
    newer = sorted((f for f in found if version_key(f[0]) > pinned_key), key=lambda f: version_key(f[0]), reverse=True)
    depends = "\n".join(f"- `{d}`" for d in pin["depends"]) or "- (none listed)"
    out = [marker(pin),
           f"**{pin['name']}** has released **{latest}**; houseCARL is pinned to **{pin['pinned']}**.",
           "", f"Source: {url}", "", "Depends on this pin:", depends, "",
           "Re-check the dependent skill or code against the new release, then bump `pinned` in "
           "`upstream-pins.json`. This issue is opened and updated by `scripts/upstream_watch.py`.",
           "", "## Release notes since the pin"]
    total = 0
    for version, notes in newer:
        text = notes.strip() or "(the source gives no notes for this version)"
        if len(text) > NOTES_PER_RELEASE:
            text = text[:NOTES_PER_RELEASE] + "\n… (truncated)"
        if total + len(text) > NOTES_TOTAL:
            out.append(f"\n… older versions left out; see {url}")
            break
        total += len(text)
        out += ["", f"### {version}", "", text]
    if not newer:
        out += ["", "(the source lists no notes for versions after the pin)"]
    return "\n".join(out) + "\n"


def open_issues(repo, token):
    """Every open issue labelled upstream, as {framework name: issue}."""
    by_name, page = {}, 1
    while True:
        batch = http_json(f"{GITHUB_API}/repos/{repo}/issues?state=open&labels={LABEL}&per_page=100&page={page}",
                          token=token) or []
        for issue in batch:
            m = re.search(r"<!-- upstream-watch:(.+?) -->", issue.get("body") or "")
            if m and "pull_request" not in issue:
                by_name.setdefault(m.group(1), issue)
        if len(batch) < 100:
            return by_name
        page += 1


def ensure_label(repo, token):
    try:
        http_json(f"{GITHUB_API}/repos/{repo}/labels", token=token, method="POST",
                  data={"name": LABEL, "color": "c5def5", "description": "A framework we pin has released past the pin"})
    except urllib.error.HTTPError as e:
        if e.code != 422:   # 422: the label already exists
            raise


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--dry-run", action="store_true", help="read only; print what would be opened or updated")
    parser.add_argument("--pins", default=os.path.join(os.path.dirname(__file__), "..", "upstream-pins.json"))
    args = parser.parse_args()

    repo = os.environ.get("GITHUB_REPOSITORY", "Avick3110/houseCARL")
    token = os.environ.get("GITHUB_TOKEN")
    if not args.dry_run and not token:
        sys.exit("GITHUB_TOKEN is not set; set it, or pass --dry-run to only print what would change.")

    with open(args.pins, encoding="utf-8") as f:
        pins = json.load(f)["pins"]

    existing, failed = None, []
    for pin in pins:
        name = pin["name"]
        try:
            latest, found, url = FETCHERS[pin["source"]["type"]](pin["source"], token)
        except Exception as e:   # one bad source fails only its own entry
            failed.append(name)
            print(f"::error title=upstream-watch::{name}: fetch failed ({e}); no issue opened for it.")
            continue

        if pin["pinned"] is None:
            print(f"{name}: latest {latest} (not pinned; nothing depends on it yet)")
            continue
        if version_key(latest) <= version_key(pin["pinned"]):
            print(f"{name}: up to date ({latest}, pinned {pin['pinned']})")
            continue

        title, body = issue_title(pin, latest), issue_body(pin, latest, found, url)
        try:
            if existing is None:
                existing = open_issues(repo, token)
            issue = existing.get(name)
            if args.dry_run:
                action = f"would update #{issue['number']}" if issue else "would open"
                print(f"{name}: BEHIND, {action}: {title}")
                continue
            if issue:
                if issue["title"] != title or (issue.get("body") or "") != body:
                    http_json(f"{GITHUB_API}/repos/{repo}/issues/{issue['number']}", token=token, method="PATCH",
                              data={"title": title, "body": body})
                    print(f"{name}: BEHIND, updated #{issue['number']}: {title}")
                else:
                    print(f"{name}: BEHIND, #{issue['number']} already current")
            else:
                ensure_label(repo, token)
                created = http_json(f"{GITHUB_API}/repos/{repo}/issues", token=token, method="POST",
                                    data={"title": title, "body": body, "labels": [LABEL]})
                print(f"{name}: BEHIND, opened #{created['number']}: {title}")
        except Exception as e:
            failed.append(name)
            print(f"::error title=upstream-watch::{name}: could not read or write its issue ({e}).")

    if failed:
        sys.exit(f"{len(failed)} entr{'y' if len(failed) == 1 else 'ies'} failed: {', '.join(failed)}")


if __name__ == "__main__":
    main()
