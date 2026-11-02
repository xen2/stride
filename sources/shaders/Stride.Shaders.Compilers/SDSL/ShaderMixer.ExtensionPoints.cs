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
    /// An effect the asset build compiles. A contributor that drops others is then a warning (SDSL0216) or an error (SDSL0136),
    /// and information in a mix built at run time, where replacing engine code is often the point.
    /// </summary>
    public bool IsAssetBuild { get; set; }

    /// <summary>
    /// Two classes in their running order inside a phase. <paramref name="Level"/> is 0 for what a class states, then 1 for the effect
    /// and one more for each of its layers: an outer level replaces the opposite pair. <paramref name="Source"/> is where it is written.
    /// </summary>
    private sealed record OrderPair(string Earlier, string Later, int Level, string Source);

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
            static string[] Names(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (contribution.Phase.Length > 0)
                methodGroup.Phases[functionId] = contribution.Phase;
            if (contribution.After.Length > 0)
                methodGroup.After[functionId] = Names(contribution.After);
            if (contribution.Before.Length > 0)
                methodGroup.Before[functionId] = Names(contribution.Before);
            if (contribution.Drops.Length > 0)
                methodGroup.Drops[functionId] = Names(contribution.Drops);
            if (contribution.AddedPhase.Split(',') is [var after, var before])
                methodGroup.AddedPhases[functionId] = (contribution.Phase, after, before);
            if (contribution.BaseCall.Length > 0)
                methodGroup.BaseCalls[functionId] = contribution.BaseCall;
            methodGroup.Lines[functionId] = contribution.Line;
            // Only the first definition writes these
            methodGroup.MixinOrder.AddRange(Names(contribution.MixinOrder));
            methodGroup.Unbind.AddRange(Names(contribution.Unbind));
            if (contribution.Form == MethodExtensionPoint.ExtendForm)
                methodGroup.Extends.Add(functionId);
            else if (contribution.Form == MethodExtensionPoint.PatchForm)
                methodGroup.Patches.Add(functionId);
        }
    }

    /// <summary>
    /// Orders the contributors of each method of a node, in passes. A phase wins over inheritance and over the order of the mix. A
    /// contributor without a phase is in the implicit phase <c>External</c>, after every declared phase, in the order of the mix.
    /// </summary>
    private static void SortContributions(MixinGlobalContext globalContext, MixinNode mixinNode)
    {
        // Names: a class that a block of the effect names exists. The classes check their own names when they compile.
        if (mixinNode.IsRoot)
            CheckLinkageClasses(globalContext, mixinNode);

        foreach (var group in mixinNode.MethodGroups.Values.Distinct())
        {
            // 1. Collect: the blocks of an effect are about the effect, so about its module methods. A block replaces what a class states.
            var rules = mixinNode.IsRoot ? CollectRules(globalContext, group) : EffectRules.None;
            ApplyPhaseRules(group, rules);
            if (group.ExtensionPoint == null)
            {
                OrderPatches(globalContext, group, rules);
                continue;
            }

            // 2. The phase list: declared phases keep their order, added phases go next to their neighbours
            AddPhases(group, rules);
            // 3. Each contributor in its phase
            CheckPhasesExist(group, rules);
            if (group.Phases.Count == 0 && group.After.Count == 0 && group.Before.Count == 0 && group.Drops.Count == 0 && group.MixinOrder.Count == 0 && group.Unbind.Count == 0 && rules.IsEmpty)
                continue;

            // 4. What [Unbind] and [Drops] take out
            RemoveReplaced(globalContext.Table.CurrentFrame, group, rules);
            // 5. The running order inside each phase, then the chain
            OrderPhases(globalContext.Table.CurrentFrame, group, rules);
            // 6. Where each step of a pipeline calls base: the form of an extend that fits its phase, then a check of every step
            PickBaseCallForms(globalContext.Table.CurrentFrame, group);
            CheckBaseCalls(group);
        }

        if (mixinNode.IsRoot)
            CheckLinkageRules(globalContext, mixinNode);
    }

    /// <summary>
    /// What the blocks of the effect say about one method. A block about a class replaces what the class states of the same kind.
    /// </summary>
    private sealed class EffectRules
    {
        public static readonly EffectRules None = new();

        /// <summary>Lists of classes in their running order, on the method.</summary>
        public List<(List<string> Classes, int Level, string Source)> MixinOrders { get; } = [];

        /// <summary>What doesn't run: a phase or a class.</summary>
        public List<(string Name, bool IsPhase, string Source)> Unbinds { get; } = [];

        /// <summary>The phase of a class, and the phase it adds when it has neighbours.</summary>
        public List<(string Class, string Phase, string After, string Before, int Level, string Source)> Phases { get; } = [];

        /// <summary>The classes a class runs after and before, in its phase.</summary>
        public List<(string Class, List<string> After, List<string> Before, int Level, string Source)> Neighbours { get; } = [];

        /// <summary>Where the phase of a contributor comes from, by method, when a block gives it.</summary>
        public Dictionary<int, string> PhaseSources { get; } = [];

        public bool IsEmpty => MixinOrders.Count == 0 && Unbinds.Count == 0 && Phases.Count == 0 && Neighbours.Count == 0;
    }

    // The level of a block, counted from the classes, which are 0
    private static int LevelOf(ShaderLinkageRule rule) => rule.Level + 1;

    // A block built in C# may not say where it is
    private static string SourceOf(ShaderLinkageRule rule) => rule.Source ?? "C#";

    /// <summary>
    /// A block about a method: its class owns it, or contributes to it when written <c>Owner.Method</c>. The parameter types of an
    /// overload pick one.
    /// </summary>
    private static bool IsAbout(ShaderLinkageRule rule, MethodGroup group)
    {
        var owner = group.ExtensionPoint is { } point ? ShaderNames.WithoutGenerics(point.Owner) : ClassName(group.Methods[0].Shader);
        return rule.Method == group.Name && IsClass(owner, rule.Owner ?? rule.Shader)
            && (rule.Parameters == null || rule.Parameters == string.Join(",", group.FunctionType.ParameterTypes.Select(x => (x.Type is PointerType pointer ? pointer.BaseType : x.Type).ToString())));
    }

    /// <summary>
    /// The classes a block names exist, as for <c>[MixinOrder]</c> and <c>[Drops]</c>, and a name in <c>Mixins</c> isn't a phase. They don't
    /// have to be in the effect.
    /// </summary>
    private static void CheckLinkageClasses(MixinGlobalContext globalContext, MixinNode mixinNode)
    {
        var loader = globalContext.Table.ShaderLoader;
        foreach (var rule in globalContext.Linkage)
        {
            var classes = new[] { rule.Shader, rule.Owner }.OfType<string>().Concat(rule.Names).Concat(rule.Mixins);
            if (rule.Kind == ShaderLinkageKind.MixinOrder)
                classes = classes.Concat(rule.After).Concat(rule.Before);
            foreach (var name in classes)
            {
                if (mixinNode.Shaders.Any(x => IsClass(ClassName(x), name)) || loader.Exists(name) || loader.FindFullNames(ShaderNames.ShortName(name)).Any(x => IsClass(x, name)))
                    continue;
                if (rule.Mixins.Contains(name) && mixinNode.MethodGroups.Values.Any(x => IsAbout(rule, x) && x.ExtensionPoint?.Phases.Contains(name) == true))
                    throw new InvalidOperationException(string.Format(SDSLErrorMessages.SDSL0254, "Unbind", $"{ShaderNames.ShortName(rule.Shader)}.{rule.Member()} ({SourceOf(rule)})", $"{name} is a phase: write Phases = {{ {name} }}"));
                throw new InvalidOperationException(string.Format(SDSLErrorMessages.SDSL0158, name, $"block of {SourceOf(rule)}") + globalContext.Table.DidYouMean(name));
            }
        }
    }

    /// <summary>
    /// A block about a class of the effect names a method of that class, or one it contributes to. A class that isn't in the effect is
    /// ignored: a layer can be about a class that only some effects mix.
    /// </summary>
    private static void CheckLinkageRules(MixinGlobalContext globalContext, MixinNode mixinNode)
    {
        foreach (var rule in globalContext.Linkage.Where(x => mixinNode.Shaders.Any(y => IsClass(ClassName(y), x.Shader))))
        {
            var groups = mixinNode.MethodGroups.Values.Distinct().Where(x => x.Name == rule.Method && IsClass(x.ExtensionPoint is { } point ? ShaderNames.WithoutGenerics(point.Owner) : ClassName(x.Methods[0].Shader), rule.Owner ?? rule.Shader)).ToList();
            string? problem;
            if (rule.Parameters == null && groups.Select(x => x.FunctionType).Distinct().Count() > 1)
                problem = $"{rule.Method} has several overloads: write the parameter types, as in {rule.Method}({string.Join(",", groups[0].FunctionType.ParameterTypes.Select(x => (x.Type is PointerType pointer ? pointer.BaseType : x.Type).ToString()))})";
            else if (!groups.Any(x => IsAbout(rule, x)))
                problem = rule.IsOnContribution ? $"{ShaderNames.ShortName(rule.Owner!)} has no method {rule.Member()[(rule.Owner!.Length + 1)..]} that is an extension point or has patches"
                    : $"has no method {rule.Member()} that is an extension point{(rule.Kind == ShaderLinkageKind.MixinOrder ? " or has patches" : "")}";
            else if (rule.IsOnContribution && !groups.Where(x => IsAbout(rule, x)).Any(x => x.Methods.Skip(1).Concat(x.Replaced.Values.Select(y => (y.Shader, 0, FunctionFlagsMask.None))).Any(y => IsClass(ClassName(y.Shader), rule.Shader))))
                problem = $"doesn't contribute to {rule.Member()}";
            else if (rule.Kind == ShaderLinkageKind.Unbind && !groups.Any(x => IsAbout(rule, x) && x.ExtensionPoint != null))
                problem = $"{rule.Member()} is not an extension point, so there is nothing to unbind";
            else
                continue;
            throw new InvalidOperationException(string.Format(SDSLErrorMessages.SDSL0154, ShaderNames.ShortName(rule.Shader), SourceOf(rule), rule.Member(), problem));
        }
    }

    /// <summary>
    /// The blocks of the effect about this method. What they name that isn't in the effect is ignored.
    /// </summary>
    private static EffectRules CollectRules(MixinGlobalContext globalContext, MethodGroup group)
    {
        var rules = new EffectRules();
        foreach (var rule in globalContext.Linkage.Where(x => IsAbout(x, group)))
        {
            var source = $"block of {SourceOf(rule)}";
            switch (rule.Kind, rule.IsOnContribution)
            {
                case (ShaderLinkageKind.MixinOrder, false):
                    rules.MixinOrders.Add((rule.Names, LevelOf(rule), source));
                    break;
                case (ShaderLinkageKind.MixinOrder, true):
                    rules.Neighbours.Add((rule.Shader, rule.After, rule.Before, LevelOf(rule), source));
                    break;
                case (ShaderLinkageKind.Unbind, _):
                    rules.Unbinds.AddRange(rule.Phases.Select(x => (x, true, source)).Concat(rule.Mixins.Select(x => (x, false, source))));
                    break;
                case (ShaderLinkageKind.Phase, _):
                    rules.Phases.Add((rule.Shader, rule.Phase, rule.After.FirstOrDefault() ?? "", rule.Before.FirstOrDefault() ?? "", LevelOf(rule), source));
                    break;
            }
        }
        return rules;
    }

    /// <summary>
    /// A block that gives a class its phase replaces the phase the class states, and the phase it adds: the outermost block wins. Two
    /// blocks of one level that disagree are an error.
    /// </summary>
    private static void ApplyPhaseRules(MethodGroup group, EffectRules rules)
    {
        foreach (var contributor in group.Methods.Skip(1))
        {
            var placed = rules.Phases.Where(x => IsClass(ClassName(contributor.Shader), x.Class)).ToList();
            if (placed.Count == 0)
                continue;

            var outer = placed.Where(x => x.Level == placed.Max(y => y.Level)).ToList();
            if (outer.Any(x => (x.Phase, x.After, x.Before) != (outer[0].Phase, outer[0].After, outer[0].Before)))
                throw new InvalidOperationException(string.Format(SDSLErrorMessages.SDSL0138, PointName(group), $"{contributor.Shader.ShaderName} is put in phase {outer[0].Phase} ({outer[0].Source}) and in phase {outer.First(x => x.Phase != outer[0].Phase || x.After != outer[0].After || x.Before != outer[0].Before).Phase} ({outer.First(x => x.Phase != outer[0].Phase || x.After != outer[0].After || x.Before != outer[0].Before).Source}), at one level"));

            var rule = outer[0];
            group.Phases[contributor.MethodId] = rule.Phase;
            rules.PhaseSources[contributor.MethodId] = rule.Source;
            if (rule.After.Length > 0 || rule.Before.Length > 0)
            {
                // A block written in C# adds the phase for its class
                if (MethodExtensionPoint.CheckAddedPhase(rule.Phase, rule.Class, $"{contributor.Shader.ShaderName}.{group.Name} ({rule.Source})", PointName(group)) is { } problem)
                    throw new InvalidOperationException(problem);
                group.AddedPhases[contributor.MethodId] = (rule.Phase, rule.After, rule.Before);
            }
            else
            {
                group.AddedPhases.Remove(contributor.MethodId);
            }
        }
    }

    /// <summary>
    /// A method that is not an extension point has no phases, and only its patches have to state their order (SDSL0148), with
    /// <c>[MixinOrder]</c> on a patch or on the method, or a block of the effect. Its plain overrides follow the order of the mix.
    /// </summary>
    private static void OrderPatches(MixinGlobalContext globalContext, MethodGroup group, EffectRules rules)
    {
        var contributors = group.Methods.Skip(1).ToList();
        var patches = contributors.Where(x => group.Patches.Contains(x.MethodId)).ToList();
        if (patches.Count < 2)
            return;

        group.Pairs.AddRange(MergePairs(group, CollectPairs(group, rules, contributors)));
        var owner = group.Methods[0].Shader;
        for (var a = 0; a < patches.Count; a++)
        {
            for (var b = a + 1; b < patches.Count; b++)
            {
                if (!IsOrdered(group, patches[a].Shader, patches[b].Shader))
                    throw new InvalidOperationException(string.Format(SDSLErrorMessages.SDSL0148, ShaderNames.WithoutGenerics(patches[a].Shader.ShaderName), ShaderNames.WithoutGenerics(patches[b].Shader.ShaderName), ShaderNames.WithoutGenerics(owner.ShaderName), group.Name));
            }
        }

        var sorted = FollowPairs(group, null, MethodExtensionPoint.ImplicitPhase, contributors).ToList();
        group.Methods.RemoveRange(1, sorted.Count);
        group.Methods.AddRange(sorted);
        SortSymbols(globalContext.Table.CurrentFrame, group, sorted.Select(x => x.MethodId).ToList());
    }

    private static string ClassName(ShaderInfo shader) => ShaderNames.WithoutGenerics(shader.FullName);

    // A name in a block of an effect is a full name when its project could resolve it, else the name as written
    private static bool IsClass(string fullName, string written) => fullName == written || fullName.EndsWith($".{written}", StringComparison.Ordinal);

    private static string PointName(MethodGroup group) => $"{ShaderNames.ShortName(group.ExtensionPoint?.Owner ?? group.Methods[0].Shader.ShaderName)}.{group.Name}";

    private static ShaderInfo ShaderOf(MethodGroup group, int method) => group.Methods.Find(x => x.MethodId == method).Shader;

    // Where a contributor is written: its class has the name of its file
    private static string ContributorSource(MethodGroup group, int method)
        => $"{ShaderNames.ShortName(ShaderNames.WithoutGenerics(ShaderOf(group, method).ShaderName))}.sdsl, line {group.Lines.GetValueOrDefault(method)}";

    /// <summary>
    /// The phases that contributors and the effect add, next to the ones the point declares, which keep their order. Several
    /// declarations of one phase share it, and all their neighbours count. An added phase goes right after its <c>After</c> neighbour,
    /// else right before its <c>Before</c> neighbour, and after the phases already added there: added phases with nothing between them
    /// keep the order they were collected in: the order of the mix.
    /// </summary>
    private static void AddPhases(MethodGroup group, EffectRules rules)
    {
        var point = group.ExtensionPoint!;
        var pointName = PointName(group);
        var declarations = group.Methods.Skip(1).Where(x => group.AddedPhases.ContainsKey(x.MethodId))
            .Select(x => (Declaration: group.AddedPhases[x.MethodId], By: x.Shader.ShaderName, Source: rules.PhaseSources.GetValueOrDefault(x.MethodId) ?? ContributorSource(group, x.MethodId)))
            .Select(x => (x.Declaration.Phase, x.Declaration.After, x.Declaration.Before, x.By, x.Source))
            .ToList();
        if (declarations.Count == 0)
            return;

        var declared = point.Phases.ToHashSet();
        var phases = point.Phases.ToList();
        var pending = declarations.Select(x => x.Phase).Where(x => !declared.Contains(x)).Distinct().ToList();
        while (pending.Count > 0)
        {
            var next = pending.FindIndex(x => declarations.Where(y => y.Phase == x).All(y => IsPlaced(y.After) && IsPlaced(y.Before)));
            if (next == -1)
            {
                var waiting = declarations.First(x => pending.Contains(x.Phase) && !(IsPlaced(x.After) && IsPlaced(x.Before)));
                var neighbour = IsPlaced(waiting.After) ? waiting.Before : waiting.After;
                if (!pending.Contains(neighbour))
                    throw new InvalidOperationException(string.Format(SDSLErrorMessages.SDSL0218, waiting.Phase, waiting.By, pointName, neighbour));
                throw new InvalidOperationException(string.Format(SDSLErrorMessages.SDSL0138, pointName, $"phases {string.Join(", ", pending)} are placed next to each other in a circle"));
            }

            var phase = pending[next];
            pending.RemoveAt(next);
            var own = declarations.Where(x => x.Phase == phase).ToList();
            var befores = own.Select(x => x.Before).Where(x => x.Length > 0).ToHashSet();
            var anchor = own.Find(x => x.After.Length > 0);
            int index;
            if (anchor.After != null)
            {
                index = phases.IndexOf(anchor.After) + 1;
                while (index < phases.Count && !declared.Contains(phases[index]) && !befores.Contains(phases[index]))
                    index++;
            }
            else
            {
                anchor = own[0];
                index = phases.IndexOf(anchor.Before);
            }
            phases.Insert(index, phase);

            foreach (var declaration in own)
            {
                foreach (var (side, neighbour) in new[] { ("after", declaration.After), ("before", declaration.Before) })
                {
                    var isKept = neighbour.Length == 0 || (side == "after" ? phases.IndexOf(neighbour) < phases.IndexOf(phase) : phases.IndexOf(phase) < phases.IndexOf(neighbour));
                    if (!isKept)
                    {
                        var placed = anchor.After != null ? $"after {anchor.After}" : $"before {anchor.Before}";
                        throw new InvalidOperationException(string.Format(SDSLErrorMessages.SDSL0138, pointName, $"phase {phase} goes {placed} ({anchor.Source}), so it can't also go {side} {neighbour} ({declaration.Source})"));
                    }
                }
            }
        }
        group.ExtensionPoint = point with { Phases = [.. phases] };

        bool IsPlaced(string neighbour) => neighbour.Length == 0 || phases.Contains(neighbour);
    }

    /// <summary>
    /// A phase that a contributor names has to exist once the phases are added: a name with a dot can be one that another class adds.
    /// </summary>
    private static void CheckPhasesExist(MethodGroup group, EffectRules rules)
    {
        var point = group.ExtensionPoint!;
        foreach (var (method, phase) in group.Phases)
        {
            var where = $"{ShaderOf(group, method).ShaderName}.{group.Name}{(rules.PhaseSources.TryGetValue(method, out var source) ? $" ({source})" : "")}";
            if (!point.Phases.Contains(phase))
                throw new InvalidOperationException(string.Format(SDSLErrorMessages.SDSL0141, phase, where, $"{PointName(group)} has the phases {string.Join(", ", point.Phases)}, and no class of the effect adds {phase}"));
        }
    }

    /// <summary>
    /// Takes out of the chain what <c>[Unbind]</c> and <c>[Drops]</c> name, a class or a whole phase. Only this chain: the class still contributes
    /// to other methods. The first definition is not a contributor, and a contributor doesn't drop itself.
    /// </summary>
    private static void RemoveReplaced(SymbolFrame frame, MethodGroup group, EffectRules rules)
    {
        bool IsNamed(IEnumerable<string> names, (ShaderInfo Shader, int MethodId, FunctionFlagsMask Flags) target)
            => names.Contains(ClassName(target.Shader)) || (group.Phases.TryGetValue(target.MethodId, out var phase) && names.Contains($"{MethodExtensionPoint.ReplacedPhasePrefix}{phase}"));

        foreach (var contributor in group.Methods.Skip(1))
        {
            var isUnbound = IsNamed(group.Unbind, contributor) || rules.Unbinds.Any(x => x.IsPhase ? group.Phases.GetValueOrDefault(contributor.MethodId) == x.Name : IsClass(ClassName(contributor.Shader), x.Name));
            if (isUnbound)
                group.Replaced.TryAdd(contributor.MethodId, (contributor.Shader, null));
        }
        foreach (var (phase, _, source) in rules.Unbinds.Where(x => x.IsPhase && !group.ExtensionPoint!.Phases.Contains(x.Name)))
            throw new InvalidOperationException(string.Format(SDSLErrorMessages.SDSL0254, "Unbind", $"{PointName(group)} ({source})", $"{PointName(group)} has no phase {phase}. It has the phases {string.Join(", ", group.ExtensionPoint!.Phases)}"));

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
    /// The pairs that order contributors: <c>[MixinOrder(After = A, Before = B)]</c> of each contributor, and each list
    /// <c>[MixinOrder(A, B, C)]</c>, which gives A before B and B before C once what isn't in the chain is left out. A block replaces the
    /// attribute of the same kind that the class writes.
    /// </summary>
    private static List<OrderPair> CollectPairs(MethodGroup group, EffectRules rules, List<(ShaderInfo Shader, int MethodId, FunctionFlagsMask Flags)> contributors)
    {
        var pairs = new List<OrderPair>();
        string? Find(string name) => contributors.Select(x => ClassName(x.Shader)).FirstOrDefault(x => IsClass(x, name));
        foreach (var contributor in contributors)
        {
            var name = ClassName(contributor.Shader);
            var blocks = rules.Neighbours.Where(x => IsClass(name, x.Class)).ToList();
            var stated = blocks.Count > 0
                ? blocks.Select(x => (x.After, x.Before, x.Level, x.Source))
                : [((group.After.GetValueOrDefault(contributor.MethodId) ?? []).ToList(), (group.Before.GetValueOrDefault(contributor.MethodId) ?? []).ToList(), 0, $"[MixinOrder] of {ContributorSource(group, contributor.MethodId)}")];
            foreach (var (after, before, level, source) in stated)
            {
                foreach (var earlier in after.Select(Find).OfType<string>().Where(x => x != name))
                    pairs.Add(new(earlier, name, level, source));
                foreach (var later in before.Select(Find).OfType<string>().Where(x => x != name))
                    pairs.Add(new(name, later, level, source));
            }
        }

        var lists = rules.MixinOrders.Count > 0 || group.MixinOrder.Count == 0
            ? rules.MixinOrders
            : [(group.MixinOrder, 0, $"[MixinOrder] of {ShaderNames.ShortName(ShaderNames.WithoutGenerics(group.Methods[0].Shader.ShaderName))}.sdsl, line {group.Lines.GetValueOrDefault(group.Methods[0].MethodId)}")];
        foreach (var (classes, level, source) in lists)
        {
            var listed = classes.Select(Find).OfType<string>().Distinct().ToList();
            for (var i = 1; i < listed.Count; i++)
                pairs.Add(new(listed[i - 1], listed[i], level, source));
        }
        return pairs;
    }

    /// <summary>
    /// An outer pair replaces the opposite pair of an inner level, and pairs that don't contradict all apply. Two opposite pairs of one
    /// level are an error.
    /// </summary>
    private static List<OrderPair> MergePairs(MethodGroup group, List<OrderPair> pairs)
    {
        var merged = new List<OrderPair>();
        foreach (var pair in pairs)
        {
            var opposite = pairs.Where(x => x.Earlier == pair.Later && x.Later == pair.Earlier).ToList();
            var outer = opposite.Count > 0 ? opposite.Max(x => x.Level) : -1;
            if (outer == pair.Level)
                throw new InvalidOperationException(string.Format(SDSLErrorMessages.SDSL0138, PointName(group), $"{ShaderNames.ShortName(pair.Earlier)} runs before {ShaderNames.ShortName(pair.Later)} ({pair.Source}) and after it ({opposite.Find(x => x.Level == outer)!.Source}), and neither is written further out to replace the other"));
            if (pair.Level > outer && !merged.Contains(pair))
                merged.Add(pair);
        }
        return merged;
    }

    /// <summary>
    /// The running order inside each phase, from the pairs merged by level: a class is the innermost level, then the effect and each of its
    /// layers. A pair between two phases is an error, as is a cycle. What no pair orders keeps the order of the mix. Then the chain: phases
    /// in their place from the default body outwards.
    /// </summary>
    private static void OrderPhases(SymbolFrame frame, MethodGroup group, EffectRules rules)
    {
        var point = group.ExtensionPoint!;
        var contributors = group.Methods.Skip(1).ToList();
        var pairs = MergePairs(group, CollectPairs(group, rules, contributors));
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
    private static IEnumerable<(ShaderInfo Shader, int MethodId, FunctionFlagsMask Flags)> FollowPairs(MethodGroup group, MethodExtensionPoint? point, string? phase, List<(ShaderInfo Shader, int MethodId, FunctionFlagsMask Flags)> contributors)
    {
        var names = contributors.Select(x => ClassName(x.Shader)).ToHashSet();
        var pairs = group.Pairs.Where(x => names.Contains(x.Earlier) && names.Contains(x.Later)).ToList();
        if (pairs.Count == 0)
            return contributors;

        var fromOutside = point != null && IsReachedFromOutside(point, phase);
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

    // What the class wrote after OpFunctionMetadataSDSL says
    private static bool IsBaseLastForm(SpirvBuffer buffer, int functionIndex)
    {
        var index = functionIndex + 2;
        if (buffer[index].Op == Op.OpExtensionPointSDSL)
            index++;
        return buffer[index].Op == Op.OpContributionSDSL && ((OpContributionSDSL)buffer[index]).Form == MethodExtensionPoint.BaseLastForm;
    }

    /// <summary>
    /// An <c>extend</c> whose phase its class couldn't place compiled twice, calling <c>base</c> first and last. The chain holds the first
    /// form: before the default body, the second one takes its place. The form left out is never called.
    /// </summary>
    private static void PickBaseCallForms(SymbolFrame frame, MethodGroup group)
    {
        var point = group.ExtensionPoint!;
        foreach (var (first, last) in group.BaseLastForms)
        {
            var index = group.Methods.FindIndex(x => x.MethodId == first);
            if (index < 0 || !group.Phases.TryGetValue(first, out var phase) || Array.IndexOf(point.Phases, phase) >= point.DefaultPhaseIndex)
                continue;

            group.Methods[index] = (group.Methods[index].Shader, last, group.Methods[index].Flags);
            foreach (var byMethod in new IDictionary<int, string[]>[] { group.After, group.Before, group.Drops })
            {
                if (byMethod.Remove(first, out var names))
                    byMethod[last] = names;
            }
            group.Phases.Remove(first);
            group.Phases[last] = phase;
            if (group.AddedPhases.Remove(first, out var added))
                group.AddedPhases[last] = added;
            group.Lines[last] = group.Lines.GetValueOrDefault(first);
            group.BaseCalls.Remove(first);
            group.BaseCalls[last] = MethodExtensionPoint.BaseCallLast;
            if (group.Extends.Remove(first))
                group.Extends.Add(last);
            if (frame.TryGetValue(group.Name, out var symbol) && symbol.Type is FunctionGroupType)
                frame[group.Name] = symbol with { GroupMembers = [.. symbol.GroupMembers.Select(x => x.IdRef == first ? x with { IdRef = last } : x)] };
        }
    }

    /// <summary>
    /// A step before the phase of the default body calls <c>base</c> last, a step after it first (SDSL0140). A class checks the phases it
    /// knows when it compiles, and the place of every phase is known here.
    /// </summary>
    private static void CheckBaseCalls(MethodGroup group)
    {
        var point = group.ExtensionPoint!;
        if (point.Kind != ExtensionPointKindSDSL.Pipeline)
            return;

        foreach (var (method, baseCall) in group.BaseCalls)
        {
            if (!group.Phases.TryGetValue(method, out var phase) || group.Replaced.ContainsKey(method))
                continue;

            var index = Array.IndexOf(point.Phases, phase);
            var problem = baseCall switch
            {
                MethodExtensionPoint.BaseCallFirst when index < point.DefaultPhaseIndex => "calls base first, so it runs after the default body",
                MethodExtensionPoint.BaseCallLast when index > point.DefaultPhaseIndex => "calls base last, so it runs before the default body",
                _ => null,
            };
            if (problem != null)
                throw new InvalidOperationException(string.Format(SDSLErrorMessages.SDSL0140, $"{ShaderOf(group, method).ShaderName}.{group.Name}", phase, PointName(group), problem, point.Phases[point.DefaultPhaseIndex]));
        }
    }

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

    /// <summary>
    /// Two contributors of one phase whose order matters and that nothing orders. <paramref name="First"/> runs first today.
    /// <paramref name="IsInComposition"/>: the point belongs to a composed node, which the rules of the effect don't reach.
    /// </summary>
    private sealed record ContributionConflict(MethodGroup Group, string Phase, ShaderInfo First, ShaderInfo Second, string What, bool IsInComposition);

    /// <summary>
    /// An effect of the asset build has to state the order. In a mix built at run time, the order of the mix decides, and the log says so.
    /// </summary>
    private bool ReportContributionConflicts(List<ContributionConflict> conflicts, ILogger log)
    {
        var result = true;
        foreach (var conflict in conflicts)
        {
            var point = $"{ShaderNames.ShortName(conflict.Group.ExtensionPoint!.Owner)}.{conflict.Group.Name}";
            if (IsAssetBuild)
            {
                log.Error(string.Format(conflict.IsInComposition ? SDSLErrorMessages.SDSL0137InComposition : SDSLErrorMessages.SDSL0137, ShaderNames.WithoutGenerics(conflict.First.ShaderName), ShaderNames.WithoutGenerics(conflict.Second.ShaderName), conflict.Phase, point, conflict.What, ShaderNames.ShortName(conflict.Group.ExtensionPoint.Owner), conflict.Group.Name));
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
    /// Inside a phase, two contributors need an order when one writes what the other uses, and two wrappers of an <c>around</c> point
    /// always do, as each one wraps the other. <c>External</c> follows the order of the mix, and code that never runs conflicts with nothing.
    /// </summary>
    private static List<ContributionConflict> FindContributionConflicts(MixinNode rootMixin, MixinGlobalContext globalContext, SpirvContext context, SpirvBuffer buffer)
    {
        var conflicts = new List<ContributionConflict>();
        Dictionary<int, int>? functions = null;
        Dictionary<int, string>? semantics = null;
        Dictionary<int, PlacePointer>? statics = null;
        var rootGroups = rootMixin.MethodGroups.Values.ToHashSet();
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
                            var (first, second) = IsReachedFromOutside(group.ExtensionPoint, phase.Key) ? (contributors[b], contributors[a]) : (contributors[a], contributors[b]);
                            conflicts.Add(new(group, phase.Key, first.Shader, second.Shader, conflict, !rootGroups.Contains(group)));
                            group.DecidedByBuilder.Add(phase.Key);
                        }
                    }
                }
            }
        }
        return conflicts;
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

    private bool ReportDroppedContributors(MixinNode rootMixin, MixinGlobalContext globalContext, ILogger log)
    {
        var result = true;
        foreach (var group in NodesOf(rootMixin).SelectMany(x => x.MethodGroups.Values).Distinct())
        {
            if (group.ExtensionPoint == null || FindDropped(group, globalContext) is not var (by, dropped))
                continue;

            // In an asset build the target syntax has to say it. The compat form stays what it always was.
            if (group.Extends.Contains(group.Methods.Find(x => x.Shader == by).MethodId))
            {
                var names = string.Join(", ", dropped.Select(x => ShaderNames.WithoutGenerics(x.ShaderName)));
                var dropMessage = string.Format(SDSLErrorMessages.SDSL0136, by.ShaderName, group.Name, names, names);
                if (IsAssetBuild)
                {
                    log.Error(dropMessage);
                    result = false;
                }
                else
                {
                    log.Info(dropMessage);
                }
                continue;
            }

            var message = string.Format(SDSLErrorMessages.SDSL0216, by.ShaderName, group.Name, string.Join(", ", dropped.Select(x => x.ShaderName)));
            if (IsAssetBuild)
                log.Warning(message);
            else
                log.Info(message);
        }
        return result;
    }

    /// <summary>
    /// What ordered the contributors of a phase, when it has several: <c>mixin order</c> for what <c>[MixinOrder]</c> of a class states,
    /// <c>block</c> for the rules of the effect, <c>builder</c> when the order of the mix decided a pair whose order matters, <c>tie-break</c>
    /// when no conflict was found (<see cref="ContributionAccesses"/>).
    /// </summary>
    private static string OrderSource(MethodGroup group, string phase)
    {
        var contributors = group.Methods.Where(x => group.Phases.GetValueOrDefault(x.MethodId) == phase).ToList();
        if (contributors.Count < 2)
            return "";

        var classes = contributors.Select(x => ClassName(x.Shader)).ToHashSet();
        var pairs = group.Pairs.Where(x => classes.Contains(x.Earlier) && classes.Contains(x.Later)).ToList();
        var sources = new List<string>();
        if (pairs.Any(x => x.Level == 0))
            sources.Add("mixin order");
        if (pairs.Any(x => x.Level > 0))
            sources.Add("block");
        if (group.DecidedByBuilder.Contains(phase))
            sources.Add("builder");
        return $"   order: {(sources.Count > 0 ? string.Join(", ", sources) : "tie-break")}";
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
