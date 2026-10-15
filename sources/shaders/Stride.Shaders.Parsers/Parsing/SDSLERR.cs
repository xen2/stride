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
    public const string SDSL0046 = "SDSL0046: `mixin clone` was removed: it has had no effect since Stride 4.4. Delete the line.";
    public const string SDSL0047 = "SDSL0047: `mixin remove` was removed: it could not be compiled since Stride 4.4. Mix the shader in only where it is needed (for example under an `if`).";


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
    public const string SDSL0119 = "SDSL0119: {0} can only be mixed at the root of an effect ([Root] on it, or on a class it inherits or imports), but it is in the composition {1}. Mix it at the root, or remove [Root].";

    // Partial structs

    public const string SDSL0120 = "SDSL0120: {0}.{1} is declared by both {2} and {3}. Rename one, or move the field into a module both import.";
    public const string SDSL0121 = "SDSL0121: {0} extends struct {1}, but no partial struct {1} is visible. Import (or inherit) the class that declares 'partial struct {1}'.";
    public const string SDSL0122 = "SDSL0122: {0} extends struct {1}, but {2} declares {1} without 'partial', so it can't get more fields. Add 'partial' to its declaration in {2}, or rename this struct.";
    public const string SDSL0123 = "SDSL0123: {0} declares struct {1}, but {2} already declares 'partial struct {1}'. Write 'extend struct {1}' to add fields to it, or rename this struct.";
    public const string SDSL0124 = "SDSL0124: {0} sees two partial structs named {1}, declared by {2} and {3}. Rename one of them.";
    public const string SDSL0125 = "SDSL0125: {0} {1} has type {2}, which is or contains the partial struct {3}. The fields of a partial struct depend on the effect, so it can't be in streams, cbuffers, buffers or groupshared variables. Use a plain struct there.";
    public const string SDSL0126 = "SDSL0126: {0} declares 'partial struct {1}', but {0} is generic. A partial struct has one owner per effect: declare it in a class without generic parameters.";
    // SDSL0127: reserved (a field of a partial struct couldn't be a struct; lifted before the first release)
    public const string SDSL0128 = "SDSL0128: struct {0} contains itself: {1}. A struct can't contain itself, directly or through an array. Remove one of these fields.";

    // Push constants
    // SDSL0129-0130 are found when a class compiles, SDSL0131-0134 when an effect is mixed

    public const string SDSL0129 = "SDSL0129: {0}: member {2} of the [PushConstant] cbuffer {1} has type {3}. A push-constant member is an int, uint or float, or a 2- or 4-component vector of them (for example uint2 or float4). Change the type, or move the member to another cbuffer.";
    public const string SDSL0130 = "SDSL0130: {0}: the [PushConstant] cbuffer {1} has instance members, so each composition instance of {0} would get its own copy, but an effect has one push-constant block. Mark its members 'module', or mark {0} [Root] if it is only mixed at the root of an effect.";
    public const string SDSL0131 = "SDSL0131: the push constants of this effect take {0} bytes, more than the {1}-byte limit: {2}. Remove members, or move some of them to another cbuffer.";
    public const string SDSL0132 = "SDSL0132: cbuffer {0} is [PushConstant] in {1} but not in {2}. Add [PushConstant] in {2}, or rename one of them.";
    public const string SDSL0133 = "SDSL0133: on Direct3D 11, the push constants of this effect use register b{0}, but the effect has {1} other cbuffers, so b{0} is taken. Use fewer cbuffers, or remove [PushConstant].";
    public const string SDSL0134 = "SDSL0134: {0}: member {1} ({2}) is at byte {3} of the push-constant block, but a {2} must start at a multiple of {4} bytes, so that the C# struct given to SetPushConstants has the same layout on every graphics API. Move it to such an offset (add a member before it), or reorder the members (for example the vectors first).";

    // Definition checks: found when a class compiles, reported as warnings (errors with ShaderLoaderBase.WarningsAsErrors)

    public const string SDSL0200 = "SDSL0200: {0}.{1}() is marked 'override', but no inherited class declares {1}: it starts a new method, and calls made through a parent never reach it. Inherit the class that declares {1}, or remove 'override'.";
    public const string SDSL0201 = "SDSL0201: base.{1}() in {0}: no inherited class declares {1}() with this signature, so mixing {0} always fails. Inherit the class that declares it, or call {1}() without 'base'.";
    public const string SDSL0202 = "SDSL0202: {0} has {1} generic parameter(s) ({2}), but {3} given: the extra values are ignored. Remove them.";
    public const string SDSL0203 = "SDSL0203: [Color] on {0}, of type {1}: only float3 and float4 (or arrays of them) can be colors. Remove [Color], or change the type.";

    // Module rules: checked when a shader that is not root-only compiles (in a composition, the mixer reports them as errors)

    public const string SDSL0204 = "SDSL0204: {0}.{1}() is module code and reads {2}, an instance member of {3}. If {0} is used in a composition, each instance has its own {2}. Mark {2} 'module', or mark {0} [Root] if it is only mixed at the root of an effect.";
    public const string SDSL0205 = "SDSL0205: {0}.{1}() is module code and calls {3}.{2}(), an instance method. If {0} is used in a composition, each instance has its own {2}(). Mark {3}.{2} 'module', or mark {0} [Root] if it is only mixed at the root of an effect.";
    public const string SDSL0206 = "SDSL0206: {0}.{1}() overrides the module method {2}.{1} but is not 'module'. If {0} is used in a composition, a module method has one override chain per effect. Write 'override module', or mark {0} [Root] if it is only mixed at the root of an effect.";
    public const string SDSL0207 = "SDSL0207: {0}.{1}() is 'module' but overrides the instance method {2}.{1}. If {0} is used in a composition, an instance method has one override chain per instance. Remove 'module', mark {2}.{1} 'module', or mark {0} [Root] if it is only mixed at the root of an effect.";
    public const string SDSL0208 = "SDSL0208: base.{1}() in {0}.{2}(): {1} is a module method, called from instance code. If {0} is used in a composition, the call fails. Make {2} 'module' ('override module' if it is an override), or mark {0} [Root] if it is only mixed at the root of an effect.";

    // [Pure] modules and old syntax

    public const string SDSL0209 = "SDSL0209: [Pure] module {0} {1}: a pure module has functions and constants only. Remove [Pure], or move the state to another module.";
    public const string SDSL0210 = "SDSL0210: [Pure] module {0} uses {1}, which is not a pure module. Mark {1} [Pure] if it can be, or remove [Pure] from {0}.";
    public const string SDSL0211 = "SDSL0211: [Pure] on shader {0}: only a module can be pure. Remove [Pure], or make {0} a module.";
    public const string SDSL0212 = "SDSL0212: {0}.{1}(): 'clone' has no effect since Stride 4.4. Remove it.";
    public const string SDSL0213 = "SDSL0213: [Pure] module {0}: {1} in {2}() works only in a pixel shader, and a pure module can run in any stage. Compute the value in the caller and pass it as a parameter, or remove [Pure] from {0}.";
    public const string SDSL0214 = "SDSL0214: 'partial' on {0} has no meaning and is ignored. Remove it.";
    public const string SDSL0215 = "SDSL0215: {0} sees two structs named {1}, declared by {2} and {3}: {1} means the one of {2} (the first inherited or imported). Rename one of them.";

    // A source loaded at run time (not imported by the asset pipeline) written for an older Stride version
    public const string SDSL0216 = "SDSL0216: {0} is written for an older Stride version ({1}); it was upgraded when it was loaded. Update the source: ShaderUpgrader.UpgradeSource gives the new text.";
}
