// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Storage;
using Stride.Shaders.Parsing;
using Stride.Shaders.Spirv.Building;

namespace Stride.Shaders.Compilers;

/// <summary>
/// A loader that finds sources by file name: <c>C.sdsl</c> for the class <c>A.B.C</c>, whose namespace is checked in the file.
/// All the files are read once, on the first lookup: a class can be in a file of another name, and a class of two files is SDSL0301.
/// </summary>
public abstract class SourceFileShaderLoader : ShaderLoaderBase
{
    private readonly Lazy<Dictionary<string, List<(string FileName, string Path)>>> filesOfClass;

    protected SourceFileShaderLoader(IShaderCache cache) : base(cache)
    {
        filesOfClass = new(ReadClassesOfFiles);
    }

    /// <summary>
    /// Reads <c>&lt;fileName&gt;.sdsl</c>, or returns false when there is no such file.
    /// </summary>
    protected abstract bool TryReadSourceFile(string fileName, out string filename, out string code, out ObjectId hash);

    /// <summary>
    /// The names, without extension, of the files <see cref="TryReadSourceFile"/> can read.
    /// </summary>
    protected abstract IEnumerable<string> ListSourceFiles();

    protected override bool ExternalFileExists(string name) => TryRead(name, out _, out _, out _);

    public override bool LoadExternalFileContent(string name, out string filename, out string code, out ObjectId hash)
        => TryRead(name, out filename, out code, out hash);

    public override IReadOnlyList<string> FindFullNames(string shortName)
        => [.. filesOfClass.Value.Keys.Where(x => ShaderNames.ShortName(x) == shortName)];

    private bool TryRead(string name, out string filename, out string code, out ObjectId hash)
    {
        // Before looking for a file: the one named after the class can be one of the two. A build reports the same mistake
        var declaredBy = filesOfClass.Value.GetValueOrDefault(name);
        if (declaredBy is { Count: > 1 })
            throw new InvalidOperationException(string.Format(SDSLErrorMessages.SDSL0301, name, declaredBy[0].Path, declaredBy[1].Path));

        var fileName = ShaderNames.ShortName(name);
        if (TryReadSourceFile(fileName, out filename, out code, out hash) && ShaderFileDeclarations.Holds(code, name))
            return true;

        if (declaredBy != null && TryReadSourceFile(declaredBy[0].FileName, out filename, out code, out hash) && ShaderFileDeclarations.Holds(code, name))
            return true;

        filename = code = "";
        hash = default;
        return false;
    }

    private Dictionary<string, List<(string FileName, string Path)>> ReadClassesOfFiles()
    {
        var result = new Dictionary<string, List<(string FileName, string Path)>>();
        foreach (var fileName in ListSourceFiles())
        {
            if (!TryReadSourceFile(fileName, out var path, out var code, out _))
                continue;

            foreach (var fullName in ShaderFileDeclarations.Read(code).Where(x => !x.IsEffect).Select(x => x.FullName).Distinct())
            {
                if (!result.TryGetValue(fullName, out var files))
                    result.Add(fullName, files = []);
                // Two listed names can lead to one file
                if (!files.Any(x => x.Path == path))
                    files.Add((fileName, path));
            }
        }
        return result;
    }
}
