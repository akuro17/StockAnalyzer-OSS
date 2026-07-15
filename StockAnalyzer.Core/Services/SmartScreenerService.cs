using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Python.Runtime;

namespace StockAnalyzer.Core.Services
{
    public class SmartScreenerService
    {
        private readonly IPythonService _pythonService;
        private readonly string _jsonDataPath;
        private readonly string _scriptPath;

        public SmartScreenerService(IPythonService pythonService, IStockAnalyzerSettings settings)
        {
            _pythonService = pythonService;
            
            // Resolve script path robustly using BaseDirectory and fallbacks
            _scriptPath = ResolveScriptPath();
            
            // Resolve JSON data path from settings (appsettings.json) with FindRepoRoot fallback
            var repoRoot = FindRepoRoot();
            var configuredPath = settings.ScreeningDataPath;
            if (!string.IsNullOrEmpty(configuredPath) && !Path.IsPathRooted(configuredPath))
            {
                _jsonDataPath = Path.Combine(repoRoot, configuredPath);
            }
            else if (!string.IsNullOrEmpty(configuredPath))
            {
                _jsonDataPath = configuredPath;
            }
            else
            {
                // Fallback: hardcoded relative path from repo root (legacy behavior)
                _jsonDataPath = Path.Combine(repoRoot, "Web", "Site", "root", "data", "screening", "latest_screening_data.json");
            }
        }

        private string ResolveScriptPath()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            
            // 1. Check strict BaseDirectory/Scripts (Deployment / Output Copy)
            var deployedPath = Path.Combine(baseDir, "Scripts");
            if (Directory.Exists(deployedPath))
            {
                return deployedPath;
            }

            // 2. Check dev relative paths locating the repo root
            try 
            {
                var repoRoot = FindRepoRoot();
                var devPath = Path.GetFullPath(Path.Combine(repoRoot, "StockAnalyzer.Core", "Scripts"));
                if (Directory.Exists(devPath))
                {
                    return devPath;
                }
            }
            catch
            {
                // Ignore path errors during fallback check
            }

            // Default to deployed path if nothing found
            return deployedPath;
        }

        private string FindRepoRoot()
        {
            // Simple heuristic: walk up until we find StockAnalyzer.sln
            var current = AppDomain.CurrentDomain.BaseDirectory;
             while (!string.IsNullOrEmpty(current))
            {
                if (File.Exists(Path.Combine(current, "StockAnalyzer.sln")))
                {
                    return current;
                }
                var parent = Directory.GetParent(current);
                if (parent == null) break;
                current = parent.FullName;
            }
            
            // Fallback: Use BaseDirectory. 
            // In a deployed scenario, we should look for data relative to the executable.
            return AppDomain.CurrentDomain.BaseDirectory; 
        }

        public async Task<List<string>> ScreenAsync(string criteria)
        {
            await _pythonService.InitializeAsync();

            return await _pythonService.RunAsync(scope =>
            {
                dynamic sys = scope.Import("sys");
                sys.path.append(_scriptPath);
                
                if (!System.IO.File.Exists(System.IO.Path.Combine(_scriptPath, "smart_screener.py")))
                {
                    throw new System.IO.FileNotFoundException($"FATAL ERROR! Path investigated: {_scriptPath}");
                }

                using var script = scope.Import("smart_screener");
                
                // Invoke python function
                // screen_stocks(json_file_path, criteria)
                using (Py.GIL())
                {
                    using var pyList = script.InvokeMethod("screen_stocks", PyObject.FromManagedObject(_jsonDataPath), PyObject.FromManagedObject(criteria));
                    var result = new List<string>();
                    
                    // Convert PyList to C# List<string>
                    int len = (int)pyList.Length();
                    for (int i = 0; i < len; i++)
                    {
                        using var item = pyList[i];
                        result.Add(item.As<string>());
                    }
                    return result;
                }
            });
        }
    }
}
