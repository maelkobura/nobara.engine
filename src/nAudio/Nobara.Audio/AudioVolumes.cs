// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Nobara.Core.Logging;

namespace Nobara.Audio;

/// <summary>
/// <c>config/audio.xml</c>: the volumes of the buses that the player changed,
/// and only those. An entry for a bus that no loaded module defines is kept
/// and written again, so that removing a module does not lose its volume.
/// </summary>
internal sealed class AudioVolumes(string path, Logger logger)
{
    /// <summary>The name of the file in the configuration folder.</summary>
    public const string FileName = "audio.xml";

    private const string Root = "AudioVolumes";
    private const string Format = "1";

    private readonly List<XElement> _unknown = [];

    /// <summary>Reads the file into the volumes, by index of bus; a problem in an entry ignores the entry with a warning.</summary>
    public void Load(SoundRegistry sounds, float[] volumes)
    {
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
            logger.Warning($"The volumes of the player in '{path}' cannot be read, so the defaults apply: {exception.Message}");
            return;
        }

        XElement root = document.Root!;
        if (root.Name.LocalName != Root || root.Attribute("format")?.Value != Format)
        {
            logger.Warning($"'{path}' is not a file of volumes of format {Format} (<{Root} format=\"{Format}\">), so the defaults apply.");
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (XElement entry in root.Elements())
        {
            string? id = entry.Attribute("id")?.Value.Trim();
            if (entry.Name.LocalName != "Bus" || string.IsNullOrEmpty(id))
            {
                Warn(entry, $"<{entry.Name.LocalName}> is not a <Bus id=\"…\" volume=\"…\"/> entry; it is ignored");
                continue;
            }

            if (!seen.Add(id))
            {
                Warn(entry, $"the bus '{id}' has a second entry; it is ignored");
                continue;
            }

            if (!sounds.TryGetBus(id, out AudioBus? bus))
            {
                Warn(entry, $"no module defines the bus '{id}'; the entry is ignored, and kept when the file is written again");
                _unknown.Add(new XElement(entry));
                continue;
            }

            if (!bus.Adjustable)
            {
                Warn(entry, $"the bus '{id}' is not adjustable; the entry is ignored");
                continue;
            }

            if (!float.TryParse(entry.Attribute("volume")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float volume) || float.IsNaN(volume) || volume < 0 || volume > 1)
            {
                Warn(entry, $"the volume of '{id}' is not a number from 0 to 1; the entry is ignored");
                continue;
            }

            volumes[bus.Index] = volume;
        }
    }

    /// <summary>Writes the volumes that differ from 1, then the unknown entries, atomically.</summary>
    public void Save(SoundRegistry sounds, float[] volumes)
    {
        var root = new XElement(Root, new XAttribute("format", Format));
        foreach (AudioBus bus in sounds.Buses.OrderBy(b => b.ObjectId, StringComparer.Ordinal))
        {
            if (bus.Adjustable && volumes[bus.Index] != 1f)
            {
                root.Add(new XElement("Bus", new XAttribute("id", bus.ObjectId), new XAttribute("volume", volumes[bus.Index].ToString("R", CultureInfo.InvariantCulture))));
            }
        }

        foreach (XElement unknown in _unknown)
        {
            root.Add(new XElement(unknown));
        }

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XComment(" The volumes of the buses that the player changed, from 0 to 1. Only the differences with the defaults are kept. "),
            root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        document.Save(temporary);
        File.Move(temporary, path, overwrite: true);
        logger.Info($"The volumes of the player are written to '{path}'.");
    }

    private void Warn(XElement entry, string message)
    {
        int line = ((IXmlLineInfo)entry).LineNumber;
        logger.Warning($"{path}({line}): {message}.");
    }
}
