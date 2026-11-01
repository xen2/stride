// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.Linq;

using Stride.Rendering;

namespace Stride.Shaders
{
    /// <summary>
    /// Manages <see cref="IShaderMixinBuilder"/> and generation of shader mixins.
    /// </summary>
    public class ShaderMixinManager
    {
        /// <summary>
        /// The message of an effect declared twice: its full name, and who declares it each time.
        /// </summary>
        public const string DuplicateEffectMessage = "SDSL0302: effect {0} is declared twice: in {1} and in {2}. Rename one or put it in another namespace.";

        private static readonly Dictionary<string, IShaderMixinBuilder> RegisteredBuilders = new Dictionary<string, IShaderMixinBuilder>();

        /// <summary>
        /// Registers a <see cref="IShaderMixinBuilder"/> with the specified sdfx effect name.
        /// </summary>
        /// <param name="sdfxEffectName">Full name of the effect.</param>
        /// <param name="builder">The builder.</param>
        /// <exception cref="System.ArgumentNullException">
        /// sdfxEffectName
        /// or
        /// builder
        /// </exception>
        /// <exception cref="System.InvalidOperationException">Another type is registered with this name. The same type registers again when its assembly reloads.</exception>
        public static void Register(string sdfxEffectName, IShaderMixinBuilder builder)
        {
            if (sdfxEffectName == null)
                throw new ArgumentNullException("sdfxEffectName");

            if (builder == null)
                throw new ArgumentNullException("builder");

            lock (RegisteredBuilders)
            {
                if (RegisteredBuilders.TryGetValue(sdfxEffectName, out var existing) && !IsSameBuilder(existing, builder))
                    throw new InvalidOperationException(string.Format(DuplicateEffectMessage, sdfxEffectName, Describe(existing), Describe(builder)));

                RegisteredBuilders[sdfxEffectName] = builder;
            }
        }

        private static bool IsSameBuilder(IShaderMixinBuilder first, IShaderMixinBuilder second)
            => first.GetType().FullName == second.GetType().FullName && first.GetType().Assembly.GetName().Name == second.GetType().Assembly.GetName().Name;

        private static string Describe(IShaderMixinBuilder builder) => $"{builder.GetType().FullName} ({builder.GetType().Assembly.GetName().Name})";

        /// <summary>
        /// Determines whether the specified PDXFX effect is registered.
        /// </summary>
        /// <param name="sdfxEffectName">Name of the PDXFX effect.</param>
        /// <returns><c>true</c> if the specified PDXFX effect is registered; otherwise, <c>false</c>.</returns>
        /// <exception cref="System.ArgumentNullException">sdfxEffectName</exception>
        public static bool Contains(string sdfxEffectName)
        {
            if (sdfxEffectName == null) throw new ArgumentNullException("sdfxEffectName");

            lock (RegisteredBuilders)
            {
                return RegisteredBuilders.ContainsKey(GetEffectName(sdfxEffectName).Key);
            }
        }

        /// <summary>
        /// Tries to get a <see cref="IShaderMixinBuilder"/> by its name.
        /// </summary>
        /// <param name="sdfxEffectName">Name of the mixin.</param>
        /// <param name="builder">The builder instance found or null if not found.</param>
        /// <returns><c>true</c> if the builder was found, <c>false</c> otherwise.</returns>
        /// <exception cref="System.ArgumentNullException">sdfxEffectName</exception>
        public static bool TryGet(string sdfxEffectName, out IShaderMixinBuilder builder)
        {
            if (sdfxEffectName == null)
                throw new ArgumentNullException("sdfxEffectName");

            lock (RegisteredBuilders)
            {
                return RegisteredBuilders.TryGetValue(sdfxEffectName, out builder);
            }
        }

        /// <summary>
        /// Generates a <see cref="ShaderMixinSource" /> for the specified names and parameters.
        /// </summary>
        /// <param name="sdfxEffectName">The name.</param>
        /// <param name="properties">The properties.</param>
        /// <returns>The result of the mixin.</returns>
        /// <exception cref="System.ArgumentNullException">
        /// sdfxEffectName
        /// or
        /// properties
        /// </exception>
        /// <exception cref="System.ArgumentException">sdfxEffectName</exception>
        public static ShaderMixinSource Generate(string sdfxEffectName, ParameterCollection properties)
        {
            if (sdfxEffectName == null) throw new ArgumentNullException("sdfxEffectName");

            if (properties == null)
                throw new ArgumentNullException("properties");

            IShaderMixinBuilder builder;
            Dictionary<string, IShaderMixinBuilder> builders;
            string rootEffectName, childEffectName;
            lock (RegisteredBuilders)
            {
                // Get the effect name and child effect name "Namespace.RootEffectName.ChildEffectName"
                var effectName = GetEffectName(sdfxEffectName);
                rootEffectName = effectName.Key;
                childEffectName = effectName.Value;

                if (!RegisteredBuilders.TryGetValue(rootEffectName, out builder))
                    throw new ArgumentException(FindFullNames(rootEffectName) is { Count: > 0 } found
                        ? $"Effect [{rootEffectName}] not found. Did you mean {string.Join(" or ", found)}?"
                        : string.Format("Xkfx effect [{0}] not found", rootEffectName), "sdfxEffectName");

                builders = new Dictionary<string, IShaderMixinBuilder>(RegisteredBuilders);
            }

            // TODO cache mixin context and avoid to recreate one (check if if thread concurrency could occur here)
            var mixinTree = new ShaderMixinSource() { Name = string.IsNullOrEmpty(childEffectName) ? rootEffectName : $"{rootEffectName}.{childEffectName}" };
            var context = new ShaderMixinContext(mixinTree, properties, builders) { ChildEffectName = childEffectName };
            try
            {
                builder.Generate(mixinTree, context);
            }
            catch (ShaderMixinDiscardException)
            {
                // We don't rethrow as this exception is on purpose to early exit/escape from a shader mixin
            }
            return mixinTree;
        }

        // The longest registered name is the root, the rest is the child. Takes the lock of RegisteredBuilders.
        private static KeyValuePair<string, string> GetEffectName(string sdfxEffectName)
        {
            for (var end = sdfxEffectName.Length; end > 0; end = sdfxEffectName.LastIndexOf('.', end - 1))
            {
                if (RegisteredBuilders.ContainsKey(sdfxEffectName.Substring(0, end)))
                    return new KeyValuePair<string, string>(sdfxEffectName.Substring(0, end), end < sdfxEffectName.Length ? sdfxEffectName.Substring(end + 1) : string.Empty);
            }

            return new KeyValuePair<string, string>(sdfxEffectName, string.Empty);
        }

        /// <summary>
        /// The full names of the registered effects with this short name, for the message of an error or a tool that takes short names.
        /// A short name with a child, <c>Root.Child</c>, gives the full names with that child.
        /// </summary>
        public static IReadOnlyList<string> FindFullNames(string shortName)
        {
            var dot = shortName.IndexOf('.');
            var root = dot < 0 ? shortName : shortName.Substring(0, dot);
            var child = dot < 0 ? string.Empty : shortName.Substring(dot);
            lock (RegisteredBuilders)
            {
                return RegisteredBuilders.Keys.Where(x => x.Substring(x.LastIndexOf('.') + 1) == root).OrderBy(x => x, StringComparer.Ordinal).Select(x => x + child).ToList();
            }
        }

        /// <summary>
        /// Un-register all registered <see cref="IShaderMixinBuilder"/>.
        /// </summary>
        public static void UnRegisterAll()
        {
            lock (RegisteredBuilders)
            {
                RegisteredBuilders.Clear();
            }
        }
    }
}
