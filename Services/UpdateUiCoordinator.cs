using System;
using System.Threading;
using System.Threading.Tasks;

namespace InspectionEditor.Services
{
    internal static class UpdateUiCoordinator
    {
        internal static readonly TimeSpan ManualBudget = TimeSpan.FromMinutes(2);

        internal static async Task RunVisibleAsync(Func<Task> update, Action<Exception> failure, Action finish)
        {
            try { await update(); }
            catch (Exception ex) { failure(ex); }
            finally { finish(); }
        }

        // Only preparation/data work belongs here, NEVER an operation that can launch
        // an installer. Run off the dispatcher so even a synchronous stall cannot pin UI.
        // WaitAsync enforces the deadline even when a dependency ignores cancellation.
        internal static async Task<T> RunPreparationAsync<T>(Func<CancellationToken, Task<T>> prepare,
            TimeSpan budget, CancellationToken cancellationToken = default)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(budget);
            var token = deadline.Token;
            var task = Task.Run(() => prepare(token), token);
            // A separate timeout also survives a dependency blocking inside one of
            // its cancellation callbacks. Cancellation requests alone are not deadlines.
            try { return await task.WaitAsync(budget, cancellationToken); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Update preparation timed out. Please try again.");
            }
            finally
            {
                // Observe late failures, but never wait forever for non-cooperative work.
                Observe(task);
                Observe(deadline.CancelAsync());
            }
        }

        internal static void Observe(Task task) => _ = task.ContinueWith(
            completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        // Never abandon a task that can launch an installer. Cancellation is followed by
        // observation, including the race where the installer has already started.
        internal static async Task<T> RunStartupAsync<T>(Func<CancellationToken, Task<T>> update, TimeSpan budget)
        {
            using var cancellation = new CancellationTokenSource();
            var task = update(cancellation.Token);
            if (await Task.WhenAny(task, Task.Delay(budget)) != task)
                cancellation.Cancel();
            return await task;
        }

        internal static async Task<T> CaptureAsync<T>(Func<Task<T>> action, Func<Exception, T> failure)
        {
            try { return await action(); }
            catch (Exception ex) { return failure(ex); }
        }
    }
}
