// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
#if STRIDE_GRAPHICS_API_DIRECT3D12
using System;
using System.Collections.Generic;
using System.Diagnostics;

using SharpDX;

namespace Stride.Graphics
{
    /// <summary>
    /// GraphicsResource class
    /// </summary>
    public abstract partial class GraphicsResourceBase
    {
        private SharpDX.Direct3D12.DeviceChild nativeDeviceChild;

        protected internal SharpDX.Direct3D12.Resource NativeResource { get; internal set; }

        protected bool IsDebugMode => GraphicsDevice != null && GraphicsDevice.IsDebugMode;

        private void Initialize()
        {
        }

        /// <summary>
        /// Gets or sets the device child.
        /// </summary>
        /// <value>The device child.</value>
        protected internal SharpDX.Direct3D12.DeviceChild NativeDeviceChild
        {
            get
            {
                return nativeDeviceChild;
            }
            set
            {
                nativeDeviceChild = value;
                NativeResource = nativeDeviceChild as SharpDX.Direct3D12.Resource;
                // Associate PrivateData to this DeviceResource
                SetDebugName(GraphicsDevice, nativeDeviceChild, Name);
            }
        }

        /// <summary>
        /// Associates the private data to the device child, useful to get the name in PIX debugger.
        /// </summary>
        internal static void SetDebugName(GraphicsDevice graphicsDevice, SharpDX.Direct3D12.DeviceChild deviceChild, string name)
        {
            if (graphicsDevice.IsDebugMode && deviceChild != null)
                deviceChild.Name = $"{name} ({deviceChild.NativePointer.ToString("X16")})";
        }

        /// <summary>
        /// Called when graphics device has been detected to be internally destroyed.
        /// </summary>
        protected internal virtual void OnDestroyed(bool immediate = false)
        {
            Destroyed?.Invoke(this, EventArgs.Empty);

            if (nativeDeviceChild != null)
            {
                if (immediate)
                {
                    // We make sure all previous command lists are completed (GPU->CPU sync point)
                    // Note: this is a huge perf-hit in realtime, so it should be only used in rare cases (i.e. backbuffer resize or application exit).
                    //       also, we currently do that one by one but we might want to batch them if it proves too slow.
                    var commandListFenceValue = GraphicsDevice.CommandListFence.NextFenceValue++;
                    GraphicsDevice.NativeCommandQueue.Signal(GraphicsDevice.CommandListFence.Fence, commandListFenceValue);
                    GraphicsDevice.CommandListFence.WaitForFenceCPUInternal(commandListFenceValue);

                    ((SharpDX.IUnknown)nativeDeviceChild).Release();
                }
                else
                {
                    // Schedule the resource for destruction (as soon as we are done with it)
                    lock (GraphicsDevice.TemporaryResources)
                        GraphicsDevice.TemporaryResources.Enqueue(new KeyValuePair<long, object>(GraphicsDevice.FrameFence.NextFenceValue, nativeDeviceChild));
                }
                nativeDeviceChild = null;
            }
            NativeResource = null;
        }

        /// <summary>
        /// Called when graphics device has been recreated.
        /// </summary>
        /// <returns>True if item transitioned to a <see cref="GraphicsResourceLifetimeState.Active"/> state.</returns>
        protected internal virtual bool OnRecreate()
        {
            return false;
        }

        protected SharpDX.Direct3D12.Device NativeDevice
        {
            get
            {
                return GraphicsDevice != null ? GraphicsDevice.NativeDevice : null;
            }
        }
        
        internal static void ReleaseComObject<T>(ref T comObject) where T : class, IUnknown
        {
            // We can't put IUnknown as a constraint on the generic as it would break compilation (trying to import SharpDX in projects with InternalVisibleTo)
            var refCountResult = comObject.Release();
            comObject = null;
        }
    }
}
 
#endif
