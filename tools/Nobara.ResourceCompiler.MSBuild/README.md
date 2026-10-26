# Nobara.ResourceCompiler.MSBuild

`Nobara.ResourceCompiler.MSBuild` holds the MSBuild task that compiles the
resources of a module, a facade over
[`Nobara.ResourceCompiler`](../Nobara.ResourceCompiler/README.md). This page
covers how the task is shipped and run. Modules do not use it directly:
[`Nobara.Sdk`](../Nobara.Sdk/README.md#schemas-and-resources) runs it.

## The task

`CompileResources` reads `module.nobara`, the `.nof` files, the descriptor set
of the schemas, the referenced assemblies, and the plugin assemblies. It
writes to its output folder:

| File | Content |
| --- | --- |
| `module.pb` | The compiled module manifest. |
| `resources.pb` | The compiled resources. |
| `files/` | Binary files changed by a plugin. |
| `embedded.txt` | One line per manifest resource to embed: its name, a vertical bar, and its path. |
| `resources.cache` | The cache of the incremental compilation. |
| `resources.stamp` | Written on every run; the output of the MSBuild target. |

Only the referenced assemblies of the dependencies declared in `module.nobara`
provide schemas and resources; a module referenced transitively provides
nothing. Compiled files are written only when their content changes, so that the C#
compiler stays up to date. Every diagnostic is logged with its file, line,
and column.

## Shipping and running

`Nobara.Sdk` ships the output folder of this project in `tasks/net10.0/`. The
task runs in a .NET task host process (`TaskFactory="TaskHostFactory"`,
`Runtime="NET"`), which ends with the task, so the plugin assemblies it loads
never stay locked by a build node.

## Known limitations

- The task needs the .NET task host: it runs under `dotnet build`, and under
  any MSBuild that can start a .NET task host.
