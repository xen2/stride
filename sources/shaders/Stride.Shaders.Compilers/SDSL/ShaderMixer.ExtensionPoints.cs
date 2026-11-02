// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Text;
using Stride.Core.Diagnostics;
using Stride.Shaders.Core;
using Stride.Shaders.Parsing;
using Stride.Shaders.Parsing.SDSL.AST;
using Stride.Shaders.Spirv.Building;
using Stride.Shaders.Spirv.Core;
using Stride.Shaders.Spirv.Core.Buffers;
using static Stride.Shaders.Spirv.Specification;

namespace Stride.Shaders.Compilers.SDSL;

public partial class ShaderMixer
{
    /// <summary>
    /// Effect compiled at asset build from a wiring file. A contributor that drops others is then a warning (SDSL0216), and
    /// information in a mix built at run time, where replacing engine code is often the point.
    /// </summary>
    public bool IsWiredEffect { get; set; }

    /// <summary>
    /// Reads what the class wrote next to <see cref="OpFunctionMetadataSDSL"/>, into the method group of the function.
    /// </summary>
    private static void ReadExtensionPoint(SpirvBuffer buffer, int functionIndex, int functionId, MethodGroup methodGroup)
    {
        var index = functionIndex + 2;
        if (buffer[index].Op == Op.OpExtensionPointSDSL && (OpExtensionPointSDSL)buffer[index] is { } point)
        {
            methodGroup.ExtensionPoint ??= new(point.Kind, point.Owner, point.Phases.Split(',', StringSplitOptions.RemoveEmptyEntries), point.Parameters.Split(',', StringSplitOptions.RemoveEmptyEntries));
            index++;
        }
        if (buffer[index].Op == Op.OpContributionSDSL && (OpContributionSDSL)buffer[index] is { } contribution)
        {
            if (contribution.Phase.Length > 0)
                methodGroup.Phases[functionId] = contribution.Phase;
            if (contribution.After.Length > 0)
                methodGroup.After[functionId] = contribution.After.Split(',');
            if (contribution.Replaces.Length > 0)
                methodGroup.Replaces[functionId] = contribution.Replaces.Split(',');
            if (contribution.AddedPhase.Length > 0 && contribution.AddedPhase.Split(',') is [var after, var before])
                methodGroup.AddedPhases[functionId] = (contribution.Phase, after, before);
            if (contribution.Form == MethodExtensionPoint.ExtendForm)
                methodGroup.Extends.Add(functionId);
        }
    }

    /// <summary>
    /// Sorts the contributors by phase: a phase wins over inheritance and over the order of the mix. A contributor without a phase
    /// is in the implicit phase <c>External</c>, outside every declared phase, in the order of the mix. Inside a phase, <c>[After]</c>,
    /// then the order of the mix.
    /// </summary>
    private static void SortContributions(MixinGlobalContext globalContext, MixinNode mixinNode)
    {
        foreach (var group in mixinNode.MethodGroups.Values.Distinct())
        {
            if (group.ExtensionPoint == null)
                continue;

            // The lines of an effect are about the effect: its module methods
            if (mixinNode.IsRoot)
                ApplyWiring(globalContext, group);
            AddPhases(group);

            var point = group.ExtensionPoint;
            if (group.Phases.Count == 0 && group.After.Count == 0 && group.Replaces.Count == 0 && group.Replaced.Count == 0)
                continue;

            RemoveReplaced(globalContext.Table.CurrentFrame, group);
            CheckAfterAgainstPhases(group, point);

            // The first definition holds the default body: the chain starts there
            var sorted = group.Methods.Skip(1)
                .GroupBy(x => group.Phases.GetValueOrDefault(x.MethodId))
                .OrderBy(x => x.Key != null ? ChainRank(point, x.Key) : int.MaxValue)
                .SelectMany(x => FollowAfter(group, point, x.Key, x.ToList()))
                .ToList();
            group.Methods.RemoveRange(1, sorted.Count);
            group.Methods.AddRange(sorted);

            SortSymbols(globalContext.Table.CurrentFrame, group, sorted.Select(x => x.MethodId).ToList());
        }
    }

