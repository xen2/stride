using Stride.Rendering;
using Stride.Shaders.Core;
using Stride.Shaders.Spirv.Building;
using Stride.Shaders.Spirv.Core;
using Stride.Shaders.Spirv.Core.Buffers;
using static Stride.Shaders.Spirv.Specification;

namespace Stride.Shaders.Compilers.SDSL;

/// <summary>
/// Interprets SDFX effect bytecode (SPIR-V buffers containing OpEffectSDFX, OpMixinSDFX, etc.)
/// to produce ShaderMixinSource trees — replacing the generated C# IShaderMixinBuilder approach.
/// </summary>
internal class EffectEvaluator(IExternalShaderLoader shaderLoader, Dictionary<string, IShaderMixinBuilder> registeredBuilders)
{
    /// <summary>
    /// Evaluates an effect from a SPIR-V buffer containing SDFX instructions.
    /// </summary>
    public ShaderMixinSource Evaluate(string effectName, ParameterCollection compilerParameters)
    {
        var mixinTree = new ShaderMixinSource();
        var context = new ShaderMixinContext(mixinTree, compilerParameters, registeredBuilders);

        // Parse "RootEffect.ChildName" format
        var dotIndex = effectName.IndexOf('.');
        if (dotIndex >= 0)
        {
            context.ChildEffectName = effectName[(dotIndex + 1)..];
            effectName = effectName[..dotIndex];
        }

        // Load the effect's SPIR-V buffer
        var shaderBuffers = shaderLoader.Cache.TryLoadFromCache(effectName, null, [], out var buffer, out _)
            ? buffer
            : throw new InvalidOperationException($"Effect '{effectName}' not found in shader cache");

        // Verify it's an SDFX effect
        if (shaderBuffers.Buffer.Count == 0 || shaderBuffers.Buffer[0].Op != Op.OpEffectSDFX)
            throw new InvalidOperationException($"'{effectName}' is not an SDFX effect (first instruction is not OpEffectSDFX)");

        InterpretEffect(shaderBuffers, context, mixinTree);

        return mixinTree;
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
                    var name = DecodeString(contextBuffer, mixin_inst.Value);
                    var generics = DecodeGenerics(contextBuffer, mixin_inst.Generics);

                    switch (mixin_inst.Kind)
                    {
                        case MixinKindSDFX.Default:
                            if (generics.Length > 0)
                                context.Mixin(mixin, name, generics);
                            else
                                context.Mixin(mixin, name);
                            break;

                        case MixinKindSDFX.Child:
                        {
                            var targetName = DecodeString(contextBuffer, mixin_inst.Target);
                            if (context.ChildEffectName == targetName)
                            {
                                if (generics.Length > 0)
                                    context.Mixin(mixin, name, generics);
                                else
                                    context.Mixin(mixin, name);
                                return; // Child effect found — stop processing
                            }
                            break;
                        }

                        case MixinKindSDFX.Remove:
                            context.RemoveMixin(mixin, name);
                            break;

                        case MixinKindSDFX.Macro:
                        {
                            var macroName = DecodeString(contextBuffer, mixin_inst.Target);
                            // The value is either a loaded local or a constant
                            object macroValue;
                            if (locals.TryGetValue(mixin_inst.Value, out var localValue))
                                macroValue = localValue;
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
                            if (generics.Length > 0)
                                context.Mixin(subMixin, name, generics);
                            else
                                context.Mixin(subMixin, name);
                            context.PopComposition();
                            break;
                        }

                        case MixinKindSDFX.ComposeAdd:
                        {
                            var compositionName = DecodeString(contextBuffer, mixin_inst.Target);
                            var subMixin = new ShaderMixinSource();
                            context.PushCompositionArray(mixin, compositionName, subMixin);
                            if (generics.Length > 0)
                                context.Mixin(subMixin, name, generics);
                            else
                                context.Mixin(subMixin, name);
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
                    // Check if the value references a local (foreach variable)
                    string name;
                    if (locals.TryGetValue(mixin_inst.Value, out var localVal) && localVal is ShaderSource shaderSource)
                    {
                        context.Mixin(mixin, shaderSource);
                        i++;
                        continue;
                    }
                    name = DecodeString(contextBuffer, mixin_inst.Value);
                    var generics = DecodeGenerics(contextBuffer, mixin_inst.Generics);

                    switch (mixin_inst.Kind)
                    {
                        case MixinKindSDFX.Default:
                            if (generics.Length > 0)
                                context.Mixin(mixin, name, generics);
                            else
                                context.Mixin(mixin, name);
                            break;
                        case MixinKindSDFX.ComposeSet:
                        {
                            var compositionName = DecodeString(contextBuffer, mixin_inst.Target);
                            var subMixin = new ShaderMixinSource();
                            context.PushComposition(mixin, compositionName, subMixin);
                            if (generics.Length > 0)
                                context.Mixin(subMixin, name, generics);
                            else
                                context.Mixin(subMixin, name);
                            context.PopComposition();
                            break;
                        }
                        case MixinKindSDFX.ComposeAdd:
                        {
                            var compositionName = DecodeString(contextBuffer, mixin_inst.Target);
                            var subMixin = new ShaderMixinSource();
                            context.PushCompositionArray(mixin, compositionName, subMixin);
                            if (generics.Length > 0)
                                context.Mixin(subMixin, name, generics);
                            else
                                context.Mixin(subMixin, name);
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
    /// </summary>
    private static object[] DecodeGenerics(SpirvContext context, LiteralArray<int> genericIds)
    {
        if (genericIds.WordCount == 0) return [];

        var result = new object[genericIds.WordCount];
        int idx = 0;
        foreach (var word in genericIds.Words)
        {
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
        // Try to find a type with the given name that has a static ParameterKey field
        // Convention: paramsType is the class name (e.g. "MaterialKeys"), fieldName is the property (e.g. "DiffuseMap")
        var keyName = $"{paramsType}.{fieldName}";

        // Search all loaded assemblies for the parameter key
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                foreach (var type in assembly.GetTypes())
                {
                    if (type.Name == paramsType)
                    {
                        var field = type.GetField(fieldName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        if (field != null && typeof(ParameterKey).IsAssignableFrom(field.FieldType))
                            return (ParameterKey)field.GetValue(null)!;

                        var prop = type.GetProperty(fieldName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        if (prop != null && typeof(ParameterKey).IsAssignableFrom(prop.PropertyType))
                            return (ParameterKey)prop.GetValue(null)!;
                    }
                }
            }
            catch
            {
                // Skip assemblies that can't be reflected
            }
        }

        return null;
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
