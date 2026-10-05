using System;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

internal sealed record RlWorkEvidence(int BudgetChargedTransitions, int BudgetChargedEpisodeStarts,
    int TransitionAttempts, int SuccessfulFinancialTransitions, int EpisodeStartAttempts, int SuccessfulEpisodeStarts,
    int RegistrationReservedTransitions, int RegistrationReservedEpisodeStarts);

/// <summary>Budget charges are never rolled back; reservations never pretend to be successful financial operations.</summary>
internal sealed class RlWorkLedger
{
    public int Transitions { get; private set; }
    public int EpisodeStarts { get; private set; }
    private int _successfulTransitions, _successfulStarts, _reservedTransitions, _reservedStarts;
    internal void Restore(RlWorkEvidence saved, int chargedTransitions, int chargedStarts)
    {
        if (saved.BudgetChargedTransitions < 0 || saved.BudgetChargedEpisodeStarts < 0
            || saved.SuccessfulFinancialTransitions < 0 || saved.SuccessfulEpisodeStarts < 0
            || saved.RegistrationReservedTransitions < 0 || saved.RegistrationReservedEpisodeStarts < 0
            || saved.SuccessfulFinancialTransitions + saved.RegistrationReservedTransitions > saved.BudgetChargedTransitions
            || saved.SuccessfulEpisodeStarts + saved.RegistrationReservedEpisodeStarts > saved.BudgetChargedEpisodeStarts
            || saved.TransitionAttempts != saved.BudgetChargedTransitions - saved.RegistrationReservedTransitions
            || saved.EpisodeStartAttempts != saved.BudgetChargedEpisodeStarts - saved.RegistrationReservedEpisodeStarts)
            throw new ArgumentException("Invalid saved work ledger.");
        Transitions = Math.Max(saved.BudgetChargedTransitions, chargedTransitions);
        EpisodeStarts = Math.Max(saved.BudgetChargedEpisodeStarts, chargedStarts);
        _successfulTransitions = saved.SuccessfulFinancialTransitions; _successfulStarts = saved.SuccessfulEpisodeStarts;
        _reservedTransitions = saved.RegistrationReservedTransitions; _reservedStarts = saved.RegistrationReservedEpisodeStarts;
        RequireCapacity(0, 0);
    }
    public void Attempt(bool start)
    {
        if (start)
        {
            if (++EpisodeStarts > RlContract.EpisodeStartLimit) throw new InvalidOperationException("RL episode-start cap exceeded.");
        }
        else if (++Transitions > RlContract.ProcessedTransitionLimit) throw new InvalidOperationException("RL transition cap exceeded.");
    }
    public void Succeeded(bool start)
    {
        if (start) _successfulStarts = checked(_successfulStarts + 1);
        else _successfulTransitions = checked(_successfulTransitions + 1);
        if (_successfulStarts > EpisodeStarts - _reservedStarts || _successfulTransitions > Transitions - _reservedTransitions)
            throw new InvalidOperationException("RL success count exceeds attempted financial work.");
    }
    public void RequireCapacity(int count, int starts)
    {
        if (count < 0 || starts < 0 || checked(Transitions + count) > RlContract.ProcessedTransitionLimit
            || checked(EpisodeStarts + starts) > RlContract.EpisodeStartLimit)
            throw new InvalidOperationException("RL qualification estimate exceeds adopted whole-job limits.");
    }
    public void ReserveRegistration(int count, int starts)
    {
        RequireCapacity(count, starts);
        Transitions += count; EpisodeStarts += starts;
        _reservedTransitions = checked(_reservedTransitions + count); _reservedStarts = checked(_reservedStarts + starts);
    }
    public RlWorkEvidence Snapshot() => new(Transitions, EpisodeStarts, Transitions - _reservedTransitions,
        _successfulTransitions, EpisodeStarts - _reservedStarts, _successfulStarts, _reservedTransitions, _reservedStarts);
}
