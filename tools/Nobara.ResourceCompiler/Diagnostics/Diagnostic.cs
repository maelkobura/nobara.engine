// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

// This file is also compiled into Nobara.Sdk (netstandard2.0), which reads
// module.nobara with the same code. Keep it free of newer APIs.

using System.Collections.Generic;
using System.Xml;
using System.Xml.Linq;

namespace Nobara.ResourceCompiler;

/// <summary>How serious a <see cref="Diagnostic"/> is.</summary>
public enum DiagnosticSeverity
{
    /// <summary>The input is invalid: the compilation fails.</summary>
    Error,

    /// <summary>The input is valid but probably not what was meant.</summary>
    Warning,
}

/// <summary>A problem found in a source file, with its position.</summary>
public sealed class Diagnostic
{
    /// <summary>Creates a diagnostic.</summary>
    /// <param name="code">The code: <c>NRC</c> followed by three digits.</param>
    /// <param name="severity">Whether the problem fails the compilation.</param>
    /// <param name="message">The message, without the code and the position.</param>
    /// <param name="file">The path of the file, or an empty string when the problem concerns no file.</param>
    /// <param name="line">The 1-based line, or 0 when unknown.</param>
    /// <param name="column">The 1-based column, or 0 when unknown.</param>
    public Diagnostic(string code, DiagnosticSeverity severity, string message, string file, int line, int column)
    {
        Code = code;
        Severity = severity;
        Message = message;
        File = file;
        Line = line;
        Column = column;
    }

    /// <summary>Gets the code: <c>NRC</c> followed by three digits.</summary>
    public string Code { get; }

    /// <summary>Gets whether the problem fails the compilation.</summary>
    public DiagnosticSeverity Severity { get; }

    /// <summary>Gets the message, without the code and the position.</summary>
    public string Message { get; }

    /// <summary>Gets the path of the file, or an empty string when the problem concerns no file.</summary>
    public string File { get; }

    /// <summary>Gets the 1-based line, or 0 when unknown.</summary>
    public int Line { get; }

    /// <summary>Gets the 1-based column, or 0 when unknown.</summary>
    public int Column { get; }

    /// <summary>Formats the diagnostic as <c>file(line,column): error CODE: message</c>.</summary>
    /// <returns>The formatted diagnostic.</returns>
    public override string ToString()
    {
        string position = Line > 0 ? $"({Line},{Column})" : string.Empty;
        string severity = Severity == DiagnosticSeverity.Error ? "error" : "warning";
        return File.Length > 0 ? $"{File}{position}: {severity} {Code}: {Message}" : $"{severity} {Code}: {Message}";
    }
}

/// <summary>A position in a source file.</summary>
public sealed class Location
{
    /// <summary>Creates a location.</summary>
    /// <param name="file">The path of the file.</param>
    /// <param name="line">The 1-based line, or 0 when unknown.</param>
    /// <param name="column">The 1-based column, or 0 when unknown.</param>
    public Location(string file, int line, int column)
    {
        File = file;
        Line = line;
        Column = column;
    }

    /// <summary>Gets the path of the file.</summary>
    public string File { get; }

    /// <summary>Gets the 1-based line, or 0 when unknown.</summary>
    public int Line { get; }

    /// <summary>Gets the 1-based column, or 0 when unknown.</summary>
    public int Column { get; }

    /// <summary>Gets the location of an XML node.</summary>
    /// <param name="file">The path of the file the node comes from.</param>
    /// <param name="node">An element or an attribute loaded with line information.</param>
    /// <returns>The location of the node, with line 0 when the node has no line information.</returns>
    public static Location Of(string file, XObject node)
    {
        var info = (IXmlLineInfo)node;
        return info.HasLineInfo() ? new Location(file, info.LineNumber, info.LinePosition) : new Location(file, 0, 0);
    }
}

/// <summary>Collects diagnostics.</summary>
public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = new List<Diagnostic>();

    /// <summary>Gets the diagnostics, in the order they were reported.</summary>
    public IReadOnlyList<Diagnostic> Items => _items;

    /// <summary>Gets whether an error was reported.</summary>
    public bool HasErrors
    {
        get
        {
            foreach (Diagnostic diagnostic in _items)
            {
                if (diagnostic.Severity == DiagnosticSeverity.Error)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Gets the number of errors reported.</summary>
    public int ErrorCount
    {
        get
        {
            int count = 0;
            foreach (Diagnostic diagnostic in _items)
            {
                if (diagnostic.Severity == DiagnosticSeverity.Error)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Adds a diagnostic.</summary>
    /// <param name="diagnostic">The diagnostic to add.</param>
    public void Add(Diagnostic diagnostic) => _items.Add(diagnostic);

    /// <summary>Adds diagnostics.</summary>
    /// <param name="diagnostics">The diagnostics to add.</param>
    public void AddRange(IEnumerable<Diagnostic> diagnostics) => _items.AddRange(diagnostics);

    /// <summary>Reports an error at a location.</summary>
    /// <param name="code">The diagnostic code.</param>
    /// <param name="message">The message.</param>
    /// <param name="at">The location, or <see langword="null"/> when there is none.</param>
    public void Error(string code, string message, Location? at) =>
        _items.Add(new Diagnostic(code, DiagnosticSeverity.Error, message, at?.File ?? string.Empty, at?.Line ?? 0, at?.Column ?? 0));

    /// <summary>Reports a warning at a location.</summary>
    /// <param name="code">The diagnostic code.</param>
    /// <param name="message">The message.</param>
    /// <param name="at">The location, or <see langword="null"/> when there is none.</param>
    public void Warning(string code, string message, Location? at) =>
        _items.Add(new Diagnostic(code, DiagnosticSeverity.Warning, message, at?.File ?? string.Empty, at?.Line ?? 0, at?.Column ?? 0));
}
