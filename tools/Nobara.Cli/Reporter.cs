// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.ResourceCompiler;

namespace Nobara.Cli;

/// <summary>Writes what the tool reports: progress to the output, problems to the error stream.</summary>
internal sealed class Reporter
{
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly Lock _gate = new();

    public Reporter(TextWriter output, TextWriter error)
    {
        _output = output;
        _error = error;
    }

    /// <summary>Gets the number of errors reported.</summary>
    public int Errors { get; private set; }

    public void Info(string message)
    {
        lock (_gate)
        {
            _output.WriteLine(message);
        }
    }

    public void Warning(string message)
    {
        lock (_gate)
        {
            _error.WriteLine("warning: " + message);
        }
    }

    public void Error(string message)
    {
        lock (_gate)
        {
            Errors++;
            _error.WriteLine("error: " + message);
        }
    }

    /// <summary>Reports diagnostics as <c>file(line,column): error CODE: message</c>.</summary>
    /// <returns><see langword="true"/> when none is an error.</returns>
    public bool Report(IEnumerable<Diagnostic> diagnostics)
    {
        bool success = true;
        lock (_gate)
        {
            foreach (Diagnostic diagnostic in diagnostics)
            {
                if (diagnostic.Severity == DiagnosticSeverity.Error)
                {
                    Errors++;
                    success = false;
                }

                _error.WriteLine(diagnostic.ToString());
            }
        }

        return success;
    }

    /// <summary>Writes the output of a child process, as it comes.</summary>
    public void Raw(string line)
    {
        lock (_gate)
        {
            _output.WriteLine(line);
        }
    }
}
