namespace AIConversationStudio.Infrastructure

open System
open System.Collections.Generic
open AIConversationStudio.Domain
open AIConversationStudio.Application

type private RegisteredGeneration = {
    CharacterId: CharacterId
    Snapshot: GenerationSnapshot
}

type private StoredSession(roomId: RoomId, limits: SessionLimits) =
    member _.RoomId = roomId
    member _.Limits = limits
    member val State = SessionRunState.Running with get, set
    member val Epoch = 0L with get, set
    member val Attempts = 0 with get, set
    member val AiPosts = 0 with get, set
    member val Sequence = 0L with get, set
    member val Entries = ResizeArray<ConversationEntry>()
    member val Generations = Dictionary<GenerationId, RegisteredGeneration>()
    member val Active = HashSet<GenerationId>()
    member val Committed = HashSet<GenerationId>()
    member val Resting = HashSet<CharacterId>()

/// A single-process store. Every session-state, budget and commit operation shares
/// one synchronization boundary; callers must not replace it with independent stores.
type InMemoryStore(clock: IClock) =
    let gate = obj ()
    let sessions = Dictionary<SessionId, StoredSession>()

    let getSession id =
        match sessions.TryGetValue id with
        | true, session -> session
        | _ -> invalidArg (nameof id) "The session does not exist."

    let finishDraining (session: StoredSession) =
        match session.State with
        | SessionRunState.Draining reason when session.Active.Count = 0 ->
            session.State <- SessionRunState.Stopped reason
        | _ -> ()

    let drain reason (session: StoredSession) =
        session.State <- SessionRunState.Draining reason
        finishDraining session

    let append speakerId name content (session: StoredSession) =
        session.Sequence <- session.Sequence + 1L
        let entry = {
            Sequence = session.Sequence
            SpeakerId = speakerId
            SpeakerName = name
            Content = content
        }
        session.Entries.Add entry
        entry

    interface IConversationStore with
        member _.CreateSession(id, roomId, limits) =
            lock gate (fun () ->
                if limits.MaxAttempts <= 0 || limits.MaxAiPosts <= 0
                   || limits.MaxGenerationAge <= TimeSpan.Zero || limits.MaxNewMessages <= 0L then
                    invalidArg (nameof limits) "Session limits must all be positive."
                if sessions.ContainsKey id then
                    invalidArg (nameof id) "The session already exists."
                if sessions.Values |> Seq.exists (fun session ->
                    session.RoomId = roomId &&
                    match session.State with
                    | SessionRunState.Stopped _ -> session.Active.Count > 0
                    | _ -> true) then
                    invalidArg (nameof roomId) "The room already has an active session."
                sessions.Add(id, StoredSession(roomId, limits)))

        member _.BeginGeneration(id, characterId, generationId) =
            lock gate (fun () ->
                let session = getSession id
                if session.Generations.ContainsKey generationId then
                    Error PostRejection.DuplicateGeneration
                elif session.Resting.Contains characterId then
                    Error PostRejection.ParticipantResting
                else
                    match session.State with
                    | SessionRunState.Running ->
                        let snapshot = {
                            Epoch = session.Epoch
                            BasedOnSequence = session.Sequence
                            StartedAt = clock.UtcNow
                            History = List.ofSeq session.Entries
                        }
                        session.Generations.Add(generationId, { CharacterId = characterId; Snapshot = snapshot })
                        session.Active.Add generationId |> ignore
                        Ok snapshot
                    | SessionRunState.Draining StopReason.MaxMessagesReached
                    | SessionRunState.Stopped StopReason.MaxMessagesReached ->
                        Error PostRejection.MaxMessagesReached
                    | _ -> Error PostRejection.SessionNotAcceptingPosts)

        member _.CompleteGeneration(id, generationId) =
            lock gate (fun () ->
                let session = getSession id
                session.Active.Remove generationId |> ignore
                finishDraining session)

        member _.Snapshot id =
            lock gate (fun () ->
                let session = getSession id
                {
                    State = session.State
                    Epoch = session.Epoch
                    Attempts = session.Attempts
                    AiPosts = session.AiPosts
                    ActiveGenerations = session.Active.Count
                    Entries = List.ofSeq session.Entries
                })

        member _.AddHumanMessage(id, name, content) =
            lock gate (fun () ->
                if String.IsNullOrWhiteSpace name then invalidArg (nameof name) "A speaker name is required."
                if String.IsNullOrWhiteSpace content then invalidArg (nameof content) "A message cannot be empty."
                append None name content (getSession id))

        member _.SetResting(id, characterId, resting) =
            lock gate (fun () ->
                let session = getSession id
                let changed =
                    if resting then session.Resting.Add characterId
                    else session.Resting.Remove characterId
                // PoC uses a session-wide epoch. Resume never revives an old draft.
                if changed then session.Epoch <- session.Epoch + 1L)

        member _.Stop(id, reason) =
            lock gate (fun () ->
                let session = getSession id
                session.Epoch <- session.Epoch + 1L
                session.State <- SessionRunState.Stopped reason)

    interface IAttemptBudget with
        member _.TryReserve id =
            lock gate (fun () ->
                let session = getSession id
                match session.State with
                | SessionRunState.Running ->
                    // The increment and closing new work are the same atomic operation.
                    session.Attempts <- session.Attempts + 1
                    if session.Attempts = session.Limits.MaxAttempts then
                        drain StopReason.MaxApiCallsReached session
                    AttemptReservation.Reserved session.Attempts
                | SessionRunState.Draining StopReason.MaxApiCallsReached
                | SessionRunState.Stopped StopReason.MaxApiCallsReached ->
                    AttemptReservation.LimitReached session.Limits.MaxAttempts
                | SessionRunState.Draining reason
                | SessionRunState.Stopped reason -> AttemptReservation.NotRunning reason)

    interface IMessageStore with
        member _.TryCommit command =
            lock gate (fun () ->
                let session = getSession command.SessionId
                let reject = PostCommitResult.Rejected
                if session.Committed.Contains command.GenerationId then
                    reject PostRejection.DuplicateGeneration
                else
                    match session.Generations.TryGetValue command.GenerationId with
                    | false, _ -> reject PostRejection.UnknownGeneration
                    | true, generation when
                        not (session.Active.Contains command.GenerationId)
                        || session.RoomId <> command.RoomId
                        || generation.CharacterId <> command.CharacterId
                        || generation.Snapshot.Epoch <> command.ExpectedEpoch
                        || generation.Snapshot.BasedOnSequence <> command.BasedOnSequence
                        || generation.Snapshot.StartedAt <> command.StartedAt ->
                        reject PostRejection.UnknownGeneration
                    | true, _ ->
                        if session.Resting.Contains command.CharacterId then
                            reject PostRejection.ParticipantResting
                        elif session.Epoch <> command.ExpectedEpoch then
                            reject PostRejection.EpochChanged
                        elif session.AiPosts >= session.Limits.MaxAiPosts then
                            reject PostRejection.MaxMessagesReached
                        else
                            match session.State with
                            | SessionRunState.Stopped _ -> reject PostRejection.SessionNotAcceptingPosts
                            | _ when clock.UtcNow - command.StartedAt >= session.Limits.MaxGenerationAge
                                     || session.Sequence - command.BasedOnSequence >= session.Limits.MaxNewMessages ->
                                reject PostRejection.StaleGeneration
                            | _ ->
                                if String.IsNullOrWhiteSpace command.Content then
                                    invalidArg (nameof command) "An AI message cannot be empty."
                                let entry = append (Some command.CharacterId) command.SpeakerName command.Content session
                                session.Committed.Add command.GenerationId |> ignore
                                session.AiPosts <- session.AiPosts + 1
                                if session.AiPosts = session.Limits.MaxAiPosts then
                                    drain StopReason.MaxMessagesReached session
                                PostCommitResult.Committed entry.Sequence)