    private static string ClassName(ShaderInfo shader) => ShaderNames.WithoutGenerics(shader.FullName);

    // A line of an effect has a full name when its project could resolve it, else the name as written
    private static bool IsClass(string fullName, string written) => fullName == written || fullName.EndsWith($".{written}", StringComparison.Ordinal);

    /// <summary>
    /// The wiring lines of the effect about this method: what they name that isn't in the effect is ignored.
    /// </summary>
    private static void ApplyWiring(MixinGlobalContext globalContext, MethodGroup group)
    {
        var point = group.ExtensionPoint!;
        foreach (var line in globalContext.Wiring.Where(x => x.Method == group.Name && IsClass(ShaderNames.WithoutGenerics(point.Owner), x.Owner)))
        {
            var pointName = $"{ShaderNames.ShortName(point.Owner)}.{group.Name}";
            switch (line.Kind)
            {
                case ShaderWiringKind.Phase:
                    group.ExtensionPoint = AddPhase(group.ExtensionPoint!, pointName, "the effect", line.Phase, line.Names[0], line.Names[1]);
                    break;
                case ShaderWiringKind.Bind:
                    CheckPhase(group, pointName, line.Phase);
                    foreach (var contributor in group.Methods.Skip(1).Where(x => IsClass(ClassName(x.Shader), line.Names[0])))
                        group.Phases[contributor.MethodId] = line.Phase;
                    break;
                case ShaderWiringKind.Order:
                    CheckPhase(group, pointName, line.Phase);
                    // For one target, the last layer wins
                    group.OrderLines[line.Phase] = line.Names;
                    break;
                case ShaderWiringKind.Unbind:
                    if (line.Phase != null)
                        CheckPhase(group, pointName, line.Phase);
                    foreach (var contributor in group.Methods.Skip(1))
                    {
                        if (line.Phase != null ? group.Phases.GetValueOrDefault(contributor.MethodId) == line.Phase : IsClass(ClassName(contributor.Shader), line.Names[0]))
                            group.Replaced.TryAdd(contributor.MethodId, (contributor.Shader, null));
                    }
                    break;
            }
        }
    }

    private static void CheckPhase(MethodGroup group, string pointName, string phase)
    {
        if (!group.ExtensionPoint!.Phases.Contains(phase))
            throw new InvalidOperationException(string.Format(SDSLErrorMessages.SDSL0141, phase, "a line of the effect", $"{pointName} declares {string.Join(", ", group.ExtensionPoint.Phases)}"));
    }

    private static void AddPhases(MethodGroup group)
    {
        foreach (var (method, (phase, after, before)) in group.AddedPhases)
        {
            var contributor = group.Methods.Find(x => x.MethodId == method).Shader;
            group.ExtensionPoint = AddPhase(group.ExtensionPoint!, $"{ShaderNames.ShortName(group.ExtensionPoint!.Owner)}.{group.Name}", contributor.ShaderName, phase, after, before);
        }
    }

    /// <summary>
    /// A phase next to the ones the point has. It goes right after <paramref name="after"/>, else right before <paramref name="before"/>.
    /// </summary>
    private static MethodExtensionPoint AddPhase(MethodExtensionPoint point, string pointName, string by, string phase, string after, string before)
    {
        if (point.Phases.Contains(phase))
            return point;

        foreach (var neighbour in new[] { after, before })
        {
            if (neighbour.Length > 0 && !point.Phases.Contains(neighbour))
                throw new InvalidOperationException(string.Format(SDSLErrorMessages.SDSL0218, phase, by, pointName, neighbour));
        }

        var afterIndex = Array.IndexOf(point.Phases, after);
        var beforeIndex = Array.IndexOf(point.Phases, before);
        if (after.Length > 0 && before.Length > 0 && afterIndex > beforeIndex)
            throw new InvalidOperationException(string.Format(SDSLErrorMessages.SDSL0138, pointName, $"phase {phase} is added after {after} and before {before}, which comes first"));

        var index = after.Length > 0 ? afterIndex + 1 : beforeIndex;
        return point with { Phases = [.. point.Phases[..index], phase, .. point.Phases[index..]] };
    }

