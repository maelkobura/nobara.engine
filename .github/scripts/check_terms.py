# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.

"""Checks that forbidden terms appear nowhere in the repository.

The terms live in `forbidden-terms.txt`, next to this script, one per line.
Matching ignores case. That file is the only one excluded from the scan.
Pull request text can be checked too: `--text-file` reads it, `-` is stdin.
"""

import argparse
import sys
from pathlib import Path

from common import REPO_ROOT, SCRIPTS_DIR, list_files, relative

TERMS_FILE = SCRIPTS_DIR / "forbidden-terms.txt"


def load_terms(path: Path = TERMS_FILE) -> list[str]:
    """Reads the list of forbidden terms."""
    lines = path.read_text(encoding="utf-8").splitlines()
    return [line.strip() for line in lines if line.strip()]


def find_terms(text: str, terms: list[str]) -> list[tuple[int, str]]:
    """Returns (line number, term) for each forbidden term found in `text`."""
    found = []
    for number, line in enumerate(text.splitlines(), start=1):
        lowered = line.lower()
        for term in terms:
            if term.lower() in lowered:
                found.append((number, term))
    return found


def check_text(text: str, terms: list[str]) -> list[str]:
    """Returns one message per forbidden term found in free text."""
    return [f"line {n}: forbidden term '{t}'" for n, t in find_terms(text, terms)]


def check_repository(
    root: Path = REPO_ROOT, terms_file: Path = TERMS_FILE, terms: list[str] | None = None
) -> list[str]:
    """Returns one message per forbidden term found in a repository file."""
    terms = terms if terms is not None else load_terms(terms_file)
    errors = []
    for path in list_files(root):
        if path.resolve() == terms_file.resolve():
            continue
        try:
            text = path.read_bytes().decode("utf-8")
        except UnicodeDecodeError:
            text = path.read_bytes().decode("latin-1")
        name = relative(path, root)
        # The path itself must not carry a forbidden term either.
        errors += [f"{name}: forbidden term '{t}' in the file name" for _, t in find_terms(name, terms)]
        errors += [f"{name}:{n}: forbidden term '{t}'" for n, t in find_terms(text, terms)]
    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--text-file", help="check this text instead of the repository ('-' is stdin)")
    args = parser.parse_args()

    if args.text_file:
        if args.text_file == "-":
            text = sys.stdin.read()
        else:
            text = Path(args.text_file).read_text(encoding="utf-8")
        errors = check_text(text, load_terms())
    else:
        errors = check_repository()

    for error in errors:
        print(error)
    if errors:
        print(f"{len(errors)} forbidden term(s) found.")
        return 1
    print("Forbidden terms: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
