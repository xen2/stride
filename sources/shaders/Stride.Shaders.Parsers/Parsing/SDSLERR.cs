namespace Stride.Shaders.Parsing;

/// <summary>
/// Error messages for SDSL
/// </summary>
public static class SDSLErrorMessages
{
    // Parse errors
    public const string SDSL0001 = "SDSL0001: Unexpected token";
    public const string SDSL0002 = "SDSL0002: vector size not supported";
    public const string SDSL0003 = "SDSL0003: matrix size not supported";
    public const string SDSL0004 = "SDSL0004: Unfinished vector declaration";
    public const string SDSL0005 = "SDSL0005: Too many values";
    public const string SDSL0006 = "SDSL0006: wrong vector size";
    public const string SDSL0007 = "SDSL0007: Expecting number or vector value";
    public const string SDSL0008 = "SDSL0008: Unfinished vector declaration";
    public const string SDSL0009 = "SDSL0009: Expected <EOF>";
    public const string SDSL0010 = "SDSL0010: Expected statement";
    public const string SDSL0011 = "SDSL0011: Expected statement or end of block";
    public const string SDSL0012 = "SDSL0012: Expected parameter or end of block";
    public const string SDSL0013 = "SDSL0013: Expected semi colon";
    public const string SDSL0014 = "SDSL0014: Expected assignment or semi colon";
    public const string SDSL0015 = "SDSL0015: Expected expression";
    public const string SDSL0016 = "SDSL0016: Expected at least one space";
    public const string SDSL0017 = "SDSL0017: Expected identifier";
    public const string SDSL0018 = "SDSL0018: Expected closing parenthesis";
    public const string SDSL0019 = "SDSL0019: Expected closing bracket";
    public const string SDSL0020 = "SDSL0020: Expected postfix expression";
    public const string SDSL0021 = "SDSL0021: Expected accessor expression";
    public const string SDSL0022 = "SDSL0022: Expected And expression";
    public const string SDSL0023 = "SDSL0023: Expected Or expression";
    public const string SDSL0024 = "SDSL0024: Expected BitWiseOr expression";
    public const string SDSL0025 = "SDSL0025: Expected BitWiseXor expression";
    public const string SDSL0026 = "SDSL0026: Expected BitWiseAnd expression";
    public const string SDSL0027 = "SDSL0027: Expected equality expression";
    public const string SDSL0028 = "SDSL0028: Expected relational expression";
    public const string SDSL0029 = "SDSL0029: Expected shift expression";
    public const string SDSL0030 = "SDSL0030: Expected additive expression";
    public const string SDSL0031 = "SDSL0031: Expected multiplicative expression";
    public const string SDSL0032 = "SDSL0032: Expected variable name";
    public const string SDSL0033 = "SDSL0033: Expected semi colon";
    public const string SDSL0034 = "SDSL0034: Expected closing chevron";
    public const string SDSL0035 = "SDSL0035: Expected open parenthesis";
    public const string SDSL0036 = "SDSL0036: Expected initializer expression";
    public const string SDSL0037 = "SDSL0037: Expected condition expression";
    public const string SDSL0038 = "SDSL0038: Expected increment expression";
    public const string SDSL0039 = "SDSL0039: Expected shader class or effect";
    public const string SDSL0040 = "SDSL0040: Expected body declaration";
    public const string SDSL0041 = "SDSL0041: Expected expression or semi colon";
    public const string SDSL0042 = "SDSL0042: Expected prefix expression";
    public const string SDSL0043 = "SDSL0043: Unexpected <EOF>";
    public const string SDSL0044 = "SDSL0044: Use of register and packoffset keyword deprecated";
    public const string SDSL0045 = "SDSL0045: `stage` is now `module`: write `module` (the Stride project upgrade does it)";


    // Semantic errors

    public const string SDSL0100 = "SDSL0100: Variable is not declared";
    public const string SDSL0101 = "SDSL0101: Variable is already declared";
    public const string SDSL0102 = "SDSL0102: Variable is not a constant";
    public const string SDSL0103 = "SDSL0103: Variable cannot be assigned to";
    public const string SDSL0104 = "SDSL0104: Cannot infer type";
    public const string SDSL0105 = "SDSL0105: Unrecognized node";
    public const string SDSL0106 = "SDSL0106: Unsupported type";
    public const string SDSL0107 = "SDSL0107: Binary expression between vector and matrix is not implemented";
    public const string SDSL0108 = "SDSL0108: Couldn't figure out type for binary operation between {0} and {1}";
    public const string SDSL0109 = "SDSL0109: Could not resolve method {0} in type {1}";
    public const string SDSL0110 = "SDSL0110: Use of undeclared identifier '{0}'";
    public const string SDSL0111 = "SDSL0111: Unimplemented: {0}";
    public const string SDSL0112 = "SDSL0112: Could not resolve member {0} in expression {1} of type {2}";
    public const string SDSL0113 = "SDSL0113: Could not resolve member {0} in structure of type {1}";
    public const string SDSL0114 = "SDSL0114: {0}.{1}({2}) at line {3} implements abstract method {4}.{1} but is not marked 'override'. Add the 'override' keyword.";
    public const string SDSL0115 = "SDSL0115: {0}.{1}({2}) at line {3} hides inherited method {4}.{1} but is not marked 'override'. Add the 'override' keyword, or rename the method.";
    public const string SDSL0116 = "SDSL0116: {0}.{1}({2}) at line {3} is marked 'override', but no inherited method {1} has this signature (inherited: {4}). Use the signature of the inherited method.";
    public const string SDSL0117 = "SDSL0117: {0} has {1} generic parameter(s) ({2}), but {3} given{4}. Give a value for each parameter; an effect log or a C# mixin tree written for an older version of the shader gives this error too: rebuild it.";
    public const string SDSL0118 = "SDSL0118: {0} declares generic parameter '{1}' of type {2} at line {3}. Texture and sampler generic parameters are not supported. Declare a texture or sampler member instead, or use a ComputeColorTexture...Sampler shader with a texture key.";

    // Definition checks: found when a class compiles, reported as warnings (errors with ShaderLoaderBase.WarningsAsErrors)

    public const string SDSL0200 = "SDSL0200: {0}.{1}() is marked 'override', but no inherited class declares {1}: it starts a new method, and calls made through a parent never reach it. Inherit the class that declares {1}, or remove 'override'.";
    public const string SDSL0201 = "SDSL0201: base.{1}() in {0}: no inherited class declares {1}() with this signature, so mixing {0} always fails. Inherit the class that declares it, or call {1}() without 'base'.";
    public const string SDSL0202 = "SDSL0202: {0} has {1} generic parameter(s) ({2}), but {3} given: the extra values are ignored. Remove them.";
    public const string SDSL0203 = "SDSL0203: [Color] on {0}, of type {1}: only float3 and float4 (or arrays of them) can be colors. Remove [Color], or change the type.";
}
