// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Linq;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Storage;
using Stride.Core.Serialization.Contents;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Shaders.Compiler;

namespace Stride.Shaders.Parsers.Tests;

/// <summary>
/// Issue #3392, shader reloading works only once. Models what the runtime does when a shader file
/// changes: <see cref="EffectCompilerBase.ResetCache"/> for the modified shader, then a recompile of
/// the effects that used it. Each edit in a row must produce its own bytecode.
/// </summary>
public class EffectReloadTests : IDisposable
{
    // The effect database and the SPIR-V cache both live in the application cache, which outlives the
    // test run, so a fresh shader name per run keeps this from depending on what an earlier run left.
    private readonly string shaderName = $"ReloadTest{Guid.NewGuid():N}";

    // Sources are looked up through a provider rather than with EffectCompiler.UseFileSystem, which
    // ShaderSourceManager only honours on Windows. The shaders live in a directory under the test
    // output so one mount covers both them and the engine's, and UrlToFilePath keeps the recorded
    // source path the real file, the way EffectCompileCommand sets it up for the asset pipeline.
    private readonly DirectoryInfo sourceDir;
    private readonly FileSystemProvider sourceProvider;
    private readonly DatabaseFileProvider database;
    private readonly EffectCompiler compiler;
    private readonly EffectCompilerCache cache;

