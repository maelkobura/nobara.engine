// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text;

namespace Nobara.Core.Logging;

/// <summary>The severity of a log message.</summary>
public enum LogLevel
{
    /// <summary>Detailed tracing, usually disabled.</summary>
    Trace,

    /// <summary>Information for debugging, usually disabled in release.</summary>
    Debug,

    /// <summary>The normal course of the game: modules loaded, steps run.</summary>
    Information,

    /// <summary>Something unexpected that the game recovers from.</summary>
    Warning,

    /// <summary>A failure, such as an exception thrown by a module.</summary>
    Error,

    /// <summary>A failure that stops a context.</summary>
    Critical,

    /// <summary>Disables logging when used as a minimum level.</summary>
    None,
}

/// <summary>One structured log message.</summary>
/// <param name="Timestamp">When the message was written, in UTC.</param>
/// <param name="Level">The severity.</param>
/// <param name="Context">The context the message comes from: <c>client</c>, <c>server</c>, or an empty string for the process.</param>
/// <param name="Source">The identifier of the module the message comes from, or <c>core</c>.</param>
/// <param name="Phase">The lifecycle step or loop phase that was running, or an empty string.</param>
/// <param name="Message">The text of the message.</param>
/// <param name="Exception">The exception the message reports, or <see langword="null"/>.</param>
public readonly record struct LogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Context,
    string Source,
    string Phase,
    string Message,
    Exception? Exception)
{
    /// <summary>Formats the entry as one line, followed by the exception when there is one.</summary>
    /// <returns>For example <c>2026-01-01 12:00:00.000 INF [client/example.blocks/Initialization] Message</c>.</returns>
    public override string ToString()
    {
        var text = new StringBuilder();
        text.Append(Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(Abbreviation(Level))
            .Append(" [")
            .Append(Context.Length > 0 ? Context : "process")
            .Append('/')
            .Append(Source);
        if (Phase.Length > 0)
        {
            text.Append('/').Append(Phase);
        }

        text.Append("] ").Append(Message);
        if (Exception is not null)
        {
            text.AppendLine().Append(Exception);
        }

        return text.ToString();
    }

    private static string Abbreviation(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        _ => "CRT",
    };
}

/// <summary>A destination of log messages: the console, a file, a test.</summary>
/// <remarks>Implementations are called from any thread and must be thread-safe.</remarks>
public interface ILogSink
{
    /// <summary>Writes one message.</summary>
    /// <param name="entry">The message.</param>
    void Write(in LogEntry entry);

    /// <summary>Writes what is buffered, before the process stops.</summary>
    void Flush();
}
