# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.

"""Checks that every source file carries the MPL 2.0 notice (Exhibit A)."""

import re
import sys
from pathlib import Path

from common import REPO_ROOT, SKIPPED_DIRS, list_files, relative

# Source files that must carry the notice, by extension.
SOURCE_EXTENSIONS = {
    ".cs", ".proto", ".props", ".targets",
    ".py", ".sh", ".ps1", ".cmd", ".bat",
}

# Only the beginning of a file is searched for the notice.
HEADER_LINES = 15

NOTICE = (
    "This Source Code Form is subject to the terms of the Mozilla Public "
    "License, v. 2.0. If a copy of the MPL was not distributed with this "
    "file, You can obtain one at https://mozilla.org/MPL/2.0/."
)

# Comment delimiters removed before comparing the notice.
_COMMENT_MARKERS = re.compile(r"<!--|-->|/\*|\*/|^\s*(//|#|\*|REM\b|::)", re.MULTILINE)


def has_notice(text: str) -> bool:
    """Returns whether the first lines of `text` contain the notice."""
    head = "\n".join(text.splitlines()[:HEADER_LINES])
    flat = " ".join(_COMMENT_MARKERS.sub(" ", head).split())
    return NOTICE in flat


def check(root: Path = REPO_ROOT) -> list[str]:
    """Returns one message per source file without the notice."""
    errors = []
    for path in list_files(root):
        if path.suffix.lower() not in SOURCE_EXTENSIONS:
            continue
        if SKIPPED_DIRS.intersection(path.relative_to(root).parts):
            continue
        text = path.read_text(encoding="utf-8-sig", errors="replace")
        if not has_notice(text):
            errors.append(f"{relative(path, root)}: missing MPL 2.0 header")
    return errors


def main() -> int:
    errors = check()
    for error in errors:
        print(error)
    if errors:
        print(f"{len(errors)} file(s) without the MPL 2.0 header.")
        return 1
    print("License headers: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
