// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Loop;

namespace Nobara.Context;

/// <summary>
/// The state stack of one context. Requests are checked against the stack as
/// it will be once the requests already made apply, so that a wrong request
/// throws in the code that makes it.
/// </summary>
internal sealed class StateStack : IStateStack
{
    private const string Prefix = "state:";

    private readonly ModuleContext _context;
    private readonly Dictionary<string, Registration> _registered = new(StringComparer.Ordinal);
    private readonly List<Entry> _stack = [];
    private readonly List<string> _ids = [];
    private readonly List<string> _projected = [];
    private readonly Queue<GlobalEvent> _events = new();
    private List<Request> _pending = [];
    private List<Request> _applying = [];
    private bool _started;
    private bool _stopping;

    public StateStack(ModuleContext context)
    {
        _context = context;
    }

    public string? InitialState { get; private set; }

    public IReadOnlyList<string> States => _ids;

    public string? Active => _ids.Count > 0 ? _ids[^1] : null;

    public bool HasPendingTransitions => _pending.Count > 0;

    public void Register(ModuleContext module, string id, Func<ApplicationState> create, StateOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(create);
        if (module.Step != LifecycleStep.Initialization)
        {
            throw new InvalidOperationException(
                $"Module '{module.Module.Id}' cannot register state '{id}' during {Describe(module.Step)}. States are registered while modules initialize.");
        }

        if (!id.StartsWith(Prefix, StringComparison.Ordinal) || id.Length == Prefix.Length)
        {
            throw new ArgumentException($"'{id}' is not a state identifier: 'state:' followed by a namespace and a name, as in 'state:example.menu'.", nameof(id));
        }

        if (_registered.TryGetValue(id, out Registration? existing))
        {
            throw new ArgumentException($"State '{id}' is already registered by module '{existing.Owner.Module.Id}'.", nameof(id));
        }

        _registered.Add(id, new Registration(id, module, create, options?.KeepStatesBelowRunning ?? false));
    }

    public bool IsRegistered(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _registered.ContainsKey(id);
    }

    public bool IsRunning(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        foreach (Entry entry in _stack)
        {
            if (entry.Registration.Id == id)
            {
                return entry.Running;
            }
        }

        return false;
    }

    public void Push(string id)
    {
        Registration registration = Find(id, "push");
        if (_projected.Contains(id))
        {
            throw new InvalidOperationException($"State '{id}' cannot be pushed: it is already in the stack ({Describe(_projected)}).");
        }

        Entry added = Create(registration);
        _projected.Add(id);
        _pending.Add(new Request(RequestKind.Push, added));
    }

    public void Pop()
    {
        if (_projected.Count == 0)
        {
            throw new InvalidOperationException("No state can be popped: the stack is empty once the transitions already requested apply.");
        }

        _projected.RemoveAt(_projected.Count - 1);
        _pending.Add(new Request(RequestKind.Pop, null));
    }

    public void Replace(string id)
    {
        Registration registration = Find(id, "replace the active state with");
        if (_projected.Count == 0)
        {
            throw new InvalidOperationException($"State '{id}' cannot replace the active state: the stack is empty once the transitions already requested apply. Push it instead.");
        }

        int index = _projected.IndexOf(id);
        if (index >= 0 && index < _projected.Count - 1)
        {
            throw new InvalidOperationException($"State '{id}' cannot replace the active state: it is already in the stack below it ({Describe(_projected)}).");
        }

        Entry added = Create(registration);
        _projected[^1] = id;
        _pending.Add(new Request(RequestKind.Replace, added));
    }

    public void PopAll()
    {
        _projected.Clear();
        _pending.Add(new Request(RequestKind.PopAll, null));
    }

    public void Raise(GlobalEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        _events.Enqueue(e);
    }

    /// <summary>
    /// Checks the registered states against the manifests, reads the initial
    /// state of the side, and requests it for the first frame.
    /// </summary>
    public void Start()
    {
        _started = true;
        _context.VerifyStates(_registered.Values.Select(r => new RegisteredState(r.Id, r.Owner.Module.Id)).ToArray());

        string side = _context.Side == Side.Server ? "server" : "client";
        string initial = _context.Distribution.GetInitialState(_context.Side)
            ?? throw new InvalidOperationException(
                $"Module '{_context.Module.Id}' runs on the {side} side, and engine.nobara declares no initial {side} state. Add the '{side}' attribute to <States>.");
        if (!_registered.ContainsKey(initial))
        {
            throw new InvalidOperationException(
                $"The initial {side} state '{initial}' is not registered: no active module of the {side} side provides it.");
        }

        InitialState = initial;
        Push(initial);
        _context.Logger.Info($"{_registered.Count} states are registered on the {side} side; the first frame enters '{initial}'.");
    }

    /// <summary>Logs the states that stay in the stack when the context stops without emptying it.</summary>
    public void Stop()
    {
        if (_ids.Count > 0)
        {
            _context.Logger.Info($"The context stops with states in the stack, which do not exit: {Describe(_ids)}.");
        }
    }

    /// <summary>Runs at the start of each frame: global events, then transitions, then the updates of the running states.</summary>
    public void RunFrameStart(in PhaseTime time)
    {
        if (!_started || _stopping)
        {
            return;
        }

        DeliverEvents();
        ApplyTransitions();
        if (_stopping)
        {
            return;
        }

        for (int i = 0; i < _stack.Count; i++)
        {
            Entry entry = _stack[i];
            if (!entry.Running || !IsActive(entry))
            {
                continue;
            }

            try
            {
                entry.State.Update(time);
            }
            catch (Exception exception) when (exception is not EngineStopException)
            {
                entry.Registration.Owner.ReportFault(exception);
            }
        }
    }

