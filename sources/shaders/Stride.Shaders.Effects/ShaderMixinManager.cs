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

        /// <summary>
        /// The message of a layer whose effect no loaded assembly declares: its full name. The generator gives this error too, when a project builds.
        /// </summary>
        public const string LayerWithoutEffectMessage = "SDSL0310: extend effect {0}: no effect has this name. Check the name and the namespace, and reference the project that declares the effect.";

        private static readonly Dictionary<string, IShaderMixinBuilder> RegisteredBuilders = new Dictionary<string, IShaderMixinBuilder>();

        /// <summary>
        /// The parts of an effect that assemblies declare: the effect, then its layers. The statements add up, and the blocks of each layer
        /// are one level further out than the ones before it.
        /// </summary>
        private sealed class LayeredEffectBuilder : IShaderMixinBuilder
        {
            public string Name { get; init; }

            public IShaderMixinBuilder Effect { get; set; }

            // Replaced, not changed: an assembly can load while another thread generates this effect
            public IReadOnlyList<IShaderMixinBuilder> Layers { get; private set; } = [];

            private readonly Dictionary<string, string[]> writtenReferences = [];

            public void Generate(ShaderMixinSource mixin, ShaderMixinContext context)
            {
                if (Effect == null)
                    throw new InvalidOperationException(string.Format(LayerWithoutEffectMessage, Name));

                Effect.Generate(mixin, context);
                foreach (var layer in Layers)
                {
                    var level = context.EnterLayer();
                    layer.Generate(mixin, context);
                    context.LeaveLayer(level);
                }
            }

            /// <summary>
            /// An assembly that references the assembly of another layer comes after it, so that a project wins over the plugins it uses:
            /// a reference in its metadata, or one the generator wrote. Layers that don't know each other are in the order of their assembly names.
            /// </summary>
            public void AddLayer(IShaderMixinBuilder builder, string[] references)
            {
                writtenReferences[builder.GetType().Assembly.GetName().Name] = references;
                var layers = Layers.Where(x => !IsSameBuilder(x, builder)).Append(builder).ToList();
                var assemblies = layers.Select(x => x.GetType().Assembly).ToList();
                int Depth(System.Reflection.Assembly assembly, HashSet<string> path)
                {
                    var depth = 0;
                    foreach (var referenced in assembly.GetReferencedAssemblies().Select(x => x.Name).Concat(writtenReferences.GetValueOrDefault(assembly.GetName().Name, [])))
                    {
                        var layer = assemblies.Find(x => x.GetName().Name == referenced);
                        if (layer != null && path.Add(referenced))
                        {
                            depth = Math.Max(depth, Depth(layer, path) + 1);
                            path.Remove(referenced);
                        }
                    }
                    return depth;
                }
                Layers = layers.OrderBy(x => Depth(x.GetType().Assembly, [])).ThenBy(x => x.GetType().Assembly.GetName().Name, StringComparer.Ordinal).ToList();
            }
        }

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
                if (RegisteredBuilders.TryGetValue(sdfxEffectName, out var existing) && existing is LayeredEffectBuilder layered)
                {
                    // The effect itself, with the layers that were there before it
                    if (layered.Effect != null && !IsSameBuilder(layered.Effect, builder))
                        throw new InvalidOperationException(string.Format(DuplicateEffectMessage, sdfxEffectName, Describe(layered.Effect), Describe(builder)));

                    layered.Effect = builder;
                    return;
                }
                if (existing != null && !IsSameBuilder(existing, builder))
                    throw new InvalidOperationException(string.Format(DuplicateEffectMessage, sdfxEffectName, Describe(existing), Describe(builder)));

                RegisteredBuilders[sdfxEffectName] = builder;
            }
        }

        /// <summary>
        /// Registers an <c>extend effect</c>: a layer on top of the effect of this name. The effect can register after it, as assemblies load in any order.
        /// </summary>
        /// <param name="sdfxEffectName">Full name of the effect.</param>
        /// <param name="builder">The builder.</param>
        /// <param name="references">The assemblies with a layer that the project of this builder references, also when its code uses none of their types.</param>
        /// <exception cref="System.ArgumentNullException">sdfxEffectName or builder</exception>
        public static void RegisterLayer(string sdfxEffectName, IShaderMixinBuilder builder, params string[] references)
        {
            if (sdfxEffectName == null)
                throw new ArgumentNullException("sdfxEffectName");

            if (builder == null)
                throw new ArgumentNullException("builder");

            lock (RegisteredBuilders)
            {
                RegisteredBuilders.TryGetValue(sdfxEffectName, out var existing);
                if (existing is not LayeredEffectBuilder layered)
                    RegisteredBuilders[sdfxEffectName] = layered = new LayeredEffectBuilder { Name = sdfxEffectName, Effect = existing };

                layered.AddLayer(builder, references);
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
        /// Generates an <see cref="EffectSource" /> for the specified names and parameters.
        /// </summary>
        /// <param name="sdfxEffectName">The name.</param>
        /// <param name="properties">The properties.</param>
        /// <returns>The effect.</returns>
        /// <exception cref="System.ArgumentNullException">
        /// sdfxEffectName
        /// or
        /// properties
        /// </exception>
        /// <exception cref="System.ArgumentException">sdfxEffectName</exception>
        public static EffectSource Generate(string sdfxEffectName, ParameterCollection properties)
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
            var effect = new EffectSource(new ShaderMixinSource { Name = string.IsNullOrEmpty(childEffectName) ? rootEffectName : $"{rootEffectName}.{childEffectName}" });
            var context = new ShaderMixinContext(effect, properties, builders) { ChildEffectName = childEffectName };
            try
            {
                builder.Generate(effect.Root, context);
            }
            catch (ShaderMixinDiscardException)
            {
                // We don't rethrow as this exception is on purpose to early exit/escape from a shader mixin
            }
            return effect;
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
