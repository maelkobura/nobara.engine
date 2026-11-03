// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.InteropServices;
using Nobara.Core.Resources;

namespace Nobara.Entity;

/// <summary>
/// Converts the free properties of an entity definition into components of
/// the module that registers it, when the definition is loaded.
/// </summary>
/// <param name="conversion">The definition, its resource, and its components so far.</param>
/// <remarks>
/// A converter runs once per definition and per context, when the definitions
/// are loaded and when they are reloaded; it may allocate. It reads the free
/// properties its module declared (<c>context.Resources.DeclareProperty</c>),
/// which the core already checked, and adds or fills components of its module.
/// </remarks>
public delegate void EntityConverter(EntityConversion conversion);

/// <summary>
/// An entity definition while it is loaded: what a <see cref="EntityConverter"/>
/// reads, and the components it adds or fills. Valid during the call of the
/// converter only.
/// </summary>
/// <remarks>
/// <para>
/// The components of the file come first, then the converters run in the
/// load order of their modules, and in the order they were added within a
/// module: a converter sees the components that the converters of its
/// dependencies set.
/// </para>
/// <para>
/// A converter reads every component of the definition, and sets those of its
/// own module only. When it throws, what it set is discarded, and the
/// exception follows the error policy of its module.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// _weight = context.Resources.DeclareProperty("Weight", PropertyType.Float);   // in Register
///
/// worlds.AddConverter(context, conversion =>                                    // in Initialize
/// {
///     if (_weight.TryGetFloat(conversion.Resource, out double weight))
///     {
///         conversion.Set(_body, new Body { Mass = (float)weight });
///     }
/// });
/// </code>
/// </example>
public sealed class EntityConversion
{
    private readonly ComponentRegistry _registry;
    private readonly List<(int Index, byte[] Value)> _components;
    private readonly List<(int Index, byte[] Value)> _pending = [];
    private string _moduleId = string.Empty;
    private bool _open;

    internal EntityConversion(ComponentRegistry registry, ResourceData resource, List<(int Index, byte[] Value)> components)
    {
        _registry = registry;
        Resource = resource;
        _components = components;
    }

    /// <summary>Gets the object identifier of the definition: <c>entity:example.animals.pig</c>.</summary>
    public string DefinitionId => Resource.ObjectId;

    /// <summary>Gets the resource of the definition, whose free properties the converter reads.</summary>
    public ResourceData Resource { get; }

    /// <summary>Gets the components of the definition so far, with those the converter set.</summary>
    public IReadOnlyList<ComponentType> Components
    {
        get
        {
            RequireOpen();
            return _components.Select(c => c.Index).Concat(_pending.Select(c => c.Index)).Distinct().Select(i => new ComponentType(i)).ToArray();
        }
    }

    /// <summary>Gets the problems reported with <see cref="Error"/>.</summary>
    internal List<string> Errors { get; } = [];

    /// <summary>Tells whether the definition has a component so far.</summary>
    /// <param name="component">The component.</param>
    /// <returns><see langword="true"/> when it has it.</returns>
    public bool Has(ComponentType component)
    {
        RequireOpen();
        return Find(component.Index) is not null;
    }

    /// <summary>Gets the initial value of a component of the definition.</summary>
    /// <typeparam name="T">The type of the component.</typeparam>
    /// <param name="component">The component.</param>
    /// <param name="value">The value, or the default value of the type.</param>
    /// <returns><see langword="true"/> when the definition has the component.</returns>
    /// <exception cref="ArgumentException">The handle comes from another context.</exception>
    public bool TryGet<T>(ComponentType<T> component, out T value)
        where T : unmanaged
    {
        RequireOpen();
        _registry.Require(component);
        byte[]? bytes = Find(component.Untyped.Index);
        value = bytes is null ? default : MemoryMarshal.Read<T>(bytes);
        return bytes is not null;
    }

    /// <summary>Adds a component of the module of the converter, or replaces its value.</summary>
    /// <typeparam name="T">The type of the component.</typeparam>
    /// <param name="component">The component; registered by the module of the converter.</param>
    /// <param name="value">The initial value.</param>
    /// <exception cref="ArgumentException">The component belongs to another module, or the handle comes from another context.</exception>
    public void Set<T>(ComponentType<T> component, in T value)
        where T : unmanaged
    {
        RequireOpen();
        _registry.Require(component);
        ComponentInfo info = _registry.Info(component);
        if (info.ModuleId != _moduleId)
        {
            throw new ArgumentException(
                $"The converter of module '{_moduleId}' sets component '{info.Name}' of module '{info.ModuleId}': a converter sets the components of its own module.",
                nameof(component));
        }

        byte[] bytes = new byte[info.Size];
        MemoryMarshal.Write(bytes, in value);
        _pending.RemoveAll(c => c.Index == component.Untyped.Index);
        _pending.Add((component.Untyped.Index, bytes));
    }

    /// <summary>Reports a free property that breaks a rule of the module: the definition fails to load.</summary>
    /// <param name="message">What is wrong, such as <c>Weight is -1, expected at least 0.</c></param>
    public void Error(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        RequireOpen();
        Errors.Add(message);
    }

    /// <summary>Opens the conversion for the converter of a module.</summary>
    internal void Open(string moduleId)
    {
        _moduleId = moduleId;
        _pending.Clear();
        _open = true;
    }

    /// <summary>Closes the conversion; keeps what the converter set when it returned normally.</summary>
    internal void Close(bool commit)
    {
        _open = false;
        if (!commit)
        {
            return;
        }

        foreach ((int index, byte[] value) in _pending)
        {
            _components.RemoveAll(c => c.Index == index);
            _components.Add((index, value));
        }
    }

    private byte[]? Find(int index)
    {
        foreach ((int pending, byte[] value) in _pending)
        {
            if (pending == index)
            {
                return value;
            }
        }

        foreach ((int existing, byte[] value) in _components)
        {
            if (existing == index)
            {
                return value;
            }
        }

        return null;
    }

    private void RequireOpen()
    {
        if (!_open)
        {
            throw new InvalidOperationException("The conversion of an entity definition is used only while its converter runs.");
        }
    }
}

/// <summary>A converter added to the entity module. Disposing it removes the converter for the definitions loaded later.</summary>
public sealed class EntityConverterHandle : IDisposable
{
    internal EntityConverterHandle(string moduleId, int order, EntityConverter converter)
    {
        ModuleId = moduleId;
        Order = order;
        Converter = converter;
    }

    /// <summary>Gets the module that added the converter.</summary>
    public string ModuleId { get; }

    /// <summary>Gets whether the handle was disposed.</summary>
    public bool IsRevoked { get; private set; }

    internal int Order { get; }

    internal EntityConverter Converter { get; }

    /// <summary>Removes the converter. Definitions loaded already keep their components.</summary>
    public void Dispose() => IsRevoked = true;
}
