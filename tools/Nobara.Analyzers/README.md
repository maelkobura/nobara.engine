# Nobara.Analyzers

`Nobara.Analyzers` holds the Roslyn analyzers that enforce the engine rules at
compile time: hot path rules, no mutable static state, no `async` in loop
code, and isolation of optional dependencies. This page covers how the
analyzers are built, shipped, and tested. The diagnostics themselves are
described in [the analyzer reference](../../docs/reference/analyzers.md).

## How a project receives them

The assembly is not a package of its own. `Nobara.Sdk` ships it in its
package, in the `analyzers/` folder, and attaches it to the compilation of
modules and script facades through its `NobaraAnalyzers` extension point. See
[the SDK page](../Nobara.Sdk/README.md#engine-analyzers).

Packing the SDK builds this project:

```sh
dotnet pack tools/Nobara.Sdk -c Release -o artifacts/packages
```

Pack again after changing the analyzers, as after any change to the SDK.

## Analyzers

| Class | Diagnostics | Rule |
| --- | --- | --- |
| `HotPathAnalyzer` | `NOB101` to `NOB105` | Code marked `[HotPath]` does not allocate, use LINQ, capture, box, or call unmarked code of its project. |
| `StaticStateAnalyzer` | `NOB201` to `NOB203` | No mutable static state. |
| `AsyncAnalyzer` | `NOB301` | No `async` in code marked `[HotPath]` or `[LoopCallback]`. |
| `OptionalDependencyAnalyzer` | `NOB401` to `NOB403` | Types of optional dependencies stay in isolation classes. |

The attributes are matched by full name (`Nobara.Core.HotPathAttribute`,
`Nobara.Core.LoopCallbackAttribute`,
`Nobara.Core.UsesOptionalDependencyAttribute`), so the analyzers do not
reference the assembly that defines them.

## Constraints

- The project targets `netstandard2.0`, as the compiler requires, and
  references `Microsoft.CodeAnalysis.CSharp` 5.0.0, the version of the
  compiler in the .NET 10.0.100 SDK. A newer compiler loads it; an older one
  does not.
- Every diagnostic is an error by default.
- No rule follows calls into other assemblies, and every check is local to a
  method body, a declaration, or a syntax tree, so the analysis time grows
  linearly with the code.
- The descriptors are listed in `AnalyzerReleases.Unshipped.md`, as the
  release tracking of Roslyn requires. Move them to
  `AnalyzerReleases.Shipped.md` when a version of the SDK is published.

## Building and testing

```sh
dotnet build Nobara.slnx
dotnet test tools/Nobara.Analyzers.Tests
```

`tools/Nobara.Analyzers.Tests` compiles samples in memory against the
assemblies of the running .NET and checks the diagnostics marked in each
sample with `{|NOB101:code|}`. The tests need no network. The tests in
`tools/Nobara.Sdk.Tests` build sample modules with the packed SDK and check
that the analyzers run where they should.

## Known limitations

See the [analyzer reference](../../docs/reference/analyzers.md#known-limitations).
