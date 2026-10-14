
using Stride.Shaders.Parsing;
using Stride.Shaders.Parsing.Analysis;
using Stride.Shaders.Parsing.SDSL.AST;
namespace Stride.Shaders.Parsers.Tests;

public class ParsingTests1
{
    public static IEnumerable<object[]> GetShaderFilePaths()
    {
        var files = Directory.GetFiles("assets/Stride/SDSL", "*.sdsl");
        foreach (var file in files)
        {
            yield return new object[] { file };
        }
        files = Directory.GetFiles("assets/Stride/SDFX", "*.sdfx");
        foreach (var file in files)
        {
            yield return new object[] { file };
        }
        files = Directory.GetFiles("assets/SDSL", "*.sdsl", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            yield return new object[] { file };
        }
    }

    [Theory]
    [MemberData(nameof(GetShaderFilePaths))]
    public void ParseFile(string path)
    {
        var text = MonoGamePreProcessor.OpenAndRun(path, []);
        var result = SDSLParser.Parse(text);
        Assert.True(result.Errors.Count == 0, path + string.Join("\n", result.Errors.Select(x => x.ToString())));
    }

    const string EveryKindOfMember = """
        shader S
        {
            {{keyword}} float A;
            {{keyword}} stream float B;
            {{keyword}} compose ComputeColor C;
            {{keyword}} SamplerState D { Filter = MIN_MAG_MIP_LINEAR; };
            cbuffer PerDraw { {{keyword}} float E; };
            rgroup PerMaterial { {{keyword}} Texture2D F; };
            {{keyword}} void G() { }
            float H;
        };
        """;

    // `module` marks a member of the shader's per-effect part
    [Fact]
    public void ModuleModifierMarksEveryKindOfMember()
    {
        var text = EveryKindOfMember.Replace("{{keyword}}", "module");
        var result = SDSLParser.Parse(text);
        Assert.True(result.Errors.Count == 0, string.Join("\n", result.Errors.Select(x => x.ToString())));

        var shader = Assert.IsType<ShaderClass>(Assert.Single(((ShaderFile)result.AST!).RootDeclarations));
        var staged = new Dictionary<string, bool>();
        foreach (var element in shader.Elements)
        {
            switch (element)
            {
                case ShaderMember member: staged[member.Name.Name] = member.IsStaged; break;
                case ShaderCompose compose: staged[compose.Name.Name] = compose.IsStaged; break;
                case ShaderSamplerState sampler: staged[sampler.Name.Name] = sampler.IsStaged; break;
                case ShaderMethod method: staged[method.Name.Name] = method.IsStaged; break;
                case ShaderBuffer buffer:
                    foreach (var member in buffer.Members)
                        staged[member.Name.Name] = member.IsStaged;
                    break;
            }
        }
        Assert.Equal(new Dictionary<string, bool> { ["A"] = true, ["B"] = true, ["C"] = true, ["D"] = true, ["E"] = true, ["F"] = true, ["G"] = true, ["H"] = false }, staged);
    }

    // `stage`, the old spelling of `module`, is an error that says the new one, once per place it is written
    [Fact]
    public void StageModifierIsAnError()
    {
        var result = SDSLParser.Parse(EveryKindOfMember.Replace("{{keyword}}", "stage"));
        var errors = result.Errors.Where(x => x.Message == SDSLErrorMessages.SDSL0045).ToList();
        Assert.Equal(7, errors.Count);
        Assert.Equal(7, errors.Select(x => x.Location.Position).Distinct().Count());
    }

    // `module X { }` is a module unit, and `import X;` an element of a class body
    [Fact]
    public void ModuleUnitAndImport()
    {
        var result = SDSLParser.Parse("""
            module M : Base<1>
            {
                import Other<2, PerMaterial>;
                float A;
            };
            shader S { import M; };
            """);
        Assert.True(result.Errors.Count == 0, string.Join("\n", result.Errors.Select(x => x.ToString())));
        var classes = ((ShaderFile)result.AST!).RootDeclarations.OfType<ShaderClass>().ToList();
        Assert.True(classes[0].IsModule);
        Assert.False(classes[1].IsModule);
        Assert.Equal(["Base<1>"], classes[0].Mixins.Select(x => x.ToString()));
        Assert.Equal("import Other<2,PerMaterial>", classes[0].Elements.OfType<ShaderImport>().Single().ToString());
        Assert.Equal("import M", classes[1].Elements.OfType<ShaderImport>().Single().ToString());
    }

    // Regression test: a trailing-dot float literal (e.g. "1.") is valid per the HLSL grammar
    // (https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx-graphics-hlsl-appendix-grammar#floating-point-numbers),
    // but a garbage character directly following it (e.g. "1.q") should still be reported as a parse error.
    [Fact]
    public void InvalidCharacterAfterTrailingDotFloatLiteralStillErrors()
    {
        var text = "shader Foo { void Bar() { float i = 1.q; } };";
        var result = SDSLParser.Parse(text);
        Assert.True(result.Errors.Count > 0, "Expected a parse error for invalid float suffix '1.q' but got none. Errors: " + string.Join("\n", result.Errors.Select(x => x.ToString())));
    }

    // A postfix '.' or '[' with nothing valid after it used to be consumed and silently dropped.
    [Theory]
    [InlineData("float i = a.;")]
    [InlineData("float i = a . ;")]
    [InlineData("float i = 1..;")]
    [InlineData("float i = 1.0.;")]
    [InlineData("float i = a.b.;")]
    [InlineData("float i = a[;")]
    [InlineData("float i = a[];")]
    [InlineData("float i = a[1;")]
    [InlineData("float i = a[1].;")]
    public void DanglingPostfixAccessorIsAnError(string statement)
    {
        var text = "shader Foo { void Bar() { float a = 0; " + statement + " } };";
        var result = SDSLParser.Parse(text);
        Assert.True(result.Errors.Count > 0, "Expected a parse error for '" + statement + "' but got none.");
    }

    [Theory]
    [InlineData("float i = a.x;")]
    [InlineData("float i = a . x;")]
    [InlineData("float i = a.b.c;")]
    [InlineData("float i = a[1];")]
    [InlineData("float i = a[1].x;")]
    [InlineData("float i = a[1][2];")]
    [InlineData("float i = a.Foo(1).x;")]
    [InlineData("float i = a++;")]
    [InlineData("float i = a[1]++;")]
    [InlineData("float i = 1.;")]
    public void PostfixAccessorChainParses(string statement)
    {
        var text = "shader Foo { void Bar() { float a = 0; " + statement + " } };";
        var result = SDSLParser.Parse(text);
        Assert.True(result.Errors.Count == 0, statement + "\n" + string.Join("\n", result.Errors.Select(x => x.ToString())));
    }

    // [Theory]
    // [MemberData(nameof(GetShaderFilePaths))]
    // public void AnalyseFile(string path)
    // {
    //     var text = MonoGamePreProcessor.OpenAndRun(path, []);
    //     var result = SDSLParser.Parse(text);
    //     Assert.True(result.Errors.Count == 0, path + string.Join("\n", result.Errors.Select(x => x.ToString())));
    // }
}
