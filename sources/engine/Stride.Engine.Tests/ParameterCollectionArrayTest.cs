// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;

using Xunit;

using Stride.Graphics;
using Stride.Rendering;
using Stride.Shaders;

namespace Stride.Engine.Tests
{
    /// <summary>
    /// An array of objects in a <see cref="ParameterCollection"/>, as a texture array of a shader takes: one slot per element.
    /// </summary>
    [EffectKeys]
    public class ParameterCollectionArrayTest
    {
        public static readonly ObjectParameterKey<object> Before = ParameterKeys.NewObject<object>();
        public static readonly ObjectParameterKey<object> Elements = ParameterKeys.NewObject<object>();
        public static readonly ObjectParameterKey<object> After = ParameterKeys.NewObject<object>();

        private static readonly object[] Four = ["a", "b", "c", "d"];

        [Fact]
        public void ArraySetBeforeTheLayoutMovesToItsSlots()
        {
            var parameters = new ParameterCollection();
            parameters.Set(After, "after");
            parameters.Set(Elements, Four);
            parameters.Set(Before, "before");

            parameters.UpdateLayout(Layout(4));

            Assert.Equal(new object[] { "before", "a", "b", "c", "d", "after" }, parameters.ObjectValues);
            Assert.Equal("a", parameters.Get(Elements));
        }

        [Fact]
        public void ArraySetAfterTheLayoutFillsItsSlots()
        {
            var parameters = new ParameterCollection();
            parameters.UpdateLayout(Layout(4));

            parameters.Set(Elements, Four);
            parameters.Set(After, "after");

            Assert.Equal(new object[] { null, "a", "b", "c", "d", "after" }, parameters.ObjectValues);
        }

        [Fact]
        public void ArrayOfANewSizeGetsNewSlots()
        {
            var parameters = new ParameterCollection();
            parameters.Set(Elements, "single");
            parameters.Set(Elements, Four);

            parameters.UpdateLayout(Layout(4));

            Assert.Equal(new object[] { null, "a", "b", "c", "d", null }, parameters.ObjectValues);
        }

        [Fact]
        public void LongerArrayKeepsItsFirstElements()
        {
            var parameters = new ParameterCollection();
            parameters.Set(Elements, ["a", "b", "c", "d", "e", "f"]);
            parameters.UpdateLayout(Layout(4));
            Assert.Equal(new object[] { null, "a", "b", "c", "d", null }, parameters.ObjectValues);

            parameters.Set(Elements, ["1", "2", "3", "4", "5"]);
            Assert.Equal(new object[] { null, "1", "2", "3", "4", null }, parameters.ObjectValues);
        }

        [Fact]
        public void ShorterArrayIsAnError()
        {
            var parameters = new ParameterCollection();
            parameters.Set(Elements, ["a", "b"]);
            var error = Assert.Throws<InvalidOperationException>(() => parameters.UpdateLayout(Layout(4)));
            Assert.Contains("is an array of 4 elements in the effect, but 2 are set", error.Message);

            var withLayout = new ParameterCollection();
            withLayout.UpdateLayout(Layout(4));
            Assert.Throws<ArgumentException>(() => withLayout.Set(Elements, ["a", "b"]));
        }

        [Fact]
        public void NullElementIsAnError()
        {
            var parameters = new ParameterCollection();
            var error = Assert.Throws<ArgumentException>(() => parameters.Set(Elements, ["a", null, "c", "d"]));
            Assert.Contains("Element 1", error.Message);
        }

        [Fact]
        public void CopierCopiesEveryElement()
        {
            var source = new ParameterCollection();
            source.Set(Elements, Four);
            var destination = new ParameterCollection();
            destination.UpdateLayout(Layout(4));

            var copier = new ParameterCollection.Copier(destination, source);
            copier.Copy();

            Assert.Equal(new object[] { null, "a", "b", "c", "d", null }, destination.ObjectValues);
        }

        [Fact]
        public void LogicalGroupWithAnArrayTakesOneSlotPerElement()
        {
            var resourceGroupLayout = new RenderSystemResourceGroupLayout { DescriptorSetLayoutBuilder = Builder(4, logicalGroup: "Group") };
            var logicalGroup = resourceGroupLayout.CreateLogicalGroup("Group");
            Assert.Equal(6, logicalGroup.DescriptorSlotCount);

            var layout = new ParameterCollectionLayout();
            layout.ProcessLogicalGroup(resourceGroupLayout, ref logicalGroup);
            Assert.Equal(6, layout.ResourceCount);
            Assert.Equal(1, layout.LayoutParameterKeyInfos[1].BindingSlot);
            Assert.Equal(4, layout.LayoutParameterKeyInfos[1].Count);
            Assert.Equal(5, layout.LayoutParameterKeyInfos[2].BindingSlot);

            var parameters = new ParameterCollection();
            parameters.Set(Elements, Four);
            parameters.Set(After, "after");
            parameters.UpdateLayout(layout);
            Assert.Equal(new object[] { null, "a", "b", "c", "d", "after" }, parameters.ObjectValues);
        }

        private static ParameterCollectionLayout Layout(int arraySize)
        {
            var layout = new ParameterCollectionLayout();
            layout.ProcessResources(Builder(arraySize, logicalGroup: null));
            return layout;
        }

        private static DescriptorSetLayoutBuilder Builder(int arraySize, string logicalGroup)
        {
            var builder = new DescriptorSetLayoutBuilder();
            builder.AddBinding(Before, logicalGroup, EffectParameterClass.ShaderResourceView, EffectParameterType.Texture2D, EffectParameterType.Float);
            builder.AddBinding(Elements, logicalGroup, EffectParameterClass.ShaderResourceView, EffectParameterType.Texture2D, EffectParameterType.Float, arraySize);
            builder.AddBinding(After, logicalGroup, EffectParameterClass.ShaderResourceView, EffectParameterType.Texture2D, EffectParameterType.Float);
            return builder;
        }
    }
}
