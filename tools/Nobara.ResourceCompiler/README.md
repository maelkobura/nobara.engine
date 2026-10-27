# Nobara.ResourceCompiler

`Nobara.ResourceCompiler` is the generic resource compiler of the engine, as a
library. It compiles the `.nof` files and the manifest of a module, checks
the manifests of a distribution, and loads the plugins that resource types
need. The [MSBuild task](../Nobara.ResourceCompiler.MSBuild/README.md) is a
facade over it; tools can call it directly, for example to compile one file
again while the engine runs. The formats are described in
[the `.nof` reference](../../docs/reference/formats/nof.md) and
[the schema options reference](../../docs/reference/formats/proto-options.md).

## What it compiles

| Input | Schema | Output |
| --- | --- | --- |
| `Resources/**/*.nof` | The `.proto` schemas of the module and of its dependencies | `nobara/resources.pb`, a `nobara.formats.ResourceBundle` |
| Binary files referenced by resources | None | `nobara/files/<path>`, as they are |
| `module.nobara` | Built in | `nobara/module.pb`, a `nobara.formats.ModuleManifest` |
| `project.nobara` and `engine.nobara` | Built in | A `nobara.formats.DistributionManifest` |
| `build.nobara` | Built in | Nothing: read only |

The schemas of the module itself are embedded as `nobara/schemas.pb`, a
`FileDescriptorSet`. The names above are the manifest resources of the
assembly of the module.

## Compiled formats

The compiled formats are protobuf messages declared in
`Protos/nobara/formats/`:

| File | Messages |
| --- | --- |
| `generic.proto` | `GenericNode`, `Value`, `SourcePosition`: the generic representation of components, free properties, and module configurations. |
| `resources.proto` | `ResourceBundle`, `Resource`, `Override`, `Query`, `Operation`, `TagContribution`. |
| `manifest.proto` | `ModuleManifest`, `DistributionManifest`. |

These files are the single definition of the formats. A reader that must not
depend on build tools generates its own classes from the same files.
`Protos/nobara/options.proto` declares the [schema
options](../../docs/reference/formats/proto-options.md).

## Compiling resources

```csharp
var compiler = new ResourceCompiler(new ResourceCompilerOptions
{
    ModuleId = "example.blocks",
    ResourcesDirectory = "Resources",
    Schemas = File.ReadAllBytes("obj/nobara/schemas.pb"),
    Dependencies = [CompiledModule.FromAssembly("Example.Items.dll")!],
    Plugins = plugins,
    CacheFile = "obj/nobara/resources.cache",
});

CompilationResult result = compiler.Compile(Directory.EnumerateFiles("Resources", "*.nof", SearchOption.AllDirectories));
```

`CompilationResult` holds the diagnostics, the bundle when no error was
found, the binary files to embed, and the files that were parsed again.
`CompiledModule.FromAssembly` reads the manifest, the schemas, and the
resources of a module from its assembly without loading it.

The pipeline of a file is: parsing, conversion from an older schema version,
validation against the schema, identifiers, references, derived resources,
content hash, and serialization. References, duplicates, and tags are checked
across the files of the module and the resources of its dependencies.

### Incremental compilation

A compiler keeps the result of each file before linking, keyed by the content
of the file and of the binary files it references, in memory and in
`CacheFile`. A file is parsed again only when it, one of its binary files,
the schemas, the plugins, or the compiler changed. Linking runs on every
compilation, so the content hash of a resource changes when a resource it
references changes. A file with an error is parsed again on the next
compilation, so its errors are reported again.

### One file at a time

`ResourceCompiler.CompileFile(path)` parses one file again and links it with
the other files of the last `Compile` call and with the dependencies. It
returns the resources and the tag contributions of that file, with their
content hashes, and the diagnostics.

## Plugins

A module that defines resource types may provide a plugin: a `.Build` project
next to its project, which references the module and this library. A plugin
assembly declares its plugins with an assembly attribute:

```csharp
[assembly: ResourcePlugin(typeof(BlockPlugin))]

public sealed class BlockPlugin : ResourcePlugin
{
    public override IReadOnlyCollection<string> ResourceTypes => ["Block"];

    public override void Validate(ResourceContext context)
    {
        Block block = context.Parse(Block.Parser);
        if (block.Hardness == 0 && block.CreateItem)
        {
            context.Error("NBLK001", "an unbreakable block cannot drop its item");
        }
    }
}
```

| Member | Use |
| --- | --- |
| `ResourceTypes` | Elements of the types the plugin handles. |
| `Schemas` | Schemas given as descriptors, in addition to those embedded in the assemblies. |
| `Converters` | `SchemaConverter` instances, each from one version of a type to the next. |
| `Validate` | Rules that the options of the schema cannot express. |
| `Derive` | Resources generated from a resource, such as the item of a block, compiled like written ones. |
| `ProcessFile` | Changes a binary file referenced by a resource of the type before it is embedded. |

`PluginLoader` loads plugin assemblies from memory, in an
`AssemblyLoadContext` of their own that shares this library and
`Google.Protobuf` with the compiler, so the files stay free to be rebuilt.

## Manifests and distributions

`ModuleManifestReader`, `ProjectManifestReader`, `EngineManifestReader`, and
`BuildManifestReader` read and validate the four files; `ManifestCompiler`
compiles a module manifest and a distribution manifest. The module manifest
reader is also compiled into `Nobara.Sdk`, so the format has one definition.

`DistributionChecker.Check` takes the project, engine, and build files and
the manifests of every module, and checks the distribution as a whole, for
one target or for each side:

