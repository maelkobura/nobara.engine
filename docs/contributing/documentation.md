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

| Type | Reader's question | What the page does |
| --- | --- | --- |
| Tutorial | "I am new, guide me." | Walks one successful path from start to finish; alternatives are left out. |
| How-to guide | "I want to do X." | Solves one problem for a reader who knows the basics; prerequisites come first. |
| Reference | "What is the exact contract?" | States facts and nothing else, completely, with every entry laid out the same way. |
| Explanation | "Why is it designed this way?" | Gives background and reasons, never steps to follow. |

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

Write the sheet first: it is the short contract that the code then follows,
and it is maintained afterward.

## API reference

Every public member carries an XML comment (`///`) in American English. The
documentation file is generated for each published project, and the compiler
treats `CS1591` (public member without a comment) as an error. It must never
be suppressed.

A comment states the contract and leaves the implementation out. Use each
element for one purpose:

| Element | Content |
| --- | --- |
| `<summary>` | One or two sentences on what the member does, without restating its name. |
| `<param>`, `<returns>` | Range of valid values, units, and whether `null` is allowed. |
| `<exception>` | Every exception thrown deliberately, with its condition. |
| `<remarks>` | Permitted threads, allocations, hot path cost, side effects, call order. |
| `<example>` | A usage sample, when the usage is not obvious. |

Three further rules apply:

- A summary that merely rewords the member name is rejected, and so is a
  placeholder comment written only to silence `CS1591`.
- Interface implementations with no behavior of their own use
  `<inheritdoc/>`.
- `internal` members are exempt from `CS1591`; comment them when the reason
  for their design would not be obvious. The message of an `[Obsolete]`
  attribute names the replacement.

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
