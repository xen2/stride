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
        /// Gets the linkage rules of this effect: orders, bindings and phases of its extension points.
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
        /// Adds a linkage rule to this effect.
        /// </summary>
        /// <param name="kind">What the rule says.</param>
        /// <param name="owner">The class of the first definition of the method.</param>
        /// <param name="method">The name of the method.</param>
        /// <param name="phase">The phase, or null.</param>
        /// <param name="names">The names of the rule.</param>
        /// <exception cref="System.ArgumentException">Too few names for this kind of rule.</exception>
        public void AddLinkageRule(ShaderLinkageKind kind, string owner, string method, string phase, params string[] names)
        {
            Linkage.Add(ShaderLinkageRule.Create(kind, owner, method, phase, names));
        }

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
