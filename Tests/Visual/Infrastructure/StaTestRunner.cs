using System;
using System.Threading;
using System.Windows.Threading;

namespace StockAnalyzer.Tests.Visual
{
    public static class StaTestRunner
    {
        public static void Run(Action action)
        {
            Exception executionException = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var dispatcher = Dispatcher.CurrentDispatcher;
                    try
                    {
                        action();
                    }
                    finally
                    {
                        FlushDispatcher(dispatcher);
                        dispatcher.InvokeShutdown();
                    }
                }
                catch (Exception ex)
                {
                    executionException = ex;
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            if (!thread.Join(5000)) 
            {
                throw new TimeoutException("STA Test Thread failed to terminate within 5 seconds.");
            }

            if (executionException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(executionException).Throw();
            }
        }

        private static void FlushDispatcher(Dispatcher dispatcher)
        {
            if (!dispatcher.HasShutdownStarted)
            {
                try
                {
                    dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    dispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
                }
                catch
                {
                }
            }
        }
    }
}
