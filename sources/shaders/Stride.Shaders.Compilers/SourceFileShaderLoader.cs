// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Storage;
using Stride.Shaders.Parsing;
using Stride.Shaders.Spirv.Building;

namespace Stride.Shaders.Compilers;

/// <summary>
/// A loader that finds sources by file name: <c>C.sdsl</c> for the class <c>A.B.C</c>, whose namespace is checked in the file.
/// </summary>
public abstract class SourceFileShaderLoader(IShaderCache cache) : ShaderLoaderBase(cache)
{
    /// <summary>
    /// Reads <c>&lt;fileName&gt;.sdsl</c>, or returns false when there is no such file.
    /// </summary>
    protected abstract bool TryReadSourceFile(string fileName, out string filename, out string code, out ObjectId hash);

    protected override bool ExternalFileExists(string name) => TryRead(name, out _, out _, out _);

    public override bool LoadExternalFileContent(string name, out string filename, out string code, out ObjectId hash)
        => TryRead(name, out filename, out code, out hash);

    public override IReadOnlyList<string> FindFullNames(string shortName)
        => TryReadSourceFile(shortName, out _, out var code, out _) ? [ShaderFileDeclarations.StorageName(code, shortName)] : [];

    private bool TryRead(string name, out string filename, out string code, out ObjectId hash)
    {
        var fileName = ShaderNames.ShortName(name);
        if (TryReadSourceFile(fileName, out filename, out code, out hash) && (fileName == name || ShaderFileDeclarations.Holds(code, fileName, name)))
            return true;

        filename = code = "";
        hash = default;
        return false;
    }
}
