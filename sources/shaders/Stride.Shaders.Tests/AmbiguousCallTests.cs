// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Diagnostics;
using Stride.Shaders.Compilers.SDSL;

namespace Stride.Shaders.Parsers.Tests;

/// <summary>
/// A bare call that two unrelated classes could answer with one signature is an error, in a run-time mix and in an asset build.
/// </summary>
public class AmbiguousCallTests : IDisposable
{
    private const string UseHelper = "stream float4 ColorTarget : SV_Target0; void PSMain() { streams.ColorTarget = float4(Helper(1.0), 0, 0, 1); }";
    private const string A = "module A { float Helper(float x) { return x * 111.0; } }";
    private const string B = "module B { float Helper(float x) { return x * 222.0; } }";
    private const string PA = "shader PA { float Helper(float x) { return x * 111.0; } }";
    private const string PB = "shader PB { float Helper(float x) { return x * 222.0; } }";

    private readonly DirectoryInfo sourceDir = Directory.CreateTempSubdirectory("stride-shader-ambiguous");

    public void Dispose()
    {
        sourceDir.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    private (bool Ok, string Log, byte[] Bytecode) Mix(bool isAssetBuild, params string[] classes)
    {
        foreach (var code in classes)
        {
            var name = code.Split(' ', StringSplitOptions.RemoveEmptyEntries).SkipWhile(x => x is "module" or "shader" or "[Root]").First().Split('<')[0];
            File.WriteAllText(Path.Combine(sourceDir.FullName, $"{name}.sdsl"), $"namespace Test;\n{code}\n");
        }

        var log = new LoggerResult();
        var mixer = new ShaderMixer(new ShaderLoader(sourceDir.FullName, "./assets/Stride/SDSL") { WarningsAsErrors = false }) { IsAssetBuild = isAssetBuild };
        var source = new ShaderMixinSource { Mixins = { new ShaderClassSource("Test.Root") } };
        var ok = mixer.MergeSDSL(new EffectSource(source), new ShaderMixer.Options(true), log, out var bytecode, out _, out _, out _);
        return (ok, string.Join("\n", log.Messages.Select(x => x.ToString())), bytecode.ToArray());
    }

    // The markers 111 and 222 in the functions that are called: an uncalled function stays in the module
    private static string Called(byte[] bytes)
    {
        var words = new uint[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, words, 0, words.Length * 4);
        var constants = new Dictionary<uint, float>();
        var uses = new Dictionary<uint, HashSet<float>>();
        var calls = new HashSet<uint>();
        var current = 0u;
        for (var i = 5; i < words.Length;)
        {
            var op = words[i] & 0xFFFF;
            var count = (int)(words[i] >> 16);
            if (op == 43 && count == 4)
                constants[words[i + 2]] = BitConverter.UInt32BitsToSingle(words[i + 3]);
            else if (op == 54)
                current = words[i + 2];
            else if (op == 56)
                current = 0;
            else if (op == 57)
                calls.Add(words[i + 3]);
            if (current != 0 && op != 54)
            {
                for (var w = 1; w < count; w++)
                {
                    if (constants.TryGetValue(words[i + w], out var value) && value is 111f or 222f)
                    {
                        if (!uses.TryGetValue(current, out var set))
                            uses[current] = set = [];
                        set.Add(value);
                    }
                }
            }
            i += count;
        }
        return string.Join(",", calls.Where(uses.ContainsKey).SelectMany(x => uses[x]).Distinct().Order());
    }

    [Theory]
    [InlineData(false, "A", "B", "shader Root { import A; import B; " + UseHelper + " }")]
    [InlineData(true, "A", "B", "shader Root { import A; import B; " + UseHelper + " }")]
    [InlineData(false, "B", "A", "shader Root { import B; import A; " + UseHelper + " }")]
    [InlineData(true, "B", "A", "shader Root { import B; import A; " + UseHelper + " }")]
    public void TwoImportsWithOneSignatureAreAnError(bool isAssetBuild, string first, string second, string root)
    {
        var (ok, log, _) = Mix(isAssetBuild, A, B, root);

        Assert.False(ok, log);
        Assert.Contains($"SDSL0248: The call Helper(float) in Root is ambiguous: {first} and {second} both declare it, and neither comes from the other, so only the order of the parents and imports would say which one runs. Write {first}.Helper(...) or {second}.Helper(...).", log);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdenticalBodiesAreAnErrorToo(bool isAssetBuild)
    {
        var same = "module B { float Helper(float x) { return x * 111.0; } }";

        var (ok, log, _) = Mix(isAssetBuild, A, same, $"shader Root {{ import A; import B; {UseHelper} }}");

        Assert.False(ok, log);
        Assert.Contains("SDSL0248: The call Helper(float) in Root is ambiguous: A and B both declare it", log);
    }

    [Theory]
    [InlineData(false, "PA", "PB")]
    [InlineData(true, "PA", "PB")]
    [InlineData(false, "PB", "PA")]
    [InlineData(true, "PB", "PA")]
    public void TwoParentsWithOneSignatureAreAnError(bool isAssetBuild, string first, string second)
    {
        var (ok, log, _) = Mix(isAssetBuild, PA, PB, $"shader Root : {first}, {second} {{ {UseHelper} }}");

        Assert.False(ok, log);
        Assert.Contains($"SDSL0248: The call Helper(float) in Root is ambiguous: {first} and {second} both declare it", log);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACallThroughThisIsABareCall(bool isAssetBuild)
    {
        var (ok, log, _) = Mix(isAssetBuild, PA, PB, "shader Root : PA, PB { stream float4 ColorTarget : SV_Target0; void PSMain() { streams.ColorTarget = float4(this.Helper(1.0), 0, 0, 1); } }");

        Assert.False(ok, log);
        Assert.Contains("SDSL0248: The call Helper(float) in Root is ambiguous: PA and PB both declare it", log);
    }

    // The return type isn't part of what a call says
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnotherReturnTypeIsTheSameSignature(bool isAssetBuild)
    {
        var other = "module B { int Helper(float x) { return 222; } }";

        var (ok, log, _) = Mix(isAssetBuild, A, other, $"shader Root {{ import A; import B; {UseHelper} }}");

        Assert.False(ok, log);
        Assert.Contains("SDSL0248: The call Helper(float) in Root is ambiguous: A and B both declare it", log);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EachClassCallsItsOwnImport(bool isAssetBuild)
    {
        var (ok, log, bytecode) = Mix(isAssetBuild, A, B,
            "module UseA { import A; float FromA() { return Helper(1.0); } }",
            "module UseB { import B; float FromB() { return Helper(1.0); } }",
            "shader Root { import UseA; import UseB; stream float4 ColorTarget : SV_Target0; void PSMain() { streams.ColorTarget = float4(FromA(), FromB(), 0, 1); } }");

        Assert.True(ok, log);
        Assert.Equal("111,222", Called(bytecode));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AQualifiedCallSaysWhichOne(bool isAssetBuild)
    {
        var (ok, log, bytecode) = Mix(isAssetBuild, A, B, "shader Root { import A; import B; stream float4 ColorTarget : SV_Target0; void PSMain() { streams.ColorTarget = float4(A.Helper(1.0), B.Helper(1.0), 0, 1); } }");

        Assert.True(ok, log);
        Assert.Equal("111,222", Called(bytecode));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EachClassCallsItsOwnParent(bool isAssetBuild)
    {
        var (ok, log, bytecode) = Mix(isAssetBuild, PA, PB,
            "shader UseA : PA { float FromA() { return Helper(1.0); } }",
            "shader UseB : PB { float FromB() { return Helper(1.0); } }",
            "shader Root : UseA, UseB { stream float4 ColorTarget : SV_Target0; void PSMain() { streams.ColorTarget = float4(FromA(), FromB(), 0, 1); } }");

        Assert.True(ok, log);
        Assert.Equal("111,222", Called(bytecode));
    }

    // The fix the message gives also works for the instance methods of two parents
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AQualifiedCallOfAParentSaysWhichOne(bool isAssetBuild)
    {
        var (ok, log, bytecode) = Mix(isAssetBuild, PA, PB, "shader Root : PA, PB { stream float4 ColorTarget : SV_Target0; void PSMain() { streams.ColorTarget = float4(PB.Helper(1.0), 0, 0, 1); } }");

        Assert.True(ok, log);
        Assert.Equal("222", Called(bytecode));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AClassOwnMethodIsNotAmbiguous(bool isAssetBuild)
    {
        var (ok, log, bytecode) = Mix(isAssetBuild, "shader Root { float Helper(float x) { return x * 111.0; } " + UseHelper + " }");

        Assert.True(ok, log);
        Assert.Equal("111", Called(bytecode));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnOverrideAgainstWhatItInheritsIsNotAmbiguous(bool isAssetBuild)
    {
        var (ok, log, bytecode) = Mix(isAssetBuild, PA,
            "shader PMid : PA { override float Helper(float x) { return x * 222.0; } }",
            $"shader Root : PMid {{ {UseHelper} }}");

        Assert.True(ok, log);
        Assert.Equal("222", Called(bytecode));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoOverridesOfOneMethodAreNotAmbiguous(bool isAssetBuild)
    {
        var (ok, log, _) = Mix(isAssetBuild, PA,
            "shader PLeft : PA { override float Helper(float x) { return base.Helper(x) + 1; } }",
            "shader PRight : PA { override float Helper(float x) { return base.Helper(x) + 2; } }",
            $"shader Root : PLeft, PRight {{ {UseHelper} }}");

        Assert.True(ok, log);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OneImportReachedTwiceIsNotAmbiguous(bool isAssetBuild)
    {
        var (ok, log, _) = Mix(isAssetBuild, A,
            "module Left { import A; float FromLeft() { return Helper(1.0); } }",
            "module Right { import A; float FromRight() { return Helper(2.0); } }",
            $"shader Root {{ import Left; import Right; {UseHelper} }}");

        Assert.True(ok, log);
    }

}
