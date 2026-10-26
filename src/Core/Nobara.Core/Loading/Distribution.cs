// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Reflection;
using Google.Protobuf;
using F = Nobara.Formats;

namespace Nobara.Core.Loading;

/// <summary>The identity of the game that a distribution describes.</summary>
public sealed class DistributionInfo
{
    internal DistributionInfo(string id, string name, string version)
    {
        Id = id;
        Name = name;
        Version = version;
    }

    /// <summary>The name of the manifest resource of the entry assembly that holds the compiled distribution manifest.</summary>
    public const string ManifestResource = "nobara/distribution.pb";

    /// <summary>Gets the identifier of the game.</summary>
    public string Id { get; }

    /// <summary>Gets the display name of the game, which also names its folder of user data.</summary>
    public string Name { get; }

    /// <summary>Gets the version of the game.</summary>
    public string Version { get; }
}

/// <summary>The compiled distribution manifest, read once per process.</summary>
internal sealed class Distribution
{
    private const uint SupportedFormat = 1;

    private readonly Dictionary<string, F.Policy> _policies = new(StringComparer.Ordinal);

    private Distribution(F.DistributionManifest manifest)
    {
        Manifest = manifest;
        Info = new DistributionInfo(manifest.Id, manifest.Name, manifest.Version);
        foreach (F.ModulePolicy policy in manifest.ErrorPolicy?.Modules ?? [])
        {
            _policies[policy.Module] = policy.Policy;
        }
    }

    public F.DistributionManifest Manifest { get; }

    public DistributionInfo Info { get; }

    /// <exception cref="ModuleCompositionException">The bytes are not a supported distribution manifest.</exception>
    public static Distribution Read(ReadOnlyMemory<byte> bytes)
    {
        F.DistributionManifest manifest;
        try
        {
            manifest = F.DistributionManifest.Parser.ParseFrom(bytes.Span);
        }
        catch (InvalidProtocolBufferException exception)
        {
            throw Invalid($"The distribution manifest cannot be read: {exception.Message}");
        }

        if (manifest.Format != SupportedFormat)
        {
            throw Invalid($"The distribution manifest has format {manifest.Format}; this core reads format {SupportedFormat}.");
        }

        return new Distribution(manifest);
    }

    /// <exception cref="ModuleCompositionException">The assembly holds no distribution manifest.</exception>
    public static Distribution Read(Assembly entryAssembly)
    {
        using Stream? stream = entryAssembly.GetManifestResourceStream(DistributionInfo.ManifestResource);
        if (stream is null)
        {
            throw Invalid($"The entry assembly '{entryAssembly.GetName().Name}' holds no distribution manifest ({DistributionInfo.ManifestResource}).");
        }

        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return Read(copy.ToArray());
    }

    public ModuleInfo CreateInfo(F.ModuleManifest manifest, bool required)
    {
        ModuleLevel level = manifest.Level == F.Level.Platform ? ModuleLevel.Platform : ModuleLevel.Business;

        // Required and platform modules always stop the game.
        ErrorPolicy policy = required || level == ModuleLevel.Platform
            ? ErrorPolicy.Stop
            : Policy(_policies.TryGetValue(manifest.Id, out F.Policy chosen) ? chosen : Manifest.ErrorPolicy?.DefaultPolicy ?? F.Policy.Unspecified);

        return new ModuleInfo(
            manifest.Id,
            manifest.ShortName,
            manifest.Version,
            level,
            Sides(manifest.Side),
            manifest.Dependencies.Select(d => new ModuleDependency(d.Id, d.Range, d.Optional)).ToArray(),
            manifest.Conflicts.ToArray(),
            manifest.Reserves.ToArray(),
            manifest.States.Select(s => new StateDeclaration(s.Id, s.Side == F.Side.Unspecified ? Sides(manifest.Side) : Sides(s.Side))).ToArray(),
            Sides(manifest.BackendRequired),
            manifest.BackendOf,
            required,
            policy);
    }

    private static ErrorPolicy Policy(F.Policy policy) => policy switch
    {
        F.Policy.Disable => ErrorPolicy.Disable,
        F.Policy.Log => ErrorPolicy.Log,
        _ => ErrorPolicy.Stop,
    };

    private static Side Sides(F.Side side) => side switch
    {
        F.Side.Client => Side.Client,
        F.Side.Server => Side.Server,
        F.Side.Both => Side.Both,
        _ => Side.None,
    };

    private static ModuleCompositionException Invalid(string message) =>
        new([new CompositionProblem(CompositionProblemKind.InvalidDistribution, string.Empty, message)]);
}
