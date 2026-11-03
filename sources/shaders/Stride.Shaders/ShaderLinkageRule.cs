// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.Linq;

using Stride.Core;

namespace Stride.Shaders
{
    /// <summary>
    /// What a linkage rule of an effect says about an extension point.
    /// </summary>
    public enum ShaderLinkageKind
    {
        /// <summary><c>order Owner.Method.Phase: X, Y;</c>: the running order of classes of one phase.</summary>
        Order,

        /// <summary><c>bind Owner.Method.Phase = X;</c>: the phase of a class that doesn't name one.</summary>
        Bind,

        /// <summary><c>unbind Owner.Method.Phase;</c> or <c>unbind Owner.Method = X;</c>: what doesn't run in this effect.</summary>
        Unbind,

        /// <summary><c>phase Owner.Method.Phase after A before B;</c>: a phase that the first definition doesn't declare.</summary>
        Phase,
    }

    /// <summary>
    /// A linkage rule of an effect.
    /// </summary>
    [DataContract("ShaderLinkageRule")]
    public sealed class ShaderLinkageRule : IEquatable<ShaderLinkageRule>
    {
        /// <summary>
        /// Creates a rule, with the names its kind needs.
        /// </summary>
        /// <exception cref="ArgumentException">Too few names for this kind of rule.</exception>
        public static ShaderLinkageRule Create(ShaderLinkageKind kind, string owner, string method, string phase, params string[] names)
        {
            var (needed, what) = kind switch
            {
                ShaderLinkageKind.Phase => (2, "the phases after and before it, empty when not given"),
                ShaderLinkageKind.Bind => (1, "the class to bind"),
                ShaderLinkageKind.Order => (1, "the classes to order"),
                ShaderLinkageKind.Unbind when phase == null => (1, "the class to unbind"),
                _ => (0, ""),
            };
            if (names.Length < needed)
                throw new ArgumentException($"A linkage rule {kind} about {owner}.{method} takes {what}, but it has {names.Length} of {needed} names.", nameof(names));

            return new ShaderLinkageRule { Kind = kind, Owner = owner, Method = method, Phase = phase, Names = [.. names] };
        }

        /// <summary>
        /// Gets or sets what the rule says.
        /// </summary>
        [DataMember(0)]
        public ShaderLinkageKind Kind { get; set; }

        /// <summary>
        /// Gets or sets the class of the first definition of the method.
        /// </summary>
        [DataMember(10)]
        public string Owner { get; set; }

        /// <summary>
        /// Gets or sets the name of the method.
        /// </summary>
        [DataMember(20)]
        public string Method { get; set; }

        /// <summary>
        /// Gets or sets the phase, or null for <c>unbind Owner.Method = X;</c>.
        /// </summary>
        [DataMember(30)]
        public string Phase { get; set; }

        /// <summary>
        /// Gets or sets the names of the rule: classes, or for <see cref="ShaderLinkageKind.Phase"/> the phases after and before it, empty when not given.
        /// </summary>
        [DataMember(40)]
        public List<string> Names { get; set; } = [];

        /// <inheritdoc/>
        public bool Equals(ShaderLinkageRule other)
            => other is not null && Kind == other.Kind && Owner == other.Owner && Method == other.Method && Phase == other.Phase && Names.SequenceEqual(other.Names);

        /// <inheritdoc/>
        public override bool Equals(object obj) => Equals(obj as ShaderLinkageRule);

        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(Kind, Owner, Method, Phase, string.Join(",", Names));

        /// <inheritdoc/>
        public override string ToString() => $"{Kind} {Owner}.{Method}.{Phase}: {string.Join(", ", Names)}";

        /// <summary>
        /// Gives the rule as an effect writes it, for a message.
        /// </summary>
        public string ToStatement() => Kind switch
        {
            ShaderLinkageKind.Order => $"order {Owner}.{Method}.{Phase}: {string.Join(", ", Names)}",
            ShaderLinkageKind.Bind => $"bind {Owner}.{Method}.{Phase} = {string.Join(", ", Names)}",
            ShaderLinkageKind.Unbind when Phase == null => $"unbind {Owner}.{Method} = {string.Join(", ", Names)}",
            ShaderLinkageKind.Unbind => $"unbind {Owner}.{Method}.{Phase}",
            _ => $"phase {Owner}.{Method}.{Phase}{(Names[0].Length > 0 ? $" after {Names[0]}" : "")}{(Names[1].Length > 0 ? $" before {Names[1]}" : "")}",
        };
    }
}
