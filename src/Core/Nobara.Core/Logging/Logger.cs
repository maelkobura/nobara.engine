// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Runtime.CompilerServices;

namespace Nobara.Core.Logging;

/// <summary>
/// Writes structured messages for one source (a module or the core) in one
/// context. Each message records the source, the context, the level, and the
/// step or phase that was running.
/// </summary>
/// <remarks>
/// <para>
/// A message written with an interpolated string, such as
/// <c>logger.Debug($"loaded {count} chunks")</c>, is neither formatted nor
/// allocated when its level is disabled.
/// </para>
/// <para>A logger can be used from any thread.</para>
/// </remarks>
public sealed class Logger
{
    private readonly LogDispatcher _dispatcher;
    private readonly PhaseTracker? _phase;

    internal Logger(LogDispatcher dispatcher, string context, string source, PhaseTracker? phase)
    {
        _dispatcher = dispatcher;
        Context = context;
        Source = source;
        _phase = phase;
    }

    /// <summary>Gets the context of the messages: <c>client</c>, <c>server</c>, or an empty string for the process.</summary>
    public string Context { get; }

    /// <summary>Gets the source of the messages: a module identifier, or <c>core</c>.</summary>
    public string Source { get; }

    /// <summary>Tells whether messages of a level are written.</summary>
    /// <param name="level">The level of a message.</param>
    /// <returns><see langword="true"/> when messages of <paramref name="level"/> reach the sinks.</returns>
    public bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= _dispatcher.MinimumLevel;

    /// <summary>Writes a message.</summary>
    /// <param name="level">The level of the message.</param>
    /// <param name="message">The text of the message.</param>
    /// <param name="exception">The exception the message reports, or <see langword="null"/>.</param>
    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        if (IsEnabled(level))
        {
            _dispatcher.Write(new LogEntry(DateTimeOffset.UtcNow, level, Context, Source, _phase?.Current ?? string.Empty, message, exception));
        }
    }

    /// <summary>Writes a message of level <see cref="LogLevel.Trace"/>.</summary>
    /// <param name="message">The text of the message.</param>
    public void Trace(string message) => Log(LogLevel.Trace, message);

    /// <summary>Writes a message of level <see cref="LogLevel.Trace"/>, formatted only when the level is enabled.</summary>
    /// <param name="message">The interpolated text of the message.</param>
    public void Trace([InterpolatedStringHandlerArgument("")] ref TraceMessage message)
    {
        if (message.Enabled)
        {
            Log(LogLevel.Trace, message.Builder.ToStringAndClear());
        }
    }

    /// <summary>Writes a message of level <see cref="LogLevel.Debug"/>.</summary>
    /// <param name="message">The text of the message.</param>
    public void Debug(string message) => Log(LogLevel.Debug, message);

    /// <summary>Writes a message of level <see cref="LogLevel.Debug"/>, formatted only when the level is enabled.</summary>
    /// <param name="message">The interpolated text of the message.</param>
    public void Debug([InterpolatedStringHandlerArgument("")] ref DebugMessage message)
    {
        if (message.Enabled)
        {
            Log(LogLevel.Debug, message.Builder.ToStringAndClear());
        }
    }

    /// <summary>Writes a message of level <see cref="LogLevel.Information"/>.</summary>
    /// <param name="message">The text of the message.</param>
    public void Info(string message) => Log(LogLevel.Information, message);

    /// <summary>Writes a message of level <see cref="LogLevel.Information"/>, formatted only when the level is enabled.</summary>
    /// <param name="message">The interpolated text of the message.</param>
    public void Info([InterpolatedStringHandlerArgument("")] ref InfoMessage message)
    {
        if (message.Enabled)
        {
            Log(LogLevel.Information, message.Builder.ToStringAndClear());
        }
    }

    /// <summary>Writes a message of level <see cref="LogLevel.Warning"/>.</summary>
    /// <param name="message">The text of the message.</param>
    public void Warning(string message) => Log(LogLevel.Warning, message);

    /// <summary>Writes a message of level <see cref="LogLevel.Warning"/>, formatted only when the level is enabled.</summary>
    /// <param name="message">The interpolated text of the message.</param>
    public void Warning([InterpolatedStringHandlerArgument("")] ref WarningMessage message)
    {
        if (message.Enabled)
        {
            Log(LogLevel.Warning, message.Builder.ToStringAndClear());
        }
    }

    /// <summary>Writes a message of level <see cref="LogLevel.Error"/>.</summary>
    /// <param name="message">The text of the message.</param>
    /// <param name="exception">The exception the message reports, or <see langword="null"/>.</param>
    public void Error(string message, Exception? exception = null) => Log(LogLevel.Error, message, exception);

    /// <summary>Writes a message of level <see cref="LogLevel.Critical"/>.</summary>
    /// <param name="message">The text of the message.</param>
    /// <param name="exception">The exception the message reports, or <see langword="null"/>.</param>
    public void Critical(string message, Exception? exception = null) => Log(LogLevel.Critical, message, exception);

    internal Logger WithSource(string source) => new(_dispatcher, Context, source, _phase);
}

