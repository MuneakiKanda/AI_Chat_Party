namespace AIConversationStudio.Application

open System
open System.Threading
open AIConversationStudio.Domain

type SessionConfiguration = {
    SessionId: SessionId
    RoomId: RoomId
    Participants: RoomParticipant list
    RoomInstructions: string
    HistoryFormat: HistoryFormat
}

/// Slice 1 exposes individual turns; the console runs a finite, sequential harness.
/// Configuration and model bindings are captured once at the session boundary.
type ConversationOrchestrator(config: SessionConfiguration, store: IConversationStore,
                              gateway: ApiGateway, tracker: IGenerationTracker,
                              events: IMessageEvents, clock: IClock, ids: IIdGenerator) =
    let participants = config.Participants |> List.map (fun p -> p.Character.Id, p) |> Map.ofList
    do
        if participants.Count <> config.Participants.Length then
            invalidArg (nameof config) "Duplicate character IDs."
        if config.Participants |> List.exists (fun p -> String.IsNullOrWhiteSpace p.Character.Name) then
            invalidArg (nameof config) "Character names must not be empty."

    member _.RunTurn(characterId, ct: CancellationToken) = task {
        let participant = participants[characterId]
        let binding = defaultArg participant.ModelOverride participant.Character.DefaultModel
        let generationId = GenerationId(ids.NewId())
        let instructions = PromptComposer.compose participant config.Participants config.RoomInstructions []
        let baseline = {
            GenerationId = generationId; SessionId = config.SessionId; RoomId = config.RoomId
            CharacterId = characterId; ConnectionId = binding.ConnectionId; ModelId = binding.ModelId
            PromptVersions = instructions |> List.choose _.TemplateVersion
            HistoryFormat = config.HistoryFormat; BasedOnSequence = 0L
            StartedAt = clock.UtcNow; EndedAt = clock.UtcNow
            AttemptCount = 0; SentCount = 0; RetryCount = 0; ToolRoundTrips = 0
            Usage = Usage.unknown; Communication = CommunicationResult.NotSent
            Normalization = None; Posting = PostingResult.Cancelled; Notification = NotificationResult.NotRequired
        }
        match store.BeginGeneration(config.SessionId, characterId, generationId) with
        | Error reason ->
            let posting =
                match (store.Snapshot config.SessionId).State with
                | SessionRunState.Draining StopReason.MaxApiCallsReached
                | SessionRunState.Stopped StopReason.MaxApiCallsReached -> PostingResult.BudgetExceeded
                | _ -> PostingResult.Discarded reason
            let record = { baseline with Posting = posting; EndedAt = clock.UtcNow }
            tracker.Complete record
            return record
        | Ok snapshot ->
            try
                let request = {
                    GenerationId = generationId; ModelId = binding.ModelId; Instructions = instructions
                    History = HistoryFormatter.format config.HistoryFormat characterId snapshot.History
                    Tools = []; Parameters = Map.empty
                }
                let! result = gateway.Send(config.SessionId, binding.ConnectionId, request, ct)
                let mutable record = {
                    baseline with BasedOnSequence = snapshot.BasedOnSequence; StartedAt = snapshot.StartedAt
                                  Communication = result.Communication; AttemptCount = result.Reserved; SentCount = result.Sent
                }
                if result.BudgetExceeded then
                    record <- { record with Posting = PostingResult.BudgetExceeded }
                elif ct.IsCancellationRequested then
                    record <- { record with Posting = PostingResult.Cancelled }
                else
                    match result.Response with
                    | Some(Ok(Text(raw, usage))) ->
                        record <- { record with Usage = usage }
                        match ResponseNormalizer.normalize participant.Character.Name (config.Participants |> List.map _.Character.Name) raw with
                        | GenerationDecision.Pass ->
                            record <- { record with Normalization = Some NormalizationResult.Pass; Posting = PostingResult.Passed }
                        | GenerationDecision.Rejected reason ->
                            record <- { record with Normalization = Some(NormalizationResult.Rejected reason); Posting = PostingResult.Rejected reason }
                        | GenerationDecision.Speak content ->
                            let command = {
                                SessionId = config.SessionId; RoomId = config.RoomId; GenerationId = generationId
                                CharacterId = characterId; SpeakerName = participant.Character.Name
                                ExpectedEpoch = snapshot.Epoch; BasedOnSequence = snapshot.BasedOnSequence
                                StartedAt = snapshot.StartedAt; Content = content
                            }
                            record <- { record with Normalization = Some NormalizationResult.Speak }
                            match store.TryCommit command with
                            | PostCommitResult.Committed sequence ->
                                record <- { record with Posting = PostingResult.Posted sequence }
                                // Notification cannot undo a committed post or suppress its audit record.
                                let notification =
                                    try
                                        events.Publish { SessionId = config.SessionId; RoomId = config.RoomId; GenerationId = generationId; Sequence = sequence }
                                        NotificationResult.Delivered
                                    with _ -> NotificationResult.Failed
                                record <- { record with Notification = notification }
                            | PostCommitResult.Rejected reason ->
                                record <- { record with Posting = PostingResult.Discarded reason }
                    | Some(Ok(ToolCalls(_, usage))) ->
                        // Tool contracts are reserved for Slice 5; never execute or resend here.
                        record <- { record with Usage = usage; Normalization = Some(NormalizationResult.Rejected RejectReason.InvalidFormat); Posting = PostingResult.Rejected RejectReason.InvalidFormat }
                    | Some(Error ChatError.Cancelled) -> record <- { record with Posting = PostingResult.Cancelled }
                    | Some(Error _) -> record <- { record with Posting = PostingResult.Failed }
                    | None -> ()
                record <- { record with EndedAt = clock.UtcNow }
                tracker.Complete record
                return record
            finally
                store.CompleteGeneration(config.SessionId, generationId)
    }
