// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Google.Protobuf;
using Nobara.ResourceCompiler.Formats;
using F = Nobara.Formats;

namespace Nobara.ResourceCompiler.Manifests;

/// <summary>Turns manifests into their compiled form.</summary>
public static class ManifestCompiler
{
    /// <summary>The version of the source format that this compiler reads and records.</summary>
    public const uint Format = 1;

    /// <summary>Compiles a module manifest.</summary>
    /// <param name="manifest">A manifest returned by <see cref="ModuleManifestReader.Read"/>.</param>
    /// <returns>The compiled manifest, embedded as <c>nobara/module.pb</c>.</returns>
    public static F.ModuleManifest Compile(ModuleManifest manifest)
    {
        var result = new F.ModuleManifest
        {
            Format = Format,
            Id = manifest.Id,
            ShortName = manifest.ShortName ?? string.Empty,
            Version = manifest.Version.Text,
            Level = manifest.Level == ModuleLevel.Platform ? F.Level.Platform : F.Level.Business,
            Side = Side(manifest.Side),
            BackendRequired = Side(manifest.BackendRequired),
            BackendOf = manifest.BackendOf ?? string.Empty,
        };
        result.Dependencies.AddRange(manifest.Dependencies.Select(d => new F.Dependency { Id = d.Id, Range = d.Range.Text, Optional = d.Optional }));
        result.Libraries.AddRange(manifest.Libraries.Select(l => new F.Library { Id = l.Id, Version = l.Version }));
        result.Conflicts.AddRange(manifest.Conflicts.Select(c => c.Name));
        result.Reserves.AddRange(manifest.Reserves.Select(r => r.Name));
        result.States.AddRange(manifest.States.Select(s => new F.StateDeclaration { Id = s.Id, Side = Side(s.Side) }));
        foreach (SettingDeclaration setting in manifest.Settings)
        {
            var compiled = new F.SettingDeclaration
            {
                Key = setting.Key,
                Scope = setting.Scope == SettingScope.User ? F.SettingDeclaration.Types.Scope.User : F.SettingDeclaration.Types.Scope.Distribution,
                Type = setting.Type switch
                {
                    SettingType.Bool => F.SettingDeclaration.Types.Type.Bool,
                    SettingType.Int => F.SettingDeclaration.Types.Type.Int,
                    SettingType.Float => F.SettingDeclaration.Types.Type.Float,
                    SettingType.String => F.SettingDeclaration.Types.Type.String,
                    _ => F.SettingDeclaration.Types.Type.Enum,
                },
                DefaultValue = setting.Default,
                Side = Side(setting.Side),
            };
            if (setting.Min is double min)
            {
                compiled.Min = min;
            }

            if (setting.Max is double max)
            {
                compiled.Max = max;
            }

            compiled.Values.AddRange(setting.Values);
            result.Settings.Add(compiled);
        }

        return result;
    }

    /// <summary>Compiles the manifest of a distribution from its project file and its engine file.</summary>
    /// <param name="project">The content of <c>project.nobara</c>.</param>
    /// <param name="engine">The content of <c>engine.nobara</c>.</param>
    /// <param name="modules">The modules of the distribution, in load order, as returned by the distribution checks.</param>
    /// <param name="requiredModules">The identifiers of the required modules.</param>
    /// <param name="hashes">The SHA-256 of the assembly of each module, by identifier, or <see langword="null"/>.</param>
    /// <param name="relativeTo">The folder the source positions of module configurations are relative to.</param>
    /// <param name="libraries">
    /// The SHA-256 of each file of <c>lib/</c>, by path relative to the folder
    /// of the installed game with forward slashes (<c>lib/Example.Library.dll</c>),
    /// or <see langword="null"/>.
    /// </param>
    /// <returns>The compiled distribution manifest.</returns>
    public static F.DistributionManifest Compile(
        ProjectManifest project,
        EngineManifest engine,
        IReadOnlyList<ModuleManifest> modules,
        IReadOnlySet<string> requiredModules,
        IReadOnlyDictionary<string, byte[]>? hashes,
        string relativeTo,
        IReadOnlyDictionary<string, byte[]>? libraries = null)
    {
        var result = new F.DistributionManifest
        {
            Format = Format,
            Id = project.Id,
            Name = project.Name,
            Version = project.Version.Text,
            Icon = project.Icon ?? string.Empty,
            ModdingEnabled = project.ModdingEnabled,
            States = new F.InitialStates { Client = engine.ClientState?.Name ?? string.Empty, Server = engine.ServerState?.Name ?? string.Empty },
            ErrorPolicy = new F.ErrorPolicy { DefaultPolicy = Policy(engine.DefaultPolicy) },
        };

        foreach (ModuleManifest module in modules)
        {
            var entry = new F.DistributionModule { Id = module.Id, Version = module.Version.Text, Required = requiredModules.Contains(module.Id) };
            if (hashes is not null && hashes.TryGetValue(module.Id, out byte[]? hash))
            {
                entry.Hash = ByteString.CopyFrom(hash);
            }

            result.Modules.Add(entry);
        }

        foreach ((string path, byte[] hash) in (libraries ?? new Dictionary<string, byte[]>()).OrderBy(l => l.Key, StringComparer.Ordinal))
        {
            result.Libraries.Add(new F.LibraryFile { Path = path, Hash = ByteString.CopyFrom(hash) });
        }

        result.ProtectedNamespaces.AddRange(project.ProtectedNamespaces.Select(n => n.Name));
        if (engine.TickRate is int rate)
        {
            result.Loop = new F.Loop { TickRate = (uint)rate };
            result.Loop.Phases.AddRange(engine.Phases.Select(p => new F.Phase { Name = p.Name, Fixed = p.Fixed, Side = Side(p.Side) }));
        }

        result.ErrorPolicy.Modules.AddRange(engine.Policies.Select(p => new F.ModulePolicy { Module = p.Module, Policy = Policy(p.Policy) }));
        result.Settings.AddRange(engine.Settings.Select(s => new F.SettingValue
        {
            Key = s.Key,
            Value = s.Value,
            Operation = s.Operation switch
            {
                SettingOperation.Default => F.SettingValue.Types.Operation.Default,
                SettingOperation.Lock => F.SettingValue.Types.Operation.Lock,
                _ => F.SettingValue.Types.Operation.Set,
            },
        }));

        string file = Path.GetRelativePath(relativeTo, engine.File).Replace('\\', '/');
        result.ModuleConfigs.AddRange(engine.ModuleConfigs.Select(c => new F.ModuleConfig { Module = c.Module, Config = GenericTree.FromElement(c.Element, file) }));
        return result;
    }

    private static F.Side Side(ModuleSide side) => side switch
    {
        ModuleSide.Client => F.Side.Client,
        ModuleSide.Server => F.Side.Server,
        ModuleSide.Both => F.Side.Both,
        _ => F.Side.Unspecified,
    };

    private static F.Policy Policy(ErrorPolicyKind policy) => policy switch
    {
        ErrorPolicyKind.Stop => F.Policy.Stop,
        ErrorPolicyKind.Disable => F.Policy.Disable,
        _ => F.Policy.Log,
    };
}
