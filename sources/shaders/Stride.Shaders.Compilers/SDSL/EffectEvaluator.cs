using Stride.Core.Diagnostics;
using Stride.Core.Storage;
using Stride.Rendering;
using Stride.Shaders.Core;
using Stride.Shaders.Parsing;
using Stride.Shaders.Parsing.SDFX.AST;
using Stride.Shaders.Parsing.Analysis;
using Stride.Shaders.Spirv.Building;
using Stride.Shaders.Spirv.Core;
using Stride.Shaders.Spirv.Core.Buffers;
using static Stride.Shaders.Spirv.Specification;

namespace Stride.Shaders.Compilers.SDSL;

/// <summary>
/// Interprets SDFX effect bytecode (SPIR-V buffers containing OpEffectSDFX, OpMixinSDFX, etc.)
/// to produce ShaderMixinSource trees — replacing the generated C# IShaderMixinBuilder approach.
/// </summary>
public class EffectEvaluator(IExternalShaderLoader shaderLoader, ShaderSourceManager sourceManager, Dictionary<string, IShaderMixinBuilder> registeredBuilders)
{
    private static readonly Logger Log = GlobalLogger.GetLogger(nameof(EffectEvaluator));

    /// <summary>
    /// Evaluates an effect from a SPIR-V buffer containing SDFX instructions.
    /// Returns null if the effect is not available as bytecode.
    /// </summary>
    public ShaderMixinSource? Evaluate(string effectName, ParameterCollection compilerParameters)
    {
        var mixinTree = new ShaderMixinSource() { Name = effectName };
        var context = new ShaderMixinContext(mixinTree, compilerParameters, registeredBuilders);

        // Parse "RootEffect.ChildName" format
        var rootEffectName = effectName;
        var dotIndex = effectName.IndexOf('.');
        if (dotIndex >= 0)
        {
            context.ChildEffectName = effectName[(dotIndex + 1)..];
            rootEffectName = effectName[..dotIndex];
        }

        // Try to load the compiled effect from cache
        ShaderBuffers? shaderBuffers;
        if (shaderLoader.Cache.TryLoadFromCache(rootEffectName, null, [], out var cached, out _))
        {
            shaderBuffers = cached;
        }
        else
        {
            // Not in cache — try to compile the .sdfx source on demand
            shaderBuffers = CompileEffectOnDemand(rootEffectName);
            if (shaderBuffers == null)
                return null; // No .sdfx source found — let the caller try other paths
        }

        var buffers = shaderBuffers.Value;

        // Verify it's an SDFX effect
        if (buffers.Buffer.Count == 0 || buffers.Buffer[0].Op != Op.OpEffectSDFX)
            return null; // Not an SDFX effect — this is a shader class, not an effect

        InterpretEffect(buffers, context, mixinTree);

        return mixinTree;
    }

    /// <summary>
    /// Compiles an .sdfx file on demand and registers the result in the cache.
    /// Returns null if the .sdfx source file is not found.
    /// </summary>
    private ShaderBuffers? CompileEffectOnDemand(string effectName)
    {
        var effectSource = sourceManager.LoadEffectSource(effectName);
        if (effectSource == null)
            return null;

        var source = effectSource.Value;
        var parsed = SDSLParser.Parse(source.Source);
        if (parsed.Errors.Count > 0)
        {
            foreach (var error in parsed.Errors)
                Log.Error(error.ToString());
            return null;
        }

        if (parsed.AST is not ShaderFile sf)
            return null;

        var declarations = sf.Namespaces.SelectMany(x => x.Declarations).Concat(sf.RootDeclarations);
        foreach (var declaration in declarations)
        {
            if (declaration is ShaderEffect effect)
            {
                var compiler = new CompilerUnit();
                var table = new SymbolTable(compiler.Context, shaderLoader);

                try
                {
                    effect.Compile(table, compiler);
                }
                catch (Exception e)
                {
                    Log.Error(e.Message, e);
                    return null;
                }

                var buffer = compiler.ToShaderBuffers();
                shaderLoader.Cache.RegisterShader(effect.Name.Name, null, [], buffer, source.Hash);

                if (effect.Name.Name == effectName)
                    return buffer;
            }
        }

        return null;
    }

