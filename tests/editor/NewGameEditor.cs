// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

// GameStudio capture for the "create a new game" flow: pick the New Game template in
// ProjectSelectionWindow, accept GameTemplateWindow defaults, wait for GameStudioWindow.
// (chatops test PR — trivial code touch to verify CI gating + bot dispatch)
using System;
using System.Threading.Tasks;
using Stride.Assets;
using Stride.Assets.Presentation.Templates;
using Stride.Core.Mathematics;
using Stride.GameStudio.AutoTesting;

namespace Stride.Editor.Tests;

[UITest(SampleTemplateId = "81d2adea-37b1-4711-834c-0d73a05c206c")]
public class NewGameEditor : IUITest
{
    public async Task Run(IUITestContext ctx)
    {
        await ctx.WaitDispatcherIdle();

        // The /NewProject arg shows ProjectSelectionWindow on launch — wait for it, pick the
        // New Game template, then proceed.
        if (!await ctx.WaitForWindow("ProjectSelectionWindow", timeoutSeconds: 30))
        {
            ctx.Exit(1);
            return;
        }
        await Task.Delay(TimeSpan.FromSeconds(1)); // let templates panel populate

        if (!await ctx.SelectTemplate(NewGameTemplateGenerator.TemplateId))
        {
            ctx.Exit(1);
            return;
        }
        await ctx.WaitFrames(2);

        // Click OK on ProjectSelectionWindow → triggers PrepareForRun on NewGameTemplateGenerator
        // which shows GameTemplateWindow (parameter dialog). Defaults are usable; close with Ok.
        if (!await ctx.CloseModalWithOk("ProjectSelectionWindow")) { ctx.Exit(1); return; }
        if (!await ctx.WaitForWindow("GameTemplateWindow", timeoutSeconds: 30)) { ctx.Exit(1); return; }
        if (!await ctx.CloseModalWithOk("GameTemplateWindow")) { ctx.Exit(1); return; }

        // Project generation runs (creates .sln, .csproj, asset folders, restores NuGet).
        // Then the editor opens it and GameStudioWindow appears.
        if (!await ctx.WaitForWindow("GameStudioWindow", timeoutSeconds: 180)) { ctx.Exit(1); return; }
        await ctx.SetWindowSize("GameStudioWindow", 2560, 1440);
        await ctx.WaitIdle();

        await ctx.Screenshot("new-game-editor");

        // Add a procedural capsule (template generator pops a material picker — pre-queue a
        // response so it picks the NewGame template's default "Sphere Material"), then drop an
        // entity referencing it into the scene where it casts a shadow on the default sphere.
        var pickerTask = ctx.QueueAssetPickerResponse("Sphere Material");
        var capsuleId = await ctx.AddAssetFromTemplate(ProceduralModelFactoryTemplateGenerator.TemplateId, "Capsule");
        await pickerTask;
        if (capsuleId == Guid.Empty) { ctx.Exit(1); return; }
        await ctx.AddEntityToScene("Capsule", capsuleId, new Vector3(0, 0.8f, -1.2f));
        await ctx.WaitIdle();
        await ctx.CapturePanel(GameSettingsAsset.DefaultSceneLocation, "scene-with-capsule", 1400, 900);

        // F5 from GameStudio: build + launch the project's .exe; capture the game window once
        // enough frames have rendered for post-effects to stabilise.
        var pid = await ctx.RunProject();
        if (pid <= 0) { ctx.Exit(1); return; }
        var hwnd = await ctx.WaitForGameWindow(pid);
        if (hwnd == IntPtr.Zero) { ctx.Exit(1); return; }
        await ctx.WaitForGameFrames(hwnd);
        await ctx.ScreenshotHwnd(hwnd, "game-running");
        await ctx.CloseGameWindow(pid);
        await ctx.CapturePanel("BuildLog", "build-log-after-run", 1200, 900);

        ctx.Exit();
    }
}
