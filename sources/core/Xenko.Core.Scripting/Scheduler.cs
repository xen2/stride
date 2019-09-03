using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Xenko.Core.Scripting
{
    public class Scheduler
    {
        private ConcurrentQueue<YieldTaskSource> yields = new ConcurrentQueue<YieldTaskSource>();

        public int Frame { get; private set; }

        public Scheduler()
        {
            callbacks = new ConcurrentQueue<SchedulerEntry>[10];
            for (int i = 0; i < callbacks.Length; ++i)
                callbacks[i] = new ConcurrentQueue<SchedulerEntry>();
        }

        public static MicroThreadSynchronizationContext CurrentContext => SynchronizationContext.Current as MicroThreadSynchronizationContext;

        public static Scheduler Current => (SynchronizationContext.Current as MicroThreadSynchronizationContext)?.Scheduler;

        ConcurrentQueue<SchedulerEntry>[] callbacks;

        public void Add(Func<ValueTask> microThreadFunction)
        {
            var syncContext = new MicroThreadSynchronizationContext(this);
            ScheduleContinuation(syncContext, (Action<object>)null, microThreadFunction);
        }

        public void Run(SyncPoint start)
        {
            var syncPointStack = new Stack<SyncPoint>();
            var processedSyncPoints = new HashSet<SyncPoint>();
            syncPointStack.Push(start);

            var previousSyncContext = SynchronizationContext.Current;
            try
            {
                while (syncPointStack.Count > 0)
                {
                    var currentSyncPoint = syncPointStack.Pop();
                    // Already processed?
                    if (!processedSyncPoints.Add(currentSyncPoint))
                        continue;

                    var announced = false;

                    // Move all callbacks from nextframe to current frame
                    currentSyncPoint.CallbackStartStep();

                    ProcessYields();

                    while (currentSyncPoint.TryGetCallback(out var callback))
                    {
                        if (!announced)
                        {
                            announced = true;
                            Console.WriteLine($"Frame {Frame} Execution Step {currentSyncPoint}");
                        }
                        SynchronizationContext.SetSynchronizationContext(callback.Context);
                        try
                        {
                            if (callback.Action != null)
                                callback.Action(callback.State);
                            else if (callback.Callback != null)
                                callback.Callback(callback.State);
                            else if (callback.Context.State == MicroThreadState.Starting)
                                callback.Context.Start(callback.State);
                        }
                        catch (Exception e)
                        {
                            callback.Context.SetException(e);
                        }

                        ProcessYields();
                    }

                    foreach (var successor in currentSyncPoint.Successors)
                    {
                        syncPointStack.Push(successor);
                    }
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousSyncContext);
                Frame++;
            }
        }

        private void ProcessYields()
        {
            // Process Yields
            while (yields.TryDequeue(out var yield))
            {
                yield.SignalCompletion();
            }
        }

        internal void ScheduleContinuation(MicroThreadSynchronizationContext context, SendOrPostCallback callback, object callbackState)
        {
            if (context.ResumeExecutionStep == null)
            {
                SynchronizationContext.SetSynchronizationContext(context);
                callback(callbackState);
            }
            else
            {
                var schedulerEntry = new SchedulerEntry
                {
                    Action = null,
                    Callback = callback,
                    State = callbackState,
                    Step = context.ResumeExecutionStep,
                    Context = context
                };
                lock (callbacks)
                {
                    context.ResumeExecutionStep.AddCallback(ref schedulerEntry, context.NextStep);
                    // Next invocation doesn't go to next step by default, unless explicitely requested again
                    context.NextStep = false;
                }
            }
        }

        internal void ScheduleContinuation(MicroThreadSynchronizationContext context, Action<object> action, object callbackState)
        {
            if (context.ResumeExecutionStep == null)
            {
                SynchronizationContext.SetSynchronizationContext(context);
                if (action != null)
                    action(callbackState);
                else
                    context.Start(callbackState);
            }
            else
            {
                var schedulerEntry = new SchedulerEntry
                {
                    Action = action,
                    Callback = null,
                    State = callbackState,
                    Step = context.ResumeExecutionStep,
                    Context = context
                };
                lock (callbacks)
                {
                    context.ResumeExecutionStep.AddCallback(ref schedulerEntry, context.NextStep);
                    // Next invocation doesn't go to next step by default, unless explicitely requested again
                    context.NextStep = false;
                }
            }
        }

        public ValueTask Yield()
        {
            var taskSource = YieldTaskSource.New();
            yields.Enqueue(taskSource);
            return new ValueTask(taskSource, 0);
        }

        internal ValueTask NextFrame()
        {
            throw new NotImplementedException();
        }
    }
}
