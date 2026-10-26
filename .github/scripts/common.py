# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.

"""Helpers shared by the repository checks."""

import subprocess
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPTS_DIR.parent.parent

# Directories that never contain files owned by the repository.
SKIPPED_DIRS = {".git", "bin", "obj", "node_modules"}


def list_files(root: Path = REPO_ROOT) -> list[Path]:
    """Returns the files of the repository, as absolute paths.

    Uses Git when the root is a Git working tree, so that ignored files are
    skipped. Falls back to a directory walk otherwise.
    """
    try:
        out = subprocess.run(
            ["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard"],
            cwd=root,
            check=True,
            capture_output=True,
        ).stdout.decode("utf-8")
        names = [n for n in out.split("\0") if n]
        return sorted(root / n for n in names if (root / n).is_file())
    except (OSError, subprocess.CalledProcessError):
        return sorted(
            p
            for p in root.rglob("*")
            if p.is_file() and not SKIPPED_DIRS.intersection(p.relative_to(root).parts)
        )


def relative(path: Path, root: Path = REPO_ROOT) -> str:
    """Returns a repository-relative path with forward slashes."""
    return path.relative_to(root).as_posix()
