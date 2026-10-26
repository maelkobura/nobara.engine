// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Sdk.Tasks;

namespace Nobara.Sdk.Tests;

public class VersionRangeConverterTests
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
    public void Converts_valid_ranges(string range, string expected)
    {
        Assert.True(VersionRangeConverter.TryConvert(range, out string nuget, out string error), error);
        Assert.Equal(expected, nuget);
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
    public void Rejects_invalid_ranges(string range)
    {
        Assert.False(VersionRangeConverter.TryConvert(range, out _, out string error));
        Assert.NotEmpty(error);
    }
}