    /// <summary>
    /// Takes out of the chain what a contributor replaces, a class or a whole phase. Only this chain: the class still contributes
    /// to other methods. The first definition is not a contributor, and a contributor doesn't replace itself.
    /// </summary>
    private static void RemoveReplaced(SymbolFrame frame, MethodGroup group)
    {
        foreach (var (method, targets) in group.Replaces)
        {
            var by = group.Methods.Find(x => x.MethodId == method).Shader;
            foreach (var target in group.Methods.Skip(1).Where(x => x.MethodId != method))
            {
                var isTarget = targets.Contains(ClassName(target.Shader))
                    || (group.Phases.TryGetValue(target.MethodId, out var phase) && targets.Contains($"{MethodExtensionPoint.ReplacedPhasePrefix}{phase}"));
                if (isTarget)
                    group.Replaced.TryAdd(target.MethodId, (target.Shader, by));
            }
        }
        if (group.Replaced.Count == 0)
            return;

        foreach (var (method, (shader, by)) in group.Replaced)
        {
            if (by != null && group.Methods.Find(x => x.Shader == by).MethodId is var byMethod && group.Replaced.TryGetValue(byMethod, out var other) && other.By == shader)
                throw new InvalidOperationException(string.Format(SDSLErrorMessages.SDSL0138, group.Name, $"{by.ShaderName} and {shader.ShaderName} replace each other"));
        }

        group.Methods.RemoveAll(x => group.Replaced.ContainsKey(x.MethodId));
        // Entry points are found in the symbol table
        if (frame.TryGetValue(group.Name, out var symbol) && symbol.Type is FunctionGroupType)
            frame[group.Name] = symbol with { GroupMembers = symbol.GroupMembers.RemoveAll(x => group.Replaced.ContainsKey(x.IdRef)) };
    }

    /// <summary>
    /// A phase wins over <c>[After]</c>: a contributor can't run after a class of a phase that comes later, or of <c>External</c>,
    /// which comes after all of them.
    /// </summary>
    private static void CheckAfterAgainstPhases(MethodGroup group, MethodExtensionPoint point)
    {
        foreach (var (method, targets) in group.After)
        {
            if (!group.Phases.TryGetValue(method, out var phase))
                continue;

            var contributor = group.Methods.Find(x => x.MethodId == method).Shader;
            foreach (var target in group.Methods.Skip(1).Where(x => targets.Contains(ClassName(x.Shader))))
            {
                var targetPhase = group.Phases.GetValueOrDefault(target.MethodId);
                if (targetPhase == null || Array.IndexOf(point.Phases, targetPhase) > Array.IndexOf(point.Phases, phase))
                    throw new InvalidOperationException(string.Format(SDSLErrorMessages.SDSL0138, group.Name, $"{contributor.ShaderName} in phase {phase} runs after {target.Shader.ShaderName} in {targetPhase ?? "External"}, which comes later"));
            }
        }
    }

