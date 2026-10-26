// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.RegularExpressions;

namespace Nobara.ResourceCompiler.Formats;

/// <summary>The forms of the identifiers used in <c>.nof</c> files, and how they are derived.</summary>
public static partial class Identifiers
{
    /// <summary>Tells whether the text is an object identifier: <c>type:namespace.name</c> in lowercase.</summary>
    /// <param name="text">The text to test.</param>
    /// <returns><see langword="true"/> for an object identifier such as <c>mesh:example.mesh.duck</c>.</returns>
    public static bool IsObjectId(string text) => ObjectId().IsMatch(text);

    /// <summary>Tells whether the text is a resource identifier: <c>type://Namespace/Name</c>.</summary>
    /// <param name="text">The text to test.</param>
    /// <returns><see langword="true"/> for a resource identifier such as <c>texture://Example/Textures/Dirt</c>.</returns>
    public static bool IsResourceId(string text) => ResourceId().IsMatch(text);

    /// <summary>Tells whether the text is a tag identifier: <c>#namespace.name</c> or <c>#type:namespace.name</c>.</summary>
    /// <param name="text">The text to test.</param>
    /// <returns><see langword="true"/> for a tag identifier such as <c>#example.wood</c>.</returns>
    public static bool IsTagId(string text) => TagId().IsMatch(text);

    /// <summary>Tells whether the text is a namespace: names separated by <c>/</c>.</summary>
    /// <param name="text">The text to test.</param>
    /// <returns><see langword="true"/> for a namespace such as <c>Example/Blocks</c>.</returns>
    public static bool IsNamespace(string text) => Namespace().IsMatch(text);

    /// <summary>Tells whether the text is the name of a resource: a letter, then letters, digits, and underscores.</summary>
    /// <param name="text">The text to test.</param>
    /// <returns><see langword="true"/> for a name such as <c>Oak_Log</c>.</returns>
    public static bool IsName(string text) => Name().IsMatch(text);

    /// <summary>Tells whether the text is a type prefix: lowercase letters, digits, and underscores, starting with a letter.</summary>
    /// <param name="text">The text to test.</param>
    /// <returns><see langword="true"/> for a prefix such as <c>block</c>.</returns>
    public static bool IsTypePrefix(string text) => TypePrefix().IsMatch(text);

    /// <summary>Tells whether the text is a qualified component name: at least two dot-separated lowercase names.</summary>
    /// <param name="text">The text to test.</param>
    /// <returns><see langword="true"/> for a name such as <c>example.physics.rigidbody</c>.</returns>
    public static bool IsComponentName(string text) => ComponentName().IsMatch(text);

    /// <summary>Tells whether the text is a localization key: at least two dot-separated lowercase names.</summary>
    /// <param name="text">The text to test.</param>
    /// <returns><see langword="true"/> for a key such as <c>block.natural.dirt</c>.</returns>
    public static bool IsLocalizationKey(string text) => LocalizationKey().IsMatch(text);

    /// <summary>Derives the object identifier of a resource.</summary>
    /// <param name="type">The type prefix: <c>block</c>.</param>
    /// <param name="ns">The namespace: <c>Example/Blocks</c>.</param>
    /// <param name="name">The name: <c>Dirt</c>.</param>
    /// <returns>The identifier: <c>block:example.blocks.dirt</c>.</returns>
    public static string ObjectIdOf(string type, string ns, string name) => $"{type}:{Dotted(ns, name)}";

    /// <summary>Derives the resource identifier of a resource.</summary>
    /// <param name="type">The type prefix: <c>texture</c>.</param>
    /// <param name="ns">The namespace: <c>Example/Textures</c>.</param>
    /// <param name="name">The name: <c>Dirt</c>.</param>
    /// <returns>The identifier: <c>texture://Example/Textures/Dirt</c>.</returns>
    public static string ResourceIdOf(string type, string ns, string name) => $"{type}://{ns}/{name}";

    /// <summary>Derives the identifier of a tag without its registry.</summary>
    /// <param name="ns">The namespace: <c>Example</c>.</param>
    /// <param name="name">The name: <c>Wood</c>.</param>
    /// <returns>The identifier: <c>example.wood</c>.</returns>
    public static string Dotted(string ns, string name) => (ns.Replace('/', '.') + "." + name).ToLowerInvariant();

    /// <summary>Gets the full form of a tag identifier, with its registry.</summary>
    /// <param name="registry">The registry used when the identifier has none.</param>
    /// <param name="tag">A tag identifier: <c>#example.wood</c> or <c>#block:example.wood</c>.</param>
    /// <returns>The registry and the dotted identifier.</returns>
    public static (string Registry, string Tag) SplitTag(string registry, string tag)
    {
        string body = tag.TrimStart('#');
        int colon = body.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? (registry, body) : (body[..colon], body[(colon + 1)..]);
    }

    /// <summary>Gets the type prefix of an object or resource identifier.</summary>
    /// <param name="id">The identifier.</param>
    /// <returns>The text before the first colon.</returns>
    public static string TypeOf(string id) => id[..id.IndexOf(':', StringComparison.Ordinal)];

    /// <summary>Gets a key that compares resource identifiers without case.</summary>
    /// <param name="resourceId">A resource identifier.</param>
    /// <returns>The identifier in lowercase.</returns>
    public static string ResourceKey(string resourceId) => resourceId.ToLowerInvariant();

    [GeneratedRegex(@"^[a-z][a-z0-9_]*:[a-z0-9_]+(\.[a-z0-9_]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ObjectId();

    [GeneratedRegex(@"^[a-z][a-z0-9_]*://[A-Za-z][A-Za-z0-9_]*(/[A-Za-z0-9_]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex ResourceId();

    [GeneratedRegex(@"^#([a-z][a-z0-9_]*:)?[a-z0-9_]+(\.[a-z0-9_]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex TagId();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*(/[A-Za-z][A-Za-z0-9_]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex Namespace();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Name();

    [GeneratedRegex(@"^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex TypePrefix();

    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex ComponentName();

    [GeneratedRegex(@"^[a-z0-9_]+(\.[a-z0-9_]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalizationKey();
}
