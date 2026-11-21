// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static Stride.Assets.CodeUpgrades;

namespace Stride.Assets.Tests;

/// <summary>
/// The titles of the upgrade steps, which the upgrade report and the upgrade guide show.
/// </summary>
public class TestUpgradeSteps
{
    private static readonly SymbolRewrite UndescribedRewrite = new(_ => [], (_, _, _) => { });

    [Fact]
    public void SymbolRewritesDescribeTheirChange()
    {
        Assert.Equal("Call `Target.IsSRgb` as a method", PropertyToMethod("TestNs.Target", "IsSRgb").Description);
        Assert.Equal("Read `PixelFormatExtensions.IsSRgb` as a property", MethodToProperty("Stride.Graphics.PixelFormatExtensions", "IsSRgb").Description);
        Assert.Equal("Rename the parameter `count` of the `IndexBufferBinding` constructor to `indexCount`", ParameterRename("Stride.Graphics.IndexBufferBinding", ".ctor", "count", "indexCount").Description);
        Assert.Equal("Rename the parameter `align` of `Utilities.AllocateMemory` to `alignment`", ParameterRename("Stride.Core.Utilities", "AllocateMemory", "align", "alignment").Description);
        Assert.Equal("Move `Utilities.FreeMemory` to `MemoryUtilities.Free`", StaticMemberMove("Stride.Core.Utilities", "FreeMemory", "Stride.Core.MemoryUtilities", "Free").Description);
        Assert.Equal("Move `Utilities.Swap` to `MemoryUtilities.Swap`", StaticMemberMove("Stride.Core.Utilities", "Swap", "Stride.Core.MemoryUtilities").Description);
        Assert.Equal("Rename `CommandList.UnsetRenderTargets` to `ResetTargets`", MemberRename("Stride.Graphics.CommandList", "UnsetRenderTargets", "ResetTargets").Description);
        Assert.Equal("Remove the calls of `CommandList.ResetQueryPool`", FluentCallRemove("Stride.Graphics.CommandList", "ResetQueryPool").Description);
        Assert.Equal("Remove the assignments and subscriptions of `GraphicsDeviceManager.DeviceReset`", AssignmentRemove("Stride.Games.GraphicsDeviceManager", "DeviceReset").Description);
        Assert.Equal("Use `Game.Audio` as an extension member of `Stride.Audio`", MemberToExtension("Stride.Engine.Game", "Audio", "Stride.Audio").Description);
        Assert.Equal("Use `ScriptComponent.Audio` through `Game`, as an extension member of `Stride.Audio`", MemberToExtension("Stride.Engine.ScriptComponent", "Audio", "Stride.Audio", throughMember: "Game").Description);
    }

    [Fact]
    public void StepHelpersTitleTheirChange()
    {
        Assert.Equal("Add `CaptureState` to the classes that implement `IInputDevice`", InterfaceMemberAdd("Stride.Input.IInputDevice", "public DeviceCaptureState CaptureState { get; } = new DeviceCaptureState();", "Stride.Input").Title);
        Assert.Equal("Move the overrides of `GamePadDeviceBase.State` to `protected RawState`", PropertyOverrideRename("Stride.Input.GamePadDeviceBase", "State", "RawState", SyntaxKind.ProtectedKeyword).Title);
        Assert.Equal("Move the overrides of `TouchController.IsPressed` to `protected IsRawPressed`", MethodOverrideRename("Stride.VirtualReality.TouchController", "IsPressed", "IsRawPressed", SyntaxKind.ProtectedKeyword).Title);
        Assert.Equal("Remove the unused `using` directives of `SharpDX`, `Vortice`", RemoveUnusedUsings("SharpDX", "Vortice").Title);
        Assert.Equal("Add `[EffectKeys]` to the types that hold static `ParameterKey` fields", AddAttributeToStaticFieldHolders("Stride.Rendering.ParameterKey", "Stride.Rendering.EffectKeysAttribute").Title);
    }

    [Fact]
    public void ARewriteStepIsTitledByItsRewrites()
    {
        Assert.Equal("Remove the calls of `Buffer.RecreateWith`", Rewrite(FluentCallRemove("Stride.Graphics.Buffer", "RecreateWith")).Title);
        Assert.Equal("Remove the calls of `Buffer.RecreateWith`; Rename `CommandList.UnsetRenderTargets` to `ResetTargets`",
            Rewrite(FluentCallRemove("Stride.Graphics.Buffer", "RecreateWith"), MemberRename("Stride.Graphics.CommandList", "UnsetRenderTargets", "ResetTargets")).Title);
        Assert.Equal("Remove the device reset hooks", Rewrite("Remove the device reset hooks", FluentCallRemove("Stride.Graphics.Buffer", "RecreateWith")).Title);
    }

    [Fact]
    public void AStepWithoutTitleIsRefused()
    {
        Assert.Throws<ArgumentException>(() => Rewrite(UndescribedRewrite));
        Assert.Throws<ArgumentException>(() => Rewrite(" ", UndescribedRewrite));
        Assert.Equal("Titled", Rewrite("Titled", UndescribedRewrite).Title);

        Assert.Throws<ArgumentException>(() => Custom(null, (solution, _, _) => Task.FromResult(solution)));
        Assert.Throws<ArgumentException>(() => Custom("", (solution, _, _) => Task.FromResult(solution)));

        var registry = new UpgradeRegistry();
        Assert.ThrowsAny<ArgumentException>(() => registry.Project("4.5.0.0", null, _ => { }));
        Assert.ThrowsAny<ArgumentException>(() => registry.Project("4.5.0.0", " ", _ => { }));
    }
}
