#!/usr/bin/env python3
"""Adds/updates the current version's entry in the Jellyfin plugin repository
manifest (manifest.json), sourced from meta.json plus the release asset that
was just built and uploaded by the CI workflow.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from datetime import datetime, timezone
from pathlib import Path


def latest_changelog_entry(full_changelog: str) -> str:
    """meta.json's changelog is one string holding every past release's notes,
    each starting with its own top-level "- " bullet. Returns just the first
    (i.e. current) entry, since that's the only one relevant to a single
    release's notes.
    """
    parts = re.split(r"(?m)^- ", full_changelog.strip())
    parts = [p for p in parts if p.strip()]
    return parts[0].strip() if parts else full_changelog.strip()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--meta", required=True, type=Path)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--checksum", required=True)
    parser.add_argument("--source-url", required=True)
    args = parser.parse_args()

    meta = json.loads(args.meta.read_text(encoding="utf-8"))

    manifest_path = args.manifest
    manifest = json.loads(manifest_path.read_text(encoding="utf-8")) if manifest_path.exists() else []

    guid = meta["guid"]
    entry = next((p for p in manifest if p.get("guid") == guid), None)
    if entry is None:
        entry = {
            "guid": guid,
            "name": meta["name"],
            "description": meta["description"],
            "overview": meta["overview"],
            "owner": meta["owner"],
            "category": meta["category"],
            "imageUrl": meta.get("imageUrl", ""),
            "versions": [],
        }
        manifest.append(entry)
    else:
        entry["name"] = meta["name"]
        entry["description"] = meta["description"]
        entry["overview"] = meta["overview"]
        entry["owner"] = meta["owner"]
        entry["category"] = meta["category"]

    version = meta["version"]
    entry["versions"] = [v for v in entry["versions"] if v.get("version") != version]
    entry["versions"].insert(0, {
        "version": version,
        "changelog": latest_changelog_entry(meta["changelog"]),
        "targetAbi": meta["targetAbi"],
        "sourceUrl": args.source_url,
        "checksum": args.checksum,
        "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    })
    entry["versions"].sort(key=lambda v: [int(p) for p in v["version"].split(".")], reverse=True)

    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"manifest.json updated: {meta['name']} {version}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
