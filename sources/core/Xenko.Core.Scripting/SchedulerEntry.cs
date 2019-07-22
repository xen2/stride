using System;
using System.Threading;

namespace Xenko.Core.Scripting
{
    internal struct SchedulerEntry
    {
        public SyncPoint Step;
        // Either Callback or Action
        public SendOrPostCallback Callback;
        public Action<object> Action;
        public object State;
        public MicroThreadSynchronizationContext Context;
    }
}
