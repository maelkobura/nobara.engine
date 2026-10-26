// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Nobara.Formats;
using Nobara.ResourceCompiler.Caching;
using Nobara.ResourceCompiler.Nof;
using Nobara.ResourceCompiler.Plugins;

namespace Nobara.ResourceCompiler;

/// <summary>What a <see cref="ResourceCompiler"/> needs to compile the resources of one module.</summary>
public sealed class ResourceCompilerOptions
{
    /// <summary>Gets the identifier of the module.</summary>
    public required string ModuleId { get; init; }

    /// <summary>Gets the folder of the resources (<c>Resources/</c>). Paths in the compiled data are relative to it.</summary>
    public required string ResourcesDirectory { get; init; }

    /// <summary>Gets the serialized schemas of the module itself, a <c>FileDescriptorSet</c>, or <see langword="null"/>.</summary>
    public byte[]? Schemas { get; init; }

    /// <summary>Gets the modules this module depends on, with their schemas and resources.</summary>
    public IReadOnlyList<CompiledModule> Dependencies { get; init; } = [];

    /// <summary>Gets the plugins of the dependencies.</summary>
    public IReadOnlyList<ResourcePlugin> Plugins { get; init; } = [];

    /// <summary>
    /// Gets the file of the incremental cache, or <see langword="null"/> to
    /// keep the cache in memory only, for the life of the compiler.
    /// </summary>
    public string? CacheFile { get; init; }
}

/// <summary>A binary file to embed in the assembly of the module.</summary>
/// <param name="Path">The path relative to the resources folder; the manifest resource is <c>nobara/files/</c> followed by it.</param>
/// <param name="SourcePath">The full path of the file on disk.</param>
/// <param name="Content">The bytes to embed when a plugin changed them, or <see langword="null"/> to embed the file as it is.</param>
public sealed record EmbeddedFile(string Path, string SourcePath, byte[]? Content);

/// <summary>The outcome of <see cref="ResourceCompiler.Compile"/>.</summary>
public sealed class CompilationResult
{
    internal CompilationResult(IReadOnlyList<Diagnostic> diagnostics, ResourceBundle? bundle, IReadOnlyList<EmbeddedFile> files, IReadOnlyList<string> compiled, IReadOnlyList<string> reused)
    {
        Diagnostics = diagnostics;
        Bundle = bundle;
        Files = files;
        CompiledFiles = compiled;
        ReusedFiles = reused;
    }

    /// <summary>Gets whether no error was reported.</summary>
    public bool Success => Bundle is not null;

    /// <summary>Gets the problems found, in order.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>Gets the compiled resources, or <see langword="null"/> when an error was reported.</summary>
    public ResourceBundle? Bundle { get; }

    /// <summary>Gets the binary files to embed.</summary>
    public IReadOnlyList<EmbeddedFile> Files { get; }

    /// <summary>Gets the <c>.nof</c> files that were parsed again, relative to the resources folder.</summary>
    public IReadOnlyList<string> CompiledFiles { get; }

    /// <summary>Gets the <c>.nof</c> files whose previous result was reused, relative to the resources folder.</summary>
    public IReadOnlyList<string> ReusedFiles { get; }
}

/// <summary>The outcome of <see cref="ResourceCompiler.CompileFile"/>.</summary>
public sealed class FileCompilationResult
{
    internal FileCompilationResult(IReadOnlyList<Diagnostic> diagnostics, IReadOnlyList<Resource> resources, IReadOnlyList<TagContribution> tags)
    {
        Diagnostics = diagnostics;
        Resources = resources;
        Tags = tags;
    }

    /// <summary>Gets whether no error was reported.</summary>
    public bool Success => Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error);

    /// <summary>Gets the problems found.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>Gets the resources defined by the file, with their content hashes; empty when an error was reported.</summary>
    public IReadOnlyList<Resource> Resources { get; }

    /// <summary>Gets the tag contributions of the file; empty when an error was reported.</summary>
    public IReadOnlyList<TagContribution> Tags { get; }
}

/// <summary>
/// Compiles the <c>.nof</c> files of one module into a resource bundle.
/// Files that did not change since the previous compilation, with the same
/// schemas and plugins, are not parsed again.
/// </summary>
/// <remarks>
/// An instance belongs to one module and is not thread-safe. The MSBuild task
/// creates one per build; the engine keeps one for hot reload.
/// </remarks>
public sealed class ResourceCompiler
{
    private const string CacheFormatVersion = "1";

    private readonly ResourceCompilerOptions _options;
    private readonly Dictionary<string, FileUnit> _units = new(StringComparer.Ordinal);
    private readonly List<Diagnostic> _setupDiagnostics;
    private readonly SchemaSet _schemas;
    private readonly PluginSet _plugins;
    private readonly byte[] _fingerprint;
    private readonly string _resources;

