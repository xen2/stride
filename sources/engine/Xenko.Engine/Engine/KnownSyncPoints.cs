using Xenko.Core.Scripting;

namespace Xenko.Engine
{
    /// <summary>
    /// Various well-known sync points that can be used to setup scripting.
    /// Please keep in mind that Update and Draw might tick at different rates.
    /// </summary>
    public static class KnownSyncPoints
    {
        public static readonly SyncPoint UpdateStart = new SyncPoint();
        public static readonly SyncPoint UpdateTransformation = new SyncPoint { Dependencies = { UpdateStart } };
        public static readonly SyncPoint UpdateCamera = new SyncPoint { Dependencies = { UpdateTransformation } };

        // TODO: LOOP?
        public static readonly SyncPoint UpdatePhysics = new SyncPoint { Dependencies = { UpdateCamera } };

        public static readonly SyncPoint UpdateEnd = new SyncPoint { Dependencies = { UpdatePhysics } };

        public static readonly SyncPoint DrawStart = new SyncPoint();
        public static readonly SyncPoint DrawScene = new SyncPoint { Dependencies = { DrawStart } };
        public static readonly SyncPoint DrawEnd = new SyncPoint { Dependencies = { DrawScene } };
    }
}
