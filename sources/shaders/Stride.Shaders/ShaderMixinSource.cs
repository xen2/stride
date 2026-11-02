// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;

using Stride.Core;
using Stride.Core.Annotations;

namespace Stride.Shaders
{
    /// <summary>
    /// A mixin performing a combination of <see cref="ShaderClassCode"/> and other mixins.
    /// </summary>
    [DataContract("ShaderMixinSource")]
    [NonIdentifiableCollectionItems]
    public sealed class ShaderMixinSource : ShaderSource, IEquatable<ShaderMixinSource>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ShaderMixinSource"/> class.
        /// </summary>
        public ShaderMixinSource()
        {
            Mixins = new List<ShaderClassCode>();
            Compositions = new Core.Collections.SortedList<string, ShaderSource>();
            Macros = new List<ShaderMacro>();
            Linkage = new List<ShaderLinkageRule>();
        }

        /// <summary>
        /// Gets or sets the name of the sdfx effect linked to this node.
        /// </summary>
        /// <value>The name of the sdfx effect.</value>
        [DataMember(0)]
        [DefaultValue(null)]
        public string Name { get; set; }

        [DataMemberIgnore]
        public ShaderMixinSource Parent { get; set; }

        // <summary>
        // Gets or sets the name of this mixin source (if this ShaderMixinSource was generated from a <see cref="ShaderMixinGeneratorSource"/>,
        // it contains the name of <see cref="ShaderMixinGeneratorSource.Name"/>.
        // </summary>
        // <value>The name.</value>
        //public string Name { get; set; }

        /// <summary>
        /// Gets or sets the mixins.
        /// </summary>
        /// <value>The mixins.</value>
        [DataMember(10)]
        public List<ShaderClassCode> Mixins { get; set; }

        /// <summary>
        /// Gets or sets the compositions.
        /// </summary>
        /// <value>The compositions.</value>
        [DataMember(20)]
        public Core.Collections.SortedList<string, ShaderSource> Compositions { get; set; }

        /// <summary>
        /// Gets or sets the macros.
        /// </summary>
        /// <value>The macros.</value>
        [DataMember(30)]
        public List<ShaderMacro> Macros { get; set; }

        /// <summary>
        /// Gets or sets the linkage rules of the effect: the attributes its blocks give to the methods of classes.
        /// </summary>
        /// <remarks>
        /// Not serialized: compiled assets hold mixins in binary form, and their build cache doesn't see a change of that form. Only a
        /// tree built by an effect has rules, so what needs them adds them itself: the hash of a compiled effect and the remote compiler.
        /// </remarks>
        [DataMemberIgnore]
        public List<ShaderLinkageRule> Linkage { get; set; }

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
        /// Adds a composition to this mixin.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="shaderSource">The shader source.</param>
        public void AddComposition(string name, ShaderSource shaderSource)
        {
            Compositions[name] = shaderSource;
        }

        /// <summary>
        /// Adds a composition to this mixin.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="shaderSourceElement">The shader source element.</param>
        /// <returns>Returns the index of the composition in the array.</returns>
        public int AddCompositionToArray(string name, ShaderSource shaderSourceElement)
        {
            ShaderSource shaderSource;
            if (!Compositions.TryGetValue(name, out shaderSource))
                Compositions.Add(name, shaderSource = new ShaderArraySource());

            var shaderArraySource = (ShaderArraySource)shaderSource;
            shaderArraySource.Add(shaderSourceElement);
            return shaderArraySource.Values.Count - 1;
        }

        /// <summary>
        /// Adds a macro to this mixin.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="value">The value.</param>
        public void AddMacro(string name, object value)
        {
            Macros.Add(new ShaderMacro(name, value));
        }

        /// <summary>
        /// Clones from the specified <see cref="ShaderMixinSource"/>.
        /// </summary>
        /// <param name="parent">The parent mixin to clone from.</param>
        /// <exception cref="System.ArgumentNullException">parent</exception>
        public void CloneFrom(ShaderMixinSource parent)
        {
            if (parent == null)
                throw new ArgumentNullException("parent", $"Cannot clone mixin [{Name}] from a null parent");

            Mixins.AddRange(parent.Mixins);
            Macros.AddRange(parent.Macros);
            Linkage.AddRange(parent.Linkage);
            foreach (var shaderBasic in parent.Compositions)
            {
                Compositions[shaderBasic.Key] = shaderBasic.Value;
            }
        }