- every module entry has a manifest, and every required dependency is present
  and active, in its version range; an optional dependency that is present
  is in its range too;
- no dependency cycle, and no platform module that depends on a business
  module;
- no conflict and no disputed reservation between active modules;
- short names, configuration keys, and state identifiers are unique;
- each initial state is provided by an active module of its side;
- each base module has an active backend on the sides where it requires one;
- configuration values match their keys, and error policies are allowed
  (`stop` for required and platform modules, no `disable` or `log` module
  required by a `stop` module);
- error policies and module configurations name modules of the distribution,
  and a target does not disable a required module.

A client target ships the modules of both sides, because a client hosts an
integrated server, and is checked on both sides; a server target ships and
checks the server side. Without a target, each side is checked and every
module is active.

It returns the diagnostics, the modules that the target ships in load order,
and the required modules. `ManifestCompiler` then compiles the distribution
manifest from them, with the SHA-256 of each module assembly and of each file
of `lib/` that the caller computed; [the command-line
tool](../Nobara.Cli/README.md) does it when it builds a target.

## Diagnostics

Every diagnostic has a file, a line, and a column when they are known.

| Code | Meaning |
| --- | --- |
| `NRC001` | A source file cannot be read. |
| `NRC002` | The assembly of a dependency cannot be read. |
| `NRC100` | A manifest is not well-formed XML, has the wrong root, or an unsupported `format`. |
| `NRC101` | Unknown element or attribute in a manifest. |
| `NRC102` | Missing or empty element or attribute in a manifest. |
| `NRC103` | Duplicate element or entry in a manifest. |
| `NRC104` | Invalid identifier in a manifest: module, short name, state, key, namespace, or name. |
| `NRC105` | Invalid semantic version. |
| `NRC106` | Invalid version range. |
| `NRC107` | Invalid value: level, side, scope, type, policy, boolean, number, or platform. |
| `NRC108` | Invalid configuration key declaration. |
| `NRC109` | Inconsistent declaration: a dependency or conflict on itself, a side where the module does not run, or an invalid backend declaration. |
| `NRC110` | Warning: a reserved feature, such as build variants. |
| `NRC200` | A module entry without a manifest, or a module provided twice. |
| `NRC201` | A required dependency is missing or not active. |
| `NRC202` | A module or a dependency is outside its version range. |
| `NRC203` | A platform module depends on a business module. |
| `NRC204` | A dependency cycle. |
| `NRC205` | Two active modules conflict. |
| `NRC206` | Two active modules reserve the same resource. |
| `NRC207` | A short name is declared twice. |
| `NRC208` | A configuration key is declared twice. |
| `NRC209` | A state is declared twice. |
| `NRC210` | An initial state is not provided by an active module. |
| `NRC211` | A base module has no active backend on a side where it requires one. |
| `NRC212` | Unknown configuration key in `engine.nobara`. |
| `NRC213` | Invalid configuration value, or wrong element for the scope of the key. |
| `NRC214` | A required or platform module has a policy other than `stop`. |
| `NRC215` | A `disable` or `log` module is required by a `stop` module. |
| `NRC216` | An error policy or a module configuration names an absent module. |
| `NRC217` | Unknown target, or a target that disables an unknown or required module. |
| `NRC300` | A `.nof` file is not well-formed XML. |
| `NRC301` | Invalid `<Nobara>` root: element, `version`, or `namespace`. |
| `NRC302` | Unknown resource type. |
| `NRC303` | Invalid resource structure: unknown element or attribute, duplicate part. |
| `NRC304` | A resource without `<Name>` or `<Content>`. |
| `NRC305` | Invalid resource name. |
| `NRC306` | Unknown field, or a field written in a form its type does not allow. |
| `NRC307` | A field set twice, or two fields of one `oneof`. |
| `NRC308` | A required field is missing. |
| `NRC309` | A value of the wrong type. |
| `NRC310` | A value outside the bounds of the schema. |
| `NRC311` | An identifier, a component name, or a localization key of the wrong form. |
| `NRC312` | A binary file that is missing or whose path is invalid. |
| `NRC313` | A schema version that cannot be read. |
| `NRC314` | Invalid free property. |
| `NRC315` | Invalid tag, member, or `<Tags>` block, or a registry that does not accept tags. |
| `NRC400` | Duplicate object identifier. |
| `NRC401` | Unknown object identifier. |
| `NRC402` | Unknown resource identifier. |
| `NRC403` | Unknown tag. |
| `NRC404` | Tags that contain each other. |
| `NRC500` | Invalid override: query, operation, or field to merge. |
| `NRC501` | Warning: a query selects nothing in the target as defined. |
| `NRC600` | Invalid schemas: unreadable descriptors, misused options, a type defined twice. |
| `NRC601` | A plugin that cannot be loaded or created, or two converters for the same version. |
| `NRC602` | A plugin or a converter threw an exception. |

Plugins report their own codes.

## Building and testing

```sh
dotnet build Nobara.slnx
dotnet test tools/Nobara.ResourceCompiler.Tests
```

The tests use schemas compiled in the test project, without plugin code, and
a test plugin that provides a type of its own. The build through MSBuild is
tested in `tools/Nobara.Sdk.Tests`.

## Known limitations

- Plugins of dependencies restored as packages are not found; only the
  `.Build` project next to a local dependency is.
- A schema cannot import the schema of another module.
- Overrides and tags are compiled and checked, not applied: applying them is
  the job of the code that loads the resources.
- The hashes of module assemblies and libraries are not computed here;
  `ManifestCompiler` records the hashes it is given.
- Localization keys are checked for their form only.
