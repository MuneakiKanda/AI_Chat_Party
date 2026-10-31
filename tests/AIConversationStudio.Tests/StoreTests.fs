namespace AIConversationStudio.Tests

open System
open System.Threading.Tasks
open Xunit
open AIConversationStudio.Domain
open AIConversationStudio.Application
open AIConversationStudio.Infrastructure

type private StoreTestClock() =
    let mutable now = DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero)
    member _.Advance duration = now <- now + duration
    interface IClock with member _.UtcNow = now

module StoreTests =
    let private setup limits =
        let clock = StoreTestClock()
        let store = InMemoryStore(clock) :> IConversationStore
        let sessionId = SessionId(Guid.NewGuid())
        let roomId = RoomId(Guid.NewGuid())
        store.CreateSession(sessionId, roomId, limits)
        clock, store, sessionId, roomId

    let private beginPost (store: IConversationStore) sessionId roomId =
        let generationId = GenerationId(Guid.NewGuid())
        let characterId = CharacterId(Guid.NewGuid())
        let snapshot =
            match store.BeginGeneration(sessionId, characterId, generationId) with
            | Ok value -> value
            | Error reason -> failwithf "Unexpected begin rejection: %A" reason
        {
            SessionId = sessionId; RoomId = roomId; GenerationId = generationId
            CharacterId = characterId; SpeakerName = "Synthetic AI"
            ExpectedEpoch = snapshot.Epoch; BasedOnSequence = snapshot.BasedOnSequence
            StartedAt = snapshot.StartedAt; Content = "Synthetic content"
        }

    [<Fact>]
    let ``T07 only one concurrent reservation can consume the final attempt`` () =
        let _, store, sessionId, roomId = setup SessionLimits.defaults
        let first = beginPost store sessionId roomId
        let second = beginPost store sessionId roomId
        for expected in 1 .. 99 do
            Assert.Equal(AttemptReservation.Reserved expected, store.TryReserve sessionId)
        let reservations =
            [| Task.Run(fun () -> store.TryReserve sessionId)
               Task.Run(fun () -> store.TryReserve sessionId) |]
            |> Task.WhenAll
            |> fun task -> task.GetAwaiter().GetResult()
        Assert.Equal(1, reservations |> Array.filter (function AttemptReservation.Reserved _ -> true | _ -> false) |> Array.length)
        Assert.Equal(1, reservations |> Array.filter ((=) (AttemptReservation.LimitReached 100)) |> Array.length)
        Assert.Equal(100, (store.Snapshot sessionId).Attempts)
        Assert.Equal(SessionRunState.Draining StopReason.MaxApiCallsReached, (store.Snapshot sessionId).State)
        store.CompleteGeneration(sessionId, first.GenerationId)
        store.CompleteGeneration(sessionId, second.GenerationId)
        Assert.Equal(SessionRunState.Stopped StopReason.MaxApiCallsReached, (store.Snapshot sessionId).State)

    [<Fact>]
    let ``T19 concurrent commits cannot exceed the final AI post slot`` () =
        let _, store, sessionId, roomId = setup SessionLimits.defaults
        for _ in 1 .. 49 do
            let command = beginPost store sessionId roomId
            store.TryReserve sessionId |> ignore
            Assert.Equal(PostCommitResult.Committed(int64 (store.Snapshot sessionId).AiPosts + 1L), store.TryCommit command)
            store.CompleteGeneration(sessionId, command.GenerationId)
        // Human posts consume a sequence but no AI budget.
        store.AddHumanMessage(sessionId, "Human", "Synthetic theme") |> ignore
        let commands = [| beginPost store sessionId roomId; beginPost store sessionId roomId |]
        let outcomes =
            commands
            |> Array.map (fun command -> Task.Run(fun () -> store.TryCommit command))
            |> Task.WhenAll
            |> fun task -> task.GetAwaiter().GetResult()
        Assert.Equal(1, outcomes |> Array.filter (function PostCommitResult.Committed _ -> true | _ -> false) |> Array.length)
        Assert.Equal(1, outcomes |> Array.filter ((=) (PostCommitResult.Rejected PostRejection.MaxMessagesReached)) |> Array.length)
        let snapshot = store.Snapshot sessionId
        Assert.Equal(50, snapshot.AiPosts)
        Assert.Equal(51, snapshot.Entries.Length)
        Assert.Equal<int64 list>([ 1L .. 51L ], snapshot.Entries |> List.map _.Sequence)
        for command in commands do store.CompleteGeneration(sessionId, command.GenerationId)
        Assert.Equal(SessionRunState.Stopped StopReason.MaxMessagesReached, (store.Snapshot sessionId).State)

    [<Fact>]
    let ``T20 existing generation commits while final reserved attempt drains`` () =
        let _, store, sessionId, roomId = setup { SessionLimits.defaults with MaxAttempts = 1 }
        let command = beginPost store sessionId roomId
        Assert.Equal(AttemptReservation.Reserved 1, store.TryReserve sessionId)
        Assert.Equal(SessionRunState.Draining StopReason.MaxApiCallsReached, (store.Snapshot sessionId).State)
        Assert.Equal(Error PostRejection.SessionNotAcceptingPosts,
                     store.BeginGeneration(sessionId, command.CharacterId, GenerationId(Guid.NewGuid())))
        Assert.Equal(PostCommitResult.Committed 1L, store.TryCommit command)
        store.CompleteGeneration(sessionId, command.GenerationId)
        Assert.Equal(SessionRunState.Stopped StopReason.MaxApiCallsReached, (store.Snapshot sessionId).State)

    [<Fact>]
    let ``T11 freshness expires at exactly sixty seconds`` () =
        let clock, store, sessionId, roomId = setup SessionLimits.defaults
        let command = beginPost store sessionId roomId
        clock.Advance(TimeSpan.FromSeconds 60.)
        Assert.Equal(PostCommitResult.Rejected PostRejection.StaleGeneration, store.TryCommit command)
        Assert.Empty((store.Snapshot sessionId).Entries)

    [<Fact>]
    let ``five new posts expire a draft but four new posts do not`` () =
        let _, store, sessionId, roomId = setup SessionLimits.defaults
        let older = beginPost store sessionId roomId
        for _ in 1 .. 4 do store.AddHumanMessage(sessionId, "Human", "New content") |> ignore
        let newer = beginPost store sessionId roomId
        Assert.Equal(PostCommitResult.Committed 5L, store.TryCommit newer)
        Assert.Equal(PostCommitResult.Rejected PostRejection.StaleGeneration, store.TryCommit older)
        let _, other, session2, room2 = setup SessionLimits.defaults
        let valid = beginPost other session2 room2
        for _ in 1 .. 4 do other.AddHumanMessage(session2, "Human", "New content") |> ignore
        Assert.Equal(PostCommitResult.Committed 5L, other.TryCommit valid)

    [<Fact>]
    let ``same generation cannot be posted or started twice`` () =
        let _, store, sessionId, roomId = setup SessionLimits.defaults
        let command = beginPost store sessionId roomId
        Assert.Equal(PostCommitResult.Committed 1L, store.TryCommit command)
        Assert.Equal(PostCommitResult.Rejected PostRejection.DuplicateGeneration, store.TryCommit command)
        store.CompleteGeneration(sessionId, command.GenerationId)
        Assert.Equal(Error PostRejection.DuplicateGeneration, store.BeginGeneration(sessionId, command.CharacterId, command.GenerationId))
        Assert.Equal(1, (store.Snapshot sessionId).AiPosts)

    [<Fact>]
    let ``rest and resume do not revive in flight drafts`` () =
        let _, store, sessionId, roomId = setup SessionLimits.defaults
        let command = beginPost store sessionId roomId
        store.SetResting(sessionId, command.CharacterId, true)
        Assert.Equal(PostCommitResult.Rejected PostRejection.ParticipantResting, store.TryCommit command)
        store.SetResting(sessionId, command.CharacterId, false)
        Assert.Equal(PostCommitResult.Rejected PostRejection.EpochChanged, store.TryCommit command)

    [<Fact>]
    let ``stop blocks late responses and new attempts`` () =
        let _, store, sessionId, roomId = setup SessionLimits.defaults
        let command = beginPost store sessionId roomId
        store.TryReserve sessionId |> ignore
        store.Stop(sessionId, StopReason.EmergencyStop)
        Assert.Equal(PostCommitResult.Rejected PostRejection.EpochChanged, store.TryCommit command)
        Assert.Equal(AttemptReservation.NotRunning StopReason.EmergencyStop, store.TryReserve sessionId)
        Assert.Empty((store.Snapshot sessionId).Entries)

    [<Fact>]
    let ``post snapshot and character cannot be substituted`` () =
        let _, store, sessionId, roomId = setup SessionLimits.defaults
        let command = beginPost store sessionId roomId
        let substitutions = [
            { command with CharacterId = CharacterId(Guid.NewGuid()) }
            { command with RoomId = RoomId(Guid.NewGuid()) }
            { command with BasedOnSequence = 999L }
            { command with StartedAt = command.StartedAt.AddMinutes 1. }
            { command with ExpectedEpoch = 999L }
        ]
        for substituted in substitutions do
            Assert.Equal(PostCommitResult.Rejected PostRejection.UnknownGeneration, store.TryCommit substituted)
        Assert.Empty((store.Snapshot sessionId).Entries)

    [<Fact>]
    let ``human posts are excluded from budgets and snapshots remain immutable`` () =
        let _, store, sessionId, _ = setup SessionLimits.defaults
        let before = store.Snapshot sessionId
        for _ in 1 .. 60 do store.AddHumanMessage(sessionId, "Human", "Synthetic content") |> ignore
        let after = store.Snapshot sessionId
        Assert.Empty(before.Entries)
        Assert.Equal(60, after.Entries.Length)
        Assert.Equal(0, after.Attempts)
        Assert.Equal(0, after.AiPosts)
        Assert.Equal(SessionRunState.Running, after.State)

    [<Fact>]
    let ``room permits only one active session`` () =
        let _, store, sessionId, roomId = setup SessionLimits.defaults
        Assert.Throws<ArgumentException>(fun () ->
            store.CreateSession(SessionId(Guid.NewGuid()), roomId, SessionLimits.defaults)) |> ignore
        store.Stop(sessionId, StopReason.UserStopped)
        store.CreateSession(SessionId(Guid.NewGuid()), roomId, SessionLimits.defaults)