    /// <summary>
    /// Walks the SPIR-V buffer and interprets SDFX instructions, calling ShaderMixinContext methods.
    /// </summary>
    private void InterpretEffect(ShaderBuffers shaderBuffers, ShaderMixinContext context, ShaderMixinSource mixin)
    {
        var buffer = shaderBuffers.Buffer;
        var contextBuffer = shaderBuffers.Context;
        var locals = new Dictionary<int, object>(); // Local variable table (SPIR-V ID → runtime value)
        int i = 0;
        var count = buffer.Count;

        while (i < count)
        {
            var instruction = buffer[i];

            switch (instruction.Op)
            {
                case Op.OpEffectSDFX:
                    // Effect header — skip
                    break;

                case Op.OpParamsUseSDFX:
                    // using params — tracked at compile time, no runtime action needed
                    break;

                case Op.OpMixinSDFX:
                {
                    var mixin_inst = (OpMixinSDFX)instruction;
                    var generics = DecodeGenerics(contextBuffer, mixin_inst.Generics, locals);

                    // Check if the mixin value is a runtime local (dynamic mixin from variable)
                    bool isDynamic = locals.TryGetValue(mixin_inst.Value, out var dynamicValue);

                    switch (mixin_inst.Kind)
                    {
                        case MixinKindSDFX.Default:
                            if (isDynamic)
                            {
                                if (dynamicValue is ShaderSource dynamicSource)
                                    context.Mixin(mixin, dynamicSource);
                                // null dynamic value = param not set, skip mixin
                            }
                            else
                                MixinByName(context, mixin, DecodeString(contextBuffer, mixin_inst.Value), generics);
                            break;

                        case MixinKindSDFX.Child:
                        {
                            var targetName = DecodeString(contextBuffer, mixin_inst.Target);
                            if (context.ChildEffectName == targetName)
                            {
                                MixinByName(context, mixin, DecodeString(contextBuffer, mixin_inst.Value), generics);
                                return; // Child effect found — stop processing
                            }
                            break;
                        }

                        case MixinKindSDFX.Remove:
                            context.RemoveMixin(mixin, DecodeString(contextBuffer, mixin_inst.Value));
                            break;

                        case MixinKindSDFX.Macro:
                        {
                            var macroName = DecodeString(contextBuffer, mixin_inst.Target);
                            // The value is either a loaded local or a constant
                            object macroValue;
                            if (isDynamic)
                                macroValue = dynamicValue!;
                            else
                                macroValue = DecodeConstantValue(contextBuffer, mixin_inst.Value);
                            mixin.AddMacro(macroName, macroValue);
                            break;
                        }

                        case MixinKindSDFX.ComposeSet:
                        {
                            var compositionName = DecodeString(contextBuffer, mixin_inst.Target);
                            var subMixin = new ShaderMixinSource();
                            context.PushComposition(mixin, compositionName, subMixin);
                            if (isDynamic)
                            {
                                if (dynamicValue is ShaderSource composeSource)
                                    context.Mixin(subMixin, composeSource);
                            }
                            else
                                MixinByName(context, subMixin, DecodeString(contextBuffer, mixin_inst.Value), generics);
                            context.PopComposition();
                            break;
                        }

                        case MixinKindSDFX.ComposeAdd:
                        {
                            var compositionName = DecodeString(contextBuffer, mixin_inst.Target);
                            var subMixin = new ShaderMixinSource();
                            context.PushCompositionArray(mixin, compositionName, subMixin);
                            if (isDynamic)
                            {
                                if (dynamicValue is ShaderSource composeAddSource)
                                    context.Mixin(subMixin, composeAddSource);
                            }
                            else
                                MixinByName(context, subMixin, DecodeString(contextBuffer, mixin_inst.Value), generics);
                            context.PopComposition();
                            break;
                        }

                        case MixinKindSDFX.Clone:
                            context.Mixin(mixin, (ShaderSource)mixin.Clone());
                            break;
                    }
                    break;
                }

                case Op.OpLoadParamSDFX:
                {
                    var loadInst = (OpLoadParamSDFX)instruction;
                    var paramKey = ResolveParameterKey(loadInst.ParamsType, loadInst.FieldName);
                    if (paramKey != null)
                    {
                        var value = GetParamDynamic(context, paramKey);
                        locals[loadInst.ResultId] = value;
                    }
                    break;
                }

                case Op.OpSetParamSDFX:
                {
                    var setInst = (OpSetParamSDFX)instruction;
                    var paramKey = ResolveParameterKey(setInst.ParamsType, setInst.FieldName);
                    if (paramKey != null)
                    {
                        object value;
                        if (locals.TryGetValue(setInst.Value, out var localValue))
                            value = localValue;
                        else
                            value = DecodeConstantValue(contextBuffer, setInst.Value);
                        SetParamDynamic(context, paramKey, value);
                    }
                    break;
                }

                case Op.OpDiscardSDFX:
                    context.Discard(); // throws ShaderMixinDiscardException
                    break;

                case Op.OpSelectionMerge:
                {
                    // Standard SPIR-V structured control flow
                    var selMerge = (OpSelectionMerge)instruction;
                    int mergeLabel = selMerge.MergeBlock;

                    // Next instruction should be OpBranchConditional
                    i++;
                    var branchCond = (OpBranchConditional)buffer[i];
                    bool conditionValue = EvaluateCondition(locals, branchCond.Condition);

                    int targetLabel = conditionValue ? branchCond.TrueLabel : branchCond.FalseLabel;

                    // Jump to the target label
                    i = FindLabel(buffer, count, i, targetLabel);
                    continue; // Don't increment i again
                }

                case Op.OpBranch:
                {
                    var branch = (OpBranch)instruction;
                    i = FindLabel(buffer, count, 0, branch.TargetLabel);
                    continue;
                }

                case Op.OpLabel:
                    // Label — just continue execution
                    break;

                case Op.OpForeachSDSL:
                {
                    var foreachInst = (OpForeachSDSL)instruction;
                    var collection = locals.TryGetValue(foreachInst.Collection, out var collObj) ? collObj : null;

                    if (collection is IEnumerable<ShaderSource> sources)
                    {
                        int bodyStart = i + 1;
                        int bodyEnd = FindForeachEnd(buffer, count, bodyStart);

                        foreach (var item in sources)
                        {
                            locals[foreachInst.ResultId] = item;
                            // Re-interpret the body for each element
                            InterpretRange(buffer, contextBuffer, context, mixin, locals, bodyStart, bodyEnd);
                        }

                        i = bodyEnd; // Skip past OpForeachEndSDSL
                    }
                    break;
                }

                case Op.OpForeachEndSDSL:
                    // Should not reach here during normal flow (handled by OpForeachSDSL)
                    break;

                case Op.OpPushParamsSDFX:
                {
                    var pushInst = (OpPushParamsSDFX)instruction;
                    if (locals.TryGetValue(pushInst.ParamsCollection, out var paramsObj) && paramsObj is ParameterCollection paramCollection)
                        context.PushParameters(paramCollection);
                    break;
                }

                case Op.OpPopParamsSDFX:
                    context.PopParameters();
                    break;
            }

            i++;
        }
    }

