using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

public interface IQValueService
{
    Task<RlQSnapshot> ReplayAsync(RlReplayRequest request, CancellationToken cancellationToken = default);
}
