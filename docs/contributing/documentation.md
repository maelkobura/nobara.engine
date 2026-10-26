# Documentation guide

This page states how documentation is organized, written, and kept up to
date in this repository. It applies to people and to coding agents.

## Principles

- Documentation is Markdown in this repository, versioned with the code.
- A change is not complete until its documentation is. Code, tests, and
  documentation arrive in the same pull request.
- Documentation describes what exists. It contains no promises about the
  future; what is missing goes in a *Known limitations* section.
- Generate what can be generated from a source of truth, such as manifests
  and XML comments. Never copy it by hand.

## Types of documentation

Each page belongs to exactly one of four types and links to the others
instead of mixing them.

| Type | The reader asks | Rules |
| --- | --- | --- |
| Tutorial | "I am new, guide me." | Follows the nominal path only and presents no alternatives. |
| How-to guide | "I want to do X." | Assumes the basics, solves one problem, lists prerequisites first. |
| Reference | "What is the exact contract?" | Describes without explaining. Exhaustive, with the same structure for every entry. |
| Explanation | "Why is it designed this way?" | Contains no instructions. |

## Layout

```text
README.md        what it is, status, build, link to docs/ (30 lines at most)
AGENTS.md        rules for coding agents
docs/
  tutorials/
  guides/
  reference/     formats, command-line tool, configuration keys
  explanation/   architecture, glossary
  adr/           architecture decision records
  contributing/  this guide
src/<module>/README.md   one module sheet per module folder
```

Create a folder only when it has content. File names are in English, in
`kebab-case`, one subject per file. Every page starts with a single `#`
title and a sentence that says what the reader finds there.

## Module sheet

Each module folder has a `README.md` with these sections, in this order, even
when empty (write "None"):

1. Purpose: one sentence.
2. Status: `experimental` or `stable`.
3. Level: core, critical platform, optional platform, or business; client,
   server, or both.
4. Dependencies: required and optional.
5. Entry points: services exposed, tick steps joined, context states
   provided.
6. Events: emitted and listened to, with synchronous or deferred dispatch.
7. Configuration: key name, type, default, effect.
8. Resources: resource types and schemas read or provided.
9. Constraints: allowed threads, hot paths, allocations, invariants.
10. Known limitations: what is missing or deliberately absent.

Sections generated from the module manifest sit between markers and are not
edited by hand:

```markdown
<!-- nobara:generated:begin dependencies -->
...
<!-- nobara:generated:end -->
```

The sheet is written before the module code. It serves as a short interface
contract, then is kept up to date.

## API reference

- All public API has XML comments (`///`) in American English. Documentation
  files are generated for every published project, and `CS1591` (missing
  comment on a public member) is an error. Never suppress it.
- A comment documents the contract, not the implementation:
  - `<summary>`: what the member does, in one or two sentences, without
    repeating its name;
  - `<param>` and `<returns>`: value domain, units, whether `null` is
    accepted;
  - `<exception>`: each exception thrown on purpose and its condition;
  - `<remarks>`: allowed threads, allocation, cost on hot paths, side
    effects, required call order;
  - `<example>` when usage is not obvious.
- Not allowed: a `<summary>` that paraphrases the name, or filler comments
  added to silence `CS1591`.
- Use `<inheritdoc/>` for interface implementations with no behavior of
  their own.
- `internal` members are not subject to `CS1591`. Comment them when the
  reason behind them is not obvious.
- An `[Obsolete]` message names the replacement.

## Keeping documentation up to date

| Change | Update |
| --- | --- |
| New public member, or changed signature or contract | XML comments |
| New module | Module sheet; the architecture page if the structure changes |
| Dependency, configuration key, or state added or removed | The module manifest, then regenerate the sheet |
| File format changed | `docs/reference/formats/` |
| Command or option of the command-line tool | `docs/reference/cli.md` |
| Architecture decision | `docs/adr/` |
| New domain term | [Glossary](../explanation/glossary.md) |
| Behavior that a guide or tutorial describes | That guide or tutorial |

Write the module sheet before the code, the reference while coding, and
guides and tutorials once the API they cover is `stable`. Code excerpts in
guides and tutorials come from compiled sample files, never from uncompiled
code.

## Writing rules

### Language and tone

- American English: *color*, *behavior*, *initialize*.
- Neutral and impersonal, for a reader who does not know the authors or the
  history of the project.
- No first person. No internal jokes, opinions about other engines, or
  disparaging comparisons.
- Examples use a neutral fictional game.
- Module names and identifiers stay as declared (`nBlocks`, `Nobara.Blocks`)
  and each is defined in the [glossary](../explanation/glossary.md).

### Style

- Answer first, context second.
- Short sentences, active voice, present tense.
- One term, one meaning, fixed in the glossary. Do not alternate two words for
  the same concept.
- Avoid *simply*, *just*, *obviously*, *easy*, and *of course*.
- Identifiers, paths, and commands are in `code` style.
- Units are always explicit: seconds, blocks, meters.
- No screenshots of text or code. Diagrams are written in Mermaid.

## Checks

The CI runs these on every pull request; each one also runs locally (see
`AGENTS.md` at the repository root):

- compilation with `CS1591` as an error;
- markdownlint on `docs/`, `README.md`, `AGENTS.md`, and module sheets;
- broken internal links and anchors;
- the list of forbidden terms, matched without regard to case, over the
  whole repository;
- the pull request title and description.
