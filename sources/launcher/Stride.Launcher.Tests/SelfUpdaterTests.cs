// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core;
using Stride.Launcher.Services;
using Xunit;

namespace Stride.Launcher.Tests;

public sealed class SelfUpdaterTests
{
    [Theory]
    [InlineData("6.0.1")]
    [InlineData("6.0.2-req")]
    public void IsUpdateCandidate_AcceptsStableAndRequired_WhenPrereleaseExcluded(string version)
    {
        Assert.True(SelfUpdater.IsUpdateCandidate(new PackageVersion(version), includePrerelease: false));
    }

    [Theory]
    [InlineData("6.1.0-beta1")]
    [InlineData("6.1.0-rc2")]
    public void IsUpdateCandidate_RejectsPrerelease_WhenPrereleaseExcluded(string version)
    {
        Assert.False(SelfUpdater.IsUpdateCandidate(new PackageVersion(version), includePrerelease: false));
    }

    [Theory]
    [InlineData("6.0.1")]
    [InlineData("6.0.2-req")]
    [InlineData("6.1.0-beta1")]
    public void IsUpdateCandidate_AcceptsEverything_WhenPrereleaseIncluded(string version)
    {
        Assert.True(SelfUpdater.IsUpdateCandidate(new PackageVersion(version), includePrerelease: true));
    }
}
