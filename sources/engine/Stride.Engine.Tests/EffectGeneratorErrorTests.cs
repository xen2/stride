// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using Xunit;
using Stride.Core.Diagnostics;
using Stride.Graphics.Regression;
using Stride.Rendering;
using Stride.Shaders;
using Stride.Shaders.Compiler;

namespace Stride.Engine.Tests
{
    /// <summary>
    /// A render feature catches what the generator of an effect throws, and draws a fallback. The log is all a user gets, so the error must be in it.
    /// </summary>
    public class EffectGeneratorErrorTests : GameTestBase
    {
        private sealed class MacroInComposition : IShaderMixinBuilder
        {
            public void Generate(ShaderMixinSource mixinTree, ShaderMixinContext context)
            {
                context.PushComposition(mixinTree, "Slot", new ShaderMixinSource());
                context.AddMacro("A", 1);
            }
        }

        [Fact]
        public void AMacroInACompositionIsInTheLog()
        {
            PerformTest(game =>
            {
                ShaderMixinManager.Register(nameof(MacroInComposition), new MacroInComposition());

                var errors = new List<string>();
                void Collect(ILogMessage message)
                {
                    if (message.Type == LogMessageType.Error)
                    {
                        lock (errors) errors.Add(message.Text);
                    }
                }

                GlobalLogger.GlobalMessageLogged += Collect;
                try
                {
                    Assert.Throws<InvalidOperationException>(() => game.EffectSystem.LoadEffect(nameof(MacroInComposition), new CompilerParameters()));
                }
                finally
                {
                    GlobalLogger.GlobalMessageLogged -= Collect;
                }

                Assert.Contains(errors, x => x.Contains("SDSL0149: macro A is set inside the composition Slot.") && x.Contains(nameof(MacroInComposition)));
            });
        }
    }
}
