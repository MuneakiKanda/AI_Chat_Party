namespace AIConversationStudio.Application

open System
open System.Threading
open System.Threading.Tasks
open AIConversationStudio.Domain

type IClock = abstract UtcNow: DateTimeOffset
type IIdGenerator = abstract NewId: unit -> Guid
type IAsyncDelay = abstract Delay: TimeSpan * CancellationToken -> Task
type IRandomSource = abstract NextDelay: TimeSpan * TimeSpan -> TimeSpan
type IProviderAdapter =
    abstract Parameters: ModelId -> ParameterDefinition list
    abstract Send: ChatRequest * CancellationToken -> Task<Result<ChatResponse, ChatError>>
type IAdapterRegistry = abstract Resolve: ConnectionId -> Result<IProviderAdapter, string>
type IAttemptBudget = abstract TryReserve: SessionId -> AttemptReservation
type IMessageStore = abstract TryCommit: PostCommand -> PostCommitResult
type IConversationStore =
    inherit IAttemptBudget
    inherit IMessageStore
    abstract CreateSession: SessionId * RoomId * SessionLimits -> unit
    abstract BeginGeneration: SessionId * CharacterId * GenerationId -> Result<GenerationSnapshot, PostRejection>
    abstract CompleteGeneration: SessionId * GenerationId -> unit
    abstract Snapshot: SessionId -> SessionSnapshot
    abstract AddHumanMessage: SessionId * string * string -> ConversationEntry
    abstract SetResting: SessionId * CharacterId * bool -> unit
    abstract Stop: SessionId * StopReason -> unit
type IGenerationTracker =
    // Adapters supply sanitized metadata only; application code does not parse provider payloads.
    abstract RecordProviderResponse: GenerationId * ProviderResponseDiagnostics -> unit
    abstract TakeProviderResponse: GenerationId -> ProviderResponseDiagnostics option
    abstract RecordAttempt: AttemptEvent -> unit
    abstract Complete: GenerationRecord -> unit
type IMessageEvents = abstract Publish: MessagePosted -> unit
