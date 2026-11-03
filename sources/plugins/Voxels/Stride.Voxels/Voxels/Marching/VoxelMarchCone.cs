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
    [Display("Cone")]
    public class VoxelMarchCone : IVoxelMarchMethod
    {
        [DataMember(0)]
        public bool EditMode = true;
        [DataMember(10)]
        public bool Fast = false;
        [DataMember(20)]
        public int Steps = 9;
        [DataMember(30)]
        public float StepScale = 1.0f;
        [DataMember(40)]
        public float ConeRatio = 1.0f;
        [DataMember(50)]
        public float StartOffset = 1.0f;

        public VoxelMarchCone()
        {

        }
        public VoxelMarchCone(int steps, float stepScale, float ratio)
        {
            Steps = steps;
            StepScale = stepScale;
            ConeRatio = ratio;
            EditMode = false;
        }
        public ShaderSource GetMarchingShader(ShaderSourceCollection attributeSamplers)
        {
            var mixin = new ShaderMixinSource();
            if (EditMode)
            {
                mixin.Mixins.Add(new ShaderClassSource(VoxelMarchConeEditModeKeys.ShaderName));
            }
            else
            {
                mixin.Mixins.Add(new ShaderClassSource(VoxelMarchConeKeys.ShaderName, Steps, StepScale, ConeRatio, StartOffset, Fast));
            }
            foreach (var sampler in attributeSamplers)
                mixin.AddCompositionToArray("AttributeSamplers", sampler);

            return mixin;
        }

        ValueParameterKey<int> StepsKey;
        ValueParameterKey<float> StepScaleKey;
        ValueParameterKey<float> ConeRatioKey;
        ValueParameterKey<int> FastKey;
        ValueParameterKey<float> OffsetKey;
        string compositionName;
        public void UpdateMarchingLayout(string compositionName)
        {
            this.compositionName = compositionName;
            if (EditMode)
            {
                StepsKey = VoxelMarchConeEditModeKeys.steps.ComposeWith(compositionName);
                StepScaleKey = VoxelMarchConeEditModeKeys.stepScale.ComposeWith(compositionName);
                ConeRatioKey = VoxelMarchConeEditModeKeys.coneRatio.ComposeWith(compositionName);
                FastKey = VoxelMarchConeEditModeKeys.fast.ComposeWith(compositionName);
                OffsetKey = VoxelMarchConeEditModeKeys.offset.ComposeWith(compositionName);
            }
        }
        public void ApplyMarchingParameters(ParameterCollection parameters)
        {
            if (EditMode)
            {
                parameters.Set(StepsKey, Steps);
                parameters.Set(StepScaleKey, StepScale);
                parameters.Set(ConeRatioKey, ConeRatio);
                parameters.Set(FastKey, Fast ? 1 : 0);
                parameters.Set(OffsetKey, StartOffset);
            }
        }
        public void ApplyAttributeSamplers(VoxelAttribute attribute, VoxelViewContext viewContext, ParameterCollection parameters)
        {
            attribute.UpdateSamplingLayout($"AttributeSamplers[0].{compositionName}");
            attribute.ApplySamplingParameters(viewContext, parameters);
        }
    }
}
