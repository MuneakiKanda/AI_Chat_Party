namespace AIConversationStudio.Application

open System
open System.Threading
open AIConversationStudio.Domain

type GatewayResult = {
    Response: Result<ChatResponse, ChatError> option
    Communication: CommunicationResult
    Reserved: int
    Sent: int
    BudgetExceeded: bool
}

/// All provider requests reserve a slot before adapter invocation. No automatic retries.
type ApiGateway(budget: IAttemptBudget, registry: IAdapterRegistry, tracker: IGenerationTracker) =
    member _.Send(sessionId, connectionId, request: ChatRequest, ct: CancellationToken) = task {
        let empty = { Response = None; Communication = CommunicationResult.NotSent; Reserved = 0; Sent = 0; BudgetExceeded = false }
        if ct.IsCancellationRequested then
            return { empty with Communication = CommunicationResult.Cancelled }
        else
            match registry.Resolve connectionId with
            | Error _ ->
                let error = ChatError.Unexpected "Connection is not registered."
                return { empty with Response = Some(Error error); Communication = CommunicationResult.Failed error }
            | Ok adapter ->
                match budget.TryReserve sessionId with
                | AttemptReservation.LimitReached _
                | AttemptReservation.NotRunning StopReason.MaxApiCallsReached ->
                    return { empty with BudgetExceeded = true }
                | AttemptReservation.NotRunning _ ->
                    return { empty with Communication = CommunicationResult.Cancelled }
                | AttemptReservation.Reserved attempt ->
                    tracker.RecordAttempt(AttemptEvent.Reserved(request.GenerationId, attempt))
                    let mutable sent = 0
                    let! response = task {
                        try
                            ct.ThrowIfCancellationRequested()
                            sent <- 1
                            tracker.RecordAttempt(AttemptEvent.Sent(request.GenerationId, attempt))
                            return! adapter.Send(request, ct)
                        with
                        | :? OperationCanceledException -> return Error ChatError.Cancelled
                        | _ -> return Error(ChatError.Unexpected "Provider adapter failed.")
                    }
                    let communication =
                        match response with
                        | Ok _ -> CommunicationResult.Completed
                        | Error ChatError.Cancelled -> CommunicationResult.Cancelled
                        | Error error -> CommunicationResult.Failed error
                    tracker.RecordAttempt(AttemptEvent.Finished(request.GenerationId, communication))
                    return { Response = Some response; Communication = communication; Reserved = 1; Sent = sent; BudgetExceeded = false }
        }