    /// <summary>
    /// The contributors of one phase in chain order: the order of the mix, moved only as far as <c>[After]</c> asks. A phase reached
    /// from the outside (<c>around</c>, or a pipeline step before the default body) runs from the end of the chain.
    /// </summary>
    private static IEnumerable<(ShaderInfo Shader, int MethodId, FunctionFlagsMask Flags)> FollowAfter(MethodGroup group, MethodExtensionPoint point, string? phase, List<(ShaderInfo Shader, int MethodId, FunctionFlagsMask Flags)> contributors)
    {
        var orderLine = phase != null ? group.OrderLines.GetValueOrDefault(phase) : null;
        if (orderLine == null && !contributors.Any(x => group.After.ContainsKey(x.MethodId)))
            return contributors;

        var fromOutside = IsReachedFromOutside(point, phase);
        var first = contributors.ToDictionary(x => x.MethodId, _ => new List<int>());
        void RunsAfter((ShaderInfo Shader, int MethodId, FunctionFlagsMask Flags) later, (ShaderInfo Shader, int MethodId, FunctionFlagsMask Flags) earlier)
        {
            if (fromOutside)
                first[earlier.MethodId].Add(later.MethodId);
            else
                first[later.MethodId].Add(earlier.MethodId);
        }

        // An order line is the strongest: [After] doesn't count between the classes it lists
        var listed = (orderLine ?? []).SelectMany(x => contributors.Where(y => IsClass(ClassName(y.Shader), x))).ToList();
        for (var i = 1; i < listed.Count; i++)
            RunsAfter(listed[i], listed[i - 1]);
        foreach (var contributor in contributors)
        {
            foreach (var target in contributors.Where(x => group.After.GetValueOrDefault(contributor.MethodId)?.Contains(ClassName(x.Shader)) == true))
            {
                if (!listed.Contains(contributor) || !listed.Contains(target))
                    RunsAfter(contributor, target);
            }
        }

        var sorted = new List<(ShaderInfo Shader, int MethodId, FunctionFlagsMask Flags)>();
        while (sorted.Count < contributors.Count)
        {
            var next = contributors.FindIndex(x => !sorted.Contains(x) && first[x.MethodId].All(y => sorted.Exists(z => z.MethodId == y)));
            if (next == -1)
                throw new InvalidOperationException(string.Format(SDSLErrorMessages.SDSL0138, group.Name, $"[After] goes in a circle between {string.Join(", ", contributors.Where(x => !sorted.Contains(x)).Select(x => x.Shader.ShaderName))}"));
            sorted.Add(contributors[next]);
        }
        return sorted;
    }

    private static bool IsReachedFromOutside(MethodExtensionPoint point, string? phase)
        => phase != null && (point.Kind != ExtensionPointKindSDSL.Pipeline || Array.IndexOf(point.Phases, phase) < point.DefaultPhaseIndex);

    /// <summary>
    /// Entry points are found in the symbol table, where the last method of a name wins: its order has to follow the chain.
    /// </summary>
    private static void SortSymbols(SymbolFrame frame, MethodGroup group, List<int> sortedIds)
    {
        if (!frame.TryGetValue(group.Name, out var symbol) || symbol.Type is not FunctionGroupType)
            return;

        var members = symbol.GroupMembers.ToArray();
        var byId = members.Where(x => sortedIds.Contains(x.IdRef)).ToDictionary(x => x.IdRef);
        var next = 0;
        for (var i = 0; i < members.Length; i++)
        {
            if (sortedIds.Contains(members[i].IdRef))
                members[i] = byId[sortedIds[next++]];
        }
        frame[group.Name] = symbol with { GroupMembers = [.. members] };
    }

    /// <summary>
    /// The place of a phase in the chain, from the default body outwards. A chain runs from its last method: a pipeline step after
    /// the default body calls <c>base</c> first, so these phases come first in their order, then the ones before the default
    /// body, which call <c>base</c> last, the earliest outermost. In an <c>around</c> point, the first phase is the outermost.
    /// </summary>
    private static int ChainRank(MethodExtensionPoint point, string phase)
    {
        var index = Array.IndexOf(point.Phases, phase);
        if (point.Kind != ExtensionPointKindSDSL.Pipeline)
            return point.Phases.Length - index;

        var defaultIndex = point.DefaultPhaseIndex;
        return index >= defaultIndex ? index - defaultIndex : point.Phases.Length - index;
    }

