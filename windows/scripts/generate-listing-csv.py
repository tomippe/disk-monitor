# -*- coding: utf-8 -*-
"""
Partner Center listing CSV を store-listings.json から生成する。

Usage:
  python scripts/generate-listing-csv.py
  python scripts/generate-listing-csv.py -o build/msix/listingData-9P47CBVHQ797.csv
"""
from __future__ import annotations

import argparse
import csv
import json
import re
import sys
from pathlib import Path

WIN_DIR = Path(__file__).resolve().parent.parent
SCRIPTS = Path(__file__).resolve().parent
DEFAULT_JSON = SCRIPTS / "store-listings.json"
DEFAULT_TEMPLATE = SCRIPTS / "listing-csv-template.csv"
# Not under build/ (gitignored) — so it shows in IDE File Changed / Explorer.
DEFAULT_OUT = WIN_DIR / "dist" / "listingData-9P47CBVHQ797.csv"

# store-listings.json keys → CSV language columns
LANG_MAP = (
    ("en-us", "en-us"),
    ("ja-jp", "ja-jp"),
    ("zh-hans", "zh-hans"),
)

COPYRIGHT = "Copyright (c) 2026 tomippe. All rights reserved."


def load_listings(path: Path) -> dict:
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    listings = data.get("listings")
    if not isinstance(listings, dict):
        raise SystemExit(f"ERROR: missing listings in {path}")
    for key, _ in LANG_MAP:
        if key not in listings:
            raise SystemExit(f"ERROR: missing listings.{key} in {path}")
    return listings


def fill_csv(template_path: Path, out_path: Path, listings: dict) -> None:
    with template_path.open("r", encoding="utf-8-sig", newline="") as f:
        reader = csv.reader(f)
        header = next(reader)
        rows = list(reader)

    # Normalize header names for lookup
    header_l = [h.strip() for h in header]
    col = {name: i for i, name in enumerate(header_l)}

    def set_lang(row: list[str], lang_col: str, value: str) -> None:
        idx = col.get(lang_col)
        if idx is None:
            return
        while len(row) <= idx:
            row.append("")
        row[idx] = value

    for row in rows:
        if not row:
            continue
        while len(row) < len(header):
            row.append("")
        field = row[0]

        if field == "Title":
            for src, dest in LANG_MAP:
                set_lang(row, dest, str(listings[src].get("title") or "Disk Monitor"))
            continue

        if field in ("Description", "ShortDescription", "ReleaseNotes"):
            json_key = {
                "Description": "description",
                "ShortDescription": "shortDescription",
                "ReleaseNotes": "releaseNotes",
            }[field]
            for src, dest in LANG_MAP:
                set_lang(row, dest, str(listings[src].get(json_key) or ""))
            continue

        m = re.fullmatch(r"Feature(\d+)", field)
        if m:
            idx = int(m.group(1)) - 1
            for src, dest in LANG_MAP:
                feats = listings[src].get("features") or []
                set_lang(row, dest, feats[idx] if isinstance(feats, list) and idx < len(feats) else "")
            continue

        m = re.fullmatch(r"Keywords?(\d+)", field)
        if m:
            idx = int(m.group(1)) - 1
            for src, dest in LANG_MAP:
                keys = listings[src].get("keywords") or []
                set_lang(row, dest, keys[idx] if isinstance(keys, list) and idx < len(keys) else "")
            continue

        if field in (
            "CopyrightTrademarkInformation",
            "CopyrightAndTrademarkInfo",
            "Copyright",
        ):
            for _, dest in LANG_MAP:
                set_lang(row, dest, COPYRIGHT)
            continue

    out_path.parent.mkdir(parents=True, exist_ok=True)
    with out_path.open("w", encoding="utf-8-sig", newline="") as f:
        writer = csv.writer(f, lineterminator="\n")
        writer.writerow(header)
        writer.writerows(rows)


def main() -> int:
    ap = argparse.ArgumentParser(
        description="Generate Partner Center listing CSV from store-listings.json"
    )
    ap.add_argument("--json", type=Path, default=DEFAULT_JSON)
    ap.add_argument("--template", type=Path, default=DEFAULT_TEMPLATE)
    ap.add_argument("-o", "--output", type=Path, default=DEFAULT_OUT)
    args = ap.parse_args()

    if not args.json.is_file():
        print(f"ERROR: listings json not found: {args.json}", file=sys.stderr)
        return 1
    if not args.template.is_file():
        print(f"ERROR: template not found: {args.template}", file=sys.stderr)
        return 1

    listings = load_listings(args.json)
    for src, _ in LANG_MAP:
        desc = listings[src].get("description") or ""
        if not str(desc).strip():
            print(f"ERROR: empty description for {src}", file=sys.stderr)
            return 1

    fill_csv(args.template, args.output, listings)
    print(f"Listing CSV: {args.output}")
    for src, _ in LANG_MAP:
        L = listings[src]
        feats = L.get("features") or []
        print(
            f"  {src}: desc={len(str(L.get('description') or ''))} "
            f"notes={len(str(L.get('releaseNotes') or ''))} "
            f"short={len(str(L.get('shortDescription') or ''))} "
            f"features={len(feats) if isinstance(feats, list) else 0}"
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
