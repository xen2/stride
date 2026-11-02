// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;

namespace Stride.Shaders
{
    /// <summary>
    /// The structural axes of an effect: the parameters of its <c>feature</c> and <c>optional</c> statements. A tool reads them from
    /// the metadata, to list the permutations of an effect without running it.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class EffectAxesAttribute : Attribute
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="EffectAxesAttribute"/> class.
        /// </summary>
        /// <param name="axes">The parameters, as written in the effect.</param>
        public EffectAxesAttribute(params string[] axes)
        {
            Axes = axes;
        }

        /// <summary>
        /// Gets the parameters, as written in the effect.
        /// </summary>
        public string[] Axes { get; }
    }
}