    /// <summary>
    /// The outermost contributor that doesn't reach the one before it, and the contributors that never run because of it. The
    /// first definition is not a contributor, and replacing what one of your own parents contributed is single inheritance.
    /// </summary>
    private static (ShaderInfo By, List<ShaderInfo> Dropped)? FindDropped(MethodGroup group, MixinGlobalContext globalContext)
    {
        for (var i = group.Methods.Count - 1; i > 0; --i)
        {
            if (globalContext.BaseCallTargets.Contains(group.Methods[i - 1].MethodId))
                continue;

            var by = group.Methods[i].Shader;
            var dropped = group.Methods.Take(i).Skip(1)
                .Where(x => (x.Flags & FunctionFlagsMask.Abstract) == 0 && !IsOwnParent(by, x.Shader))
                .Select(x => x.Shader)
                .ToList();
            return dropped.Count > 0 ? (by, dropped) : null;
        }
        return null;
    }

    // A module is imported, not inherited: the classes that import it don't replace it by being its children
    private static bool IsOwnParent(ShaderInfo shader, ShaderInfo parent)
    {
        if (parent.IsModule || shader.Source?.Buffer is not { } buffers)
            return false;

        var name = ShaderNames.WithoutGenerics(parent.FullName);
        var imports = new Dictionary<int, string>();
        foreach (var i in buffers.Context)
        {
            if (i.Op == Op.OpImportShaderSDSL && (OpImportShaderSDSL)i is { } import)
                imports[import.ResultId] = import.ShaderName;
            else if (i.Op == Op.OpMixinInheritSDSL && (OpMixinInheritSDSL)i is { } inherit && imports.GetValueOrDefault(inherit.Shader) == name)
                return true;
        }
        return false;
    }

    private static IEnumerable<MixinNode> NodesOf(MixinNode node)
    {
        yield return node;
        foreach (var composition in node.Compositions.Values.Concat(node.CompositionArrays.Values.SelectMany(x => x)))
        {
            foreach (var child in NodesOf(composition))
                yield return child;
        }
    }

    /// <summary>Two contributors of one phase whose order matters and that nothing orders. <paramref name="First"/> runs first today.</summary>
    private sealed record ContributionConflict(MethodGroup Group, string Phase, ShaderInfo First, ShaderInfo Second, string What);

    /// <summary>
    /// An effect from a wiring file has to state the order. In a mix built at run time, the order of the mix decides, and the log says so.
    /// </summary>
    private bool ReportContributionConflicts(List<ContributionConflict> conflicts, ILogger log)
    {
        var result = true;
        foreach (var conflict in conflicts)
        {
            var point = $"{ShaderNames.ShortName(conflict.Group.ExtensionPoint!.Owner)}.{conflict.Group.Name}";
            if (IsWiredEffect)
            {
                log.Error(string.Format(SDSLErrorMessages.SDSL0137, conflict.First.ShaderName, conflict.Second.ShaderName, conflict.Phase, point, conflict.What));
                result = false;
            }
            else
            {
                log.Info($"{point}, phase {conflict.Phase}: {conflict.First.ShaderName} runs before {conflict.Second.ShaderName} because of the order of the mix. They {conflict.What}.");
            }
        }
        return result;
    }

