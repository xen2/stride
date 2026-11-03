// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

// Prints the running order of an effect, as the game's assemblies give it.
using Stride.Rendering;
using Stride.Shaders;

foreach (var mixin in ShaderMixinManager.Generate("ShaderPipe.PipeEffect", new ParameterCollection()).Root.Mixins)
    System.Console.WriteLine($"MIXIN {mixin.ClassName}");
return 0;