/// <summary>Builds the text of a message of level <see cref="LogLevel.Trace"/>; written by the compiler for an interpolated string.</summary>
[InterpolatedStringHandler]
public ref struct TraceMessage
{
    internal DefaultInterpolatedStringHandler Builder;
    internal readonly bool Enabled;

    /// <summary>Starts a message, which is built only when its level is enabled.</summary>
    /// <param name="literalLength">The length of the literal parts.</param>
    /// <param name="formattedCount">The number of formatted values.</param>
    /// <param name="logger">The logger that writes the message.</param>
    /// <param name="isEnabled">Set to whether the message is built.</param>
    public TraceMessage(int literalLength, int formattedCount, Logger logger, out bool isEnabled)
    {
        isEnabled = Enabled = logger.IsEnabled(LogLevel.Trace);
        Builder = isEnabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount, CultureInfo.InvariantCulture) : default;
    }

    /// <summary>Appends a literal part.</summary>
    /// <param name="value">The text.</param>
    public void AppendLiteral(string value) => Builder.AppendLiteral(value);

    /// <summary>Appends a value.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="value">The value.</param>
    public void AppendFormatted<T>(T value) => Builder.AppendFormatted(value);

    /// <summary>Appends a value with a format.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="format">The format of the value.</param>
    public void AppendFormatted<T>(T value, string? format) => Builder.AppendFormatted(value, format);
}

/// <summary>Builds the text of a message of level <see cref="LogLevel.Debug"/>; written by the compiler for an interpolated string.</summary>
[InterpolatedStringHandler]
public ref struct DebugMessage
{
    internal DefaultInterpolatedStringHandler Builder;
    internal readonly bool Enabled;

    /// <summary>Starts a message, which is built only when its level is enabled.</summary>
    /// <param name="literalLength">The length of the literal parts.</param>
    /// <param name="formattedCount">The number of formatted values.</param>
    /// <param name="logger">The logger that writes the message.</param>
    /// <param name="isEnabled">Set to whether the message is built.</param>
    public DebugMessage(int literalLength, int formattedCount, Logger logger, out bool isEnabled)
    {
        isEnabled = Enabled = logger.IsEnabled(LogLevel.Debug);
        Builder = isEnabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount, CultureInfo.InvariantCulture) : default;
    }

    /// <summary>Appends a literal part.</summary>
    /// <param name="value">The text.</param>
    public void AppendLiteral(string value) => Builder.AppendLiteral(value);

    /// <summary>Appends a value.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="value">The value.</param>
    public void AppendFormatted<T>(T value) => Builder.AppendFormatted(value);

    /// <summary>Appends a value with a format.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="format">The format of the value.</param>
    public void AppendFormatted<T>(T value, string? format) => Builder.AppendFormatted(value, format);
}

