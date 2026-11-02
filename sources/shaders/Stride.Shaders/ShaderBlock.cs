// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;

namespace Stride.Shaders
{
    /// <summary>
    /// <c>extend shader Shader { ... }</c> in an effect: attributes given to the methods of a class, for this effect only. It replaces
    /// the attribute of the same kind that the class writes, and covers every generic version of the class.
    /// </summary>
    public sealed class ShaderBlock
    {
        private readonly string source;
        private readonly Action<ShaderLinkageRule> add;

        internal ShaderBlock(string shader, string source, Action<ShaderLinkageRule> add)
        {
            ArgumentException.ThrowIfNullOrEmpty(shader);
            Shader = shader;
            this.source = source;
            this.add = add;
        }

        /// <summary>
        /// Gets the class of the block, by full name, or by a name it ends with.
        /// </summary>
        public string Shader { get; }

        /// <summary>
        /// Gives a method of the class to put attributes on.
        /// </summary>
        /// <param name="method">A method of the class (<c>Run</c>), or a method it contributes to (<c>Owner.Run</c>), with the parameter types of an overload (<c>Run(float3, int)</c>).</param>
        /// <returns>The method, to put attributes on.</returns>
        public ShaderBlockMethod Method(string method)
        {
            ArgumentException.ThrowIfNullOrEmpty(method);
            var open = method.IndexOf('(');
            var parameters = open >= 0 ? method[(open + 1)..method.LastIndexOf(')')].Replace(" ", "") : null;
            var name = open >= 0 ? method[..open].Trim() : method.Trim();
            var dot = name.LastIndexOf('.');
            return new ShaderBlockMethod(this, dot >= 0 ? name[..dot] : null, name[(dot + 1)..], parameters);
        }

        internal void Add(ShaderLinkageRule rule)
        {
            rule.Shader = Shader;
            rule.Source ??= source;
            add(rule);
        }
    }

    /// <summary>
    /// A method of a <see cref="ShaderBlock"/>, with its attributes.
    /// </summary>
    public sealed class ShaderBlockMethod
    {
        private readonly ShaderBlock block;
        private readonly string owner;
        private readonly string method;
        private readonly string parameters;

        internal ShaderBlockMethod(ShaderBlock block, string owner, string method, string parameters)
        {
            this.block = block;
            this.owner = owner;
            this.method = method;
            this.parameters = parameters;
        }

        /// <summary>
        /// <c>[MixinOrder(A, B, C)] Method;</c>: these classes run in this order, in their phase. A class that isn't in the effect is left out.
        /// </summary>
        /// <param name="classes">The classes, in their running order.</param>
        /// <returns>This method.</returns>
        /// <exception cref="ArgumentException">The method is one the class contributes to, or fewer than two classes are given.</exception>
        public ShaderBlockMethod MixinOrder(params ReadOnlySpan<string> classes)
        {
            Check(isOnContribution: false, "[MixinOrder(A, B)] lists classes on a method of the class. On a method it contributes to, use MixinOrderAfter or MixinOrderBefore");
            if (classes.Length < 2)
                throw new ArgumentException("[MixinOrder] lists at least two classes.", nameof(classes));
            return Add(new() { Kind = ShaderLinkageKind.MixinOrder, Names = [.. classes] });
        }

        /// <summary>
        /// <c>[MixinOrder(After = { A, B })] Owner.Method;</c>: the class runs after these classes, in its phase.
        /// </summary>
        /// <param name="classes">The classes it runs after.</param>
        /// <returns>This method.</returns>
        /// <exception cref="ArgumentException">The method is not one the class contributes to, or no class is given.</exception>
        public ShaderBlockMethod MixinOrderAfter(params ReadOnlySpan<string> classes)
        {
            Check(isOnContribution: true, "[MixinOrder(After = A)] goes on a method the class contributes to, written Owner.Method. On a method of the class, list the classes with MixinOrder");
            if (classes.Length == 0)
                throw new ArgumentException("[MixinOrder(After = ...)] takes the classes it runs after.", nameof(classes));
            return Add(new() { Kind = ShaderLinkageKind.MixinOrder, After = [.. classes] });
        }

