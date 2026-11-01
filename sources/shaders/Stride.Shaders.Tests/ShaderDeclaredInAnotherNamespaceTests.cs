// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Shaders.Compiler;
using Stride.Shaders.Compilers;
using Stride.Shaders.Spirv.Tools;
using Spv = Stride.Shaders.Spirv.Tools.Spv;

namespace Stride.Shaders.Parsers.Tests;

/// <summary>
/// A file whose classes live in several namespaces: each class is validated against that file, whichever namespace it is in.
/// A reload goes as the file watcher does it, with <see cref="ShaderSourceManager.DeleteObsoleteCache"/> for the file that changed, then a compile on the same loader.
/// </summary>
public class ShaderDeclaredInAnotherNamespaceTests : IDisposable
{
    private const string EngineNamespace = "Cache.Engine";
    private const string GameNamespace = "Cache.Game";

    // The SPIR-V cache lives in the application cache, which outlives the test run: fresh names per run.
    private readonly string target = $"CacheTarget{Guid.NewGuid():N}";
    private readonly string other = $"CacheOther{Guid.NewGuid():N}";
    private readonly string user = $"CacheUser{Guid.NewGuid():N}";

    private readonly DirectoryInfo sourceDir = Directory.CreateTempSubdirectory("stride-shader-namespace-src");
    private readonly FileSystemProvider sources;
    private readonly List<EffectCompiler> compilers = [];

    public ShaderDeclaredInAnotherNamespaceTests()
    {
        sources = new FileSystemProvider($"/shadersources-{Guid.NewGuid():N}", sourceDir.FullName);
        Write(target, TargetCode);
        Write(other, OtherCode);
        Write(user, UserCode);
    }

    public void Dispose()
    {
        foreach (var compiler in compilers)
            compiler.Dispose();
        sources.Dispose();
        sourceDir.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    private string TargetFullName => $"{EngineNamespace}.{target}";

    private string OtherFullName => $"{GameNamespace}.{other}";

    private string UserFullName => $"{EngineNamespace}.{user}";

    private string TargetCode => $$"""
        namespace {{EngineNamespace}};

        shader {{target}}
        {
            int TargetValue() { return 1; }
        }
        """;

    private string OtherClass => $$"""
        namespace {{GameNamespace}}
        {
            shader {{other}}
            {
                int OtherValue() { return 2; }
            }
        }
        """;

    private string OtherCode => OtherClass;

    private string OtherAlsoDeclaringTarget => $$"""
        {{OtherClass}}

        namespace {{EngineNamespace}}
        {
            shader {{target}}
            {
            }
        }
        """;

    private string OtherFixed => OtherClass + "\n// fixed\n";

    private string UserCode => $$"""
        namespace {{EngineNamespace}};

        shader {{user}} : {{target}}
        {
            int UserValue() { return TargetValue(); }
        }
        """;

    [Fact]
    public void ClassCompilesAgainOnceTheOtherFileIsFixed()
    {
        var session = NewSession();
        Load(session, TargetFullName);
        Reload(session, OtherAlsoDeclaringTarget);

        Reload(session, OtherFixed);

        AssertOriginal(session);
    }

    [Fact]
    public void ClassCompilesAgainWithANewCacheInstanceOnceTheOtherFileIsFixed()
    {
        var session = NewSession();
        Load(session, TargetFullName);
        Reload(session, OtherAlsoDeclaringTarget);

        Reload(session, OtherFixed);

        AssertOriginal(NewSession());
    }

    [Fact]
    public void ClassCompilesAgainWithANewCacheInstanceWhenTheOtherFileIsFixedWhileClosed()
    {
        var session = NewSession();
        Load(session, TargetFullName);
        Reload(session, OtherAlsoDeclaringTarget);

        Write(other, OtherFixed);

        AssertOriginal(NewSession());
    }

    // A pasted copy holds only the other class, byte for byte, so both files have the same hash and only the name beside it tells them apart
    [Fact]
    public void PastedCopyLeavesNoWarningOnTheOriginalOnceItIsFixed()
    {
        var session = NewSession();
        Load(session, TargetFullName);

        Write(other, TargetCode);
        session.SourceManager.DeleteObsoleteCache([OtherFullName, $"{EngineNamespace}.{other}"]);
        Load(session, $"{EngineNamespace}.{other}");

        Write(other, OtherFixed);
        session.SourceManager.DeleteObsoleteCache([OtherFullName, $"{EngineNamespace}.{other}"]);
        Load(session, OtherFullName);

        var log = new LoggerResult();
        var newSession = NewSession();
        newSession.Log = log;
        AssertOriginal(newSession);
        Assert.DoesNotContain(log.Messages, x => x.Text.Contains("SDSL0245"));
    }

    [Fact]
    public void SecondClassOfAFileInAnotherNamespaceStaysUsable()
    {
        var second = $"CacheSecond{Guid.NewGuid():N}";
        var secondFullName = $"{EngineNamespace}.{second}";
        Write(other, $$"""
            {{OtherClass}}

            namespace {{EngineNamespace}}
            {
                shader {{second}}
                {
                    int SecondValue() { return 3; }
                }
            }
            """);

        var session = NewSession();
        Load(session, OtherFullName);
        Assert.Contains("SecondValue", Load(session, secondFullName));
        Assert.Contains("SecondValue", Load(NewSession(), secondFullName));
    }

    private void AssertOriginal(FileShaderLoader session)
    {
        Assert.Contains("TargetValue", Load(session, TargetFullName));
        Assert.Contains("UserValue", Load(session, UserFullName));
    }

    private void Write(string name, string code) => File.WriteAllText(Path.Combine(sourceDir.FullName, $"{name}.sdsl"), code);

    private void Reload(FileShaderLoader session, string code)
    {
        Write(other, code);
        session.SourceManager.DeleteObsoleteCache([OtherFullName]);
        Load(session, OtherFullName);
    }

    private static string Load(FileShaderLoader session, string name)
    {
        Assert.True(session.LoadExternalBuffer(name, [], out var buffer, out _, out _), $"could not load shader {name}");
        return Spv.Dis(buffer, DisassemblerFlags.Name);
    }

    /// <summary>The loader of a new <see cref="EffectCompiler"/>, with an empty in-memory cache over the application cache.</summary>
    private FileShaderLoader NewSession()
    {
        var compiler = new EffectCompiler(sources);
        compilers.Add(compiler);
        compiler.SourceDirectories.Add("");
        return compiler.GetFileShaderLoader();
    }
}