    /// <summary>Creates a compiler and loads the schemas and the cache.</summary>
    /// <param name="options">The module, its schemas, its dependencies, and its plugins.</param>
    public ResourceCompiler(ResourceCompilerOptions options)
    {
        _options = options;
        _resources = Path.GetFullPath(options.ResourcesDirectory);
        var diagnostics = new DiagnosticBag();
        _plugins = new PluginSet(options.Plugins, diagnostics);

        var sources = new List<SchemaSource>();
        if (options.Schemas is not null)
        {
            sources.Add(new SchemaSource(options.ModuleId, options.Schemas));
        }

        sources.AddRange(options.Dependencies.Where(d => d.Schemas is not null).Select(d => new SchemaSource(d.Id, d.Schemas!)));
        _schemas = SchemaSet.Build(sources, options.Plugins.SelectMany(p => p.Schemas.Select(s => (Owner: p.GetType().Assembly.GetName().Name ?? "plugin", File: s))), diagnostics);
        _setupDiagnostics = diagnostics.Items.ToList();
        _fingerprint = Fingerprint(options, _schemas);
        LoadCache();
    }

    /// <summary>Gets the schemas known to the compiler: the module's own and those of its dependencies.</summary>
    public SchemaSet Schemas => _schemas;

    /// <summary>Compiles the resources of the module.</summary>
    /// <param name="files">The full paths of every <c>.nof</c> file of the module.</param>
    /// <returns>The bundle, the files to embed, and the diagnostics.</returns>
    public CompilationResult Compile(IEnumerable<string> files)
    {
        var diagnostics = new DiagnosticBag();
        diagnostics.AddRange(_setupDiagnostics);
        var compiled = new List<string>();
        var reused = new List<string>();
        var units = new List<FileUnit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool unitFailed = false;

        foreach (string file in files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
        {
            string relative = Relative(file);
            seen.Add(relative);
            FileUnit? unit = Load(file, relative, diagnostics, out bool wasReused);
            if (unit is null)
            {
                unitFailed = true;
                continue;
            }

            (wasReused ? reused : compiled).Add(relative);
            units.Add(unit);
        }

        foreach (string stale in _units.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _units.Remove(stale);
        }

        if (diagnostics.HasErrors || unitFailed)
        {
            SaveCache();
            return new CompilationResult(diagnostics.Items.ToList(), null, [], compiled, reused);
        }

        (ResourceBundle bundle, List<EmbeddedFile> embedded) = LinkAll(units, diagnostics);
        SaveCache();
        return diagnostics.HasErrors
            ? new CompilationResult(diagnostics.Items.ToList(), null, [], compiled, reused)
            : new CompilationResult(diagnostics.Items.ToList(), bundle, embedded, compiled, reused);
    }

    /// <summary>
    /// Compiles one file again, for hot reload, and links it with the other
    /// files of the last <see cref="Compile"/> and with the dependencies.
    /// </summary>
    /// <param name="file">The full path of a <c>.nof</c> file of the module.</param>
    /// <returns>The resources and tag contributions of that file, and the diagnostics of the whole link.</returns>
    public FileCompilationResult CompileFile(string file)
    {
        var diagnostics = new DiagnosticBag();
        diagnostics.AddRange(_setupDiagnostics);
        string full = Path.GetFullPath(file);
        string relative = Relative(full);
        _units.Remove(relative);
        FileUnit? unit = Load(full, relative, diagnostics, out _);
        if (unit is null || diagnostics.HasErrors)
        {
            return new FileCompilationResult(diagnostics.Items.ToList(), [], []);
        }

        (ResourceBundle bundle, _) = LinkAll(_units.Values.ToList(), diagnostics);
        if (diagnostics.HasErrors)
        {
            return new FileCompilationResult(diagnostics.Items.ToList(), [], []);
        }

        var resources = bundle.Resources.Where(r => r.Source?.File == relative).ToList();
        return new FileCompilationResult(diagnostics.Items.ToList(), resources, unit.Tags.ToList());
    }

    private FileUnit? Load(string file, string relative, DiagnosticBag diagnostics, out bool reused)
    {
        reused = false;
        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Error("NRC001", $"the file cannot be read: {exception.Message}", new Location(file, 0, 0));
            _units.Remove(relative);
            return null;
        }

        byte[] textHash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        if (_units.TryGetValue(relative, out FileUnit? cached) && cached.TextHash.Span.SequenceEqual(textHash) && FilesUnchanged(cached))
        {
            reused = true;
            foreach (CachedDiagnostic warning in cached.Warnings)
            {
                diagnostics.Warning(warning.Code, warning.Message, new Location(file, (int)(warning.Source?.Line ?? 0), (int)(warning.Source?.Column ?? 0)));
            }

            return cached;
        }

        var local = new DiagnosticBag();
        FileUnit unit = new NofParser(_schemas, _plugins, _resources).Parse(file, relative, text, local);
        diagnostics.AddRange(local.Items);
        if (local.HasErrors)
        {
            _units.Remove(relative);
            return null;
        }

        unit.TextHash = ByteString.CopyFrom(textHash);
        foreach (FileInput input in unit.Files)
        {
            input.Hash = ByteString.CopyFrom(HashFile(input.Path));
        }

        foreach (Diagnostic warning in local.Items.Where(d => d.Severity == DiagnosticSeverity.Warning))
        {
            unit.Warnings.Add(new CachedDiagnostic { Code = warning.Code, Message = warning.Message, Source = new SourcePosition { File = relative, Line = (uint)warning.Line, Column = (uint)warning.Column } });
        }

        _units[relative] = unit;
        return unit;
    }

