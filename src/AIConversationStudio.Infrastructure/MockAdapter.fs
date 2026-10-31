namespace AIConversationStudio.Infrastructure

open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open AIConversationStudio.Domain
open AIConversationStudio.Application

/// Capture is opt-in and kept only in memory for synthetic test scenarios.
type MockAdapter(respond: ChatRequest -> CancellationToken -> Task<Result<ChatResponse, ChatError>>, ?captureRequests: bool) =
    let requests = ConcurrentQueue<ChatRequest>()
    let capture = defaultArg captureRequests false
    member _.Requests = requests.ToArray() |> Array.toList
    interface IProviderAdapter with
        member _.Parameters _ = []
        member _.Send(request, ct) =
            if capture then requests.Enqueue request
            respond request ct
