// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;

using Stride.Core;
using Stride.Core.Annotations;
using Stride.Shaders;

namespace Stride.Rendering.Materials.ComputeColors
{
    /// <summary>
    /// A node that describe a binary operation between two <see cref="IComputeNode"/>
    /// </summary>
    [DataContract(Inherited = true)]
    [Display("Binary Operator")]
    [InlineProperty]
    public abstract class ComputeBinaryBase<T> : ComputeNode where T : class, IComputeNode
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ComputeBinaryBase{T}"/> class.
        /// </summary>
        protected ComputeBinaryBase()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ComputeBinaryBase{T}"/> class.
        /// </summary>
        /// <param name="leftChild">The left child.</param>
        /// <param name="rightChild">The right child.</param>
        /// <param name="binaryOperator">The material binary operand.</param>
        protected ComputeBinaryBase(T leftChild, T rightChild, BinaryOperator binaryOperator)
        {
            LeftChild = leftChild;
            RightChild = rightChild;
            Operator = binaryOperator;
        }

        /// <summary>
        /// The operation to blend the nodes.
        /// </summary>
        /// <userdoc>
        /// The operation between the left (background) and the right (foreground) sub-nodes.
        /// </userdoc>
        [DataMember(10)]
        [InlineProperty]
        public BinaryOperator Operator { get; set; }

        /// <summary>
        /// The left (background) child node.
        /// </summary>
        /// <userdoc>
        /// The map used for the left (background) node.
        /// </userdoc>
        [DataMember(20)]
        [Display("Left")]
        public T LeftChild { get; set; }

        /// <summary>
        /// The right (foreground) child node.
        /// </summary>
        /// <userdoc>
        /// The map used for the right (foreground) node.
        /// </userdoc>
        [DataMember(30)]
        [Display("Right")]
        public T RightChild { get; set; }

        /// <inheritdoc/>
        public override IEnumerable<IComputeNode> GetChildren(object context = null)
        {
            if (LeftChild != null)
                yield return LeftChild;
            if (RightChild != null)
                yield return RightChild;
        }

        private const string BackgroundCompositionName = "color1";
        private const string ForegroundCompositionName = "color2";

        public override ShaderSource GenerateShaderSource(ShaderGeneratorContext context, MaterialComputeColorKeys baseKeys)
        {
            var leftShaderSource = LeftChild?.GenerateShaderSource(context, baseKeys);
            var rightShaderSource = RightChild?.GenerateShaderSource(context, baseKeys);

            var shaderSource = new ShaderClassSource(GetCorrespondingShaderSourceName(Operator));
            var mixin = new ShaderMixinSource();
            mixin.Mixins.Add(shaderSource);
            if (leftShaderSource != null)
                mixin.AddComposition(BackgroundCompositionName, leftShaderSource);
            if (rightShaderSource != null)
                mixin.AddComposition(ForegroundCompositionName, rightShaderSource);

            return mixin;
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return "Binary operation";
        }

        /// <summary>
        /// Get the name of the ShaderClassSource corresponding to the operation
        /// </summary>
        /// <param name="binaryOperand">The operand.</param>
        /// <returns>The name of the ShaderClassSource.</returns>
        private static string GetCorrespondingShaderSourceName(BinaryOperator binaryOperand)
        {
            switch (binaryOperand)
            {
                case BinaryOperator.Add:
                    return ComputeColorAdd3dsKeys.ShaderName;
                case BinaryOperator.AddMath:
                    return ComputeColorAddKeys.ShaderName;
                case BinaryOperator.Average:
                    return ComputeColorAverageKeys.ShaderName;
                case BinaryOperator.Color:
                    return ComputeColorColorKeys.ShaderName;
                case BinaryOperator.ColorBurn:
                    return ComputeColorColorBurnKeys.ShaderName;
                case BinaryOperator.ColorDodge:
                    return ComputeColorColorDodgeKeys.ShaderName;
                case BinaryOperator.Darken:
                    return ComputeColorDarken3dsKeys.ShaderName; //"ComputeColorDarkenMaya" //TODO: change this
                case BinaryOperator.Desaturate:
                    return ComputeColorDesaturateKeys.ShaderName;
                case BinaryOperator.Difference:
                    return ComputeColorDifference3dsKeys.ShaderName; //"ComputeColorDifferenceMaya" //TODO: change this
                case BinaryOperator.Divide:
                    return ComputeColorDivideKeys.ShaderName;
                case BinaryOperator.Exclusion:
                    return ComputeColorExclusionKeys.ShaderName;
                case BinaryOperator.HardLight:
                    return ComputeColorHardLightKeys.ShaderName;
                case BinaryOperator.HardMix:
                    return ComputeColorHardMixKeys.ShaderName;
                case BinaryOperator.Hue:
                    return ComputeColorHueKeys.ShaderName;
                case BinaryOperator.Illuminate:
                    return ComputeColorIlluminateKeys.ShaderName;
                case BinaryOperator.In:
                    return ComputeColorInKeys.ShaderName;
                case BinaryOperator.Lighten:
                    return ComputeColorLighten3dsKeys.ShaderName; //"ComputeColorLightenMaya" //TODO: change this
                case BinaryOperator.LinearBurn:
                    return ComputeColorLinearBurnKeys.ShaderName;
                case BinaryOperator.LinearDodge:
                    return ComputeColorLinearDodgeKeys.ShaderName;
                case BinaryOperator.Mask:
                    return ComputeColorMaskKeys.ShaderName;
                case BinaryOperator.Multiply:
                    return ComputeColorMultiplyKeys.ShaderName; //return "ComputeColorMultiply3ds"; //"ComputeColorMultiplyMaya" //TODO: change this
                case BinaryOperator.Out:
                    return ComputeColorOutKeys.ShaderName;
                case BinaryOperator.Over:
                    return ComputeColorOver3dsKeys.ShaderName; //TODO: change this to "ComputeColorLerpAlpha"
                case BinaryOperator.Overlay:
                    return ComputeColorOverlay3dsKeys.ShaderName; //"ComputeColorOverlayMaya" //TODO: change this
                case BinaryOperator.PinLight:
                    return ComputeColorPinLightKeys.ShaderName;
                case BinaryOperator.Saturate:
                    return ComputeColorSaturateKeys.ShaderName;
                case BinaryOperator.Saturation:
                    return ComputeColorSaturationKeys.ShaderName;
                case BinaryOperator.Screen:
                    return ComputeColorScreenKeys.ShaderName;
                case BinaryOperator.SoftLight:
                    return ComputeColorSoftLightKeys.ShaderName;
                case BinaryOperator.Subtract:
                    return ComputeColorSubtractKeys.ShaderName; // "ComputeColorSubtract3ds" "ComputeColorSubtractMaya" //TODO: change this
                case BinaryOperator.SubstituteAlpha:
                    return ComputeColorSubstituteAlphaKeys.ShaderName;
                case BinaryOperator.Threshold:
                    return ComputeColorThresholdKeys.ShaderName;
                default:
                    throw new ArgumentOutOfRangeException("binaryOperand");
            }
        }
    }
}
