using System.Text;
using Stride.Shaders.Core;
using Stride.Shaders.Spirv.Building;

namespace Stride.Shaders.Compilers.SDSL;

public partial class ShaderMixer
{
    /// <summary>
    /// Represents a mixin node which will merge multiple shaders.
    /// </summary>
    /// <example>
    /// The following shader would have 3 <see cref="MixinNode"/>: top-level stage one (with MyShader and MyBase) and 2 compositions nodes (Comp0 and Comp1).
    /// <code>
    /// shader MyShader : MyBase
    /// {
    ///     ComputeColor Comp0;
    ///     ComputeColor Comp1;
    /// }
    /// </code>
    /// </example>
    /// <param name="stage"></param>
    private partial class MixinNode(MixinNode? stage, string? compositionPath)
    {
        /// <summary>
        /// If we are inside a composition node, this provides access to the stage (top-level) <see cref="MixinNode"/>.
        /// </summary>
        public MixinNode? Stage { get; } = stage;

        public bool IsRoot => Stage == null;

        public string? CompositionPath { get; } = compositionPath;

        public int StartInstruction { get; internal set; }
        public int EndInstruction { get; internal set; }

        /// <summary>
        /// List of shaders mixed in this node.
        /// </summary>
        public List<ShaderInfo> Shaders { get; } = new();

        public Dictionary<string, ShaderInfo> ShadersByName { get; } = new();
        public Dictionary<(string MethodName, FunctionType FunctionType), int> MethodGroupsByName { get; } = new();
        public Dictionary<int, MethodGroup> MethodGroups { get; } = new();

        public Dictionary<int, MixinNode> Compositions { get; } = new();
        public Dictionary<int, MixinNode[]> CompositionArrays { get; } = new();

        public override string ToString()
            => $"MixinNode ({(CompositionPath != null ? $" {CompositionPath} " : "")}{StartInstruction}..{EndInstruction}) ({Shaders.Count} shaders, {Compositions.Count} compositions, {CompositionArrays.Count} composition arrays)";

        public string ToDetailedString()
        {
            var sb = new StringBuilder();
            Recurse(sb, this);
            return sb.ToString();

            static void Recurse(StringBuilder sb, MixinNode node, int indent = 0)
            {
                sb.Append(' ', indent * 2);
                sb.AppendLine(node.ToString());
                foreach (var composition in node.Compositions)
                    Recurse(sb, composition.Value, indent + 1);
                foreach (var compositions in node.CompositionArrays)
                    foreach (var composition in compositions.Value)
                        Recurse(sb, composition, indent + 1);
            }
        }
    }

    class MethodGroup(string name, FunctionType functionType, ShaderInfo shader)
    {
        public string Name { get; } = name;
        public FunctionType FunctionType { get; } = functionType;
        public ShaderInfo Shader { get; } = shader;
        public List<(ShaderInfo Shader, int MethodId, Spirv.Specification.FunctionFlagsMask Flags)> Methods { get; } = new();

        /// <summary>Set when the first definition declares a kind; see <see cref="SortContributions"/>.</summary>
        public MethodExtensionPoint? ExtensionPoint { get; set; }

        /// <summary>The phase of each contributor that names one, by method.</summary>
        public Dictionary<int, string> Phases { get; } = new();

        /// <summary>The full names of the classes a contributor runs after in its phase, by method: <c>[MixinOrder(After = ...)]</c>.</summary>
        public Dictionary<int, string[]> After { get; } = new();

        /// <summary>The full names of the classes a contributor runs before in its phase, by method: <c>[MixinOrder(Before = ...)]</c>.</summary>
        public Dictionary<int, string[]> Before { get; } = new();

        /// <summary>What a contributor drops, by method: full names of classes, and phases with their prefix.</summary>
        public Dictionary<int, string[]> Drops { get; } = new();

        /// <summary>The contributors taken out of the chain, by method, and the class that did it: <c>[Drops]</c>, or null for <c>[Unbind]</c>.</summary>
        public Dictionary<int, (ShaderInfo Shader, ShaderInfo? By)> Replaced { get; } = new();

        /// <summary>The phases added by a contributor, by method: the phases after and before it, empty when not given.</summary>
        public Dictionary<int, (string Phase, string After, string Before)> AddedPhases { get; } = new();

        /// <summary>Where the body of a contributor to a pipeline calls <c>base</c>, by method: first, last, only, none or other.</summary>
        public Dictionary<int, string> BaseCalls { get; } = new();

        /// <summary>The line of each contributor in its file, by method, for messages.</summary>
        public Dictionary<int, int> Lines { get; } = new();

        /// <summary>The classes the first definition lists in their running order: <c>[MixinOrder(A, B)]</c>.</summary>
        public List<string> MixinOrder { get; } = new();

        /// <summary>What the first definition takes out of the chain: full names of classes, and phases with their prefix.</summary>
        public List<string> Unbind { get; } = new();

        /// <summary>The pairs that order contributors inside their phase, once merged: see <see cref="OrderPhases"/>.</summary>
        public List<OrderPair> Pairs { get; } = new();

        /// <summary>The phases where the order of the mix decided between contributors whose order matters.</summary>
        public HashSet<string> DecidedByBuilder { get; } = new();

        public override string ToString() => $"{Name} (shader: {Shader}, function Id: {string.Join(", ", Methods.Select(x => $"{x.Shader.ShaderName} {x.MethodId}"))})";
    }
}
