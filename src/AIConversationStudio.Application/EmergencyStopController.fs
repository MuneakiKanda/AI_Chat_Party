namespace AIConversationStudio.Application

open System
open System.Threading
open AIConversationStudio.Domain

/// Close reservations and advance epoch before signalling external cancellation.
/// Cancellation requests do not guarantee that the provider stops processing.
type EmergencyStopController(store: IConversationStore, sessionId: SessionId) =
    let cancellation = new CancellationTokenSource()
    member _.Token = cancellation.Token
    member _.Stop() =
        store.Stop(sessionId, StopReason.EmergencyStop)
        try cancellation.Cancel() with :? AggregateException -> ()
    interface IDisposable with member _.Dispose() = cancellation.Dispose()
