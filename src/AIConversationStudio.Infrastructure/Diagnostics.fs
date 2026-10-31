namespace AIConversationStudio.Infrastructure

open System
open System.Collections.Concurrent
open System.Text.Json
open AIConversationStudio.Domain
open AIConversationStudio.Application

type SystemClock() = interface IClock with member _.UtcNow = DateTimeOffset.UtcNow
type RandomIds() = interface IIdGenerator with member _.NewId() = Guid.NewGuid()

/// Metadata only: no requests, personas, conversation text or raw error messages.
module SafeRequestLogger =
    let private errorCode = function
        | ChatError.ConnectionFailedBeforeSend _ -> "ConnectionFailedBeforeSend"
        | ChatError.RateLimited _ -> "RateLimited"
        | ChatError.QuotaExceeded _ -> "QuotaExceeded"
        | ChatError.AuthenticationFailed -> "AuthenticationFailed"
        | ChatError.ModelNotAvailable _ -> "ModelNotAvailable"
        | ChatError.TimedOutAfterSend -> "TimedOutAfterSend"
        | ChatError.StreamInterrupted -> "StreamInterrupted"
        | ChatError.Cancelled -> "Cancelled"
        | ChatError.Unexpected _ -> "Unexpected"
    let format (record: GenerationRecord) =
        let (GenerationId generation) = record.GenerationId
        let (SessionId session) = record.SessionId
        let (RoomId room) = record.RoomId
        let (CharacterId character) = record.CharacterId
        let (ConnectionId connection) = record.ConnectionId
        let (ModelId model) = record.ModelId
        let communication =
            match record.Communication with
            | CommunicationResult.Failed error -> "Failed:" + errorCode error
            | CommunicationResult.Completed -> "Completed"
            | CommunicationResult.Cancelled -> "Cancelled"
            | CommunicationResult.NotSent -> "NotSent"
        JsonSerializer.Serialize {|
            generationId = generation
            sessionId = session
            roomId = room
            characterId = character
            connectionId = connection
            modelId = model
            promptVersions = List.toArray record.PromptVersions
            historyFormat = string record.HistoryFormat
            basedOnSequence = record.BasedOnSequence
            startedAt = record.StartedAt
            endedAt = record.EndedAt
            attempts = record.AttemptCount
            sent = record.SentCount
            retries = record.RetryCount
            toolRoundTrips = record.ToolRoundTrips
            inputTokens = Option.toNullable record.Usage.InputTokens
            outputTokens = Option.toNullable record.Usage.OutputTokens
            communication = communication
            notification = string record.Notification
            posting = sprintf "%A" record.Posting
            normalization = sprintf "%A" record.Normalization
        |}

type GenerationTracker() =
    let records = ConcurrentQueue<GenerationRecord>()
    let attempts = ConcurrentQueue<AttemptEvent>()
    member _.Records = records.ToArray() |> Array.toList
    member _.Attempts = attempts.ToArray() |> Array.toList
    interface IGenerationTracker with
        member _.RecordAttempt value = attempts.Enqueue value
        member _.Complete value = records.Enqueue value

type MessageEvents() =
    let messages = ConcurrentQueue<MessagePosted>()
    member _.Messages = messages.ToArray() |> Array.toList
    interface IMessageEvents with member _.Publish value = messages.Enqueue value