    /// <summary>
    /// Interprets a range of instructions [start, end) — used for loop bodies.
    /// </summary>
    private void InterpretRange(SpirvBuffer buffer, SpirvContext contextBuffer, ShaderMixinContext context,
        ShaderMixinSource mixin, Dictionary<int, object> locals, int start, int end)
    {
        // Create a temporary ShaderBuffers-like view and recurse through the same switch
        // For simplicity, reuse the main interpreter with index bounds
        int i = start;
        while (i < end)
        {
            var instruction = buffer[i];

            // Handle the same ops (simplified — only ops that appear in foreach bodies)
            switch (instruction.Op)
            {
                case Op.OpMixinSDFX:
                {
                    var mixin_inst = (OpMixinSDFX)instruction;
                    var generics = DecodeGenerics(contextBuffer, mixin_inst.Generics, locals);
                    bool isDynamic = locals.TryGetValue(mixin_inst.Value, out var dynamicValue);

                    switch (mixin_inst.Kind)
                    {
                        case MixinKindSDFX.Default:
                            if (isDynamic)
                            {
                                if (dynamicValue is ShaderSource dynamicSource)
                                    context.Mixin(mixin, dynamicSource);
                                // null dynamic value = param not set, skip mixin
                            }
                            else
                                MixinByName(context, mixin, DecodeString(contextBuffer, mixin_inst.Value), generics);
                            break;
                        case MixinKindSDFX.ComposeSet:
                        {
                            var compositionName = DecodeString(contextBuffer, mixin_inst.Target);
                            var subMixin = new ShaderMixinSource();
                            context.PushComposition(mixin, compositionName, subMixin);
                            if (isDynamic)
                            {
                                if (dynamicValue is ShaderSource composeSource)
                                    context.Mixin(subMixin, composeSource);
                            }
                            else
                                MixinByName(context, subMixin, DecodeString(contextBuffer, mixin_inst.Value), generics);
                            context.PopComposition();
                            break;
                        }
                        case MixinKindSDFX.ComposeAdd:
                        {
                            var compositionName = DecodeString(contextBuffer, mixin_inst.Target);
                            var subMixin = new ShaderMixinSource();
                            context.PushCompositionArray(mixin, compositionName, subMixin);
                            if (isDynamic)
                            {
                                if (dynamicValue is ShaderSource composeAddSource)
                                    context.Mixin(subMixin, composeAddSource);
                            }
                            else
                                MixinByName(context, subMixin, DecodeString(contextBuffer, mixin_inst.Value), generics);
                            context.PopComposition();
                            break;
                        }
                    }
                    break;
                }

                case Op.OpLoadParamSDFX:
                {
                    var loadInst = (OpLoadParamSDFX)instruction;
                    var paramKey = ResolveParameterKey(loadInst.ParamsType, loadInst.FieldName);
                    if (paramKey != null)
                        locals[loadInst.ResultId] = GetParamDynamic(context, paramKey);
                    break;
                }

                case Op.OpSelectionMerge:
                {
                    var selMerge = (OpSelectionMerge)instruction;
                    int mergeLabel = selMerge.MergeBlock;
                    i++;
                    var branchCond = (OpBranchConditional)buffer[i];
                    bool conditionValue = EvaluateCondition(locals, branchCond.Condition);
                    int targetLabel = conditionValue ? branchCond.TrueLabel : branchCond.FalseLabel;
                    i = FindLabel(buffer, end, i, targetLabel);
                    continue;
                }

                case Op.OpBranch:
                {
                    var branch = (OpBranch)instruction;
                    i = FindLabel(buffer, end, 0, branch.TargetLabel);
                    continue;
                }

                case Op.OpLabel:
                    break;

                case Op.OpPushParamsSDFX:
                {
                    var pushInst = (OpPushParamsSDFX)instruction;
                    if (locals.TryGetValue(pushInst.ParamsCollection, out var paramsObj) && paramsObj is ParameterCollection pc)
                        context.PushParameters(pc);
                    break;
                }

                case Op.OpPopParamsSDFX:
                    context.PopParameters();
                    break;
            }

            i++;
        }
    }