    private void DeliverEvents()
    {
        // An event raised while events are delivered waits for the next frame.
        for (int count = _events.Count; count > 0; count--)
        {
            GlobalEvent e = _events.Dequeue();
            bool handled = false;
            if (_stack.Count > 0 && _stack[^1] is { } active && IsActive(active))
            {
                try
                {
                    handled = active.State.OnGlobalEvent(e);
                }
                catch (Exception exception) when (exception is not EngineStopException)
                {
                    active.Registration.Owner.ReportFault(exception);
                }
            }

            if (!handled)
            {
                e.ApplyDefaultAction(this);
            }

            _context.Logger.Info($"Global event '{e.GetType().Name}' {(handled ? $"handled by state '{Active}'" : "applied its default action")}.");
        }
    }

    private void ApplyTransitions()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        // A transition requested while these apply goes to the next frame.
        (_applying, _pending) = (_pending, _applying);
        foreach (Request request in _applying)
        {
            Apply(request);
        }

        _applying.Clear();
        if (_stack.Count == 0 && _pending.Count == 0)
        {
            _stopping = true;
            _context.Logger.Info("The state stack is empty: the context stops.");
            _context.RequestStop();
        }
    }

    private void Apply(Request request)
    {
        Entry? added = request.Added;
        string? before = Active;
        int remove = request.Kind switch
        {
            RequestKind.Pop or RequestKind.Replace => Math.Min(1, _stack.Count),
            RequestKind.PopAll => _stack.Count,
            _ => 0,
        };

        for (int i = 0; i < remove; i++)
        {
            Entry top = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            _ids.RemoveAt(_ids.Count - 1);
            Call(top, Callback.Exit);
        }

        if (added is not null)
        {
            _stack.Add(added);
            _ids.Add(added.Registration.Id);
        }

        UpdateRunning(added);
        if (added is not null)
        {
            Call(added, Callback.Enter);
        }

        _context.Logger.Info(request.Kind switch
        {
            RequestKind.Push => before is null ? $"Pushed state '{Active}'." : $"Pushed state '{Active}' over '{before}'.",
            RequestKind.Pop => $"Popped state '{before}'" + (Active is null ? "." : $"; '{Active}' is active."),
            RequestKind.Replace => $"Replaced state '{before}' with '{Active}'.",
            _ => $"Popped every state ({remove}).",
        });
    }

    // A state runs when every state above it keeps the states below running.
    // The states that stop running are suspended from the top down, then those
    // that run again are resumed from the bottom up. A state that just entered
    // the stack runs and is neither suspended nor resumed.
    private void UpdateRunning(Entry? added)
    {
        bool[] runs = new bool[_stack.Count];
        bool running = true;
        for (int i = _stack.Count - 1; i >= 0; i--)
        {
            runs[i] = running;
            running = running && _stack[i].Registration.KeepStatesBelowRunning;
        }

        if (added is not null)
        {
            added.Running = true;
        }

        for (int i = _stack.Count - 1; i >= 0; i--)
        {
            Entry entry = _stack[i];
            if (!runs[i] && entry.Running)
            {
                entry.Running = false;
                Call(entry, Callback.Suspend);
            }
        }

        for (int i = 0; i < _stack.Count; i++)
        {
            Entry entry = _stack[i];
            if (runs[i] && !entry.Running)
            {
                entry.Running = true;
                Call(entry, Callback.Resume);
            }
        }
    }

    private void Call(Entry entry, Callback callback)
    {
        if (!IsActive(entry))
        {
            return;
        }

        try
        {
            switch (callback)
            {
                case Callback.Enter:
                    entry.State.Enter();
                    break;
                case Callback.Exit:
                    entry.State.Exit();
                    break;
                case Callback.Suspend:
                    entry.State.Suspend();
                    break;
                case Callback.Resume:
                    entry.State.Resume();
                    break;
            }
        }
        catch (Exception exception) when (exception is not EngineStopException)
        {
            entry.Registration.Owner.ReportFault(exception);
        }
    }

    // The code of a disabled module is not called; its states stay in the stack.
    private bool IsActive(Entry entry) => _context.IsModuleActive(entry.Registration.Owner.Module.Id);

    // The instance is created when the transition is requested, so that a
    // factory that fails throws in the code that requests it.
    private static Entry Create(Registration registration)
    {
        ApplicationState state = registration.Create()
            ?? throw new InvalidOperationException($"The factory of state '{registration.Id}' returned null.");
        state.Id = registration.Id;
        return new Entry(registration, state);
    }

    private Registration Find(string id, string action)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _registered.TryGetValue(id, out Registration? registration)
            ? registration
            : throw new ArgumentException($"Cannot {action} state '{id}': no module registers it on this side.", nameof(id));
    }

    private static string Describe(IReadOnlyList<string> ids) => ids.Count == 0 ? "empty" : "bottom to top: " + string.Join(", ", ids);

    private static string Describe(LifecycleStep step) => step switch
    {
        LifecycleStep.Registration => "registration",
        LifecycleStep.Initialization => "initialization",
        LifecycleStep.Start => "start",
        LifecycleStep.Execution => "execution",
        _ => step.ToString().ToLowerInvariant(),
    };

    private enum RequestKind
    {
        Push,
        Pop,
        Replace,
        PopAll,
    }

    private enum Callback
    {
        Enter,
        Exit,
        Suspend,
        Resume,
    }

    private readonly record struct Request(RequestKind Kind, Entry? Added);

    private sealed record Registration(string Id, ModuleContext Owner, Func<ApplicationState> Create, bool KeepStatesBelowRunning);

    private sealed class Entry(Registration registration, ApplicationState state)
    {
        public Registration Registration { get; } = registration;

        public ApplicationState State { get; } = state;

        public bool Running { get; set; }
    }
}
