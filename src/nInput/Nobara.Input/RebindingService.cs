// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Xml;
using System.Xml.Linq;
using Nobara.Core;
using Nobara.Frames;

namespace Nobara.Input;

/// <summary>
/// The bindings of the player: the changes, the conflicts, the listening for
/// the next input, and <c>config/input.xml</c>, which holds only the
/// differences with the defaults.
/// </summary>
internal sealed class RebindingService : IInputRebinding
{
    /// <summary>The name of the file in the configuration folder.</summary>
    public const string FileName = "input.xml";

    /// <summary>The root element of the file.</summary>
    public const string Root = "InputBindings";

    /// <summary>The version of the format of the file.</summary>
    public const string Format = "1";

    /// <summary>How far the mouse moves, in logical units, before a listening takes it as an axis.</summary>
    private const float MotionThreshold = 20;

    private readonly InputSystem _system;
    private readonly ModuleContext _context;
    private readonly List<XElement> _unknown = [];

    public RebindingService(InputSystem system, ModuleContext context)
    {
        _system = system;
        _context = context;
    }

    public string FilePath => Path.Combine(_context.Paths.Config, FileName);

    public IReadOnlyList<BindingConflict> FindConflicts(InputAction action, InputBinding binding)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(binding);
        var conflicts = new List<BindingConflict>();
        foreach (InputAction other in _system.Actions)
        {
            if (other == action || !ActiveTogether(action.Map, other.Map))
            {
                continue;
            }

            for (int i = 0; i < other.Bindings.Count; i++)
            {
                if (other.Bindings[i].ConflictsWith(binding))
                {
                    conflicts.Add(new BindingConflict(other, i));
                }
            }
        }

