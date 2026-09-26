using System;
using StockAnalyzer.Core.Models.Drawing;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Status codes representing the outcome of a drawing layer or object command.
/// </summary>
public enum DrawingCommandStatus
{
    Success,
    NoChange,
    NotFound,
    Locked,
    InvalidArgument,
    CrossPanel,
    ConfirmationRequired,
    Busy,
    InvalidState,
    CapacityExceeded
}

/// <summary>
/// Result structure returned upon calling BeginEdit on a drawing transaction.
/// </summary>
public readonly record struct DrawingBeginResult(DrawingEditToken Token, DrawingCommandStatus Status)
{
    public bool IsSuccess => Status == DrawingCommandStatus.Success;

    public static DrawingBeginResult Succeeded(DrawingEditToken token) => new(token, DrawingCommandStatus.Success);
    public static DrawingBeginResult Busy() => new(DrawingEditToken.Empty, DrawingCommandStatus.Busy);
    public static DrawingBeginResult Fail(DrawingCommandStatus status) => new(DrawingEditToken.Empty, status);
}

/// <summary>
/// Immutable result representation for all drawing layer, object, and history operations.
/// </summary>
public readonly record struct DrawingCommandResult(
    DrawingCommandStatus Status,
    string? Message = null,
    Guid? TargetId = null,
    FrozenContextState? RestoredState = null)
{
    public bool IsSuccess => Status == DrawingCommandStatus.Success;

    public static DrawingCommandResult Success(Guid? targetId = null) =>
        new(DrawingCommandStatus.Success, TargetId: targetId);

    public static DrawingCommandResult Success(FrozenContextState restoredState) =>
        new(DrawingCommandStatus.Success, RestoredState: restoredState);

    public static DrawingCommandResult NoChange() =>
        new(DrawingCommandStatus.NoChange);

    public static DrawingCommandResult Busy() =>
        new(DrawingCommandStatus.Busy);

    public static DrawingCommandResult Fail(DrawingCommandStatus status, string? message = null, Guid? targetId = null, FrozenContextState? restoredState = null) =>
        new(status, Message: message, TargetId: targetId, RestoredState: restoredState);
}
