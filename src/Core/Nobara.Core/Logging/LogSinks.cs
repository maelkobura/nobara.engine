// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text;

namespace Nobara.Core.Logging;

/// <summary>Writes log messages to the standard output, and errors to the standard error.</summary>
public sealed class ConsoleLogSink : ILogSink
{
    private readonly Lock _gate = new();

    /// <inheritdoc/>
    public void Write(in LogEntry entry)
    {
        string line = entry.ToString();
        lock (_gate)
        {
            (entry.Level >= LogLevel.Error ? Console.Error : Console.Out).WriteLine(line);
        }
    }

    /// <inheritdoc/>
    public void Flush()
    {
        lock (_gate)
        {
            Console.Out.Flush();
            Console.Error.Flush();
        }
    }
}

/// <summary>Writes log messages to a file of the logs folder, one file per run.</summary>
public sealed class FileLogSink : ILogSink, IDisposable
{
    private readonly Lock _gate = new();
    private StreamWriter? _writer;

    /// <summary>Opens a new file in a folder, named after the current time.</summary>
    /// <param name="folder">The logs folder; it is created when missing.</param>
    /// <exception cref="IOException">The file cannot be created.</exception>
    public FileLogSink(string folder)
    {
        Directory.CreateDirectory(folder);
        string name = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture);
        string path = System.IO.Path.Combine(folder, name + ".log");
        for (int attempt = 1; File.Exists(path); attempt++)
        {
            path = System.IO.Path.Combine(folder, $"{name}-{attempt}.log");
        }

        Path = path;
        _writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
    }

    /// <summary>Gets the path of the file.</summary>
    public string Path { get; }

    /// <inheritdoc/>
    public void Write(in LogEntry entry)
    {
        string line = entry.ToString();
        lock (_gate)
        {
            _writer?.WriteLine(line);
            if (entry.Level >= LogLevel.Error)
            {
                _writer?.Flush();
            }
        }
    }

    /// <inheritdoc/>
    public void Flush()
    {
        lock (_gate)
        {
            _writer?.Flush();
        }
    }

    /// <summary>Flushes and closes the file.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
