// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Rendering;
using Stride.Shaders.Parsing;

namespace Stride.Shaders.Parsers.Tests;

/// <summary>
/// The full names of the shaders of the test assets, by short name: a test names most of them by the name of their file.
/// </summary>
static class TestShader
{
    private static readonly Lazy<Dictionary<string, string>> FullNames = new(() =>
    {
        var names = new Dictionary<string, string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "assets"), "*.sd*", SearchOption.AllDirectories))
        {
            foreach (var declaration in ShaderFileDeclarations.Read(File.ReadAllText(file)))
                names.TryAdd(declaration.Name, declaration.FullName);
        }
        return names;
    });

    /// <summary>
    /// The full name of a test or engine shader, or the name itself when no asset declares it: a test can write its own files.
    /// </summary>
    public static string FullName(string name) => FullNames.Value.GetValueOrDefault(name, name);

    /// <summary>
    /// The full name of a shader a test writes in <c>namespace Stride.Shaders.Tests</c>, or of an engine shader it uses.
    /// </summary>
    public static string Written(string name) => name.Contains('.') ? name : FullNames.Value.GetValueOrDefault(name, $"Stride.Shaders.Tests.{name}");

    /// <summary>
    /// The test effect of this name, or a shader of this name as an effect.
    /// </summary>
    public static EffectSource Effect(string name)
    {
        var fullName = FullName(name);
        return ShaderMixinManager.Contains(fullName) ? ShaderMixinManager.Generate(fullName, new ParameterCollection()) : new EffectSource(new ShaderClassSource(fullName));
    }
}
