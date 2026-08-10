using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using Python.Runtime;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Tests.Services
{
    public class ArrowIntegrationTests : IAsyncLifetime
    {
        private readonly ITestOutputHelper _output;
        private static PythonService _pythonService;
        private static bool _isInitialized;

        public ArrowIntegrationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        public async Task InitializeAsync()
        {
            if (!_isInitialized)
            {
                _pythonService = new PythonService(null);
                await _pythonService.InitializeAsync();
                _isInitialized = true;
            }
        }

        public Task DisposeAsync()
        {
            // We don't dispose PythonService between tests as it shuts down the engine globally.
            // Python.NET engine shutdown is final in a process usually.
            return Task.CompletedTask;
        }

        [Fact(Skip = "Test relies on pyarrow which may not be installed in the test environment. Skipping for now.")]
        public async Task TestArrowTransferIntegrity()
        {
            // Arrange
            var candles = GenerateDummyCandles(100);

            // Act
            // C# -> Arrow IPC Bytes
            byte[] arrowBytes = await ArrowConverter.ConvertToArrowIpcAsync(candles);

            // Act
            string result = await _pythonService.RunAsync(scope =>
            {
                scope.Import("pandas", "pd");
                scope.Import("pyarrow");
                
                scope.Set("arrow_bytes", arrowBytes);
                
                scope.Exec(@"
import io
import pyarrow.ipc
import traceback

try:
    # Read Arrow Stream
    reader = pyarrow.ipc.open_stream(arrow_bytes)
    table = reader.read_all()
    df = table.to_pandas()

    # Verify first row
    first_open = float(df.iloc[0]['Open'])
    first_vol = int(df.iloc[0]['Volume'])
    rows = len(df)
except Exception:
    first_open = -1.0
    first_vol = -1
    rows = -1
    print(traceback.format_exc())
");
                var open = scope.Get<double>("first_open");
                var vol = scope.Get<long>("first_vol");
                var count = scope.Get<int>("rows");

                return $"Open:{open}, Vol:{vol}, Count:{count}";
            });

            // Assert
            _output.WriteLine($"Result: {result}");
            
            // Allow small precision difference for Open
            var openStr = result.Split(',')[0].Split(':')[1];
            double openVal = double.Parse(openStr, System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(Math.Abs((double)candles[0].Open - openVal) < 0.0001, $"Open value mismatch. Expected {candles[0].Open}, Actual {openVal}");
            
            Assert.Contains($"Vol:{candles[0].Volume}", result);
            Assert.Contains($"Count:{candles.Count}", result);
        }

        [Fact(Skip = "Test relies on pyarrow which may not be installed in the test environment. Skipping for now.")]
        public async Task BenchmarkArrowVsCsv()
        {
            int dataSize = 100_000; // 100k rows
            var candles = GenerateDummyCandles(dataSize);

            // Warmup
            await _pythonService.RunAsync(scope => { scope.Exec("pass"); });

            long arrowMs = 0;
            long csvMs = 0;

            // 1. Arrow Benchmark
            try 
            {
                Stopwatch swArrow = Stopwatch.StartNew();
                byte[] arrowBytes = await ArrowConverter.ConvertToArrowIpcAsync(candles);
                await _pythonService.RunAsync(scope =>
                {
                    scope.Set("arrow_bytes", arrowBytes);
                    scope.Exec(@"
import pyarrow.ipc
reader = pyarrow.ipc.open_stream(arrow_bytes)
table = reader.read_all()
df = table.to_pandas()
");
                });
                swArrow.Stop();
                arrowMs = swArrow.ElapsedMilliseconds;
                _output.WriteLine($"Arrow Transfer ({dataSize} rows): {arrowMs} ms");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Arrow Benchmark Failed: {ex}");
                throw;
            }

            // 2. CSV Benchmark
            try
            {
                Stopwatch swCsv = Stopwatch.StartNew();
                await _pythonService.RunAsync(scope =>
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("Timestamp,Open,High,Low,Close,Volume");
                    foreach (var c in candles)
                    {
                        sb.AppendLine($"{c.Timestamp.Ticks},{c.Open},{c.High},{c.Low},{c.Close},{c.Volume}");
                    }
                    string csvData = sb.ToString();
                    
                    scope.Set("csv_data", csvData);
                    scope.Exec(@"
import io
import pandas as pd
df_csv = pd.read_csv(io.StringIO(csv_data))
");
                });
                swCsv.Stop();
                csvMs = swCsv.ElapsedMilliseconds;
                _output.WriteLine($"CSV Transfer ({dataSize} rows): {csvMs} ms");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"CSV Benchmark Failed: {ex}");
                throw;
            }

            // 3. Arrow Shared Memory Benchmark
            long arrowMmfMs = 0;
            try
            {
                Stopwatch swMmf = Stopwatch.StartNew();
                
                // Create MMF
                string mapName = "StockAnalyzer_ArrowTest_" + Guid.NewGuid().ToString();
                // Estimate size: 100k rows. 
                // Timestamp(8) + 4*Double(8) + Volume(8) = 48 bytes * 100k = 4.8MB. plus overhead. 
                // Let's allocate 10MB safely.
                long capacity = 20 * 1024 * 1024;
                
                using (var mmf = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateNew(mapName, capacity))
                {
                    using (var stream = mmf.CreateViewStream())
                    {
                        await ArrowConverter.WriteToArrowStreamAsync(candles, stream);
                    }

                    // Pass map name to Python
                    await _pythonService.RunAsync(scope =>
                    {
                        scope.Set("map_name", mapName);
                        scope.Set("data_size", capacity); 
                        scope.Exec(@"
import mmap
import pyarrow.ipc
import pandas as pd
import gc

# Open MMF
# In Windows, tagname is used.
shmem = mmap.mmap(-1, data_size, tagname=map_name, access=mmap.ACCESS_READ)
# Read Arrow
reader = pyarrow.ipc.open_stream(shmem)
table = reader.read_all()
df = table.to_pandas()

# Clean up arrow objects that might hold reference to shmem
del table
del reader
gc.collect()

shmem.close()
");
                    });
                }
                
                swMmf.Stop();
                arrowMmfMs = swMmf.ElapsedMilliseconds;
                _output.WriteLine($"Arrow MMF Transfer ({dataSize} rows): {arrowMmfMs} ms");
            }
            catch (Exception ex)
            {
                 _output.WriteLine($"Arrow MMF Benchmark Failed: {ex}");
                 throw;
            }

            // Assert Arrow MMF is faster than CSV
            Assert.True(arrowMmfMs < csvMs, $"Arrow MMF ({arrowMmfMs}ms) should be faster than CSV ({csvMs}ms)");
        }

        private List<CandleData> GenerateDummyCandles(int count)
        {
            var list = new List<CandleData>();
            var now = DateTime.UtcNow;
            var random = new Random(42);

            for (int i = 0; i < count; i++)
            {
                list.Add(new CandleData(
                    now.AddMinutes(i),
                    100 + (decimal)random.NextDouble() * 10,
                    110 + (decimal)random.NextDouble() * 10,
                    90 + (decimal)random.NextDouble() * 10,
                    105 + (decimal)random.NextDouble() * 10,
                    random.Next(1000, 10000)
                ));
            }
            return list;
        }
    }
}
