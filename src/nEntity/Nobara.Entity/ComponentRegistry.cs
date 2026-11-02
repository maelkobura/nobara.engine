// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nobara.Core;
using Nobara.Core.Jobs;

namespace Nobara.Entity;

/// <summary>
/// The component types of one context, shared by all its worlds. Components
/// are registered on the loop thread, at any time; worker threads only read
/// the arrays, which are replaced, never changed in place, when they grow.
/// </summary>
internal sealed unsafe class ComponentRegistry : IDisposable
{
    private readonly ModuleJobs _jobs;
    private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, int> _byType = [];
    private ComponentInfo[] _infos = new ComponentInfo[32];
    private Entry[] _entries = new Entry[32];
    private int _count;
    private bool _disposed;

    public ComponentRegistry(ModuleJobs jobs)
    {
        _jobs = jobs;
    }

    public int Count => _count;

    public IReadOnlyList<ComponentInfo> Infos => new ArraySegment<ComponentInfo>(_infos, 0, _count);

    [HotPath]
    public ref readonly Entry this[int index] => ref _entries[index];

    public ComponentInfo Info(ComponentType type)
    {
        Require(type);
        return _infos[type.Index];
    }

    /// <summary>Registers a component of a module, after checking that its name is qualified by the module.</summary>
    public ComponentType<T> Register<T>(string moduleId, string name, ComponentOptions? options, in T defaultValue)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(name);
        string prefix = moduleId + ".";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length == prefix.Length || name.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException(
                $"Module '{moduleId}' cannot register component '{name}': the name of a component starts with the identifier of the module that registers it, then a dot and a name, such as '{moduleId}.position'.",
                nameof(name));
        }

        return RegisterCore(moduleId, name, options, defaultValue);
    }

    /// <summary>Registers a component known only by its <see cref="Type"/>, such as one of an assembly loaded at run time.</summary>
    public ComponentType Register(string moduleId, string name, Type type, ComponentOptions? options)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!type.IsValueType || type.IsGenericTypeDefinition || type.IsByRefLike || ContainsReferences(type))
        {
            throw new ArgumentException(
                $"Component '{name}' of module '{moduleId}' has the type '{type.FullName}', which is not an unmanaged struct: a component holds no reference.",
                nameof(type));
        }

        MethodInfo register = typeof(ComponentRegistry).GetMethod(nameof(RegisterBoxed), BindingFlags.NonPublic | BindingFlags.Instance)!.MakeGenericMethod(type);
        try
        {
            return (ComponentType)register.Invoke(this, [moduleId, name, options])!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(exception.InnerException);
            throw;
        }
    }

    public bool TryGet(string name, out ComponentType type)
    {
        if (name is not null && _byName.TryGetValue(name, out int index))
        {
            type = new ComponentType(index);
            return true;
        }

        type = default;
        return false;
    }

    public bool TryGet<T>(out ComponentType<T> type)
        where T : unmanaged
    {
        if (_byType.TryGetValue(typeof(T), out int index))
        {
            type = new ComponentType<T>(new ComponentType(index));
            return true;
        }

        type = default;
        return false;
    }

    /// <summary>Checks that a handle names a component of this registry.</summary>
    [HotPath]
    public void Require(ComponentType type)
    {
        if ((uint)type.Index >= (uint)_count)
        {
            throw new ArgumentException("The component handle is not valid: it is the default value, or comes from another context.", nameof(type));
        }
    }

    /// <summary>Checks that a handle names a component of type <typeparamref name="T"/> in this registry.</summary>
    [HotPath]
    public void Require<T>(ComponentType<T> type)
        where T : unmanaged
    {
        Require(type.Untyped);
        if (_infos[type.Untyped.Index].Type != typeof(T))
        {
            throw new ArgumentException($"The component handle does not match the type '{typeof(T).Name}': it comes from another context.", nameof(type));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (int i = 0; i < _count; i++)
        {
            NativeMemory.AlignedFree(_entries[i].Default);
        }
    }

    private static bool ContainsReferences(Type type) =>
        (bool)typeof(RuntimeHelpers).GetMethod(nameof(RuntimeHelpers.IsReferenceOrContainsReferences))!.MakeGenericMethod(type).Invoke(null, null)!;

    // Called by reflection from Register(Type): the struct is unmanaged, checked before.
    private ComponentType RegisterBoxed<T>(string moduleId, string name, ComponentOptions? options)
        where T : unmanaged =>
        Register<T>(moduleId, name, options, default).Untyped;

    private ComponentType<T> RegisterCore<T>(string moduleId, string name, ComponentOptions? options, in T defaultValue)
        where T : unmanaged
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_byName.TryGetValue(name, out int existing))
        {
            throw new ArgumentException($"Component '{name}' is already registered, by module '{_infos[existing].ModuleId}'.", nameof(name));
        }

        if (_byType.TryGetValue(typeof(T), out existing))
        {
            throw new ArgumentException(
                $"The type '{typeof(T).FullName}' is already registered as component '{_infos[existing].Name}'; a type is registered under one name only.",
                nameof(name));
        }

        int index = _count;
        if (index == _infos.Length)
        {
            // Replaced, not changed in place: a worker thread may read the old arrays.
            ComponentInfo[] infos = new ComponentInfo[index * 2];
            Entry[] entries = new Entry[index * 2];
            Array.Copy(_infos, infos, index);
            Array.Copy(_entries, entries, index);
            _infos = infos;
            _entries = entries;
        }

        bool track = options?.TrackChanges ?? false;
        var handle = new ComponentType(index);
        JobData data = _jobs.CreateData("component " + name);
        byte* value = (byte*)NativeMemory.AlignedAlloc((nuint)Math.Max(1, sizeof(T)), 16);
        *(T*)value = defaultValue;
        _infos[index] = new ComponentInfo(handle, name, typeof(T), sizeof(T), moduleId, track, data);
        _entries[index] = new Entry(sizeof(T), track, data, value);
        _byName.Add(name, index);
        _byType.Add(typeof(T), index);
        Volatile.Write(ref _count, index + 1);
        return new ComponentType<T>(handle);
    }

    /// <summary>What the storage needs of a component, read on the hot path.</summary>
    internal readonly struct Entry(int size, bool track, JobData data, byte* defaultValue)
    {
        public int Size { get; } = size;

        public bool Track { get; } = track;

        public JobData Data { get; } = data;

        public byte* Default { get; } = defaultValue;
    }
}
