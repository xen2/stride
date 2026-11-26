// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;

namespace Stride.Graphics;

public class TextureSample
{
    private DataBox[] staging;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        ReleaseStaging();
    }

    public void Recreate(DataBox[] boxes)
    {
        if (boxes == null)
            throw new ArgumentNullException(nameof(boxes));
        OnRecreate(boxes);
        ReleaseStaging();
    }

    private void OnRecreate(DataBox[] boxes)
    {
        foreach (var box in boxes)
        {
            if (box.DataPointer == IntPtr.Zero)
                throw new ArgumentException("A box has no data.", nameof(boxes));
        }
        staging = boxes;
    }

    private void ReleaseStaging()
    {
        staging = null;
    }

    public bool IsValid()
    {
        return staging != null && Width > 0;
    }
}

public struct DataBox
{
    public IntPtr DataPointer;
}
