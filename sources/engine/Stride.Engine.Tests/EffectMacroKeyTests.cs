// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Xunit;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Shaders;
using Stride.Shaders.Compiler;

namespace Stride.Engine.Tests
{
    /// <summary>
    /// The macros that depend on the render stage and on the material come from keys, and the effect writes them on its root.
    /// </summary>
    public class EffectMacroKeyTests
    {
        private static List<ShaderMacro> Generate(Action<CompilerParameters> configure)
        {
            RuntimeHelpers.RunModuleConstructor(typeof(StrideEffectBaseKeys).Module.ModuleHandle);
            var parameters = new CompilerParameters();
            configure(parameters);
            return ShaderMixinManager.Generate("Stride.Rendering.StrideEffectBase", parameters).Macros;
        }

        [Fact]
        public void TheRenderStageKeysBecomeMacros()
        {
            var macros = Generate(parameters =>
            {
                parameters.Set(StrideEffectBaseKeys.RenderTargetCount, 3);
                parameters.Set(StrideEffectBaseKeys.MultisampleCount, 4);
            });

            Assert.Contains(new ShaderMacro("STRIDE_RENDER_TARGET_COUNT", 3), macros);
            Assert.Contains(new ShaderMacro("STRIDE_MULTISAMPLE_COUNT", 4), macros);
        }

        [Fact]
        public void AKeyLeftAtZeroGivesNoMacro()
        {
            var macros = Generate(_ => { });

            Assert.DoesNotContain(macros, x => x.Name is "STRIDE_RENDER_TARGET_COUNT" or "STRIDE_MULTISAMPLE_COUNT" or "InputControlPointCount");
        }

        [Fact]
        public void TheControlPointsOfATessellationBecomeAMacro()
        {
            var macros = Generate(parameters =>
            {
                parameters.Set(MaterialKeys.TessellationShader, new ShaderMixinSource());
                parameters.Set(MaterialKeys.TessellationInputControlPoints, 12);
            });

            Assert.Contains(new ShaderMacro("InputControlPointCount", 12), macros);
        }
    }
}
