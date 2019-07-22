using System;
using System.Threading;
using System.Threading.Tasks;

namespace Xenko.Core.Scripting
{
    public class MicroThreadSynchronizationContext : SynchronizationContext
    {
        private Scheduler scheduler;

        internal SyncPoint ResumeExecutionStep;
        internal bool NextStep;

        internal Exception Exception;
        internal MicroThreadState State;

        public MicroThreadSynchronizationContext(Scheduler scheduler)
        {
            this.scheduler = scheduler;
            State = MicroThreadState.Starting;
        }

        public Scheduler Scheduler => scheduler;

        public override SynchronizationContext CreateCopy()
        {
            return base.CreateCopy();
        }

        public override void Post(SendOrPostCallback d, object state)
        {
            scheduler.ScheduleContinuation(this, d, state);
        }

        public void ResumeUntil(bool nextStep = false, SyncPoint executionStep = null)
        {
            ResumeExecutionStep = executionStep;
            NextStep = nextStep;
        }

        public ValueTask YieldUntil(bool nextStep = false, SyncPoint syncPoint = null)
        {
            ResumeUntil(nextStep, syncPoint);
            return scheduler.Yield();
        }

        internal async ValueTask Start(object state)
        {
            var microThreadFunction = (Func<ValueTask>)state;
            State = MicroThreadState.Running;
            try
            {
                await microThreadFunction();
                State = MicroThreadState.Completed;
            }
            catch (OperationCanceledException e)
            {
                // Exit gracefully on cancellation exceptions
                SetException(e);
            }
            catch (Exception e)
            {
                SetException(e);
            }
            finally
            {

            }
        }

        internal void SetException(Exception exception)
        {
            Exception = exception;

            // Depending on if exception was raised from outside or inside, set appropriate state
            State = (exception is OperationCanceledException) ? MicroThreadState.Canceled : MicroThreadState.Failed;
        }
    }
}
