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
    /// An effect the asset build compiles. A contributor that drops others is then a warning (SDSL0216), and
    /// information in a mix built at run time, where replacing engine code is often the point.
    /// </summary>
    public bool IsAssetBuild { get; set; }

    /// <summary>
    /// Two classes in their running order inside a phase. <paramref name="Source"/> is where it is written.
    /// </summary>
    private sealed record OrderPair(string Earlier, string Later, string Source);

    /// <summary>
    /// Reads what the class wrote next to <see cref="OpFunctionMetadataSDSL"/>, into the method group of the function.
    /// </summary>
    private static void ReadExtensionPoint(SpirvBuffer buffer, int functionIndex, int functionId, MethodGroup methodGroup)
    {
        var index = functionIndex + 2;
        if (buffer[index].Op == Op.OpExtensionPointSDSL && (OpExtensionPointSDSL)buffer[index] is { } point)
        {
            methodGroup.ExtensionPoint ??= new(point.Kind, point.Owner, point.Phases.Split(',', StringSplitOptions.RemoveEmptyEntries));
            index++;
        }
        if (buffer[index].Op == Op.OpContributionSDSL && (OpContributionSDSL)buffer[index] is { } contribution)
        {
            static string[] Names(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (contribution.Phase.Length > 0)
                methodGroup.Phases[functionId] = contribution.Phase;
            if (contribution.After.Length > 0)
                methodGroup.After[functionId] = Names(contribution.After);
            if (contribution.Before.Length > 0)
                methodGroup.Before[functionId] = Names(contribution.Before);
            if (contribution.Drops.Length > 0)
                methodGroup.Drops[functionId] = Names(contribution.Drops);
            methodGroup.Lines[functionId] = contribution.Line;
            // Only the first definition writes it
            methodGroup.MixinOrder.AddRange(Names(contribution.MixinOrder));
        }
    }

    /// <summary>
    /// Orders the contributors of each method of a node, in passes. A phase wins over inheritance and over the order of the mix. A
    /// contributor without a phase is in the implicit phase <c>External</c>, after every declared phase, in the order of the mix.
    /// </summary>
    private static void SortContributions(MixinGlobalContext globalContext, MixinNode mixinNode)
    {
        foreach (var group in mixinNode.MethodGroups.Values.Distinct())
        {
            if (group.ExtensionPoint == null)
                continue;
            if (group.Phases.Count == 0 && group.After.Count == 0 && group.Before.Count == 0 && group.Drops.Count == 0 && group.MixinOrder.Count == 0)
                continue;

            // 1. What [Drops] takes out
            RemoveReplaced(globalContext.Table.CurrentFrame, group);
            // 2. The running order inside each phase, then the chain
            OrderPhases(globalContext.Table.CurrentFrame, group);
        }
    }

    private static string ClassName(ShaderInfo shader) => ShaderNames.WithoutGenerics(shader.FullName);

    private static string PointName(MethodGroup group) => $"{ShaderNames.ShortName(group.ExtensionPoint?.Owner ?? group.Methods[0].Shader.ShaderName)}.{group.Name}";

    private static ShaderInfo ShaderOf(MethodGroup group, int method) => group.Methods.Find(x => x.MethodId == method).Shader;

    // Where a contributor is written: its class has the name of its file
    private static string ContributorSource(MethodGroup group, int method)
        => $"{ShaderNames.ShortName(ShaderNames.WithoutGenerics(ShaderOf(group, method).ShaderName))}.sdsl, line {group.Lines.GetValueOrDefault(method)}";

    /// <summary>
    /// Takes out of the chain what <c>[Drops]</c> names, a class or a whole phase. Only this chain: the class still contributes
    /// to other methods. The first definition is not a contributor, and a contributor doesn't drop itself.
    /// </summary>
    private static void RemoveReplaced(SymbolFrame frame, MethodGroup group)
    {
        bool IsNamed(IEnumerable<string> names, (ShaderInfo Shader, int MethodId, FunctionFlagsMask Flags) target)
            => names.Contains(ClassName(target.Shader)) || (group.Phases.TryGetValue(target.MethodId, out var phase) && names.Contains($"{MethodExtensionPoint.ReplacedPhasePrefix}{phase}"));

        var replacedBy = new Dictionary<ShaderInfo, List<ShaderInfo>>();
        foreach (var (method, targets) in group.Drops)
        {
            var by = ShaderOf(group, method);
            foreach (var target in group.Methods.Skip(1).Where(x => x.MethodId != method && IsNamed(targets, x)))
            {
                group.Replaced.TryAdd(target.MethodId, (target.Shader, by));
                if (!replacedBy.TryGetValue(by, out var replaced))
                    replacedBy[by] = replaced = [];
                replaced.Add(target.Shader);
            }
        }
        if (group.Replaced.Count == 0)
            return;

        foreach (var by in replacedBy.Keys)
        {
            if (FindCircle(by, by, []) is { } circle)
                throw new InvalidOperationException(string.Format(SDSLErrorMessages.SDSL0138, PointName(group), $"{string.Join(" and ", circle.Select(x => x.ShaderName))} drop each other"));
        }

        group.Methods.RemoveAll(x => group.Replaced.ContainsKey(x.MethodId));
        // Entry points are found in the symbol table
        if (frame.TryGetValue(group.Name, out var symbol) && symbol.Type is FunctionGroupType)
            frame[group.Name] = symbol with { GroupMembers = symbol.GroupMembers.RemoveAll(x => group.Replaced.ContainsKey(x.IdRef)) };

        List<ShaderInfo>? FindCircle(ShaderInfo from, ShaderInfo to, HashSet<ShaderInfo> visited)
        {
            foreach (var next in replacedBy.GetValueOrDefault(from) ?? [])
            {
                if (next == to)
                    return [from];
                if (visited.Add(next) && FindCircle(next, to, visited) is { } path)
                    return [from, .. path];
            }
            return null;
        }
    }

    /// <summary>
    /// The pairs that order contributors: <c>[MixinOrder(After = A, Before = B)]</c> of each contributor, and the list
    /// <c>[MixinOrder(A, B, C)]</c> of the first definition, which gives A before B and B before C once what isn't in the chain is left out.
    /// </summary>
    private static List<OrderPair> CollectPairs(MethodGroup group, List<(ShaderInfo Shader, int MethodId, FunctionFlagsMask Flags)> contributors)
    {
        var pairs = new List<OrderPair>();
        var names = contributors.Select(x => ClassName(x.Shader)).ToHashSet();
        foreach (var contributor in contributors)
        {
            var name = ClassName(contributor.Shader);
            var source = $"[MixinOrder] of {ContributorSource(group, contributor.MethodId)}";
            foreach (var earlier in (group.After.GetValueOrDefault(contributor.MethodId) ?? []).Where(x => names.Contains(x) && x != name))
                pairs.Add(new(earlier, name, source));
            foreach (var later in (group.Before.GetValueOrDefault(contributor.MethodId) ?? []).Where(x => names.Contains(x) && x != name))
                pairs.Add(new(name, later, source));
        }

        var listed = group.MixinOrder.Where(names.Contains).Distinct().ToList();
        var listSource = $"[MixinOrder] of {ShaderNames.ShortName(ShaderNames.WithoutGenerics(group.Methods[0].Shader.ShaderName))}.sdsl, line {group.Lines.GetValueOrDefault(group.Methods[0].MethodId)}";
        for (var i = 1; i < listed.Count; i++)
            pairs.Add(new(listed[i - 1], listed[i], listSource));
        return pairs;
    }

    /// <summary>
    /// Pairs that don't contradict all apply. Two opposite pairs are an error.
    /// </summary>
    private static List<OrderPair> MergePairs(MethodGroup group, List<OrderPair> pairs)
    {
        var merged = new List<OrderPair>();
        foreach (var pair in pairs)
        {
            if (pairs.Find(x => x.Earlier == pair.Later && x.Later == pair.Earlier) is { } opposite)
                throw new InvalidOperationException(string.Format(SDSLErrorMessages.SDSL0138, PointName(group), $"{ShaderNames.ShortName(pair.Earlier)} runs before {ShaderNames.ShortName(pair.Later)} ({pair.Source}) and after it ({opposite.Source})"));
            if (!merged.Contains(pair))
                merged.Add(pair);
        }
        return merged;
    }

    /// <summary>
    /// The running order inside each phase, from the pairs. A pair between two phases is an error, as is a cycle. What no pair orders keeps
    /// the order of the mix. Then the chain: phases in their place from the default body outwards.
    /// </summary>
    private static void OrderPhases(SymbolFrame frame, MethodGroup group)
    {
        var point = group.ExtensionPoint!;
        var contributors = group.Methods.Skip(1).ToList();
        var pairs = MergePairs(group, CollectPairs(group, contributors));
        string PhaseOf(string name) => contributors.Where(x => ClassName(x.Shader) == name).Select(x => group.Phases.GetValueOrDefault(x.MethodId)).FirstOrDefault() ?? MethodExtensionPoint.ImplicitPhase;
        foreach (var pair in pairs)
        {
            var (earlierPhase, laterPhase) = (PhaseOf(pair.Earlier), PhaseOf(pair.Later));
            if (earlierPhase != laterPhase)
                throw new InvalidOperationException(string.Format(SDSLErrorMessages.SDSL0138, PointName(group), $"{pair.Source} orders {ShaderNames.ShortName(pair.Earlier)}, in phase {earlierPhase}, and {ShaderNames.ShortName(pair.Later)}, in phase {laterPhase}. [MixinOrder] orders classes of one phase: the phases already order the others"));
        }
        group.Pairs.AddRange(pairs);

        // The first definition holds the default body: the chain starts there
        var sorted = contributors
            .GroupBy(x => group.Phases.GetValueOrDefault(x.MethodId))
            .OrderBy(x => x.Key != null ? ChainRank(point, x.Key) : int.MaxValue)
            .SelectMany(x => FollowPairs(group, point, x.Key, x.ToList()))
            .ToList();
        group.Methods.RemoveRange(1, sorted.Count);
        group.Methods.AddRange(sorted);

        SortSymbols(frame, group, sorted.Select(x => x.MethodId).ToList());
    }

    /// <summary>
    /// The contributors of one phase in chain order: the order of the mix, moved only as far as the pairs ask. A phase reached from the
    /// outside (<c>around</c>, or a pipeline step before the default body) runs from the end of the chain.
    /// </summary>
    private static IEnumerable<(ShaderInfo Shader, int MethodId, FunctionFlagsMask Flags)> FollowPairs(MethodGroup group, MethodExtensionPoint point, string? phase, List<(ShaderInfo Shader, int MethodId, FunctionFlagsMask Flags)> contributors)
    {
        var names = contributors.Select(x => ClassName(x.Shader)).ToHashSet();
        var pairs = group.Pairs.Where(x => names.Contains(x.Earlier) && names.Contains(x.Later)).ToList();
        if (pairs.Count == 0)
            return contributors;

        var fromOutside = IsReachedFromOutside(point, phase);
        var first = contributors.ToDictionary(x => x.MethodId, _ => new List<int>());
        foreach (var pair in pairs)
        {
            foreach (var earlier in contributors.Where(x => ClassName(x.Shader) == pair.Earlier))
            {
                foreach (var later in contributors.Where(x => ClassName(x.Shader) == pair.Later))
                {
                    if (fromOutside)
                        first[earlier.MethodId].Add(later.MethodId);
                    else
                        first[later.MethodId].Add(earlier.MethodId);
                }
            }
        }

        var sorted = new List<(ShaderInfo Shader, int MethodId, FunctionFlagsMask Flags)>();
        while (sorted.Count < contributors.Count)
        {
            var next = contributors.FindIndex(x => !sorted.Contains(x) && first[x.MethodId].All(y => sorted.Exists(z => z.MethodId == y)));
            if (next == -1)
            {
                var left = contributors.Where(x => !sorted.Contains(x)).Select(x => ClassName(x.Shader)).ToHashSet();
                var circle = pairs.Where(x => left.Contains(x.Earlier) && left.Contains(x.Later)).Select(x => $"{ShaderNames.ShortName(x.Earlier)} before {ShaderNames.ShortName(x.Later)} ({x.Source})");
                throw new InvalidOperationException(string.Format(SDSLErrorMessages.SDSL0138, PointName(group), $"these orders go in a circle: {string.Join("; ", circle)}"));
            }
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

    /// <summary>
    /// Inside a phase, two contributors need an order when one writes what the other uses, and two wrappers of an <c>around</c> point
    /// always do, as each one wraps the other. An effect of the asset build has to state it. <c>External</c> follows the order of the mix,
    /// and code that never runs conflicts with nothing.
    /// </summary>
    private static bool CheckContributionConflicts(MixinNode rootMixin, MixinGlobalContext globalContext, SpirvContext context, SpirvBuffer buffer, ILogger log)
    {
        var result = true;
        Dictionary<int, int>? functions = null;
        Dictionary<int, string>? semantics = null;
        Dictionary<int, PlacePointer>? statics = null;
        foreach (var group in rootMixin.MethodGroups.Values.Distinct())
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
                semantics ??= FindSemantics(context);
                statics ??= FindStatics(context, buffer);
                var chain = group.Methods.Select(x => x.MethodId).ToHashSet();
                var contributors = phase.Reverse().ToList();
                var accesses = contributors.Select(x => FindAccesses(context, buffer, functions, x.MethodId, chain, statics)).ToList();
                var isAround = group.ExtensionPoint.Kind == ExtensionPointKindSDSL.Around;
                for (var a = 0; a < contributors.Count; a++)
                {
                    for (var b = a + 1; b < contributors.Count; b++)
                    {
                        if ((isAround ? "wrap each other" : accesses[a].ConflictWith(accesses[b], context, semantics)) is { } conflict && !IsOrdered(group, contributors[a].Shader, contributors[b].Shader))
                        {
                            log.Error(string.Format(SDSLErrorMessages.SDSL0137, contributors[a].Shader.ShaderName, contributors[b].Shader.ShaderName, phase.Key, $"{ShaderNames.ShortName(group.ExtensionPoint.Owner)}.{group.Name}", conflict));
                            result = false;
                        }
                    }
                }
            }
        }
        return result;
    }

    /// <summary>
    /// A pair orders two contributors, also through a third one, and so does inheritance: a class comes after its parents.
    /// </summary>
    private static bool IsOrdered(MethodGroup group, ShaderInfo a, ShaderInfo b)
    {
        return IsOwnParent(a, b) || IsOwnParent(b, a) || Reaches(ClassName(a), ClassName(b)) || Reaches(ClassName(b), ClassName(a));

        bool Reaches(string from, string to)
        {
            var visited = new HashSet<string> { from };
            var next = new Queue<string>([from]);
            while (next.TryDequeue(out var name))
            {
                foreach (var pair in group.Pairs.Where(x => x.Earlier == name))
                {
                    if (pair.Later == to)
                        return true;
                    if (visited.Add(pair.Later))
                        next.Enqueue(pair.Later);
                }
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

    private static Dictionary<int, string> FindSemantics(SpirvContext context)
    {
        var semantics = new Dictionary<int, string>();
        foreach (var i in context)
        {
            if (i.Op == Op.OpDecorateString && (OpDecorateString)i is { Decoration: Decoration.UserSemantic, Target: var target, Value: var semantic })
                semantics[target] = semantic;
        }
        return semantics;
    }

    private static Dictionary<int, PlacePointer> FindStatics(SpirvContext context, SpirvBuffer buffer)
    {
        var statics = new Dictionary<int, PlacePointer>();
        foreach (var i in buffer)
        {
            if (i.Op == Op.OpVariableSDSL && (OpVariableSDSL)i is { StorageClass: Spirv.Specification.StorageClass.Private } variable)
                statics[variable.ResultId] = new(new(variable.ResultId, "", context.Names.GetValueOrDefault(variable.ResultId, $"%{variable.ResultId}")), true, true, (context.ReverseTypes[variable.ResultType] as PointerType)?.BaseType);
        }
        return statics;
    }

    /// <summary>
    /// What several contributors can reach outside their own functions: a parameter of the point or a module static, and a path of
    /// constant indexes. An index that isn't constant stands for the whole array. <paramref name="Name"/> is for the message.
    /// </summary>
    private readonly record struct Place(int Root, string Path, string Name)
    {
        public bool Overlaps(Place other) => Root == other.Root && (Path.StartsWith(other.Path, StringComparison.Ordinal) || other.Path.StartsWith(Path, StringComparison.Ordinal));
    }

    /// <summary>
    /// A pointer into a place. <paramref name="IsWritable"/> is false below a parameter that a helper takes by value: its writes stay in
    /// its copy. <paramref name="IsExact"/> is false below an index that isn't constant.
    /// </summary>
    private readonly record struct PlacePointer(Place Place, bool IsWritable, bool IsExact, SymbolType? Type)
    {
        public PlacePointer Index(ReadOnlySpan<int> indexes, SpirvContext context)
        {
            var (path, name, isExact, type) = (Place.Path, Place.Name, IsExact, Type);
            foreach (var indexId in indexes)
            {
                if (!isExact || !context.TryGetConstantValue(indexId, out var value, out _))
                {
                    isExact = false;
                    break;
                }

                var index = Convert.ToInt32(value);
                path += $"{index}.";
                (name, type) = type switch
                {
                    StructuredType structured => ($"{name}.{structured.Members[index].Name}", structured.Members[index].Type),
                    ArrayType array => ($"{name}[{index}]", array.BaseType),
                    _ => ($"{name}[{index}]", (SymbolType?)null),
                };
            }
            return new(Place with { Path = path, Name = name }, IsWritable, isExact, type);
        }
    }

    /// <summary>
    /// What a contributor does that others can see: the streams its body and the functions it calls read and write, the places it
    /// reads and writes, and side effects.
    /// </summary>
    private sealed class ContributionAccesses
    {
        public HashSet<Place> PlaceReads { get; } = [];
        public HashSet<Place> PlaceWrites { get; } = [];

        public HashSet<int> Reads { get; } = [];
        public HashSet<int> Writes { get; } = [];

        /// <summary>A store to the whole of <c>streams</c>, such as <c>streams = input[i]</c>.</summary>
        public bool WritesEveryField { get; set; }

        /// <summary>A write to a buffer or a texture, an atomic operation, or a discard.</summary>
        public bool HasSideEffect { get; set; }

        /// <summary>
        /// The words of the message when one writes what the other reads or writes, null when their order doesn't matter. Streams with
        /// one semantic are one field: the interface step merges them.
        /// </summary>
        public string? ConflictWith(ContributionAccesses other, SpirvContext context, Dictionary<int, string> semantics)
        {
            object Field(int id) => semantics.TryGetValue(id, out var semantic) ? semantic : id;
            string Name(int id) => $"streams.{context.Names[id]}";

            var shared = WrittenByFirst(this, other).Concat(WrittenByFirst(other, this)).ToHashSet();
            var places = PlacesWrittenByFirst(this, other).Concat(PlacesWrittenByFirst(other, this)).Distinct();
            if (shared.Count > 0 || places.Any())
            {
                var names = Reads.Concat(Writes).Concat(other.Reads).Concat(other.Writes).Distinct()
                    .Where(x => shared.Contains(Field(x)))
                    .GroupBy(Field)
                    .Select(x => x.Count() > 1 ? $"{string.Join(" and ", x.Select(Name).Order(StringComparer.Ordinal))} (semantic {x.Key})" : Name(x.First()))
                    .Concat(places)
                    .Order(StringComparer.Ordinal);
                return $"share {string.Join(", ", names)}, which one of them writes";
            }

            if (WritesEveryField && other.WritesEveryField)
                return "both write the whole of streams";
            return HasSideEffect && other.HasSideEffect ? "both have a side effect (a write to a buffer or a texture, or a discard)" : null;

            IEnumerable<object> WrittenByFirst(ContributionAccesses first, ContributionAccesses second)
            {
                var used = second.Reads.Concat(second.Writes).Select(Field);
                return first.WritesEveryField ? used : first.Writes.Select(Field).Intersect(used);
            }

            static IEnumerable<string> PlacesWrittenByFirst(ContributionAccesses first, ContributionAccesses second)
                => first.PlaceWrites.Where(x => second.PlaceReads.Concat(second.PlaceWrites).Any(x.Overlaps)).Select(x => x.Name);
        }
    }

    /// <summary>
    /// <paramref name="chain"/> holds the methods of the extension point: a <c>base</c> call reaches the next contributor, whose
    /// accesses are its own.
    /// </summary>
    private static ContributionAccesses FindAccesses(SpirvContext context, SpirvBuffer buffer, Dictionary<int, int> functions, int contributor, HashSet<int> chain, Dictionary<int, PlacePointer> statics)
    {
        var accesses = new ContributionAccesses();
        var visited = new HashSet<int>();
        var visitedForPlaces = new HashSet<string>();
        Visit(contributor);
        VisitPlaces(contributor, null);
        return accesses;

        // An argument goes by a copy: the caller loads the value (not for out), stores it in a variable, makes the call, and stores the
        // variable back. That copy is neither a read nor a write: the callee decides, and what the next contributor does in the chain is its own.
        void VisitPlaces(int function, Dictionary<int, PlacePointer>? arguments)
        {
            var start = functions[function];
            var functionType = (FunctionType)context.ReverseTypes[((OpFunction)buffer[start]).FunctionType];
            var pointers = new Dictionary<int, PlacePointer>(statics);
            var callArguments = new HashSet<int>();
            var chainArguments = new HashSet<int>();
            var loads = new Dictionary<int, int>();
            var parameter = 0;
            for (var index = start + 1; buffer[index].Op != Op.OpFunctionEnd; index++)
            {
                var i = buffer[index];
                if (i.Op == Op.OpFunctionParameter)
                {
                    var id = ((OpFunctionParameter)i).ResultId;
                    if (arguments != null)
                    {
                        if (arguments.TryGetValue(parameter, out var argument))
                            pointers[id] = argument;
                    }
                    else if (IsWrittenBack(functionType, parameter) && (context.ReverseTypes[((OpFunctionParameter)i).ResultType] as PointerType)?.BaseType is { } type and not StreamsType)
                    {
                        pointers[id] = new(new(-parameter - 1, "", context.Names.GetValueOrDefault(id, $"parameter {parameter}")), true, true, type);
                    }
                    parameter++;
                }
                else if (i.Op == Op.OpFunctionCall)
                {
                    foreach (var argument in ((OpFunctionCall)i).Arguments.Elements.Span)
                    {
                        callArguments.Add(argument);
                        if (chain.Contains(((OpFunctionCall)i).Function))
                            chainArguments.Add(argument);
                    }
                }
                else if (i.Op == Op.OpLoad)
                {
                    loads[((OpLoad)i).ResultId] = ((OpLoad)i).Pointer;
                }
            }

            var copiedToArgument = new HashSet<int>();
            for (var index = start + 1; buffer[index].Op != Op.OpFunctionEnd; index++)
            {
                if (buffer[index].Op == Op.OpStore && (OpStore)buffer[index] is var store && callArguments.Contains(store.Pointer))
                    copiedToArgument.Add(store.ObjectId);
            }

            var copies = new Dictionary<int, int>();
            for (var index = start + 1; buffer[index].Op != Op.OpFunctionEnd; index++)
            {
                var i = buffer[index];
                switch (i.Op)
                {
                    case Op.OpAccessChain:
                        var access = (OpAccessChain)i;
                        if (pointers.TryGetValue(access.BaseId, out var chained))
                            pointers[access.ResultId] = chained.Index(access.Indexes.Elements.Span, context);
                        break;
                    case Op.OpLoad:
                        var load = (OpLoad)i;
                        if (pointers.TryGetValue(load.Pointer, out var read) && !copiedToArgument.Contains(load.ResultId))
                            accesses.PlaceReads.Add(read.Place);
                        break;
                    case Op.OpStore:
                        var store = (OpStore)i;
                        var source = loads.GetValueOrDefault(store.ObjectId, -1);
                        if (callArguments.Contains(store.Pointer) && pointers.ContainsKey(source))
                            copies[store.Pointer] = source;
                        else if (pointers.TryGetValue(store.Pointer, out var written) && written.IsWritable && !chainArguments.Contains(source) && copies.GetValueOrDefault(source, -1) != store.Pointer)
                            accesses.PlaceWrites.Add(written.Place);
                        break;
                    case Op.OpFunctionCall:
                        var call = (OpFunctionCall)i;
                        if (chain.Contains(call.Function) || !functions.TryGetValue(call.Function, out var calleeStart))
                            break;
                        var calleeType = (FunctionType)context.ReverseTypes[((OpFunction)buffer[calleeStart]).FunctionType];
                        var calleeArguments = new Dictionary<int, PlacePointer>();
                        var values = call.Arguments.Elements.Span;
                        for (var a = 0; a < values.Length; a++)
                        {
                            if (pointers.TryGetValue(copies.GetValueOrDefault(values[a], values[a]), out var argument))
                                calleeArguments[a] = argument with { IsWritable = argument.IsWritable && IsWrittenBack(calleeType, a) };
                        }
                        if (visitedForPlaces.Add($"{call.Function}:{string.Join(",", calleeArguments.Select(x => $"{x.Key}={x.Value.Place.Root}/{x.Value.Place.Path}/{x.Value.IsWritable}"))}"))
                            VisitPlaces(call.Function, calleeArguments);
                        break;
                }
            }
        }

        static bool IsWrittenBack(FunctionType type, int parameter) => (type.ParameterTypes[parameter].Modifiers & (ParameterModifiers.InOut | ParameterModifiers.Out)) != 0;

        void Visit(int function)
        {
            if (!visited.Add(function) || !functions.TryGetValue(function, out var index))
                return;

            var streams = new HashSet<int>();
            var stageStreams = new HashSet<int>();
            var streamMembers = new Dictionary<int, int>();
            var bufferPointers = new HashSet<int>();
            for (; buffer[index].Op != Op.OpFunctionEnd; index++)
            {
                var i = buffer[index];
                switch (i.Op)
                {
                    case Op.OpStreamsSDSL:
                        streams.Add(((OpStreamsSDSL)i).ResultId);
                        stageStreams.Add(((OpStreamsSDSL)i).ResultId);
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
                        // A store to a local copy or a parameter isn't a write of the stage's streams: an inout argument is stored back by its caller
                        else if (stageStreams.Contains(pointer))
                            accesses.WritesEveryField = true;
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

    private void ReportDroppedContributors(MixinNode rootMixin, MixinGlobalContext globalContext, ILogger log)
    {
        foreach (var group in rootMixin.MethodGroups.Values.Distinct())
        {
            if (group.ExtensionPoint == null || FindDropped(group, globalContext) is not var (by, dropped))
                continue;

            var message = string.Format(SDSLErrorMessages.SDSL0216, by.ShaderName, group.Name, string.Join(", ", dropped.Select(x => x.ShaderName)));
            if (IsAssetBuild)
                log.Warning(message);
            else
                log.Info(message);
        }
    }

    /// <summary>
    /// The lines below a method line: the kind, the contributors of each phase in their running order, and what the chain drops.
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
                // A phase reached from the outside runs from the end of the chain
                if (IsReachedFromOutside(point, phase))
                    names = names.Reverse();
                // The first definition sits in the phase of the default body, and an around point reaches it last
                if (phase == defaultPhase)
                    names = names.Prepend(group.Methods[0].Shader.ShaderName);
                builder.AppendLine($"    {phase}: {(names.Any() ? string.Join(" > ", names) : "(none)")}");
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
            builder.AppendLine($"    replaced by {replaced.Key.ShaderName}: {string.Join(", ", replaced.Select(x => x.Shader.ShaderName))}");
    }
}