        /// <summary>
        /// <c>[MixinOrder(Before = { A, B })] Owner.Method;</c>: the class runs before these classes, in its phase.
        /// </summary>
        /// <param name="classes">The classes it runs before.</param>
        /// <returns>This method.</returns>
        /// <exception cref="ArgumentException">The method is not one the class contributes to, or no class is given.</exception>
        public ShaderBlockMethod MixinOrderBefore(params ReadOnlySpan<string> classes)
        {
            Check(isOnContribution: true, "[MixinOrder(Before = A)] goes on a method the class contributes to, written Owner.Method. On a method of the class, list the classes with MixinOrder");
            if (classes.Length == 0)
                throw new ArgumentException("[MixinOrder(Before = ...)] takes the classes it runs before.", nameof(classes));
            return Add(new() { Kind = ShaderLinkageKind.MixinOrder, Before = [.. classes] });
        }

        /// <summary>
        /// <c>[Unbind(Phases = { P, Q })] Method;</c>: these phases don't run in this effect.
        /// </summary>
        /// <param name="phases">The phases.</param>
        /// <returns>This method.</returns>
        /// <exception cref="ArgumentException">The method is one the class contributes to, or no phase is given.</exception>
        public ShaderBlockMethod UnbindPhases(params ReadOnlySpan<string> phases)
        {
            Check(isOnContribution: false, "[Unbind] goes on a method of the class");
            if (phases.Length == 0)
                throw new ArgumentException("[Unbind(Phases = ...)] takes phases.", nameof(phases));
            return Add(new() { Kind = ShaderLinkageKind.Unbind, Phases = [.. phases] });
        }

        /// <summary>
        /// <c>[Unbind(Mixins = { X, Y })] Method;</c>: the contributions of these classes don't run in this effect.
        /// </summary>
        /// <param name="mixins">The classes.</param>
        /// <returns>This method.</returns>
        /// <exception cref="ArgumentException">The method is one the class contributes to, or no class is given.</exception>
        public ShaderBlockMethod UnbindMixins(params ReadOnlySpan<string> mixins)
        {
            Check(isOnContribution: false, "[Unbind] goes on a method of the class");
            if (mixins.Length == 0)
                throw new ArgumentException("[Unbind(Mixins = ...)] takes classes.", nameof(mixins));
            return Add(new() { Kind = ShaderLinkageKind.Unbind, Mixins = [.. mixins] });
        }

        /// <summary>
        /// <c>[Phase(P)] Owner.Method;</c>: the class runs in this phase. With <paramref name="after"/> or <paramref name="before"/>, it adds
        /// the phase there, and the name of an added phase has a prefix: <c>Vendor.Wind</c>.
        /// </summary>
        /// <param name="phase">The phase.</param>
        /// <param name="after">The phase the added phase comes after, or null.</param>
        /// <param name="before">The phase the added phase comes before, or null.</param>
        /// <returns>This method.</returns>
        /// <exception cref="ArgumentException">The method is not one the class contributes to.</exception>
        public ShaderBlockMethod Phase(string phase, string after = null, string before = null)
        {
            Check(isOnContribution: true, "[Phase] goes on a method the class contributes to, written Owner.Method");
            ArgumentException.ThrowIfNullOrEmpty(phase);
            return Add(new() { Kind = ShaderLinkageKind.Phase, Phase = phase, After = after != null ? [after] : [], Before = before != null ? [before] : [] });
        }

        private void Check(bool isOnContribution, string problem)
        {
            if ((owner != null) != isOnContribution)
                throw new ArgumentException($"extend shader {block.Shader} {{ ... {owner}{(owner != null ? "." : "")}{method}; }}: {problem}.");
        }

        private ShaderBlockMethod Add(ShaderLinkageRule rule)
        {
            rule.Owner = owner;
            rule.Method = method;
            rule.Parameters = parameters;
            block.Add(rule);
            return this;
        }
    }
}