    public EffectReloadTests()
    {
        sourceDir = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, $"reload-src-{Guid.NewGuid():N}"));
        sourceProvider = new FileSystemProvider($"/reloadtest-{Guid.NewGuid():N}", AppContext.BaseDirectory);

        database = new DatabaseFileProvider(ObjectDatabase.CreateDefaultDatabase());
        compiler = new EffectCompiler(sourceProvider) { ImplicitUsings = ShaderImplicitUsings.List };
        // Relative to the provider's base path: it resolves a url by appending it, mount point included.
        compiler.SourceDirectories.Add(sourceDir.Name);
        compiler.SourceDirectories.Add("assets/Stride/SDSL");
        cache = new EffectCompilerCache(compiler, database) { CompileEffectAsynchronously = false };
        compiler.UrlToFilePath[ShaderUrl] = ShaderPath;
    }

    private string ShaderPath => Path.Combine(sourceDir.FullName, $"{shaderName}.sdsl");

    private string ShaderUrl => $"{sourceDir.Name}/{shaderName}.sdsl";

    public void Dispose()
    {
        cache.Dispose();
        compiler.Dispose();
        database.Dispose();
        sourceProvider.Dispose();
        sourceDir.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    private string ShaderWithColor(string color) => $$"""
        shader {{shaderName}} : SpriteBase
        {
            module override float4 Shading()
            {
                return float4({{color}});
            }
        };
        """;

    private string InNamespace(string color) => $"namespace Reload.Test;\n{ShaderWithColor(color)}";

    private string FullName => $"Reload.Test.{shaderName}";

    [Fact]
    public void EveryEditOfAShaderInANamespaceIsSeen()
    {
        var red = CompileAfterEdit(InNamespace("1, 0, 0, 1"), FullName);
        var green = CompileAfterEdit(InNamespace("0, 1, 0, 1"), FullName);
        var blue = CompileAfterEdit(InNamespace("0, 0, 1, 1"), FullName);

        Assert.NotEqual(red, green);
        Assert.NotEqual(green, blue);
    }

    // A dependency in a namespace: its edit invalidates the class that was compiled with it
    [Fact]
    public void AnEditOfAParentInANamespaceIsSeen()
    {
        var parentName = $"ReloadParent{Guid.NewGuid():N}";
        var parentPath = Path.Combine(sourceDir.FullName, $"{parentName}.sdsl");
        string Parent(string color) => $$"""
            namespace Reload.Test.Parents;
            shader {{parentName}} : SpriteBase
            {
                module float4 Color() { return float4({{color}}); }
            };
            """;
        var child = $$"""
            namespace Reload.Test;
            using Reload.Test.Parents;
            shader {{shaderName}} : {{parentName}}
            {
                module override float4 Shading() { return Color(); }
            };
            """;

        File.WriteAllText(parentPath, Parent("1, 0, 0, 1"));
        var red = CompileAfterEdit(child, FullName);

        File.WriteAllText(parentPath, Parent("0, 1, 0, 1"));
        cache.ResetCache([$"Reload.Test.Parents.{parentName}"]);
        var green = CompileAfterEdit(child, FullName);

        Assert.NotEqual(red, green);
    }

    [Fact]
    public void AWatchedFileGivesTheFullNamesOfItsShaders()
    {
        var map = new ShaderSourceFileMap();
        map.Add(ShaderPath, FullName);
        map.Add(ShaderPath, $"Other.{shaderName}");

        var modified = new HashSet<string>();
        map.AddShadersOf(ShaderPath, modified);
        Assert.Equal([$"Other.{shaderName}", FullName], modified.Order());

        modified.Clear();
        map.AddShadersOf(Path.Combine(sourceDir.FullName, "SomethingElse.sdsl"), modified);
        Assert.Empty(modified);
    }

    private sealed class EffectIds(EffectCompilerBase compiler) : EffectCompilerChain(compiler)
    {
        public List<ObjectId> Ids { get; } = [];

        public override TaskOrResult<EffectBytecodeCompilerResult> Compile(EffectSource effect, EffectCompilerParameters effectParameters, CompilerParameters compilerParameters, ObjectId effectInputHash)
        {
            Ids.Add(effectInputHash);
            return base.Compile(effect, effectParameters, compilerParameters, effectInputHash);
        }
    }

    // One shader, one id: a class alone or in a tree
    [Fact]
    public void AShaderHasOneEffectId()
    {
        File.WriteAllText(ShaderPath, InNamespace("1, 0, 0, 1"));
        var ids = new EffectIds(compiler);
        var parameters = new CompilerParameters { EffectParameters = EffectCompilerParameters.Default with { Platform = GraphicsPlatform.Vulkan } };

        ids.Compile(new EffectSource(new ShaderClassSource(TestShader.FullName(FullName))), parameters);
        ids.Compile(new EffectSource(new ShaderMixinSource { Name = FullName, Mixins = { new ShaderClassSource(TestShader.FullName(FullName)) } }), parameters);

        Assert.Equal(2, ids.Ids.Count);
        Assert.Equal(ids.Ids[0], ids.Ids[1]);
    }

    [Fact]
    public void EveryShaderEditIsSeen()
    {
        var red = CompileAfterEdit(ShaderWithColor("1, 0, 0, 1"));
        var green = CompileAfterEdit(ShaderWithColor("0, 1, 0, 1"));
        var blue = CompileAfterEdit(ShaderWithColor("0, 0, 1, 1"));

        Assert.NotEqual(red, green);
        // The second reload is the one that used to be dropped: the effect's input hash doesn't change
        // when a shader file does, so the result compiled by the first reload was memoized forever.
        Assert.NotEqual(green, blue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailureIsLoggedOncePerShaderEdit(bool compileAsynchronously)
    {
        cache.CompileEffectAsynchronously = compileAsynchronously;
        var logged = new List<string>();
        void OnMessage(ILogMessage message)
        {
            if (message.Text.Contains(shaderName))
            {
                lock (logged)
                    logged.Add(message.Text);
            }
        }

        GlobalLogger.GlobalMessageLogged += OnMessage;
        try
        {
            var error = $"{ShaderPath}(5,";

            // The runtime retries a failed effect every few seconds: each retry fails, one is logged
            File.WriteAllText(ShaderPath, ShaderWithColor("1, 0, 0, 1) + (;"));
            cache.ResetCache([shaderName]);
            AssertRetryFails();
            AssertRetryFails();
            AssertRetryFails();
            Assert.StartsWith(error, Assert.Single(logged));

            // A new version with the same error is logged again
            File.WriteAllText(ShaderPath, ShaderWithColor("1, 0, 0, 1) + (;") + "\n");
            cache.ResetCache([shaderName]);
            AssertRetryFails();
            AssertRetryFails();
            Assert.Equal(2, logged.Count);
            Assert.Equal(logged[0], logged[1]);

            // And it recovers once fixed
            CompileAfterEdit(ShaderWithColor("0, 1, 0, 1"));
            Assert.Equal(2, logged.Count);
        }
        finally
        {
            GlobalLogger.GlobalMessageLogged -= OnMessage;
        }
    }

    private void AssertRetryFails()
    {
        var parameters = new CompilerParameters
        {
            EffectParameters = EffectCompilerParameters.Default with { Platform = GraphicsPlatform.Vulkan },
        };
        var results = cache.Compile(new EffectSource(new ShaderClassSource(shaderName)), parameters);
        Assert.True(results.Bytecode.WaitForResult().CompilationLog.HasErrors);
    }

    /// <summary>Writes a new version of the shader, then reloads it the way the runtime does.</summary>
    private byte[] CompileAfterEdit(string code, string? fullName = null)
    {
        File.WriteAllText(ShaderPath, code);
        cache.ResetCache([fullName ?? shaderName]);

        // Vulkan rather than the Direct3D11 default: it consumes SPIR-V directly, so this compiles on
        // every platform, where the D3D backend throws NotSupportedException off Windows.
        var parameters = new CompilerParameters
        {
            EffectParameters = EffectCompilerParameters.Default with { Platform = GraphicsPlatform.Vulkan },
        };
        var results = cache.Compile(new EffectSource(new ShaderClassSource(fullName ?? shaderName)), parameters);
        Assert.False(results.HasErrors, string.Join(Environment.NewLine, results.Messages.Select(m => m.Text)));

        var completed = results.Bytecode.WaitForResult();
        Assert.False(completed.CompilationLog.HasErrors,
            string.Join(Environment.NewLine, completed.CompilationLog.Messages.Select(m => m.Text)));
        return completed.Bytecode.Stages[0].Data;
    }
}
