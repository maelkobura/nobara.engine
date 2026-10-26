# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.

"""Runs every repository check locally, in the order the CI runs them.

Usage: check_all.py [--skip-dotnet] [--skip-markdownlint]

markdownlint needs Node.js (it runs through `npx`); the build needs the .NET
SDK pinned by `global.json`.
"""

import argparse
import shutil
import subprocess
import sys

from common import REPO_ROOT, SCRIPTS_DIR

# Keep in sync with .github/workflows/ci.yml.
MARKDOWNLINT = "markdownlint-cli2@0.18.1"
MARKDOWN_GLOBS = ["docs/**/*.md", "README.md", "AGENTS.md", "src/*/README.md"]


def run(name: str, command: list[str]) -> bool:
    print(f"\n== {name}")
    return subprocess.run(command, cwd=REPO_ROOT).returncode == 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--skip-dotnet", action="store_true")
    parser.add_argument("--skip-markdownlint", action="store_true")
    args = parser.parse_args()

    py = sys.executable
    results = {
        "script tests": run(
            "Script tests",
            [py, "-m", "unittest", "discover", "-s", str(SCRIPTS_DIR / "tests")],
        ),
        "license headers": run("License headers", [py, str(SCRIPTS_DIR / "check_headers.py")]),
        "forbidden terms": run("Forbidden terms", [py, str(SCRIPTS_DIR / "check_terms.py")]),
        "internal links": run("Internal links", [py, str(SCRIPTS_DIR / "check_links.py")]),
    }

    if not args.skip_markdownlint:
        npx = shutil.which("npx")
        if npx:
            results["markdownlint"] = run("markdownlint", [npx, "--yes", MARKDOWNLINT, *MARKDOWN_GLOBS])
        else:
            print("\n== markdownlint\nnpx not found; install Node.js or pass --skip-markdownlint.")
            results["markdownlint"] = False

    if not args.skip_dotnet:
        dotnet = shutil.which("dotnet")
        if dotnet:
            results["build"] = run("Build", [dotnet, "build", "Nobara.slnx", "--nologo"])
            results["tests"] = run("Tests", [dotnet, "test", "Nobara.slnx", "--nologo"])
        else:
            print("\n== .NET\ndotnet not found; install the SDK or pass --skip-dotnet.")
            results["build"] = False

    failed = [name for name, ok in results.items() if not ok]
    print("\n" + ("FAILED: " + ", ".join(failed) if failed else "All checks passed."))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
