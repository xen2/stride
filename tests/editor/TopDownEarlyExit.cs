// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Threading.Tasks;
using Stride.GameStudio.AutoTesting;

namespace Stride.Editor.Tests;

// CANARY: closes Game Studio while the thumbnails of a heavy project are still building.
// One class per try so each try gets its own generated project folder; the delays sweep the build window.
public abstract class TopDownEarlyExitBase : IUITest
{
    protected abstract int DelayMs { get; }

    public async Task Run(IUITestContext ctx)
    {
        var opened = await ctx.WaitForAnyWindow(new[] { GameStudioWindowNames.GameStudio, GameStudioWindowNames.ProjectSelection }, timeoutSeconds: 180);
        if (opened != GameStudioWindowNames.GameStudio)
        {
            ctx.Exit(1);
            return;
        }

        await Task.Delay(DelayMs);
        ctx.Exit();
    }
}

[UITest(SampleTemplateId = "A363FBC5-89EF-4E7A-B870-6D070813D034")]
public class TopDownEarlyExit1 : TopDownEarlyExitBase { protected override int DelayMs => 1000; }

[UITest(SampleTemplateId = "A363FBC5-89EF-4E7A-B870-6D070813D034")]
public class TopDownEarlyExit2 : TopDownEarlyExitBase { protected override int DelayMs => 2000; }

[UITest(SampleTemplateId = "A363FBC5-89EF-4E7A-B870-6D070813D034")]
public class TopDownEarlyExit3 : TopDownEarlyExitBase { protected override int DelayMs => 3000; }

[UITest(SampleTemplateId = "A363FBC5-89EF-4E7A-B870-6D070813D034")]
public class TopDownEarlyExit4 : TopDownEarlyExitBase { protected override int DelayMs => 4000; }

[UITest(SampleTemplateId = "A363FBC5-89EF-4E7A-B870-6D070813D034")]
public class TopDownEarlyExit5 : TopDownEarlyExitBase { protected override int DelayMs => 6000; }

[UITest(SampleTemplateId = "A363FBC5-89EF-4E7A-B870-6D070813D034")]
public class TopDownEarlyExit6 : TopDownEarlyExitBase { protected override int DelayMs => 8000; }
