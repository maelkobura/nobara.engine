// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.ResourceCompiler.Manifests;

namespace Nobara.Sdk.Tests;

/// <summary>Checks the version ranges as compiled into the SDK task assembly (netstandard2.0).</summary>
public class VersionRangeTests
{
    [Theory]
    [InlineData("^1.0", "[1.0,2.0.0)")]
    [InlineData("^1.2.3", "[1.2.3,2.0.0)")]
    [InlineData("^ 2.1", "[2.1,3.0.0)")]
    [InlineData("^0.3.1", "[0.3.1,0.4.0)")]
    [InlineData("^0.0.3", "[0.0.3,0.0.4)")]
    [InlineData("^0.0", "[0.0,0.1.0)")]
    [InlineData("^0", "[0,1.0.0)")]
    [InlineData("~1.2", "[1.2,1.3.0)")]
    [InlineData("~1.2.3", "[1.2.3,1.3.0)")]
    [InlineData("~1", "[1,2.0.0)")]
    [InlineData(">= 2.0", "[2.0,)")]
    [InlineData(">=2.0.1", "[2.0.1,)")]
    [InlineData("> 2.0", "(2.0,)")]
    [InlineData("<= 3.0", "(,3.0]")]
    [InlineData("< 3.0", "(,3.0)")]
    [InlineData(">=1.0 <2.0", "[1.0,2.0)")]
    [InlineData("<2.0 >1.0", "(1.0,2.0)")]
    [InlineData(">= 1.0 <= 2.0", "[1.0,2.0]")]
    [InlineData("^1.0-beta.1", "[1.0-beta.1,2.0.0)")]
    public void Converts_valid_ranges(string text, string expected)
    {
        Assert.True(VersionRange.TryParse(text, out VersionRange? range, out string error), error);
        Assert.Equal(expected, range!.NuGet);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2.3")]
    [InlineData("latest")]
    [InlineData("^")]
    [InlineData("^a.b")]
    [InlineData(">=")]
    [InlineData(">= 1.0 >= 2.0")]
    [InlineData("< 1.0 <= 2.0")]
    [InlineData("^1.0 <2.0")]
    [InlineData("^99999999999999999999")]
    public void Rejects_invalid_ranges(string text)
    {
        Assert.False(VersionRange.TryParse(text, out _, out string error));
        Assert.NotEmpty(error);
    }
}
