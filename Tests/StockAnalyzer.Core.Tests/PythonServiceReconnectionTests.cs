using Xunit;
using StockAnalyzer.Core.Services;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Tests
{
    /// <summary>
    /// Regression test for a bug where PythonService.InitializeExternalProcessAsync only ever
    /// called PythonProcessManager.StartAsync() once (gated on _processManager == null). Once
    /// any transient failure tore the named-pipe connection down (PythonProcessManager's
    /// catch-all calls CleanupConnection(), nulling the pipe reader/writer/client), the
    /// long-lived PythonService singleton stayed permanently poisoned for the rest of the app
    /// session: every subsequent Python-backed call dereferenced the null pipe fields via
    /// the `!` null-forgiving operators in PythonProcessManager.SendCommandAsync, surfacing as
    /// a bare "Object reference not set to an instance of an object.".
    /// </summary>
    [Collection("PythonIpc")]
    public class PythonServiceReconnectionTests
    {
        private sealed class ReconnectSettings : TestHelpers.PythonIpcTestSettingsBase
        {
            public override string PipeName => "testreconnectpipe1";
        }

        [Fact]
        public async Task PythonCall_SelfHealsAfterPriorConnectionFailure()
        {
            var pythonService = new PythonService(new ReconnectSettings());
            await pythonService.InitializeAsync();
            await pythonService.InitializeExternalProcessAsync();

            // 0) Establish a healthy baseline connection.
            var baseline = await pythonService.PingExternalProcessAsync();
            Assert.Contains("pong", baseline);

            // 1) Directly simulate what any transient IOException/etc. does in production:
            //    PythonProcessManager.CleanupConnection() nulls the pipe reader/writer/client
            //    and sets _isConnected=false. Invoked via reflection so this test does not
            //    depend on any particular failure trigger.
            var processManagerField = typeof(PythonService).GetField("_processManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var processManager = processManagerField!.GetValue(pythonService);
            Assert.NotNull(processManager);
            var cleanupMethod = processManager!.GetType().GetMethod("CleanupConnection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            cleanupMethod!.Invoke(processManager, null);

            // 2) The next call on the SAME PythonService instance must succeed after the standard
            //    reconnect step -- proving the connection self-heals instead of staying permanently
            //    broken for the rest of the app session.
            await pythonService.InitializeExternalProcessAsync();
            var healed = await pythonService.PingExternalProcessAsync();
            Assert.Contains("pong", healed);
        }
    }
}