    /// <summary>
    /// Inside a phase, two contributors need an order only when one writes what the other uses. <c>External</c> follows the order of
    /// the mix, and code that never runs conflicts with nothing.
    /// </summary>
    private static List<ContributionConflict> FindContributionConflicts(MixinNode rootMixin, MixinGlobalContext globalContext, SpirvContext context, SpirvBuffer buffer)
    {
        var conflicts = new List<ContributionConflict>();
        Dictionary<int, int>? functions = null;
        foreach (var group in NodesOf(rootMixin).SelectMany(x => x.MethodGroups.Values).Distinct())
        {
            if (group.ExtensionPoint == null)
                continue;

            var running = new List<(ShaderInfo Shader, int MethodId, FunctionFlagsMask Flags)>();
            for (var i = group.Methods.Count - 1; i > 0; --i)
            {
                running.Add(group.Methods[i]);
                if (!globalContext.BaseCallTargets.Contains(group.Methods[i - 1].MethodId))
                    break;
            }

            foreach (var phase in running.Where(x => group.Phases.ContainsKey(x.MethodId)).GroupBy(x => group.Phases[x.MethodId]).Where(x => x.Count() > 1))
            {
                functions ??= FindFunctions(buffer);
                var chain = group.Methods.Select(x => x.MethodId).ToHashSet();
                var contributors = phase.Reverse().ToList();
                var accesses = contributors.Select(x => FindAccesses(context, buffer, functions, x.MethodId, chain)).ToList();
                for (var a = 0; a < contributors.Count; a++)
                {
                    for (var b = a + 1; b < contributors.Count; b++)
                    {
                        if (accesses[a].ConflictWith(accesses[b], context) is { } conflict && !IsOrdered(group, phase.Key, contributors, contributors[a].Shader, contributors[b].Shader))
                        {
                            var (first, second) = IsReachedFromOutside(group.ExtensionPoint, phase.Key) ? (contributors[b], contributors[a]) : (contributors[a], contributors[b]);
                            conflicts.Add(new(group, phase.Key, first.Shader, second.Shader, conflict));
                            group.DecidedByBuilder.Add(phase.Key);
                        }
                    }
                }
            }
        }
        return conflicts;
    }

    /// <summary>
    /// <c>[After]</c> orders a pair, also through a third contributor, and so does inheritance: a class comes after its parents.
    /// </summary>
    private static bool IsOrdered(MethodGroup group, string phase, List<(ShaderInfo Shader, int MethodId, FunctionFlagsMask Flags)> contributors, ShaderInfo a, ShaderInfo b)
    {
        if (group.OrderLines.GetValueOrDefault(phase) is { } line && line.Exists(x => IsClass(ClassName(a), x)) && line.Exists(x => IsClass(ClassName(b), x)))
            return true;

        return IsOwnParent(a, b) || IsOwnParent(b, a) || RunsAfter(a, b, []) || RunsAfter(b, a, []);

        bool RunsAfter(ShaderInfo later, ShaderInfo earlier, HashSet<string> visited)
        {
            var contributor = contributors.Find(x => x.Shader == later);
            foreach (var target in group.After.GetValueOrDefault(contributor.MethodId) ?? [])
            {
                if (target == ClassName(earlier))
                    return true;
                if (visited.Add(target) && contributors.Find(x => ClassName(x.Shader) == target) is { Shader: { } next } && RunsAfter(next, earlier, visited))
                    return true;
            }
            return false;
        }
    }

    private static Dictionary<int, int> FindFunctions(SpirvBuffer buffer)
    {
        var functions = new Dictionary<int, int>();
        for (var index = 0; index < buffer.Count; index++)
        {
            if (buffer[index].Op == Op.OpFunction)
                functions[((OpFunction)buffer[index]).ResultId] = index;
        }
        return functions;
    }

    /// <summary>
    /// What a contributor does that others can see: the streams its body and the functions it calls read and write, and side effects.
    /// </summary>
    private sealed class ContributionAccesses
    {
        public HashSet<int> Reads { get; } = [];
        public HashSet<int> Writes { get; } = [];

        /// <summary>A write to a buffer or a texture, an atomic operation, or a discard.</summary>
        public bool HasSideEffect { get; set; }

        /// <summary>
        /// The words of the message when one writes what the other reads or writes, null when their order doesn't matter.
        /// </summary>
        public string? ConflictWith(ContributionAccesses other, SpirvContext context)
        {
            var shared = Writes.Intersect(other.Writes).Union(Writes.Intersect(other.Reads)).Union(Reads.Intersect(other.Writes)).ToList();
            if (shared.Count > 0)
                return $"share {string.Join(", ", shared.Select(x => $"streams.{context.Names[x]}").Order(StringComparer.Ordinal))}, which one of them writes";
            return HasSideEffect && other.HasSideEffect ? "both have a side effect (a write to a buffer or a texture, or a discard)" : null;
        }
    }

