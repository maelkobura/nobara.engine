// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics.CodeAnalysis;
using Nobara.Audio.Schemas;
using Nobara.Core;
using Nobara.Core.Jobs;
using Nobara.Core.Logging;
using Nobara.Core.Resources;

namespace Nobara.Audio;

/// <summary>A bus resource decoded once per process.</summary>
internal sealed class BusDefinition(ResourceData data, Bus content)
{
    public ResourceData Data { get; } = data;

    public Bus Content { get; } = content;

    public static BusDefinition Decode(ResourceData data) => new(data, Bus.Parser.ParseFrom(data.Content.Span));
}

/// <summary>One reason why the sounds or the buses cannot be loaded.</summary>
/// <param name="ObjectId">The sound or the bus at fault.</param>
/// <param name="Message">What is wrong.</param>
/// <param name="Source">The position in the <c>.nof</c> file, as precise as the compiled data allows.</param>
public sealed record AudioProblem(string ObjectId, string Message, SourcePosition Source)
{
    /// <summary>Returns the problem as compilers write it.</summary>
    /// <returns>For example <c>Resources/sounds.nof(3,5): sound:example.sounds.click: …</c>.</returns>
    public override string ToString() => string.IsNullOrEmpty(Source.File) ? $"{ObjectId}: {Message}" : $"{Source}: {ObjectId}: {Message}";
}

/// <summary>
/// Thrown when the sounds or the buses of the loaded modules cannot be used:
/// a bus whose parent is unknown, a cycle in the tree, or a content that an
/// override broke.
/// </summary>
public sealed class AudioLoadException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="problems">Every problem found, at least one.</param>
    public AudioLoadException(IReadOnlyList<AudioProblem> problems)
        : base("The sounds cannot be loaded:" + string.Concat(problems.Select(p => Environment.NewLine + "- " + p)))
    {
        Problems = problems;
    }

    /// <summary>Gets every problem found.</summary>
    public IReadOnlyList<AudioProblem> Problems { get; }
}

/// <summary>The sound events and the buses of one context, and the decoded sounds of a client.</summary>
internal sealed class SoundRegistry : ISoundRegistry
{
    private readonly ModuleJobs _jobs;
    private readonly Logger _logger;
    private readonly List<SoundEvent> _sounds = [];
    private readonly Dictionary<string, SoundEvent> _soundsById = new(StringComparer.Ordinal);
    private readonly List<AudioBus> _buses = [];
    private readonly Dictionary<string, AudioBus> _busesById = new(StringComparer.Ordinal);
    private readonly Dictionary<SoundVariant, ClipWork> _pending = [];
    private readonly Dictionary<SoundVariant, SoundClip?> _clips = [];
    private bool _frozen;

    public SoundRegistry(ModuleJobs jobs, Logger logger, bool decodes)
    {
        _jobs = jobs;
        _logger = logger;
        Decodes = decodes;
    }

    public IReadOnlyList<SoundEvent> Sounds
    {
        get
        {
            RequireFrozen();
            return _sounds;
        }
    }

    public IReadOnlyList<AudioBus> Buses
    {
        get
        {
            RequireFrozen();
            return _buses;
        }
    }

    public AudioBus Master
    {
        get
        {
            RequireFrozen();
            return _buses[0];
        }
    }

    public bool Decodes { get; }

    /// <summary>Gets the number of variants that are still decoding, for the tests.</summary>
    internal int PendingCount => _pending.Count;

    public SoundEvent Get(string objectId) =>
        TryGet(objectId, out SoundEvent? sound) ? sound : throw new KeyNotFoundException($"No loaded module defines '{objectId}'.");

