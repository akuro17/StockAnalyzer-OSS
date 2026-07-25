using System;
using System.Windows.Interop;
using System.Windows.Media;

namespace StockAnalyzer.Tests.Visual
{
    internal sealed class RenderModeScope : IDisposable
    {
        private readonly RenderMode _originalMode;
        private bool _disposed;
        private static readonly object _lock = new object();

        public RenderModeScope(RenderMode temporaryMode)
        {
            lock (_lock)
            {
                _originalMode = RenderOptions.ProcessRenderMode;
                if (_originalMode != temporaryMode)
                {
                    try
                    {
                        RenderOptions.ProcessRenderMode = temporaryMode;
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                lock (_lock)
                {
                    try
                    {
                        if (RenderOptions.ProcessRenderMode != _originalMode)
                        {
                            RenderOptions.ProcessRenderMode = _originalMode;
                        }
                    }
                    catch
                    {
                    }
                    _disposed = true;
                }
            }
        }
    }
}
