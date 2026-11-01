// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;

namespace Stride.Rendering;

/// <summary>
/// The namespaces every shader and effect sees without a <c>using</c>, at the same level as the usings of its file.
/// Changing this list changes what shaders resolve: bump <c>ShaderCompilerVersion.Shader</c>.
/// </summary>
public static class ShaderImplicitUsings
{
    /// <summary>
    /// The namespaces, separated by commas. A constant, so that the effect generator can read it from the compilation.
    /// </summary>
    public const string Namespaces = "Stride.Rendering,Stride.Rendering.Materials,Stride.Rendering.Lights,Stride.Rendering.Images,Stride.Rendering.Shadows";

    /// <summary>
    /// The namespaces, as a list.
    /// </summary>
    public static readonly IReadOnlyList<string> List = Namespaces.Split(',');
}
