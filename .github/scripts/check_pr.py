# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.

"""Checks a pull request title and description.

The title becomes the first line of the squashed commit and the description
becomes its body, so both are checked:

- title: Conventional Commits format, known type, scope that names an
  existing folder under `src/` or a tool under `tools/` (without the
  `Nobara.` prefix), at most 72 characters;
- description: no forbidden term.

Usage: check_pr.py --title TITLE [--body-file FILE]
"""

import argparse
import re
import sys
from pathlib import Path

from check_terms import check_text, load_terms
from common import REPO_ROOT

TYPES = ("feat", "fix", "perf", "refactor", "docs", "test", "build", "ci", "chore")
MAX_TITLE_LENGTH = 72
TOOL_PREFIX = "Nobara."

_TITLE = re.compile(
    r"^(?P<type>[a-z]+)(?:\((?P<scope>[^()\s]+)\))?(?P<breaking>!)?: (?P<description>.+)$"
)


def known_scopes(root: Path = REPO_ROOT) -> set[str]:
    """Returns the scopes allowed by the folders that exist in `root`."""
    scopes = set()
    src = root / "src"
    if src.is_dir():
        scopes.update(p.name for p in src.iterdir() if p.is_dir())
    tools = root / "tools"
    if tools.is_dir():
        for p in tools.iterdir():
            if p.is_dir():
                scopes.add(p.name[len(TOOL_PREFIX):] if p.name.startswith(TOOL_PREFIX) else p.name)
    return scopes


def check_title(title: str, scopes: set[str]) -> list[str]:
    """Returns one message per problem found in a pull request title."""
    errors = []
    if len(title) > MAX_TITLE_LENGTH:
        errors.append(f"title is {len(title)} characters long, the maximum is {MAX_TITLE_LENGTH}")
    match = _TITLE.match(title)
    if not match:
        return errors + ["title must look like 'type(scope)!: description' (scope and '!' are optional)"]
    if match["type"] not in TYPES:
        errors.append(f"unknown type '{match['type']}', expected one of: {', '.join(TYPES)}")
    if match["scope"] is not None and match["scope"] not in scopes:
        allowed = ", ".join(sorted(scopes)) or "none yet"
        errors.append(f"unknown scope '{match['scope']}', existing scopes: {allowed}")
    description = match["description"]
    if description[0].isupper():
        errors.append("description must start with a lowercase letter")
    if description.endswith("."):
        errors.append("description must not end with a period")
    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--title", required=True)
    parser.add_argument("--body-file", help="file with the pull request description")
    args = parser.parse_args()

    errors = [f"title: {e}" for e in check_title(args.title, known_scopes())]
    terms = load_terms()
    errors += [f"title: {e}" for e in check_text(args.title, terms)]
    if args.body_file:
        body = Path(args.body_file).read_text(encoding="utf-8")
        errors += [f"description: {e}" for e in check_text(body, terms)]

    for error in errors:
        print(error)
    if errors:
        return 1
    print("Pull request title and description: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
