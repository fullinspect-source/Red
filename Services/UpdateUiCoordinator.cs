using System;
using System.Threading;
using System.Threading.Tasks;

namespace InspectionEditor.Services
{
    internal static class UpdateUiCoordinator
    {
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