    #region Helpers

    private static void MixinByName(ShaderMixinContext context, ShaderMixinSource mixin, string name, object[] generics)
    {
        if (generics.Length > 0)
            context.Mixin(mixin, name, generics);
        else
            context.Mixin(mixin, name);
    }

    /// <summary>
    /// Decodes a string from a SPIR-V ID by looking up OpConstantStringSDSL in the context buffer.
    /// </summary>
    private static string DecodeString(SpirvContext context, int id)
    {
        if (id == 0) return string.Empty;

        foreach (var inst in context)
        {
            if (inst.Op == Op.OpConstantStringSDSL)
            {
                var strInst = (OpConstantStringSDSL)inst;
                if (strInst.ResultId == id)
                    return strInst.LiteralString;
            }
        }

        throw new InvalidOperationException($"Could not find OpConstantStringSDSL with ID {id}");
    }

    /// <summary>
    /// Decodes generic argument values from a LiteralArray of SPIR-V IDs.
    /// Values may be constants (in the context buffer) or runtime-resolved locals (from OpLoadParamSDFX).
    /// </summary>
    private static object[] DecodeGenerics(SpirvContext context, LiteralArray<int> genericIds, Dictionary<int, object>? locals = null)
    {
        if (genericIds.WordCount == 0) return [];

        var result = new object[genericIds.WordCount];
        int idx = 0;
        foreach (var word in genericIds.Words)
        {
            if (locals != null && locals.TryGetValue(word, out var localValue))
                result[idx++] = localValue;
            else
                result[idx++] = context.GetConstantValue(word);
        }
        return result;
    }

    /// <summary>
    /// Decodes a constant value (non-string) from the context buffer.
    /// </summary>
    private static object DecodeConstantValue(SpirvContext context, int id)
    {
        return context.GetConstantValue(id);
    }

    /// <summary>
    /// Evaluates a boolean condition from the local variable table.
    /// </summary>
    private static bool EvaluateCondition(Dictionary<int, object> locals, int conditionId)
    {
        if (locals.TryGetValue(conditionId, out var value))
        {
            return value switch
            {
                bool b => b,
                int n => n != 0,
                null => false,
                ShaderSource => true, // non-null shader source = true
                _ => Convert.ToBoolean(value),
            };
        }
        return false;
    }