    /// <summary>
    /// <paramref name="chain"/> holds the methods of the extension point: a <c>base</c> call reaches the next contributor, whose
    /// accesses are its own.
    /// </summary>
    private static ContributionAccesses FindAccesses(SpirvContext context, SpirvBuffer buffer, Dictionary<int, int> functions, int contributor, HashSet<int> chain)
    {
        var accesses = new ContributionAccesses();
        var visited = new HashSet<int>();
        Visit(contributor);
        return accesses;

        void Visit(int function)
        {
            if (!visited.Add(function) || !functions.TryGetValue(function, out var index))
                return;

            var streams = new HashSet<int>();
            var streamMembers = new Dictionary<int, int>();
            var bufferPointers = new HashSet<int>();
            for (; buffer[index].Op != Op.OpFunctionEnd; index++)
            {
                var i = buffer[index];
                switch (i.Op)
                {
                    case Op.OpStreamsSDSL:
                        streams.Add(((OpStreamsSDSL)i).ResultId);
                        break;
                    case Op.OpFunctionParameter when (OpFunctionParameter)i is var parameter && context.ReverseTypes[parameter.ResultType] is PointerType { BaseType: StreamsType }:
                        streams.Add(parameter.ResultId);
                        break;
                    case Op.OpVariable when (OpVariable)i is var variable && context.ReverseTypes[variable.ResultType] is PointerType { BaseType: StreamsType }:
                        streams.Add(variable.ResultId);
                        break;
                    case Op.OpAccessChain:
                        var access = (OpAccessChain)i;
                        if (streams.Contains(access.BaseId))
                            streamMembers[access.ResultId] = access.Indexes.Elements.Span[0];
                        else if (streamMembers.TryGetValue(access.BaseId, out var member))
                            streamMembers[access.ResultId] = member;
                        else if (context.ReverseTypes[access.ResultType] is PointerType { StorageClass: Spirv.Specification.StorageClass.StorageBuffer })
                            bufferPointers.Add(access.ResultId);
                        break;
                    case Op.OpLoad:
                        if (streamMembers.TryGetValue(((OpLoad)i).Pointer, out var read))
                            accesses.Reads.Add(read);
                        break;
                    case Op.OpStore:
                        var pointer = ((OpStore)i).Pointer;
                        if (streamMembers.TryGetValue(pointer, out var written))
                            accesses.Writes.Add(written);
                        else if (bufferPointers.Contains(pointer))
                            accesses.HasSideEffect = true;
                        break;
                    case Op.OpImageWrite or Op.OpKill or Op.OpTerminateInvocation or Op.OpDemoteToHelperInvocation
                        or Op.OpAtomicIAdd or Op.OpAtomicISub or Op.OpAtomicUMin or Op.OpAtomicUMax or Op.OpAtomicSMin or Op.OpAtomicSMax
                        or Op.OpAtomicAnd or Op.OpAtomicOr or Op.OpAtomicXor or Op.OpAtomicExchange or Op.OpAtomicCompareExchange
                        or Op.OpAtomicStore or Op.OpAtomicIIncrement or Op.OpAtomicIDecrement:
                        accesses.HasSideEffect = true;
                        break;
                    case Op.OpFunctionCall:
                        var callee = ((OpFunctionCall)i).Function;
                        if (!chain.Contains(callee))
                            Visit(callee);
                        break;
                }
            }
        }
    }

