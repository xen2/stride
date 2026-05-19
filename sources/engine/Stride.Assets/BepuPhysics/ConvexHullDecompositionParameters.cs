// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core;
using Stride.Core.Serialization.Contents;
using Stride.Physics;

namespace Stride.BepuPhysics.Assets;

[ContentSerializer(typeof(DataContentSerializer<ConvexHullDecompositionParameters>))]
[DataContract("BepuDecompositionParameters")]
[Display("DecompositionParameters")]
public class ConvexHullDecompositionParameters
{
    /// <userdoc>
    /// If this is unchecked the following parameters are totally ignored, as only a simple convex hull of the whole model will be generated.
    /// </userdoc>
    public bool Enabled { get; set; }

    /// <userdoc>
    /// Maximum number of output convex hulls. Lower values produce a coarser decomposition.
    /// </userdoc>
    [DataMember(60)]
    public int MaxConvexHulls { get; set; } = 64;

    /// <userdoc>
    /// Voxel grid resolution (10,000 - 64,000,000). Higher values give finer decomposition but take longer.
    /// </userdoc>
    [DataMember(70)]
    public int Resolution { get; set; } = 400000;

    /// <userdoc>
    /// Maximum recursion depth when splitting hulls (1 - 32).
    /// </userdoc>
    [DataMember(80)]
    public int MaxRecursionDepth { get; set; } = 10;

    /// <userdoc>
    /// Stop splitting once the voxel volume error is within this percentage of the source volume. Raising this gives simpler hulls.
    /// </userdoc>
    [DataMember(90)]
    public double MinimumVolumePercentErrorAllowed { get; set; } = 1.0;

    /// <userdoc>
    /// Shrink-wrap hull vertices to the source mesh surface. Improves visual fit.
    /// </userdoc>
    [DataMember(100)]
    public bool ShrinkWrap { get; set; } = true;

    /// <userdoc>
    /// How to classify interior voxels during voxelization. Use RaycastFill for meshes with holes.
    /// </userdoc>
    [DataMember(110)]
    public VhacdFillMode FillMode { get; set; } = VhacdFillMode.FloodFill;

    /// <userdoc>
    /// Maximum number of vertices allowed in any output convex hull (4 - 1024).
    /// </userdoc>
    [DataMember(120)]
    public int MaxNumVerticesPerCH { get; set; } = 64;

    public bool Match(object obj)
    {
        var other = obj as ConvexHullDecompositionParameters;

        if (other == null)
        {
            return false;
        }

        return other.Enabled == Enabled &&
               other.MaxConvexHulls == MaxConvexHulls &&
               other.Resolution == Resolution &&
               other.MaxRecursionDepth == MaxRecursionDepth &&
               other.MinimumVolumePercentErrorAllowed == MinimumVolumePercentErrorAllowed &&
               other.ShrinkWrap == ShrinkWrap &&
               other.FillMode == FillMode &&
               other.MaxNumVerticesPerCH == MaxNumVerticesPerCH;
    }
}
