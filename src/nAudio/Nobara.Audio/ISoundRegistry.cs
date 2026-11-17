// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics.CodeAnalysis;

namespace Nobara.Audio;

/// <summary>
/// The sound events and the buses of one context, and the decoded sounds of
/// a client. Resolve it with <c>context.Resolve&lt;ISoundRegistry&gt;()</c> and
/// call it on the loop thread of the context, from initialization on.
/// </summary>
public interface ISoundRegistry
{
    /// <summary>Gets the sound events, by <see cref="SoundEvent.Index"/>.</summary>
    IReadOnlyList<SoundEvent> Sounds { get; }

    /// <summary>Gets the buses, by <see cref="AudioBus.Index"/>: the master bus first.</summary>
    IReadOnlyList<AudioBus> Buses { get; }

    /// <summary>Gets the master bus, <c>bus:nobara.audio.master</c>, the root of the tree.</summary>
    AudioBus Master { get; }

    /// <summary>Gets whether the context decodes sounds: <see langword="true"/> on the client only.</summary>
    bool Decodes { get; }

    /// <summary>Gets a sound event.</summary>
    /// <param name="objectId">Its identifier: <c>sound:example.sounds.stonebreak</c>.</param>
    /// <returns>The event.</returns>
    /// <exception cref="KeyNotFoundException">No loaded module defines it.</exception>
    SoundEvent Get(string objectId);

    /// <summary>Finds a sound event.</summary>
    /// <param name="objectId">Its identifier.</param>
    /// <param name="sound">The event, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a loaded module defines it.</returns>
    bool TryGet(string objectId, [NotNullWhen(true)] out SoundEvent? sound);

    /// <summary>Gets a bus.</summary>
    /// <param name="objectId">Its identifier: <c>bus:nobara.audio.effects</c>.</param>
    /// <returns>The bus.</returns>
    /// <exception cref="KeyNotFoundException">No loaded module defines it.</exception>
    AudioBus GetBus(string objectId);

    /// <summary>Finds a bus.</summary>
    /// <param name="objectId">Its identifier.</param>
    /// <param name="bus">The bus, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a loaded module defines it.</returns>
    bool TryGetBus(string objectId, [NotNullWhen(true)] out AudioBus? bus);

    /// <summary>
    /// Gets a variant decoded in memory. The client decodes every variant of
    /// the events that do not stream, in background jobs, from the
    /// initialization of nAudio on.
    /// </summary>
    /// <param name="variant">A variant of an event that does not stream.</param>
    /// <param name="clip">The decoded samples, or <see langword="null"/> while they decode, on the server, or when the decoding failed.</param>
    /// <returns><see langword="true"/> when the variant is decoded.</returns>
    bool TryGetClip(SoundVariant variant, [NotNullWhen(true)] out SoundClip? clip);

    /// <summary>Waits for every decoding in progress, from the loop thread; a failure is logged as an error.</summary>
    void CompleteDecoding();

    /// <summary>Opens a stream that decodes a variant in blocks, in background jobs. Client only.</summary>
    /// <param name="variant">Any variant; events marked to stream are meant for it.</param>
    /// <param name="loop">Whether the stream goes back to the start at the end of the file.</param>
    /// <returns>The stream; dispose it.</returns>
    /// <exception cref="InvalidOperationException">The context is a server, which decodes nothing.</exception>
    SoundStream OpenStream(SoundVariant variant, bool loop);

    /// <summary>Opens a stream that starts at a frame of the file, as a sound that went on in time without being heard. Client only.</summary>
    /// <param name="variant">Any variant.</param>
    /// <param name="loop">Whether the stream goes back to the start at the end of the file.</param>
    /// <param name="startFrame">The frame to start from, from 0 to the length of the variant excluded; 0 outside these bounds.</param>
    /// <returns>The stream; dispose it.</returns>
    /// <exception cref="InvalidOperationException">The context is a server, which decodes nothing.</exception>
    SoundStream OpenStream(SoundVariant variant, bool loop, long startFrame);
}
