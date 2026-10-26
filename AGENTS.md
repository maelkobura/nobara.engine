# Rules for coding agents

This file applies to every coding agent that changes this repository. The
documentation rules in full are in
[the documentation guide](docs/contributing/documentation.md).

## Git

1. Never push to `main`. Work on a branch named `type/short-description`
   (`feat/time-scale`, `docs/glossary`) created from an up-to-date `main`,
   and open a pull request.
2. One pull request is one feature or one fix. Do not mix a refactoring with
   a behavior change.
3. The pull request title follows Conventional Commits:
   `type(scope)!: description`.
   - Types: `feat`, `fix`, `perf`, `refactor`, `docs`, `test`, `build`, `ci`,
     `chore`.
   - Scope: the name of a module folder under `src/`, or the name of a tool
     under `tools/` without the `Nobara.` prefix. Omit the scope for a change
     that spans the repository.
   - The description is in the imperative mood, starts with a lowercase
     letter, has no final period. The whole title is at most 72 characters.
   - Commit messages and titles are neutral: they do not refer to planning
     steps.
4. The description explains why the change is made, then lists the
   documentation files that were changed. It becomes the body of the
   squashed commit.
5. A breaking change to a public API adds `!` to the title and a
   `BREAKING CHANGE:` paragraph to the description.
6. Never bypass a CI check: no `#pragma warning disable`, no `<NoWarn>`, no
   disabled rule, no disabled job.
7. Never merge a pull request. Only squash merging is enabled.

## Language and content

- Write all code, comments, documentation, commit messages, and pull request
  text in American English, even when the task is given in another language.
- Do not mention terms listed in `.github/scripts/forbidden-terms.txt`
  anywhere in the repository, including pull request titles and
  descriptions. Examples use a neutral fictional game.
- Before changing a module, read its `README.md` and follow it. If the code
  contradicts it, report the difference.
- Every public API member has an XML comment (`CS1591` is an error). Do not
  add filler comments to silence it.
- A change is complete when it has code, tests, and documentation. See the
  update matrix in the documentation guide.
- Do not document what is not implemented; list it under *Known
  limitations* instead.
- Add every new domain term to the glossary.
- Code excerpts in guides come from compiled sample files.
- End each task by listing the documentation files changed, or why none was.

## Licensing

- Every source file (`.cs`, `.proto`, `.props`, `.targets`, and scripts)
  starts with the MPL 2.0 notice, in the comment syntax of the file:

  ```text
  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at https://mozilla.org/MPL/2.0/.
  ```

- Every third-party dependency has a license compatible with MPL 2.0 (for
  example MIT, BSD, Apache 2.0). Check before adding one, and mention it in
  the pull request.

## Checks

Every CI check can run locally from the repository root. Python 3.12 or
later is required; Node.js is required for markdownlint.

| Check | Command |
| --- | --- |
| All checks | `python .github/scripts/check_all.py` |
| Build and tests | `dotnet pack tools/Nobara.Sdk -c Release -o artifacts/packages`, then `dotnet build Nobara.slnx` then `dotnet test Nobara.slnx` |
| License headers | `python .github/scripts/check_headers.py` |
| Forbidden terms | `python .github/scripts/check_terms.py` |
| Internal Markdown links | `python .github/scripts/check_links.py` |
| Markdown style | `npx markdownlint-cli2 "docs/**/*.md" README.md AGENTS.md "src/*/README.md" "tools/*/README.md"` |
| Pull request title and description | `python .github/scripts/check_pr.py --title "chore: example" --body-file body.md` |
| Tests of the check scripts | `python -m unittest discover -s .github/scripts/tests` |

## Notes

- `Nobara.slnx` is hand-written while it is empty. Once the command-line
  tool can generate it, it is generated and must not be edited by hand.
- Every module project uses `Nobara.Sdk` (`<Project Sdk="Nobara.Sdk/1.0.0">`).
  The SDK is resolved from the local feed `artifacts/packages` (see
  `NuGet.config`), so a fresh clone runs the pack command of the table above
  once, and again after changing `tools/Nobara.Sdk` (then run
  `dotnet build-server shutdown` so no build node keeps the old task assembly). The tests of the SDK pack
  it themselves.
- Until the command-line tool can generate it, write `obj/nobara/modules.props`
  by hand: it maps the identifier of each local module to its project, and a
  dependency that is not listed is restored as a NuGet package. `obj/` is not
  tracked by Git. The format is described in
  [the SDK page](tools/Nobara.Sdk/README.md#local-modules).
- Sections of a module `README.md` between `nobara:generated` markers are
  generated. Do not edit them by hand.
