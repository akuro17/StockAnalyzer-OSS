using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services
{
    public class ArrowConverter
    {
        // Arrow Schema Definition
        private static readonly Schema CandleSchema = new Schema.Builder()
#pragma warning disable CS8600
            .Field(new Field("Timestamp", new TimestampType(TimeUnit.Millisecond, (string)null), false))
#pragma warning restore CS8600
            .Field(new Field("Open", DoubleType.Default, false))
            .Field(new Field("High", DoubleType.Default, false))
            .Field(new Field("Low", DoubleType.Default, false))
            .Field(new Field("Close", DoubleType.Default, false))
            .Field(new Field("Volume", Int64Type.Default, false))
            .Build();

        public static async Task<byte[]> ConvertToArrowIpcAsync(List<CandleData> candles)
        {
            using var memoryStream = new MemoryStream();
            await WriteToArrowStreamAsync(candles, memoryStream);
            return memoryStream.ToArray();
        }

        public static async Task WriteToArrowStreamAsync(List<CandleData> candles, Stream outputStream)
        {
            var recordBatch = CreateRecordBatch(candles);

            // LeaveOpen: true to not close the stream when writer disposes? 
            // ArrowStreamWriter does not close the base stream by default usually?
            // Actually it depends. But 'using' writer will flush.
            using var writer = new ArrowStreamWriter(outputStream, CandleSchema, leaveOpen: true);
            
            await writer.WriteRecordBatchAsync(recordBatch);
            await writer.WriteEndAsync();
        }

        private static RecordBatch CreateRecordBatch(List<CandleData> candles)
        {
            int length = candles.Count;

            var timestampBuilder = new TimestampArray.Builder();
            var openBuilder = new DoubleArray.Builder();
            var highBuilder = new DoubleArray.Builder();
            var lowBuilder = new DoubleArray.Builder();
            var closeBuilder = new DoubleArray.Builder();
            var volumeBuilder = new Int64Array.Builder();

            // Reserving capacity for performance
            timestampBuilder.Reserve(length);
            openBuilder.Reserve(length);
            highBuilder.Reserve(length);
            lowBuilder.Reserve(length);
            closeBuilder.Reserve(length);
            volumeBuilder.Reserve(length);

            foreach (var candle in candles)
            {
                 timestampBuilder.Append(new DateTimeOffset(candle.Timestamp));
                 openBuilder.Append((double)candle.Open);
                 highBuilder.Append((double)candle.High);
                 lowBuilder.Append((double)candle.Low);
                 closeBuilder.Append((double)candle.Close);
                 volumeBuilder.Append(candle.Volume);
            }

            var recordBatch = new RecordBatch(CandleSchema, new IArrowArray[]
            {
                timestampBuilder.Build(),
                openBuilder.Build(),
                highBuilder.Build(),
                lowBuilder.Build(),
                closeBuilder.Build(),
                volumeBuilder.Build()
            }, length);

            return recordBatch;
        }
    }
}
