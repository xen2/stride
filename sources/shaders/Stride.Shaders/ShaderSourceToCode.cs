// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Stride.Shaders
{
    /// <summary>
    /// Generates C# code to easily recreate a specific <see cref="ShaderSource"/> (i.e. for a unit test).
    /// </summary>
    public static class ShaderSourceToCode
    {
        public static string ToCode(this ShaderSource source)
        {
            var sb = new StringBuilder();
            ToCode(source, sb);
            return sb.ToString();
        }

        public static string ToCode(this EffectSource effect)
        {
            var sb = new StringBuilder("new EffectSource(");
            ToCode(effect.Root, sb);
            sb.Append(")");
            if (effect.Macros.Count > 0 || effect.Linkage.Count > 0)
            {
                sb.Append($"\n{{\n");
                if (effect.Macros.Count > 0)
                {
                    sb.Append($"Macros = {{ ");
                    foreach (var macro in effect.Macros)
                        sb.Append($"new ShaderMacro(\"{macro.Name}\", \"{macro.Definition}\"),");
                    sb.Append($" }},\n");
                }
                if (effect.Linkage.Count > 0)
                {
                    sb.Append($"Linkage =\n{{\n");
                    foreach (var rule in effect.Linkage)
                    {
                        sb.Append($"new ShaderLinkageRule {{ Kind = ShaderLinkageKind.{rule.Kind}, Shader = \"{rule.Shader}\"");
                        foreach (var (name, value) in new[] { ("Owner", rule.Owner), ("Method", rule.Method), ("Parameters", rule.Parameters), ("Phase", rule.Phase) })
                        {
                            if (value != null)
                                sb.Append($", {name} = \"{value}\"");
                        }
                        foreach (var (name, values) in new[] { ("Names", rule.Names), ("After", rule.After), ("Before", rule.Before), ("Phases", rule.Phases), ("Mixins", rule.Mixins) })
                        {
                            if (values.Count > 0)
                                sb.Append($", {name} = {{ {string.Join(", ", values.Select(x => $"\"{x}\""))} }}");
                        }
                        if (rule.Level != 0)
                            sb.Append($", Level = {rule.Level}");
                        sb.Append($" }},\n");
                    }
                    sb.Append($"}},\n");
                }
                sb.Append($"}}");
            }
            return sb.ToString();
        }

        static void ToCode(ShaderSource source, StringBuilder sb)
        {
            switch (source)
            {
                case ShaderClassSource classSource:
                    sb.Append($"new ShaderClassSource(\"{classSource.ClassName}\"");
                    if (classSource.GenericArguments != null)
                    {
                        sb.Append(",");
                        sb.Append(string.Join(",", classSource.GenericArguments.Select(x => $"\"{x}\"")));
                    }
                    sb.Append(")");
                    break;
                case ShaderArraySource arraySource:
                    sb.AppendLine($"new ShaderArraySource");
                    sb.AppendLine($"{{");
                    foreach (var x in arraySource.Values)
                    {
                        ToCode(x, sb);
                        sb.AppendLine(",");
                    }
                    sb.Append($"}}");
                    break;
                case ShaderMixinSource mixinSource:
                    sb.Append($"new ShaderMixinSource\n{{\n");

                    if (mixinSource.Mixins != null && mixinSource.Mixins.Count > 0)
                    {
                        bool appendLine = mixinSource.Mixins.Count > 1 || mixinSource.Mixins[0] is not ShaderClassSource;
                        sb.Append($"Mixins =");
                        if (appendLine)
                            sb.AppendLine();
                        sb.Append($"{{");
                        if (appendLine)
                            sb.AppendLine();
                        for (int i = 0; i < mixinSource.Mixins.Count; i++)
                        {
                            ToCode(mixinSource.Mixins[i], sb);
                            if (appendLine)
                                sb.AppendLine(",");
                        }
                        sb.AppendLine($"}},");
                    }

                    if (mixinSource.Compositions != null && mixinSource.Compositions.Count > 0)
                    {
                        bool appendLine = mixinSource.Compositions.Count > 1 || mixinSource.Compositions.First().Value is not ShaderClassSource;
                        sb.Append($"Compositions =");
                        if (appendLine)
                            sb.AppendLine();
                        sb.Append($"{{");
                        if (appendLine)
                            sb.AppendLine();
                        var keys = mixinSource.Compositions.Keys.ToList();
                        keys.Sort();
                        for (int i = 0; i < keys.Count; i++)
                        {
                            var key = keys[i];
                            sb.Append($"[\"{key}\"] = ");
                            ToCode(mixinSource.Compositions[key], sb);
                            if (appendLine)
                                sb.AppendLine(",");
                        }
                        sb.AppendLine($"}},");
                    }

                    sb.Append($"}}");
                    break;
                default:
                    throw new NotImplementedException();
            }
        }
    }
}
