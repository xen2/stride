using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace Xenko.Core.Scripting
{
    public class SyncPoint
    {
        private int callbacksCurrentStep;

        private ConcurrentQueue<SchedulerEntry> callbacks = new ConcurrentQueue<SchedulerEntry>();

        private ConcurrentQueue<SchedulerEntry> nextStepCallbacks = new ConcurrentQueue<SchedulerEntry>();

        public Collection<SyncPoint> Dependencies { get; }

        public List<SyncPoint> Successors { get; } = new List<SyncPoint>();

        internal void AddCallback(ref SchedulerEntry schedulerEntry, bool nextStep)
        {
            var currentCallbacks = nextStep ? nextStepCallbacks : callbacks;
            currentCallbacks.Enqueue(schedulerEntry);
        }

        internal bool TryGetCallback(out SchedulerEntry schedulerEntry)
        {
            // Process immediate callbacks first
            if (callbacks.TryDequeue(out schedulerEntry))
                return true;

            // Then process callbacks that should be done during this step
            if (Interlocked.Decrement(ref callbacksCurrentStep) >= 0)
                return nextStepCallbacks.TryDequeue(out schedulerEntry);

            return false;
        }

        internal void CallbackStartStep()
        {
            // Remember how many callbacks we have to process in next step list (new ones will be added)
            callbacksCurrentStep = nextStepCallbacks.Count;
        }

        public ValueTask During()
        {
            var context = Scheduler.CurrentContext;
            context.ResumeUntil(true, this);
            return context.Scheduler.Yield();
        }

        public SyncPoint()
        {
            Dependencies = new DependencyCollection(this);
        }

        class DependencyCollection : Collection<SyncPoint>
        {
            public SyncPoint Owner { get; }

            internal DependencyCollection(SyncPoint owner)
            {
                Owner = owner;
            }

            protected override void InsertItem(int index, SyncPoint item)
            {
                base.InsertItem(index, item);
                item.Successors.Add(Owner);
            }

            protected override void RemoveItem(int index)
            {
                Items[index].Successors.Remove(Owner);
                base.RemoveItem(index);
            }

            protected override void SetItem(int index, SyncPoint item)
            {
                Items[index].Successors.Remove(Owner);
                base.SetItem(index, item);
                item.Successors.Add(Owner);
            }

            protected override void ClearItems()
            {
                foreach (var item in Items)
                    item.Successors.Remove(Owner);
                base.ClearItems();
            }
        }
    }
}
