// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.Linq;

using Stride.Core;

namespace Stride.Shaders
{
    /// <summary>
    /// The attribute that a block of an effect gives to a method of a class.
    /// </summary>
    public enum ShaderLinkageKind
    {
        /// <summary>
        /// <c>[MixinOrder(A, B)] Method;</c>: these classes run in this order, in their phase. On a method the class contributes to,
        /// <c>[MixinOrder(After = A, Before = B)] Owner.Method;</c>: the class runs after A and before B, in its phase.
        /// </summary>
        MixinOrder,

        /// <summary><c>[Unbind(Phases = { P }, Mixins = { X })] Method;</c>: what doesn't run in this effect.</summary>
        Unbind,

        /// <summary><c>[Phase(P)] Owner.Method;</c>: the phase the class runs in, and with <c>After</c> or <c>Before</c>, the phase it adds.</summary>
        Phase,
    }

    /// <summary>
    /// One attribute of a block of an effect, <c>extend shader Shader { [Attribute] Method; }</c>, about one method of a class.
    /// </summary>
    [DataContract("ShaderLinkageRule")]
    public sealed class ShaderLinkageRule : IEquatable<ShaderLinkageRule>
    {
        /// <summary>
        /// Gets or sets the attribute.
        /// </summary>
        [DataMember(0)]
        public ShaderLinkageKind Kind { get; set; }

        /// <summary>
        /// Gets or sets the class of the block, by full name.
        /// </summary>
        [DataMember(10)]
        public string Shader { get; set; }

        /// <summary>
        /// Gets or sets the class of the first definition of a method that <see cref="Shader"/> contributes to, or null when the rule is
        /// about a method of <see cref="Shader"/> itself.
        /// </summary>
        [DataMember(20)]
        public string Owner { get; set; }

        /// <summary>
        /// Gets or sets the name of the method.
        /// </summary>
        [DataMember(30)]
        public string Method { get; set; }

        /// <summary>
        /// Gets or sets the parameter types of an overload, separated by commas, or null when the method has one signature.
        /// </summary>
        [DataMember(40)]
        public string Parameters { get; set; }

        /// <summary>
        /// Gets or sets the phase of a <see cref="ShaderLinkageKind.Phase"/> rule.
        /// </summary>
        [DataMember(50)]
        public string Phase { get; set; }

        /// <summary>
        /// Gets or sets the classes of <c>[MixinOrder(A, B)]</c>, in their running order.
        /// </summary>
        [DataMember(60)]
        public List<string> Names { get; set; } = [];

        /// <summary>
        /// Gets or sets the classes this class runs after, or for <see cref="ShaderLinkageKind.Phase"/> the phase the added phase comes after.
        /// </summary>
        [DataMember(70)]
        public List<string> After { get; set; } = [];

        /// <summary>
        /// Gets or sets the classes this class runs before, or for <see cref="ShaderLinkageKind.Phase"/> the phase the added phase comes before.
        /// </summary>
        [DataMember(80)]
        public List<string> Before { get; set; } = [];

        /// <summary>
        /// Gets or sets the phases of <c>[Unbind]</c>.
        /// </summary>
        [DataMember(90)]
        public List<string> Phases { get; set; } = [];

        /// <summary>
        /// Gets or sets the classes of <c>[Unbind]</c>.
        /// </summary>
        [DataMember(100)]
        public List<string> Mixins { get; set; } = [];

        /// <summary>
        /// Gets or sets where the rule comes from: 0 for the effect, then one more for each layer, in the order layers run. A rule of an
        /// outer level replaces the opposite order of an inner one.
        /// </summary>
        [DataMember(110)]
        public int Level { get; set; }

        /// <summary>
        /// Gets or sets where the rule is written, for messages. It is not part of what the rule is.
        /// </summary>
        [DataMember(120)]
        public string Source { get; set; }

        /// <summary>
        /// Gets whether the rule is about a method the class contributes to, written <c>Owner.Method</c>.
        /// </summary>
        public bool IsOnContribution => Owner != null;

        /// <inheritdoc/>
        public bool Equals(ShaderLinkageRule other) => other is not null && ToString() == other.ToString();

        /// <inheritdoc/>
        public override bool Equals(object obj) => Equals(obj as ShaderLinkageRule);

        /// <inheritdoc/>
        public override int GetHashCode() => ToString().GetHashCode();

        /// <inheritdoc/>
        public override string ToString() => $"{ToStatement()}{(Level > 0 ? $" (layer {Level})" : "")}";

        /// <summary>
        /// Gives the rule as an effect writes it, for a message.
        /// </summary>
        public string ToStatement() => $"extend shader {Shader} {{ [{Attribute()}] {Member()}; }}";

        /// <summary>
        /// Gives the method as a block writes it: <c>Run</c>, <c>Owner.Run</c>, or with the parameter types of an overload.
        /// </summary>
        public string Member() => $"{(Owner != null ? $"{Owner}." : "")}{Method}{(Parameters != null ? $"({Parameters})" : "")}";

        private string Attribute()
        {
            static string List(List<string> names) => names.Count == 1 ? names[0] : $"{{ {string.Join(", ", names)} }}";
            var keys = Kind switch
            {
                ShaderLinkageKind.MixinOrder when Names.Count > 0 => Names,
                ShaderLinkageKind.Unbind => new[] { ("Phases", Phases), ("Mixins", Mixins) }.Where(x => x.Item2.Count > 0).Select(x => $"{x.Item1} = {{ {string.Join(", ", x.Item2)} }}"),
                _ => (Kind == ShaderLinkageKind.Phase ? [Phase] : Enumerable.Empty<string>())
                    .Concat(new[] { ("After", After), ("Before", Before) }.Where(x => x.Item2.Count > 0).Select(x => $"{x.Item1} = {List(x.Item2)}")),
            };
            return $"{Kind}({string.Join(", ", keys)})";
        }
    }
}
