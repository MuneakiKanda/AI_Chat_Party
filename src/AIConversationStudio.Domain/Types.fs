namespace AIConversationStudio.Domain

open System

type ConnectionId = ConnectionId of Guid
type CharacterId = CharacterId of Guid
type RoomId = RoomId of Guid
type SessionId = SessionId of Guid
type GenerationId = GenerationId of Guid
type ModelId = ModelId of string

type ModelBinding = { ConnectionId: ConnectionId; ModelId: ModelId }
type Character = { Id: CharacterId; Name: string; PersonaMarkdown: string; DefaultModel: ModelBinding }
type RoomParticipant = { Character: Character; ModelOverride: ModelBinding option; AdditionalInstructions: string option }

[<RequireQualifiedAccess>]
type HistoryFormat = NamedRoleMessages | ScriptTranscript
[<RequireQualifiedAccess>]
type LogicalRole = Self | Others
type ConversationEntry = { Sequence: int64; SpeakerId: CharacterId option; SpeakerName: string; Content: string }
type FormattedHistory = RoleMessages of (LogicalRole * string) list | Transcript of string
[<RequireQualifiedAccess>]
type InstructionKind = AppRules | RoomInstructions | Persona | ToolGuide
type InstructionSegment = { Kind: InstructionKind; Content: string; TemplateVersion: string option }

type ParameterValue = BoolValue of bool | IntValue of int | DecimalValue of decimal | ChoiceValue of string
type ParameterType = Boolean | Integer of int option * int option | Decimal of decimal option * decimal option | Choice of string list
type ParameterDefinition = { Key: string; DisplayName: string; ParameterType: ParameterType; Required: bool; DefaultValue: ParameterValue option }
type ToolDefinition = { Name: string; Description: string; InputSchemaJson: string }
type ToolCall = { Id: string; Name: string; ArgumentsJson: string }
type Usage = { InputTokens: int64 option; OutputTokens: int64 option }
module Usage =
    let unknown = { InputTokens = None; OutputTokens = None }

[<RequireQualifiedAccess>]
type ChatError =
    | ConnectionFailedBeforeSend of string
    | RateLimited of TimeSpan option
    | QuotaExceeded of string
    | AuthenticationFailed
    | ModelNotAvailable of string
    | TimeoutSendStateUnknown
    | TransportFailed
    | ProviderRejected of statusCode: int
    | ProviderServerError of statusCode: int
    | InvalidResponse
    | CredentialUnavailable
    | TimedOutAfterSend
    | StreamInterrupted
    | Cancelled
    | Unexpected of string

type ChatRequest = {
    GenerationId: GenerationId
    ModelId: ModelId
    Instructions: InstructionSegment list
    History: FormattedHistory
    Tools: ToolDefinition list
    Parameters: Map<string, ParameterValue>
}
type ChatResponse = Text of string * Usage | ToolCalls of ToolCall list * Usage
[<RequireQualifiedAccess>]
type RejectReason = EmptyOutput | ImpersonatesOthers | InvalidFormat
[<RequireQualifiedAccess>]
type GenerationDecision = Speak of string | Pass | Rejected of RejectReason
[<RequireQualifiedAccess>]
type StopReason = MaxApiCallsReached | MaxMessagesReached | UserStopped | EmergencyStop
[<RequireQualifiedAccess>]
type SessionRunState = Running | Draining of StopReason | Stopped of StopReason
type SessionLimits = { MaxAttempts: int; MaxAiPosts: int; MaxGenerationAge: TimeSpan; MaxNewMessages: int64 }
module SessionLimits =
    let defaults = { MaxAttempts = 100; MaxAiPosts = 50; MaxGenerationAge = TimeSpan.FromSeconds 60.; MaxNewMessages = 5L }

[<RequireQualifiedAccess>]
type AttemptReservation = Reserved of int | LimitReached of int | NotRunning of StopReason
[<RequireQualifiedAccess>]
type PostRejection = MaxMessagesReached | ParticipantResting | SessionNotAcceptingPosts | EpochChanged | StaleGeneration | DuplicateGeneration | UnknownGeneration
[<RequireQualifiedAccess>]
type PostCommitResult = Committed of int64 | Rejected of PostRejection
type GenerationSnapshot = { Epoch: int64; BasedOnSequence: int64; StartedAt: DateTimeOffset; History: ConversationEntry list }
type PostCommand = {
    SessionId: SessionId
    RoomId: RoomId
    GenerationId: GenerationId
    CharacterId: CharacterId
    SpeakerName: string
    ExpectedEpoch: int64
    BasedOnSequence: int64
    StartedAt: DateTimeOffset
    Content: string
}
type SessionSnapshot = {
    State: SessionRunState
    Epoch: int64
    Attempts: int
    AiPosts: int
    ActiveGenerations: int
    Entries: ConversationEntry list
}
[<RequireQualifiedAccess>]
type CommunicationResult = NotSent | Completed | Failed of ChatError | Cancelled
[<RequireQualifiedAccess>]
type PostingResult = Posted of int64 | Passed | Rejected of RejectReason | Discarded of PostRejection | Failed | Cancelled | BudgetExceeded
[<RequireQualifiedAccess>]
type NormalizationResult = Speak | Pass | Rejected of RejectReason
[<RequireQualifiedAccess>]
type NotificationResult = NotRequired | Delivered | Failed
type ProviderResponseDiagnostics = {
    StatusCode: int
    ErrorCode: string option
    ErrorType: string option
    RequestId: string option
}
type GenerationRecord = {
    GenerationId: GenerationId
    SessionId: SessionId
    RoomId: RoomId
    CharacterId: CharacterId
    ConnectionId: ConnectionId
    ModelId: ModelId
    PromptVersions: string list
    HistoryFormat: HistoryFormat
    BasedOnSequence: int64
    StartedAt: DateTimeOffset
    EndedAt: DateTimeOffset
    AttemptCount: int
    SentCount: int
    RetryCount: int
    ToolRoundTrips: int
    Usage: Usage
    Communication: CommunicationResult
    Normalization: NormalizationResult option
    Posting: PostingResult
    Notification: NotificationResult
    ProviderResponse: ProviderResponseDiagnostics option
}
[<RequireQualifiedAccess>]
type AttemptEvent = Reserved of GenerationId * int | Sent of GenerationId * int | Finished of GenerationId * CommunicationResult
type MessagePosted = { SessionId: SessionId; RoomId: RoomId; GenerationId: GenerationId; Sequence: int64 }