    private bool ReportDroppedContributors(MixinNode rootMixin, MixinGlobalContext globalContext, ILogger log)
    {
        var result = true;
        foreach (var group in NodesOf(rootMixin).SelectMany(x => x.MethodGroups.Values).Distinct())
        {
            if (group.ExtensionPoint == null || FindDropped(group, globalContext) is not var (by, dropped))
                continue;

            // The target syntax has to say it. The compat form stays what it always was.
            if (group.Extends.Contains(group.Methods.Find(x => x.Shader == by).MethodId))
            {
                var names = string.Join(", ", dropped.Select(x => ShaderNames.WithoutGenerics(x.ShaderName)));
                log.Error(string.Format(SDSLErrorMessages.SDSL0136, by.ShaderName, group.Name, names, names));
                result = false;
                continue;
            }

            var message = string.Format(SDSLErrorMessages.SDSL0216, by.ShaderName, group.Name, string.Join(", ", dropped.Select(x => x.ShaderName)));
            if (IsWiredEffect)
                log.Warning(message);
            else
                log.Info(message);
        }
        return result;
    }

    /// <summary>
    /// What ordered the contributors of a phase, when it has several: <c>after</c> for what <c>[After]</c> states, <c>builder</c> when
    /// the order of the mix decided a pair whose order matters, <c>tie-break</c> when it doesn't matter.
    /// </summary>
    private static string OrderSource(MethodGroup group, string phase)
    {
        var contributors = group.Methods.Where(x => group.Phases.GetValueOrDefault(x.MethodId) == phase).ToList();
        if (contributors.Count < 2)
            return "";

        var classes = contributors.Select(x => ClassName(x.Shader)).ToList();
        var sources = new List<string>();
        if (group.OrderLines.ContainsKey(phase))
            sources.Add("order line");
        else if (contributors.Any(x => group.After.GetValueOrDefault(x.MethodId)?.Any(classes.Contains) == true))
            sources.Add("after");
        if (group.DecidedByBuilder.Contains(phase))
            sources.Add("builder");
        return $"   order: {(sources.Count > 0 ? string.Join(", ", sources) : "tie-break")}";
    }

    /// <summary>
    /// The lines below a method line: the kind, the running order per phase, and what the chain drops.
    /// </summary>
    private static void ExplainExtensionPoint(StringBuilder builder, MethodGroup group, MixinGlobalContext globalContext)
    {
        var point = group.ExtensionPoint!;
        var kind = point.Kind.ToString().ToLowerInvariant();
        if (point.Kind == ExtensionPointKindSDSL.Replace)
        {
            builder.AppendLine($"    {kind}: {group.Methods[^1].Shader.ShaderName}");
        }
        else
        {
            builder.AppendLine($"    {kind}");
            var defaultPhase = point.Kind == ExtensionPointKindSDSL.Pipeline ? point.Phases[point.DefaultPhaseIndex] : null;
            foreach (var phase in point.Phases)
            {
                var names = group.Methods.Where(x => group.Phases.GetValueOrDefault(x.MethodId) == phase).Select(x => x.Shader.ShaderName);
                // The first definition sits in the phase of the default body, and an around point reaches it last
                if (phase == defaultPhase)
                    names = names.Prepend(group.Methods[0].Shader.ShaderName);
                builder.AppendLine($"    {phase}: {(names.Any() ? string.Join(" > ", names) : "(none)")}{OrderSource(group, phase)}");
            }
            if (defaultPhase == null)
                builder.AppendLine($"    default: {group.Methods[0].Shader.ShaderName}");

            var external = group.Methods.Skip(1).Where(x => !group.Phases.ContainsKey(x.MethodId)).ToList();
            if (external.Count > 0)
                builder.AppendLine($"    External: {string.Join(" > ", external.Select(x => x.Shader.ShaderName))}");
        }

        if (FindDropped(group, globalContext) is var (by, dropped))
            builder.AppendLine($"    dropped by {by.ShaderName}: {string.Join(", ", dropped.Select(x => x.ShaderName))}");
        foreach (var replaced in group.Replaced.Values.GroupBy(x => x.By))
            builder.AppendLine($"    {(replaced.Key != null ? $"replaced by {replaced.Key.ShaderName}" : "not bound by the effect")}: {string.Join(", ", replaced.Select(x => x.Shader.ShaderName))}");
    }
}