    public bool TryGet(string objectId, [NotNullWhen(true)] out SoundEvent? sound)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        RequireFrozen();
        return _soundsById.TryGetValue(objectId, out sound);
    }

    public AudioBus GetBus(string objectId) =>
        TryGetBus(objectId, out AudioBus? bus) ? bus : throw new KeyNotFoundException($"No loaded module defines '{objectId}'.");

    public bool TryGetBus(string objectId, [NotNullWhen(true)] out AudioBus? bus)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        RequireFrozen();
        return _busesById.TryGetValue(objectId, out bus);
    }

    public bool TryGetClip(SoundVariant variant, [NotNullWhen(true)] out SoundClip? clip)
    {
        ArgumentNullException.ThrowIfNull(variant);
        RequireFrozen();
        if (_pending.TryGetValue(variant, out ClipWork? work) && work.Handle.IsCompleted)
        {
            Collect(work);
        }

        clip = _clips.GetValueOrDefault(variant);
        return clip is not null;
    }

    public void CompleteDecoding()
    {
        RequireFrozen();
        foreach (ClipWork work in _pending.Values.ToList())
        {
            work.Handle.Complete();
            Collect(work);
        }
    }

    public SoundStream OpenStream(SoundVariant variant, bool loop)
    {
        ArgumentNullException.ThrowIfNull(variant);
        RequireFrozen();
        if (!Decodes)
        {
            throw new InvalidOperationException("A server decodes no sound: only a client opens streams.");
        }

        return new SoundStream(_jobs, variant, loop);
    }

    /// <summary>Builds the tree of buses and numbers the sounds, then starts decoding on a client.</summary>
    /// <exception cref="AudioLoadException">A bus or a sound cannot be used.</exception>
    public void Freeze(IReadOnlyList<BusDefinition> buses, IReadOnlyList<SoundDefinition> sounds)
    {
        if (_frozen)
        {
            throw new InvalidOperationException("The sound registry is already frozen.");
        }

        var problems = new List<AudioProblem>();
        BuildBuses(buses, problems);
        var events = new List<SoundEvent>();
        foreach (SoundDefinition definition in sounds.OrderBy(s => s.Data.ObjectId, StringComparer.Ordinal))
        {
            if (Build(definition, events.Count, problems) is { } sound)
            {
                events.Add(sound);
            }
        }

        if (problems.Count > 0)
        {
            throw new AudioLoadException(problems);
        }

        foreach (SoundEvent sound in events)
        {
            _sounds.Add(sound);
            _soundsById.Add(sound.ObjectId, sound);
        }

        _frozen = true;
        foreach (SoundEvent sound in _sounds)
        {
            StartDecoding(sound);
        }
    }

    /// <summary>Applies the changes of sounds while the game runs; numbers and buses stay as they are.</summary>
    public void ReloadSounds(ResourceReload reload, IReadOnlyList<SoundDefinition> sounds)
    {
        foreach (ResourceData added in reload.Added)
        {
            reload.Refuse(added.ObjectId, "a new sound takes effect at the next start: sounds are numbered when nAudio initializes");
        }

        foreach (string removed in reload.Removed)
        {
            reload.Refuse(removed, "a removed sound takes effect at the next start: sounds are numbered when nAudio initializes");
        }

        Dictionary<string, SoundDefinition> byId = sounds.ToDictionary(s => s.Data.ObjectId, StringComparer.Ordinal);
        foreach (ResourceData changed in reload.Changed)
        {
            if (!_soundsById.TryGetValue(changed.ObjectId, out SoundEvent? old) || !byId.TryGetValue(changed.ObjectId, out SoundDefinition? definition))
            {
                continue;
            }

            var problems = new List<AudioProblem>();
            if (Build(definition, old.Index, problems) is not { } sound)
            {
                reload.Refuse(changed.ObjectId, string.Join("; ", problems.Select(p => p.Message)));
                continue;
            }

            foreach (SoundVariant variant in old.Variants)
            {
                _pending.Remove(variant);
                _clips.Remove(variant);
            }

            _sounds[old.Index] = sound;
            _soundsById[sound.ObjectId] = sound;
            StartDecoding(sound);
        }
    }

    private void BuildBuses(IReadOnlyList<BusDefinition> definitions, List<AudioProblem> problems)
    {
        var byId = new Dictionary<string, BusDefinition>(StringComparer.Ordinal);
        foreach (BusDefinition definition in definitions)
        {
            byId[definition.Data.ObjectId] = definition;
        }

        if (!byId.ContainsKey(AudioRules.MasterBus))
        {
            problems.Add(new AudioProblem(AudioRules.MasterBus, "the master bus is missing: nobara.audio provides it", new SourcePosition()));
            return;
        }

        foreach (BusDefinition definition in byId.Values)
        {
            string id = definition.Data.ObjectId;
            foreach ((string message, _) in AudioRules.CheckBus(id, definition.Content))
            {
                problems.Add(new AudioProblem(id, message, definition.Data.Source));
            }

            string parent = definition.Content.Parent;
            if (id == AudioRules.MasterBus)
            {
                if (parent.Length > 0)
                {
                    problems.Add(new AudioProblem(id, "the master bus is the root of the tree and has no parent", definition.Data.Source));
                }
            }
            else if (parent.Length == 0)
            {
                problems.Add(new AudioProblem(id, $"a bus needs a <Parent>: only {AudioRules.MasterBus} is a root", definition.Data.Source));
            }
            else if (!byId.ContainsKey(parent))
            {
                problems.Add(new AudioProblem(id, $"the parent '{parent}' is not a bus of the loaded modules", definition.Data.Source));
            }
        }

        // A bus that does not reach the master bus by its parents is in a cycle, or below one.
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (BusDefinition definition in byId.Values)
        {
            var path = new List<string>();
            string? current = definition.Data.ObjectId;
            while (current is not null && current != AudioRules.MasterBus && byId.TryGetValue(current, out BusDefinition? node))
            {
                int seen = path.IndexOf(current);
                if (seen >= 0)
                {
                    List<string> cycle = path.Skip(seen).ToList();
                    if (cycle.All(reported.Add))
                    {
                        problems.Add(new AudioProblem(current, $"the buses form a cycle: {string.Join(" -> ", cycle)} -> {current}", node.Data.Source));
                    }

                    break;
                }

                path.Add(current);
                current = node.Content.Parent.Length == 0 ? null : node.Content.Parent;
            }
        }

        if (problems.Count > 0)
        {
            return;
        }

        IEnumerable<BusDefinition> ordered = byId.Values
            .OrderBy(b => b.Data.ObjectId == AudioRules.MasterBus ? 0 : 1)
            .ThenBy(b => b.Data.ObjectId, StringComparer.Ordinal);
        foreach (BusDefinition definition in ordered)
        {
            var bus = new AudioBus(definition.Data.ObjectId, _buses.Count, definition.Content.Adjustable, definition.Content.DisplayName);
            _buses.Add(bus);
            _busesById.Add(bus.ObjectId, bus);
        }

        foreach (AudioBus bus in _buses)
        {
            string parent = byId[bus.ObjectId].Content.Parent;
            if (parent.Length > 0)
            {
                bus.Parent = _busesById[parent];
                bus.Parent.AddChild(bus);
            }
        }
    }

    private SoundEvent? Build(SoundDefinition definition, int index, List<AudioProblem> problems)
    {
        int before = problems.Count;
        string id = definition.Data.ObjectId;
        foreach ((string message, _, _) in AudioRules.CheckSound(definition.Content))
        {
            problems.Add(new AudioProblem(id, message, definition.Data.Source));
        }

        if (definition.Variants.Count == 0)
        {
            problems.Add(new AudioProblem(id, "a sound needs at least one <Variant>", definition.Data.Source));
        }

        if (!_busesById.TryGetValue(definition.Content.Category, out AudioBus? bus))
        {
            // Without buses, the problems of the tree are already reported.
            if (_buses.Count > 0)
            {
                problems.Add(new AudioProblem(id, $"the category '{definition.Content.Category}' is not a bus of the loaded modules", definition.Data.Source));
            }

            return null;
        }

        return problems.Count > before ? null : new SoundEvent(definition, index, bus);
    }

    private void StartDecoding(SoundEvent sound)
    {
        if (!Decodes || sound.Streamed)
        {
            return;
        }

        foreach (SoundVariant variant in sound.Variants)
        {
            if (_clips.ContainsKey(variant) || _pending.ContainsKey(variant))
            {
                continue;
            }

            var work = new ClipWork(variant);
            work.Handle = _jobs.Schedule(new ClipJob(work), priority: JobPriority.Background);
            _pending.Add(variant, work);
        }
    }

    private void Collect(ClipWork work)
    {
        if (!_pending.Remove(work.Variant))
        {
            return;
        }

        if (work.Error is { } error)
        {
            _logger.Error($"The file '{work.Variant.File}' cannot be decoded: {error.Message}", error);
            _clips[work.Variant] = null;
            return;
        }

        _clips[work.Variant] = new SoundClip(work.Variant, work.Samples, work.Written);
    }

    private void RequireFrozen()
    {
        if (!_frozen)
        {
            throw new InvalidOperationException("The sounds are read from the initialization of nobara.audio on.");
        }
    }
}
