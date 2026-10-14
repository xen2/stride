// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;

namespace Stride.Graphics;

/// <summary>
/// Spike R3 (throwaway): hooks to time pipeline creation and read driver statistics. Single-threaded use only.
/// </summary>
public static class R3PipelineSpike
{
    /// <summary>
    /// Vulkan: when set, every shader stage gets a <c>VkSpecializationInfo</c> with constant ids 0..n-1 (32-bit values).
    /// </summary>
    public static uint[]? SpecializationValues;

    /// <summary>
    /// Vulkan: create pipelines with <c>CAPTURE_STATISTICS</c> and fill <see cref="LastStatistics"/> (needs
    /// <c>VK_KHR_pipeline_executable_properties</c>, see <see cref="HasExecutableProperties"/>).
    /// </summary>
    public static bool CaptureStatistics;

    /// <summary>
    /// Vulkan: the device enabled <c>VK_KHR_pipeline_executable_properties</c>.
    /// </summary>
    public static bool HasExecutableProperties;

    /// <summary>
    /// Time of the last driver pipeline creation call only (<c>vkCreateGraphicsPipelines</c>, <c>CreateGraphicsPipelineState</c>).
    /// </summary>
    public static double LastDriverMilliseconds;

    /// <summary>
    /// Per executable: <c>name: stat=value; ...</c>.
    /// </summary>
    public static List<string> LastStatistics = [];
}