    private (ResourceBundle Bundle, List<EmbeddedFile> Files) LinkAll(List<FileUnit> units, DiagnosticBag diagnostics)
    {
        var fileHashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (FileInput input in units.SelectMany(u => u.Files))
        {
            fileHashes.TryAdd(input.Path, input.Hash.ToByteArray());
        }

        var linker = new Linker(_options.ModuleId, _resources, _schemas, diagnostics);
        ResourceBundle bundle = linker.Link(units, _options.Dependencies, fileHashes);
        var embedded = new List<EmbeddedFile>();
        foreach (string path in bundle.Files)
        {
            string source = Path.Combine(_resources, path.Replace('/', Path.DirectorySeparatorChar));
            Resource? owner = bundle.Resources.FirstOrDefault(r => r.References.Contains("file:" + path));
            IReadOnlyList<ResourcePlugin> plugins = owner is null ? [] : _plugins.For(owner.Element);
            if (plugins.Count == 0)
            {
                embedded.Add(new EmbeddedFile(path, source, null));
                continue;
            }

            byte[] content = File.ReadAllBytes(source);
            foreach (ResourcePlugin plugin in plugins)
            {
                var context = new FileContext(path, content, owner!.ObjectId, (code, message, isError) =>
                {
                    if (isError)
                    {
                        diagnostics.Error(code, message, new Location(source, 0, 0));
                    }
                    else
                    {
                        diagnostics.Warning(code, message, new Location(source, 0, 0));
                    }
                });
                try
                {
                    content = plugin.ProcessFile(context);
                }
                catch (Exception exception)
                {
                    diagnostics.Error("NRC602", $"plugin {plugin.GetType().FullName} failed on the file '{path}': {exception.Message}", new Location(source, 0, 0));
                }
            }

            embedded.Add(new EmbeddedFile(path, source, content));
        }

        return (bundle, embedded);
    }

    private bool FilesUnchanged(FileUnit unit) =>
        unit.Files.All(f => File.Exists(Path.Combine(_resources, f.Path)) && f.Hash.Span.SequenceEqual(HashFile(f.Path)));

    private byte[] HashFile(string path)
    {
        using FileStream stream = File.OpenRead(Path.Combine(_resources, path.Replace('/', Path.DirectorySeparatorChar)));
        return SHA256.HashData(stream);
    }

    private string Relative(string file) => Path.GetRelativePath(_resources, file).Replace('\\', '/');

    private void LoadCache()
    {
        if (_options.CacheFile is null || !File.Exists(_options.CacheFile))
        {
            return;
        }

        try
        {
            CompilationCache cache = CompilationCache.Parser.ParseFrom(File.ReadAllBytes(_options.CacheFile));
            if (cache.Fingerprint.Span.SequenceEqual(_fingerprint))
            {
                foreach (FileUnit unit in cache.Files)
                {
                    _units[unit.Path] = unit;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidProtocolBufferException)
        {
            // An unreadable cache is discarded: every file is compiled again.
        }
    }

    private void SaveCache()
    {
        if (_options.CacheFile is null)
        {
            return;
        }

        var cache = new CompilationCache { Format = 1, Fingerprint = ByteString.CopyFrom(_fingerprint) };
        cache.Files.AddRange(_units.Values.OrderBy(u => u.Path, StringComparer.Ordinal));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_options.CacheFile))!);
        File.WriteAllBytes(_options.CacheFile, cache.ToByteArray());
    }

    private static byte[] Fingerprint(ResourceCompilerOptions options, SchemaSet schemas)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string text) => hash.AppendData(Encoding.UTF8.GetBytes(text + "\n"));
        Add(CacheFormatVersion);
        Add(typeof(ResourceCompiler).Assembly.ManifestModule.ModuleVersionId.ToString());
        Add(options.ModuleId);
        Add(Path.GetFullPath(options.ResourcesDirectory));
        hash.AppendData(schemas.Fingerprint);
        foreach (ResourcePlugin plugin in options.Plugins)
        {
            Assembly assembly = plugin.GetType().Assembly;
            Add(plugin.GetType().AssemblyQualifiedName ?? plugin.GetType().FullName ?? string.Empty);
            Add(assembly.ManifestModule.ModuleVersionId.ToString());
        }

        return hash.GetHashAndReset();
    }
}
