// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Sean Boettger <sean@whypenguins.com>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.Text;
using Stride.Core;
using Stride.Shaders;

namespace Stride.Rendering.Voxels
{
    [DataContract(DefaultMemberMode = DataMemberMode.Default)]
    [Display("Beam")]
    public class VoxelMarchBeam : IVoxelMarchMethod
    {
        public int Steps = 9;
        public float StepScale = 1.0f;
        public float BeamDiameter = 1.0f;
        public VoxelMarchBeam()
        {

        }
        public VoxelMarchBeam(int steps, float stepScale, float diameter)
        {
            Steps = steps;
            StepScale = stepScale;
            BeamDiameter = diameter;
        }
        public ShaderSource GetMarchingShader(ShaderSourceCollection attributeSamplers)
        {
            var mixin = new ShaderMixinSource();
            mixin.Mixins.Add(new ShaderClassSource(VoxelMarchBeamKeys.ShaderName, Steps, StepScale, BeamDiameter));
            foreach (var sampler in attributeSamplers)
                mixin.AddCompositionToArray("AttributeSamplers", sampler);
            return mixin;
        }

        string compositionName;
        public void UpdateMarchingLayout(string compositionName)
        {
            this.compositionName = compositionName;
        }
        public void ApplyMarchingParameters(ParameterCollection parameters)
        {
        }
        public void ApplyAttributeSamplers(VoxelAttribute attribute, VoxelViewContext viewContext, ParameterCollection parameters)
        {
            attribute.UpdateSamplingLayout($"AttributeSamplers[0].{compositionName}");
            attribute.ApplySamplingParameters(viewContext, parameters);
        }
    }
}