        /// <summary>
        /// Clones from the specified <see cref="ShaderMixinSource"/>. Clones members too.
        /// </summary>
        /// <param name="parent">The parent mixin to clone from.</param>
        /// <exception cref="System.ArgumentNullException">parent</exception>
        public void DeepCloneFrom(ShaderMixinSource parent)
        {
            if (parent == null)
                throw new ArgumentNullException("parent", $"Cannot deep clone mixin [{Name}] from a null parent");

            foreach (var mixin in parent.Mixins)
                Mixins.Add((ShaderClassCode)mixin.Clone());
            Macros.AddRange(parent.Macros);
            Linkage.AddRange(parent.Linkage);
            foreach (var shaderBasic in parent.Compositions)
            {
                Compositions[shaderBasic.Key] = (ShaderSource)shaderBasic.Value.Clone();
            }
        }

        public override bool Equals(object against)
        {
            if (ReferenceEquals(null, against)) return false;
            if (ReferenceEquals(this, against)) return true;
            if (against.GetType() != this.GetType()) return false;
            return Equals((ShaderMixinSource)against);
        }

        public bool Equals(ShaderMixinSource other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;

            // Doesn't check for Parent for Children
            return
                Mixins.SequenceEqualAllowNull(other.Mixins) &&
                Macros.SequenceEqualAllowNull(other.Macros) &&
                Linkage.SequenceEqualAllowNull(other.Linkage) &&
                Compositions.SequenceEqualAllowNull(other.Compositions);
        }
        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = 0;
                foreach (var current in Mixins)
                    hashCode = (hashCode * 397) ^ (current?.GetHashCode() ?? 0);
                foreach (var current in Macros)
                    hashCode = (hashCode * 397) ^ current.GetHashCode();
                foreach (var current in Compositions)
                    hashCode = (hashCode * 397) ^ current.GetHashCode();
                foreach (var current in Linkage)
                    hashCode = (hashCode * 397) ^ current.GetHashCode();
                return hashCode;
            }
        }

        public override object Clone()
        {
            var newMixin = (ShaderMixinSource)MemberwiseClone();
            newMixin.Compositions = Compositions == null ? null : ToSortedList(Compositions.Select(x => new KeyValuePair<string, ShaderSource>(x.Key, (ShaderSource)x.Value.Clone())));
            newMixin.Mixins = Mixins == null ? null : Mixins.Select(x => (ShaderClassCode)x.Clone()).ToList();
            newMixin.Macros = Macros == null ? null : new List<ShaderMacro>(Macros.ToArray());
            newMixin.Linkage = Linkage == null ? null : new List<ShaderLinkageRule>(Linkage);
            return newMixin;
        }

        private static Core.Collections.SortedList<TKey, TValue> ToSortedList<TKey, TValue>(IEnumerable<KeyValuePair<TKey, TValue>> list)
        {
            var values = new Core.Collections.SortedList<TKey, TValue>();
            foreach (var item in list)
                values.Add(item.Key, item.Value);
            return values;
        }

        public override string ToString()
        {
            var result = new StringBuilder();

            result.Append("mixin");

            if (Mixins != null && Mixins.Count > 0)
            {
                result.Append(" ");
                for (int i = 0; i < Mixins.Count; i++)
                {
                    if (i > 0)
                        result.Append(", ");
                    result.Append(Mixins[i]);
                }
            }

            if (Compositions != null && Compositions.Count > 0)
            {
                result.Append(" [");
                var keys = Compositions.Keys.ToList();
                keys.Sort();
                for (int i = 0; i < keys.Count; i++)
                {
                    var key = keys[i];
                    if (i > 0)
                        result.Append(", ");
                    result.AppendFormat("{{{0} = {1}}}", key, Compositions[key]);
                }
                result.Append("]");
            }
            return result.ToString();
        }

        internal bool ShouldSerializeMacros()
        {
            // If collection is non-null and empty, skip serialization
            return Macros == null || Macros.Count != 0;
        }

        internal bool ShouldSerializeMixins()
        {
            // If collection is non-null and empty, skip serialization
            return Mixins == null || Mixins.Count != 0;
        }

        internal bool ShouldSerializeCompositions()
        {
            // If collection is non-null and empty, skip serialization
            return Compositions == null || Compositions.Count != 0;
        }
    }
}
