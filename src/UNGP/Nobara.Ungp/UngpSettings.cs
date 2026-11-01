// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using Nobara.Graphics;

namespace Nobara.Ungp;

/// <summary>The names of the configuration keys of UNGP, and how their values read.</summary>
public static class UngpSettings
{
    /// <summary>The render resolution as a fraction of the viewport, from 0.25 to 2. Scope <c>user</c>; applies at the next frame.</summary>
    public const string RenderScale = "ungp.renderScale";

    /// <summary>The color of the plain sky, as <c>#RRGGBB</c> in sRGB. Scope <c>distribution</c>; applies at the next frame.</summary>
    public const string SkyColor = "ungp.skyColor";

    /// <summary>The factor applied to the HDR colors before tonemapping, from 0.0625 to 16. Scope <c>distribution</c>; applies at the next frame.</summary>
    public const string Exposure = "ungp.exposure";

    /// <summary>The default value of <see cref="SkyColor"/>.</summary>
    public const string DefaultSkyColor = "#87CEEB";

    /// <summary>Reads a color written <c>#RRGGBB</c> in sRGB, as the linear color that shaders use.</summary>
    /// <param name="value">The text, such as <c>#87CEEB</c>; <see langword="null"/> is not a color.</param>
    /// <param name="color">The linear color, opaque; the default value when the text is not a color.</param>
    /// <returns><see langword="true"/> when the text is a color.</returns>
    public static bool TryParseColor(string? value, out ColorF color)
    {
        color = default;
        if (value is not { Length: 7 } || value[0] != '#'
            || !uint.TryParse(value.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb))
        {
            return false;
        }

        color = new ColorF(Linear((rgb >> 16) & 0xFF), Linear((rgb >> 8) & 0xFF), Linear(rgb & 0xFF), 1);
        return true;
    }

    // The transfer function of sRGB, from an 8-bit value to a linear value.
    private static float Linear(uint value)
    {
        float c = value / 255f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }
}
