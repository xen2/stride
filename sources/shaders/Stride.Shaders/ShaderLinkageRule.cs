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
    }
}
