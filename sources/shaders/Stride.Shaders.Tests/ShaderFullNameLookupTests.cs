// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.IO;
using Stride.Shaders.Compilers;
using Stride.Shaders.Parsing;

namespace Stride.Shaders.Parsers.Tests;

/// <summary>
/// How a full name finds its source: <c>shaders/A.B.C.sdsl</c> in a database, <c>dir/C.sdsl</c> with the namespace checked in a source directory.
/// </summary>
public class ShaderFullNameLookupTests : IDisposable
{
    private readonly DirectoryInfo sourceDir = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, $"lookup-src-{Guid.NewGuid():N}"));
    private readonly FileSystemProvider provider = new($"/lookuptest-{Guid.NewGuid():N}", AppContext.BaseDirectory);

    public void Dispose()
    {
        provider.Dispose();
        sourceDir.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    private ShaderSourceManager Manager()
    {
        var manager = new ShaderSourceManager(provider);
        manager.LookupDirectoryList.Add(sourceDir.Name);
        return manager;
    }

    private void WriteFile(string name, string code) => File.WriteAllText(Path.Combine(sourceDir.FullName, name), code);

    [Theory]
    [InlineData("C", "C")]
    [InlineData("A.B.C", "C")]
    [InlineData("A.B.C<3,X.Y>", "C")]
    public void ShortNames(string fullName, string shortName) => Assert.Equal(shortName, ShaderNames.ShortName(fullName));

    [Fact]
    public void GenericArgumentsAreNotANamespace()
    {
        Assert.Equal("A.B", ShaderNames.Namespace("A.B.C<3,X.Y>"));
        Assert.Null(ShaderNames.Namespace("C<3,X.Y>"));
        Assert.Equal("C<3,X.Y>", ShaderNames.ShortNameWithGenerics("A.B.C<3,X.Y>"));
    }

    [Fact]
    public void DeclarationsAreReadFromTheText()
    {
        var declarations = ShaderFileDeclarations.Read("""
            // namespace Comment; shader InComment { };
            /* shader InBlockComment { }; */
            using Some.Thing;
            [Root]
            shader Global<int N> : Parent<N> { float x; struct S { float y; }; };
            namespace A.B
            {
                using Other;
                [Attribute("shader InString")]
                internal partial module InBlock { };
                effect MyEffect { mixin InBlock; };
            }
            namespace C.D;
            params MyParams { bool X; };
            class FileScoped { void F() { if (true) { } } };
            """);

        Assert.Equal([
            new(null, "Global", false),
            new("A.B", "InBlock", false),
            new("A.B", "MyEffect", true),
            new("C.D", "FileScoped", false),
        ], declarations);
    }

    [Fact]
    public void AFileIsStoredUnderTheNamespaceOfItsClass()
    {
        Assert.Equal("C", ShaderFileDeclarations.StorageName("shader C { };", "C"));
        Assert.Equal("A.B.C", ShaderFileDeclarations.StorageName("namespace A.B; shader C { };", "C"));
        // The class named like the file wins, then the first class; the URL keeps the file name
        Assert.Equal("Y.C", ShaderFileDeclarations.StorageName("namespace X { shader D { }; } namespace Y { shader C { }; }", "C"));
        Assert.Equal("X.C", ShaderFileDeclarations.StorageName("namespace X { shader D { }; } namespace Y { shader E { }; }", "C"));
    }

    [Fact]
    public void TheManagerFindsAFileStoredUnderItsFullName()
    {
        WriteFile("A.B.Dotted.sdsl", "namespace A.B; shader Dotted { };");

        var manager = Manager();
        Assert.True(manager.IsClassExists("A.B.Dotted"));
        Assert.Contains("shader Dotted", manager.LoadShaderSource("A.B.Dotted").Source);
        Assert.False(manager.IsClassExists("A.C.Dotted"));
    }

    [Fact]
    public void TheManagerChecksTheNamespaceOfASourceFile()
    {
        WriteFile("Plain.sdsl", "namespace A.B; shader Plain { };");

        var manager = Manager();
        Assert.True(manager.IsClassExists("A.B.Plain"));
        Assert.Contains("shader Plain", manager.LoadShaderSource("A.B.Plain").Source);
        Assert.False(manager.IsClassExists("A.C.Plain"));
    }

    [Fact]
    public void TheManagerFindsAShortNameInEveryNamespace()
    {
        WriteFile("Twice.sdsl", "shader Twice { };");
        WriteFile("A.B.Twice.sdsl", "namespace A.B; shader Twice { };");
        WriteFile("X.Twice.sdsl", "namespace X; shader Twice { };");
        WriteFile("X.NotTwice.sdsl", "namespace X; shader NotTwice { };");

        Assert.Equal(["A.B.Twice", "Twice", "X.Twice"], Manager().FindFullNames("Twice").Order());
        Assert.Empty(Manager().FindFullNames("Nothing"));
    }

    [Fact]
    public void TheTestLoaderChecksTheNamespaceOfASourceFile()
    {
        WriteFile("InFile.sdsl", "namespace A.B; shader InFile { };");

        var loader = new ShaderLoader(sourceDir.FullName);
        Assert.True(loader.Exists("A.B.InFile"));
        Assert.False(loader.Exists("A.C.InFile"));
        Assert.Equal(["A.B.InFile"], loader.FindFullNames("InFile"));
    }
}
