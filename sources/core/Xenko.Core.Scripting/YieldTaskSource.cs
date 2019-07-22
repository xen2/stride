// Copyright (c) Xenko contributors (https://xenko.com) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace Xenko.Core.Scripting
{
    public class YieldTaskSource : IValueTaskSource
    {
        private short token;
        private bool completed;

        private Action<object> continuation;
        private object state;

        private ExecutionContext executionContext;
        private object scheduler;

        private static ConcurrentQueue<YieldTaskSource> pool = new ConcurrentQueue<YieldTaskSource>();

        public static YieldTaskSource New()
        {
            if (!pool.TryDequeue(out var result))
            {
                result = new YieldTaskSource();
            }
            return result;
        }

        public void GetResult(short token)
        {
            Reset();
            pool.Enqueue(this);
        }

        public ValueTaskSourceStatus GetStatus(short token)
        {
            return completed ? ValueTaskSourceStatus.Succeeded : ValueTaskSourceStatus.Pending;
        }

        public void OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            if ((flags & ValueTaskSourceOnCompletedFlags.FlowExecutionContext) != 0)
            {
                this.executionContext = ExecutionContext.Capture();
            }

            if ((flags & ValueTaskSourceOnCompletedFlags.UseSchedulingContext) != 0)
            {
                SynchronizationContext sc = SynchronizationContext.Current;
                if (sc != null && sc.GetType() != typeof(SynchronizationContext))
                {
                    this.scheduler = sc;
                }
                else
                {
                    TaskScheduler ts = TaskScheduler.Current;
                    if (ts != TaskScheduler.Default)
                    {
                        this.scheduler = ts;
                    }
                }
            }

            this.continuation = continuation;
            this.state = state;
        }

        public void SignalCompletion()
        {
            completed = true;

            if (executionContext != null)
            {
                //ExecutionContext.RunInternal(
                //    executionContext,
                //    (ref YieldTaskSource s) => s.InvokeContinuation(),
                //    ref this);
            }
            else
            {
                //InvokeContinuation();
            }

            InvokeContinuation();
        }

        private void InvokeContinuation()
        {
            Scheduler.Current.ScheduleContinuation(scheduler as MicroThreadSynchronizationContext, continuation, state);
            //continuation(state);
        }

        public void Reset()
        {
            completed = false;
            continuation = null;
            state = null;
        }
    }
}
