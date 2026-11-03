// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stride.Shaders
{
    /// <summary>
    /// What an effect compiler takes to give one effect: the tree of shaders, and the macros and linkage rules of the whole effect.
    /// </summary>
    public sealed class EffectSource : IEquatable<EffectSource>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="EffectSource"/> class.
        /// </summary>
        /// <param name="root">The root of the tree of shaders.</param>
        public EffectSource(ShaderMixinSource root)
        {
            Root = root ?? throw new ArgumentNullException(nameof(root));
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="EffectSource"/> class, for one class named after it.
        /// </summary>
        /// <param name="root">The class.</param>
        public EffectSource(ShaderClassCode root)
        {
            ArgumentNullException.ThrowIfNull(root);
            Root = new ShaderMixinSource { Name = root.ClassName, Mixins = { root } };
        }

        /// <summary>
        /// Gets the root of the tree of shaders.
        /// </summary>
        public ShaderMixinSource Root { get; }

        /// <summary>
        /// Gets the macros that every class of this effect compiles with.
        /// </summary>
        public List<ShaderMacro> Macros { get; } = [];

        /// <summary>
        /// Gets the linkage rules of this effect: the attributes its blocks give to the methods of classes.
        /// </summary>
        public List<ShaderLinkageRule> Linkage { get; } = [];

        /// <summary>
        /// Gets the name of the effect.
        /// </summary>
        public string Name => Root.Name;

        /// <summary>
        /// Adds a macro to this effect.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="value">The value.</param>
        public void AddMacro(string name, object value) => Macros.Add(new ShaderMacro(name, value));

        /// <summary>
        /// Starts a block of this effect, <c>extend shader Shader { ... }</c>, to give attributes to the methods of a class.
        /// </summary>
        /// <param name="shader">The class, by full name, or by a name it ends with.</param>
        /// <param name="source">Where the block is written, for messages.</param>
        /// <returns>The block.</returns>
        public ShaderBlock ExtendShader(string shader, string source = null) => new(shader, source, Linkage.Add);

        /// <summary>
        /// <c>extend shader Owner { [MixinOrder(A, B)] Method; }</c>: these classes run in this order, in their phase.
        /// </summary>
        /// <param name="owner">The class of the first definition of the method.</param>
        /// <param name="method">The method, with the parameter types of an overload.</param>
        /// <param name="classes">The classes, in their running order.</param>
        public void MixinOrder(string owner, string method, params ReadOnlySpan<string> classes) => ExtendShader(owner).Method(method).MixinOrder(classes);

        /// <summary>
        /// <c>extend shader Owner { [Unbind(Phases = { P, Q })] Method; }</c>: these phases don't run in this effect.
        /// </summary>
        /// <param name="owner">The class of the first definition of the method.</param>
        /// <param name="method">The method, with the parameter types of an overload.</param>
        /// <param name="phases">The phases.</param>
        public void UnbindPhases(string owner, string method, params ReadOnlySpan<string> phases) => ExtendShader(owner).Method(method).UnbindPhases(phases);

        /// <summary>
        /// <c>extend shader Owner { [Unbind(Mixins = { X, Y })] Method; }</c>: the contributions of these classes don't run in this effect.
        /// </summary>
        /// <param name="owner">The class of the first definition of the method.</param>
        /// <param name="method">The method, with the parameter types of an overload.</param>
        /// <param name="mixins">The classes.</param>
        public void UnbindMixins(string owner, string method, params ReadOnlySpan<string> mixins) => ExtendShader(owner).Method(method).UnbindMixins(mixins);

        /// <summary>
        /// Gives a copy of this effect, its tree included.
        /// </summary>
        public EffectSource Clone()
        {
            var clone = new EffectSource((ShaderMixinSource)Root.Clone());
            clone.Macros.AddRange(Macros);
            clone.Linkage.AddRange(Linkage);
            return clone;
        }

        public bool Equals(EffectSource other) => other is not null && (ReferenceEquals(this, other) || (Root.Equals(other.Root) && Macros.SequenceEqual(other.Macros) && Linkage.SequenceEqual(other.Linkage)));

        public override bool Equals(object obj) => Equals(obj as EffectSource);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Root);
            foreach (var macro in Macros)
                hash.Add(macro);
            foreach (var rule in Linkage)
                hash.Add(rule);
            return hash.ToHashCode();
        }
    }
}