    /// <summary>
    /// Finds the instruction index of a label in the buffer.
    /// </summary>
    private static int FindLabel(SpirvBuffer buffer, int count, int searchStart, int labelId)
    {
        for (int j = searchStart; j < count; j++)
        {
            if (buffer[j].Op == Op.OpLabel)
            {
                var label = (OpLabel)buffer[j];
                if (label.ResultId == labelId)
                    return j;
            }
        }
        throw new InvalidOperationException($"Label {labelId} not found in SPIR-V buffer");
    }

    /// <summary>
    /// Finds the OpForeachEndSDSL matching a foreach body start.
    /// </summary>
    private static int FindForeachEnd(SpirvBuffer buffer, int count, int bodyStart)
    {
        int depth = 1;
        for (int j = bodyStart; j < count; j++)
        {
            if (buffer[j].Op == Op.OpForeachSDSL) depth++;
            if (buffer[j].Op == Op.OpForeachEndSDSL)
            {
                depth--;
                if (depth == 0) return j;
            }
        }
        throw new InvalidOperationException("OpForeachEndSDSL not found");
    }

    /// <summary>
    /// Resolves a "ParamsType.FieldName" pair to a runtime ParameterKey using reflection.
    /// </summary>
    private static ParameterKey? ResolveParameterKey(string paramsType, string fieldName)
    {
        var keyName = $"{paramsType}.{fieldName}";

        lock (parameterKeyCache)
        {
            if (parameterKeyCache.TryGetValue(keyName, out var cached))
                return cached;
        }

        ParameterKey? resolved = null;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                foreach (var type in assembly.GetTypes())
                {
                    if (type.Name == paramsType)
                    {
                        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
                        var field = type.GetField(fieldName, flags);
                        if (field != null && typeof(ParameterKey).IsAssignableFrom(field.FieldType))
                        {
                            resolved = (ParameterKey)field.GetValue(null)!;
                            goto done;
                        }

                        var prop = type.GetProperty(fieldName, flags);
                        if (prop != null && typeof(ParameterKey).IsAssignableFrom(prop.PropertyType))
                        {
                            resolved = (ParameterKey)prop.GetValue(null)!;
                            goto done;
                        }
                    }
                }
            }
            catch
            {
                // Skip assemblies that can't be reflected
            }
        }

        done:
        lock (parameterKeyCache) { parameterKeyCache[keyName] = resolved; }
        return resolved;
    }

    // Cache resolved parameter keys to avoid repeated reflection
    private static readonly Dictionary<string, ParameterKey?> parameterKeyCache = new();

    /// <summary>
    /// Gets a parameter value dynamically via reflection on the ParameterKey generic type.
    /// </summary>
    private static object GetParamDynamic(ShaderMixinContext context, ParameterKey key)
    {
        // ShaderMixinContext.GetParam<T> requires the concrete generic type.
        // Use reflection to call it with the correct T.
        var getParamMethod = typeof(ShaderMixinContext).GetMethod("GetParam")!;
        var keyType = key.GetType();

        // Extract T from PermutationParameterKey<T>
        while (keyType != null && (!keyType.IsGenericType || keyType.GetGenericTypeDefinition() != typeof(PermutationParameterKey<>)))
            keyType = keyType.BaseType;

        if (keyType == null)
            return null!;

        var paramType = keyType.GetGenericArguments()[0];
        var genericMethod = getParamMethod.MakeGenericMethod(paramType);
        return genericMethod.Invoke(context, [key])!;
    }

    /// <summary>
    /// Sets a parameter value dynamically via reflection.
    /// </summary>
    private static void SetParamDynamic(ShaderMixinContext context, ParameterKey key, object value)
    {
        var setParamMethod = typeof(ShaderMixinContext).GetMethod("SetParam")!;
        var keyType = key.GetType();

        while (keyType != null && (!keyType.IsGenericType || keyType.GetGenericTypeDefinition() != typeof(PermutationParameterKey<>)))
            keyType = keyType.BaseType;

        if (keyType == null) return;

        var paramType = keyType.GetGenericArguments()[0];
        var genericMethod = setParamMethod.MakeGenericMethod(paramType);
        genericMethod.Invoke(context, [key, value]);
    }

    #endregion
}
