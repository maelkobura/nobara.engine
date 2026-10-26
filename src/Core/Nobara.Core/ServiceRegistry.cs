// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core.Loading;

namespace Nobara.Core;

/// <summary>The state of one module in one context.</summary>
internal sealed class ModuleRuntime
{
    public ModuleRuntime(ModuleInfo info, LoadedModule loaded, int order)
    {
        Info = info;
        Loaded = loaded;
        Order = order;
    }

    public ModuleInfo Info { get; }

    public LoadedModule Loaded { get; }

    /// <summary>The position of the module in the load order of its context.</summary>
    public int Order { get; }

    public IModule? Instance { get; set; }

    public ModuleContext Context { get; set; } = null!;

    public bool Registered { get; set; }

    public bool InitializeCalled { get; set; }

    public bool Started { get; set; }

    public bool Released { get; set; }

    public bool Disabled { get; set; }

    public List<Action<EmergencyStop>> EmergencyHooks { get; } = [];

    public bool DependsOn(string moduleId, bool requiredOnly) =>
        Info.Dependencies.Any(d => string.Equals(d.ModuleId, moduleId, StringComparison.Ordinal) && (!requiredOnly || !d.Optional));
}

/// <summary>
/// The services published in one context. The registry knows which module
/// published each service, and lets a module resolve only its own services
/// and those of its declared dependencies.
/// </summary>
internal sealed class ServiceRegistry
{
    private readonly Dictionary<Type, (ModuleRuntime Owner, object Instance)> _services = [];
    private readonly Func<Type, string?> _moduleOfType;

    /// <param name="moduleOfType">The identifier of the module whose assembly defines a type, or <see langword="null"/>.</param>
    public ServiceRegistry(Func<Type, string?> moduleOfType)
    {
        _moduleOfType = moduleOfType;
    }

    public void Publish(ModuleRuntime owner, Type type, object instance)
    {
        if (_services.TryGetValue(type, out var existing))
        {
            throw new InvalidOperationException(
                $"Module '{owner.Info.Id}' publishes service '{type.FullName}', which module '{existing.Owner.Info.Id}' already publishes.");
        }

        _services.Add(type, (owner, instance));
    }

    /// <returns>The instance, or <see langword="null"/> when no active module publishes it.</returns>
    /// <exception cref="ServiceResolutionException">The publisher is not a declared dependency of <paramref name="requester"/>.</exception>
    public object? Resolve(ModuleRuntime requester, Type type)
    {
        if (_services.TryGetValue(type, out var entry))
        {
            CheckAccess(requester, type, entry.Owner.Info.Id);
            return entry.Instance;
        }

        // The service of an inactive optional dependency is absent; the
        // service of an undeclared module is refused even when absent.
        string? definer = _moduleOfType(type);
        if (definer is not null)
        {
            CheckAccess(requester, type, definer);
        }

        return null;
    }

    /// <summary>Removes the services of a disabled module.</summary>
    public void Withdraw(ModuleRuntime owner)
    {
        foreach (Type type in _services.Where(s => s.Value.Owner == owner).Select(s => s.Key).ToList())
        {
            _services.Remove(type);
        }
    }

    private static void CheckAccess(ModuleRuntime requester, Type type, string ownerId)
    {
        if (!string.Equals(ownerId, requester.Info.Id, StringComparison.Ordinal) && !requester.DependsOn(ownerId, requiredOnly: false))
        {
            throw new ServiceResolutionException(
                type,
                ServiceFailure.UndeclaredDependency,
                $"Module '{requester.Info.Id}' asked for service '{type.FullName}' of module '{ownerId}', which is not one of its declared dependencies.");
        }
    }
}