/// <summary>Builds the text of a message of level <see cref="LogLevel.Information"/>; written by the compiler for an interpolated string.</summary>
[InterpolatedStringHandler]
public ref struct InfoMessage
{
    internal DefaultInterpolatedStringHandler Builder;
    internal readonly bool Enabled;

    /// <summary>Starts a message, which is built only when its level is enabled.</summary>
    /// <param name="literalLength">The length of the literal parts.</param>
    /// <param name="formattedCount">The number of formatted values.</param>
    /// <param name="logger">The logger that writes the message.</param>
    /// <param name="isEnabled">Set to whether the message is built.</param>
    public InfoMessage(int literalLength, int formattedCount, Logger logger, out bool isEnabled)
    {
        isEnabled = Enabled = logger.IsEnabled(LogLevel.Information);
        Builder = isEnabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount, CultureInfo.InvariantCulture) : default;
    }

    /// <summary>Appends a literal part.</summary>
    /// <param name="value">The text.</param>
    public void AppendLiteral(string value) => Builder.AppendLiteral(value);

    /// <summary>Appends a value.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="value">The value.</param>
    public void AppendFormatted<T>(T value) => Builder.AppendFormatted(value);

    /// <summary>Appends a value with a format.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="format">The format of the value.</param>
    public void AppendFormatted<T>(T value, string? format) => Builder.AppendFormatted(value, format);
}

/// <summary>Builds the text of a message of level <see cref="LogLevel.Warning"/>; written by the compiler for an interpolated string.</summary>
[InterpolatedStringHandler]
public ref struct WarningMessage
{
    internal DefaultInterpolatedStringHandler Builder;
    internal readonly bool Enabled;

    /// <summary>Starts a message, which is built only when its level is enabled.</summary>
    /// <param name="literalLength">The length of the literal parts.</param>
    /// <param name="formattedCount">The number of formatted values.</param>
    /// <param name="logger">The logger that writes the message.</param>
    /// <param name="isEnabled">Set to whether the message is built.</param>
    public WarningMessage(int literalLength, int formattedCount, Logger logger, out bool isEnabled)
    {
        isEnabled = Enabled = logger.IsEnabled(LogLevel.Warning);
        Builder = isEnabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount, CultureInfo.InvariantCulture) : default;
    }

    /// <summary>Appends a literal part.</summary>
    /// <param name="value">The text.</param>
    public void AppendLiteral(string value) => Builder.AppendLiteral(value);

    /// <summary>Appends a value.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="value">The value.</param>
    public void AppendFormatted<T>(T value) => Builder.AppendFormatted(value);

    /// <summary>Appends a value with a format.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="format">The format of the value.</param>
    public void AppendFormatted<T>(T value, string? format) => Builder.AppendFormatted(value, format);
}

/// <summary>The sinks and the minimum level shared by every logger of a process.</summary>
internal sealed class LogDispatcher
{
    private readonly ILogSink[] _sinks;

    public LogDispatcher(IEnumerable<ILogSink> sinks, LogLevel minimumLevel)
    {
        _sinks = sinks.ToArray();
        MinimumLevel = minimumLevel;
    }

    public LogLevel MinimumLevel { get; }

    public void Write(in LogEntry entry)
    {
        foreach (ILogSink sink in _sinks)
        {
            try
            {
                sink.Write(entry);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // A sink that fails must not take the game down with it.
            }
        }
    }

    public void Flush()
    {
        foreach (ILogSink sink in _sinks)
        {
            try
            {
                sink.Flush();
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // Nothing more can be done about a sink that fails.
            }
        }
    }
}

/// <summary>The step or phase a context is running, read by its loggers.</summary>
internal sealed class PhaseTracker
{
    public volatile string Current = string.Empty;
}