        return conflicts;
    }

    public void SetBindings(InputAction action, IReadOnlyList<InputBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(bindings);
        foreach (InputBinding binding in bindings)
        {
            ArgumentNullException.ThrowIfNull(binding, nameof(bindings));
            if (!binding.Accepts(action.ValueKind))
            {
                throw new ArgumentException($"The binding '{binding}' cannot give the value of '{action.Id}', a {action.ValueKind}.", nameof(bindings));
            }
        }

        action.SetCustomBindings(bindings.ToArray());
    }

    public void Rebind(InputAction action, int index, InputBinding binding)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, action.Bindings.Count);
        var bindings = action.Bindings.ToList();
        if (index == bindings.Count)
        {
            bindings.Add(binding);
        }
        else
        {
            bindings[index] = binding;
        }

        SetBindings(action, bindings);
    }

    public void RemoveBinding(InputAction action, int index)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, action.Bindings.Count);
        var bindings = action.Bindings.ToList();
        bindings.RemoveAt(index);
        action.SetCustomBindings(bindings);
    }

    public void SetInteraction(InputAction action, InputInteraction interaction)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.ValueKind is InputValueKind.Axis1D or InputValueKind.Axis2D)
        {
            throw new ArgumentException($"'{action.Id}' is an axis; only a button or a float has an interaction.", nameof(action));
        }

        action.SetCustomInteraction(interaction);
    }

    public void ResetToDefault(InputAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action.SetCustomBindings(null);
        action.SetCustomInteraction(null);
    }

    public void ResetAllToDefault()
    {
        foreach (InputAction action in _system.Actions)
        {
            ResetToDefault(action);
        }
    }

    public InputListening Listen(InputValueKind kind, Action<InputBinding?> captured, Scancode cancel = Scancode.Escape)
    {
        ArgumentNullException.ThrowIfNull(captured);
        var listening = new InputListening(kind, captured, cancel);
        _system.SetListening(listening);
        return listening;
    }

    public void Save()
    {
        var root = new XElement(Root, new XAttribute("format", Format));
        foreach (InputAction action in _system.Actions)
        {
            if (!action.HasCustomBindings && !action.HasCustomInteraction)
            {
                continue;
            }

            var entry = new XElement("Action", new XAttribute("id", action.Id));
            if (action.CustomBindings is { } bindings)
            {
                entry.Add(InputXml.ToXml(bindings));
            }

            if (action.CustomInteraction is { } interaction)
            {
                entry.Add(InputXml.ToXml(interaction));
            }

            root.Add(entry);
        }

        foreach (XElement unknown in _unknown)
        {
            root.Add(new XElement(unknown));
        }

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XComment(" The bindings that the player changed. Only the differences with the defaults are kept. "),
            root);
        string path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        document.Save(temporary);
        File.Move(temporary, path, overwrite: true);
        _context.Logger.Info($"The bindings of the player are written to '{path}'.");
    }

    /// <summary>Reads <c>config/input.xml</c>; a problem in an entry ignores the entry with a warning.</summary>
    public void Load()
    {
        string path = FilePath;
        if (!File.Exists(path))
        {
            return;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(path, LoadOptions.SetLineInfo);
        }
        catch (Exception exception) when (exception is XmlException or IOException or UnauthorizedAccessException)
        {
            _context.Logger.Warning($"The bindings of the player in '{path}' cannot be read, so the defaults apply: {exception.Message}");
            return;
        }

        XElement root = document.Root!;
        if (root.Name.LocalName != Root || root.Attribute("format")?.Value != Format)
        {
            _context.Logger.Warning($"'{path}' is not a file of bindings of format {Format} (<{Root} format=\"{Format}\">), so the defaults apply.");
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (XElement entry in root.Elements())
        {
            string? id = entry.Attribute("id")?.Value.Trim();
            if (entry.Name.LocalName != "Action" || string.IsNullOrEmpty(id))
            {
                Warn(path, entry, $"<{entry.Name.LocalName}> is not an <Action id=\"…\"> entry; it is ignored");
                continue;
            }

            if (!seen.Add(id))
            {
                Warn(path, entry, $"the action '{id}' has a second entry; it is ignored");
                continue;
            }

            if (!_system.TryGetAction(id, out InputAction? action))
            {
                Warn(path, entry, $"no module declares the action '{id}'; the entry is ignored, and kept when the file is written again");
                _unknown.Add(new XElement(entry));
                continue;
            }

            Apply(path, entry, action);
        }
    }

    /// <summary>Hands a raw event to the listening.</summary>
    /// <returns><see langword="true"/> when the listening took the event.</returns>
    public bool Listen(InputListening listening, in RawInputEvent e)
    {
        if (!listening.IsListening)
        {
            return false;
        }

        switch (e.Kind)
        {
            case RawInputKind.KeyDown when !e.IsRepeat:
                if (e.Scancode == listening.Cancel && listening.Modifiers.Count == 0)
                {
                    End(listening, null);
                }
                else if (IsModifier(e.Scancode))
                {
                    InputControl modifier = InputControl.Key(e.Scancode);
                    if (listening.Kind is InputValueKind.Button or InputValueKind.Float && !listening.Modifiers.Contains(modifier))
                    {
                        listening.Modifiers.Add(modifier);
                    }
                }
                else if (listening.Kind != InputValueKind.Axis2D && e.Scancode != Scancode.Unknown)
                {
                    End(listening, WithModifiers(listening, InputControl.Key(e.Scancode)));
                }

                return true;
            case RawInputKind.KeyUp:
                if (IsModifier(e.Scancode) && listening.Modifiers.Remove(InputControl.Key(e.Scancode)))
                {
                    End(listening, WithModifiers(listening, InputControl.Key(e.Scancode)));
                }

                return true;
            case RawInputKind.MouseButtonDown:
                if (listening.Kind != InputValueKind.Axis2D && MouseOf(e.Button) is { } button)
                {
                    End(listening, WithModifiers(listening, InputControl.Mouse(button)));
                }

                return true;
            case RawInputKind.MouseWheel:
                ListenWheel(listening, e);
                return true;
            case RawInputKind.MouseMotion:
                ListenMotion(listening, e);
                return true;
            case RawInputKind.KeyDown or RawInputKind.MouseButtonUp:
                return true;
            default:
                return false;
        }
    }

    private static bool ActiveTogether(InputMap? a, InputMap? b) => a is null || b is null || a == b || (a.IsActive && b.IsActive);

    private static bool IsModifier(Scancode scancode) => scancode is >= Scancode.LeftControl and <= Scancode.RightMeta;

    private static MouseControl? MouseOf(MouseButton button) => button switch
    {
        MouseButton.Left => MouseControl.Left,
        MouseButton.Right => MouseControl.Right,
        MouseButton.Middle => MouseControl.Middle,
        MouseButton.X1 => MouseControl.Back,
        MouseButton.X2 => MouseControl.Forward,
        _ => null,
    };

    private static InputBinding WithModifiers(InputListening listening, InputControl trigger) =>
        listening.Modifiers.Count == 0 ? InputBinding.Key(trigger) : InputBinding.Combo([.. listening.Modifiers, trigger]);

    private void ListenWheel(InputListening listening, in RawInputEvent e)
    {
        bool vertical = Math.Abs(e.DeltaY) >= Math.Abs(e.DeltaX);
        switch (listening.Kind)
        {
            case InputValueKind.Button or InputValueKind.Float:
                MouseControl step = vertical ? (e.DeltaY > 0 ? MouseControl.WheelUp : MouseControl.WheelDown) : (e.DeltaX > 0 ? MouseControl.WheelRight : MouseControl.WheelLeft);
                End(listening, WithModifiers(listening, InputControl.Mouse(step)));
                break;
            case InputValueKind.Axis1D:
                End(listening, InputBinding.Axis(InputControl.Mouse(vertical ? MouseControl.Wheel : MouseControl.WheelX)));
                break;
        }
    }

    private void ListenMotion(InputListening listening, in RawInputEvent e)
    {
        if (listening.Kind is InputValueKind.Button or InputValueKind.Float)
        {
            return;
        }

        listening.MotionX += e.DeltaX;
        listening.MotionY += e.DeltaY;
        float x = Math.Abs(listening.MotionX);
        float y = Math.Abs(listening.MotionY);
        if (Math.Max(x, y) < MotionThreshold)
        {
            return;
        }

        MouseControl axis = listening.Kind == InputValueKind.Axis2D ? MouseControl.Delta : (x >= y ? MouseControl.DeltaX : MouseControl.DeltaY);
        End(listening, InputBinding.Axis(InputControl.Mouse(axis)));
    }

    private void End(InputListening listening, InputBinding? binding)
    {
        listening.Dispose();
        try
        {
            listening.Captured(binding);
        }
        catch (Exception exception) when (exception is not EngineStopException)
        {
            _context.ReportFault(exception);
        }
    }

    private void Apply(string path, XElement entry, InputAction action)
    {
        foreach (XElement child in entry.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "Bindings":
                    List<InputBinding>? bindings = InputXml.FromXml(child, out string? error);
                    if (bindings is null)
                    {
                        Warn(path, child, $"the bindings of '{action.Id}' are ignored: {error}");
                    }
                    else if (bindings.FirstOrDefault(b => !b.Accepts(action.ValueKind)) is { } wrong)
                    {
                        Warn(path, child, $"the bindings of '{action.Id}' are ignored: '{wrong}' cannot give the value of a {action.ValueKind}");
                    }
                    else
                    {
                        action.SetCustomBindings(bindings);
                    }

                    break;
                case "Interaction":
                    InputInteraction? interaction = InputXml.InteractionFromXml(child, out string? problem);
                    if (interaction is null)
                    {
                        Warn(path, child, $"the interaction of '{action.Id}' is ignored: {problem}");
                    }
                    else if (action.ValueKind is InputValueKind.Axis1D or InputValueKind.Axis2D)
                    {
                        Warn(path, child, $"the interaction of '{action.Id}' is ignored: an axis has no interaction");
                    }
                    else
                    {
                        action.SetCustomInteraction(interaction);
                    }

                    break;
                default:
                    Warn(path, child, $"<{child.Name.LocalName}> is not part of an entry, which holds <Bindings> and <Interaction>; it is ignored");
                    break;
            }
        }
    }

    private void Warn(string path, XElement at, string message)
    {
        int line = ((IXmlLineInfo)at).LineNumber;
        _context.Logger.Warning($"{path}({line}): {message}.");
    }
}
