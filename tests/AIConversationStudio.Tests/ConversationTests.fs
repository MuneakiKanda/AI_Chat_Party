namespace AIConversationStudio.Tests

open System
open System.Threading
open System.Threading.Tasks
open System.Text.Json
open Xunit
open AIConversationStudio.Domain
open AIConversationStudio.Application
open AIConversationStudio.Infrastructure

module ConversationTests =
    let private fixedTime = DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.Zero)
    let private guid value = Guid(value, 0s, 0s, Array.zeroCreate<byte> 8)
    let private firstId = CharacterId(guid 1)
    let private secondId = CharacterId(guid 2)
    let private firstConnection = ConnectionId(guid 11)
    let private secondConnection = ConnectionId(guid 12)
    let private firstName = "ちゃぴ子先生"
    let private secondName = "オパ子先生"

    type private SequenceIds() =
        let mutable next = 100
        interface IIdGenerator with
            member _.NewId() = guid (Interlocked.Increment(&next))

    type private Fixture = {
        Store: IConversationStore
        Tracker: GenerationTracker
        Events: MessageEvents
        First: MockAdapter
        Second: MockAdapter
        Config: SessionConfiguration
        Clock: IClock
        Ids: IIdGenerator
        Runner: ConversationOrchestrator
    }

    let private reply result (_: ChatRequest) (_: CancellationToken) = Task.FromResult result
    let private say text = reply (Ok(Text(text, { InputTokens = Some 10L; OutputTokens = Some 5L })))

    let private create format limits firstResponse secondResponse =
        let clock = { new IClock with member _.UtcNow = fixedTime }
        let ids = SequenceIds() :> IIdGenerator
        let store = InMemoryStore(clock) :> IConversationStore
        let tracker = GenerationTracker()
        let events = MessageEvents()
        let first = MockAdapter(firstResponse, captureRequests = true)
        let second = MockAdapter(secondResponse, captureRequests = true)
        let participant id name connection model = {
            Character = { Id = id; Name = name; PersonaMarkdown = "synthetic-persona-marker-8472"
                          DefaultModel = { ConnectionId = connection; ModelId = ModelId model } }
            ModelOverride = None; AdditionalInstructions = Some "Stay concise."
        }
        let config = {
            SessionId = SessionId(guid 21); RoomId = RoomId(guid 22)
            Participants = [ participant firstId firstName firstConnection "mock-first"
                             participant secondId secondName secondConnection "mock-second" ]
            RoomInstructions = "Discuss the supplied topic."
            HistoryFormat = format
        }
        store.CreateSession(config.SessionId, config.RoomId, limits)
        let registry = AdapterRegistry [ firstConnection, first :> IProviderAdapter; secondConnection, second :> IProviderAdapter ]
        let gateway = ApiGateway(store, registry, tracker)
        let runner = ConversationOrchestrator(config, store, gateway, tracker, events, clock, ids)
        { Store = store; Tracker = tracker; Events = events; First = first; Second = second
          Config = config; Clock = clock; Ids = ids; Runner = runner }

    let private standard firstResponse secondResponse =
        create HistoryFormat.NamedRoleMessages SessionLimits.defaults firstResponse secondResponse

    let private reconfigure fixture config registry =
        ConversationOrchestrator(config, fixture.Store, ApiGateway(fixture.Store, registry, fixture.Tracker),
                                 fixture.Tracker, fixture.Events, fixture.Clock, fixture.Ids)

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``T01 T02 T03 two characters complete one roundtrip from their own history viewpoint`` transcript = task {
        let format = if transcript then HistoryFormat.ScriptTranscript else HistoryFormat.NamedRoleMessages
        let fixture = create format SessionLimits.defaults (say "ちゃぴ子先生：First answer") (say "Second answer")
        fixture.Store.AddHumanMessage(fixture.Config.SessionId, "Human", "Synthetic topic") |> ignore
        let! first = fixture.Runner.RunTurn(firstId, CancellationToken.None)
        let! second = fixture.Runner.RunTurn(secondId, CancellationToken.None)
        let snapshot = fixture.Store.Snapshot fixture.Config.SessionId
        Assert.Equal<int64 list>([ 1L; 2L; 3L ], snapshot.Entries |> List.map _.Sequence)
        Assert.Equal<string list>([ "Synthetic topic"; "First answer"; "Second answer" ], snapshot.Entries |> List.map _.Content)
        Assert.Equal(PostingResult.Posted 2L, first.Posting)
        Assert.Equal(PostingResult.Posted 3L, second.Posting)
        Assert.Equal(1L, first.BasedOnSequence)
        Assert.Equal(2L, second.BasedOnSequence)
        Assert.Equal(GenerationId(guid 101), first.GenerationId)
        Assert.Equal(GenerationId(guid 102), second.GenerationId)
        Assert.Equal(fixedTime, first.StartedAt)
        Assert.Equal(fixedTime, second.EndedAt)
        let secondRequest = Assert.Single fixture.Second.Requests
        match secondRequest.History with
        | RoleMessages messages ->
            Assert.Equal<(LogicalRole * string) list>(
                [ LogicalRole.Others, "Human：Synthetic topic"; LogicalRole.Others, "ちゃぴ子先生：First answer" ], messages)
        | Transcript text -> Assert.Equal("Human：Synthetic topic\nちゃぴ子先生：First answer", text)
        Assert.Equal(2, snapshot.Attempts)
        Assert.Equal(2, snapshot.AiPosts)
        Assert.Equal(0, snapshot.ActiveGenerations)
        Assert.Equal<int64 list>([ 2L; 3L ], fixture.Events.Messages |> List.map _.Sequence)
        Assert.Equal(2, fixture.Tracker.Records.Length)
        Assert.Equal(6, fixture.Tracker.Attempts.Length)
        for record in fixture.Tracker.Records do
            Assert.Equal(CommunicationResult.Completed, record.Communication)
            Assert.Equal(Some NormalizationResult.Speak, record.Normalization)
            Assert.Equal(Some 10L, record.Usage.InputTokens)
            Assert.Equal(1, record.AttemptCount)
            Assert.Equal(1, record.SentCount)
            Assert.Equal(0, record.RetryCount)
            Assert.NotEmpty(record.PromptVersions)
    }

    [<Fact>]
    let ``T04 PASS is a successful communication without posting or events`` () = task {
        let fixture = standard (say "[PASS]") (say "Unused")
        let! record = fixture.Runner.RunTurn(firstId, CancellationToken.None)
        Assert.Equal(CommunicationResult.Completed, record.Communication)
        Assert.Equal(PostingResult.Passed, record.Posting)
        Assert.Equal(Some NormalizationResult.Pass, record.Normalization)
        Assert.Equal(1, (fixture.Store.Snapshot fixture.Config.SessionId).Attempts)
        Assert.Empty((fixture.Store.Snapshot fixture.Config.SessionId).Entries)
        Assert.Empty(fixture.Events.Messages)
        Assert.Single(fixture.First.Requests) |> ignore
    }

    [<Theory>]
    [<InlineData("オパ子先生：Impersonated answer", true)>]
    [<InlineData("", false)>]
    let ``T05 rejected responses consume one attempt but never post`` raw impersonation = task {
        let fixture = standard (say raw) (say "Unused")
        let! record = fixture.Runner.RunTurn(firstId, CancellationToken.None)
        let reason = if impersonation then RejectReason.ImpersonatesOthers else RejectReason.EmptyOutput
        Assert.Equal(CommunicationResult.Completed, record.Communication)
        Assert.Equal(PostingResult.Rejected reason, record.Posting)
        Assert.Equal(Some(NormalizationResult.Rejected reason), record.Normalization)
        Assert.Equal(1, record.AttemptCount)
        Assert.Empty(fixture.Events.Messages)
        Assert.Empty((fixture.Store.Snapshot fixture.Config.SessionId).Entries)
        Assert.Single(fixture.First.Requests) |> ignore
    }

    [<Fact>]
    let ``T08 exhausted attempt budget never invokes another adapter`` () = task {
        let fixture = create HistoryFormat.NamedRoleMessages { SessionLimits.defaults with MaxAttempts = 1 } (say "Last allowed answer") (say "Forbidden")
        let! first = fixture.Runner.RunTurn(firstId, CancellationToken.None)
        let! second = fixture.Runner.RunTurn(secondId, CancellationToken.None)
        Assert.Equal(PostingResult.Posted 1L, first.Posting)
        Assert.Equal(PostingResult.BudgetExceeded, second.Posting)
        Assert.Equal(CommunicationResult.NotSent, second.Communication)
        Assert.Equal(0, second.AttemptCount)
        Assert.Empty(fixture.Second.Requests)
        let snapshot = fixture.Store.Snapshot fixture.Config.SessionId
        Assert.Equal(1, snapshot.Attempts)
        Assert.Equal(SessionRunState.Stopped StopReason.MaxApiCallsReached, snapshot.State)
    }

    [<Theory>]
    [<InlineData(0)>]
    [<InlineData(1)>]
    [<InlineData(2)>]
    [<InlineData(3)>]
    [<InlineData(4)>]
    [<InlineData(5)>]
    let ``T09 T14 failures are classified without retries regeneration or fallback`` errorCase = task {
        let error =
            match errorCase with
            | 0 -> ChatError.TimedOutAfterSend
            | 1 -> ChatError.AuthenticationFailed
            | 2 -> ChatError.QuotaExceeded "Synthetic quota error"
            | 3 -> ChatError.ModelNotAvailable "Synthetic missing model"
            | 4 -> ChatError.ConnectionFailedBeforeSend "Synthetic connection error"
            | _ -> ChatError.RateLimited(Some(TimeSpan.FromSeconds 3.))
        let fixture = standard (reply (Error error)) (say "Must not be used as fallback")
        let! record = fixture.Runner.RunTurn(firstId, CancellationToken.None)
        Assert.Equal(CommunicationResult.Failed error, record.Communication)
        Assert.Equal(PostingResult.Failed, record.Posting)
        Assert.Equal(firstConnection, record.ConnectionId)
        Assert.Equal(ModelId "mock-first", record.ModelId)
        Assert.Equal(1, record.AttemptCount)
        Assert.Equal(0, record.RetryCount)
        Assert.Single(fixture.First.Requests) |> ignore
        Assert.Empty(fixture.Second.Requests)
        Assert.Empty(fixture.Events.Messages)
        Assert.Equal(0, (fixture.Store.Snapshot fixture.Config.SessionId).ActiveGenerations)
    }

    [<Fact>]
    let ``T10 cancelled token before generation does not consume an attempt`` () = task {
        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()
        let fixture = standard (say "Never sent") (say "Unused")
        let! record = fixture.Runner.RunTurn(firstId, cancellation.Token)
        Assert.Equal(CommunicationResult.Cancelled, record.Communication)
        Assert.Equal(PostingResult.Cancelled, record.Posting)
        Assert.Equal(0, record.AttemptCount)
        Assert.Empty(fixture.First.Requests)
        Assert.Empty(fixture.Tracker.Attempts)
        Assert.Empty(fixture.Events.Messages)
    }

    [<Fact>]
    let ``T10 adapter ignoring cancellation can complete communication but cannot post`` () = task {
        use cancellation = new CancellationTokenSource()
        let response _ _ =
            cancellation.Cancel()
            Task.FromResult(Ok(Text("Late response", Usage.unknown)))
        let fixture = standard response (say "Unused")
        let! record = fixture.Runner.RunTurn(firstId, cancellation.Token)
        Assert.Equal(CommunicationResult.Completed, record.Communication)
        Assert.Equal(PostingResult.Cancelled, record.Posting)
        Assert.Equal(1, record.AttemptCount)
        Assert.Equal(1, record.SentCount)
        Assert.Empty(fixture.Events.Messages)
        Assert.Empty((fixture.Store.Snapshot fixture.Config.SessionId).Entries)
    }

    [<Fact>]
    let ``T18 cancellation after reservation preserves budget without a sent event`` () = task {
        use cancellation = new CancellationTokenSource()
        let fixture = standard (say "Never sent") (say "Unused")
        let cancellingTracker = {
            new IGenerationTracker with
                member _.RecordProviderResponse(id, response) = (fixture.Tracker :> IGenerationTracker).RecordProviderResponse(id, response)
                member _.TakeProviderResponse id = (fixture.Tracker :> IGenerationTracker).TakeProviderResponse id
                member _.RecordAttempt event =
                    (fixture.Tracker :> IGenerationTracker).RecordAttempt event
                    match event with
                    | AttemptEvent.Reserved _ -> cancellation.Cancel()
                    | _ -> ()
                member _.Complete record = (fixture.Tracker :> IGenerationTracker).Complete record
        }
        let registry = AdapterRegistry [firstConnection, fixture.First :> IProviderAdapter]
        let gateway = ApiGateway(fixture.Store, registry, cancellingTracker)
        let runner = ConversationOrchestrator(fixture.Config, fixture.Store, gateway, cancellingTracker,
                                             fixture.Events, fixture.Clock, fixture.Ids)
        let! record = runner.RunTurn(firstId, cancellation.Token)
        Assert.Equal(1, record.AttemptCount)
        Assert.Equal(0, record.SentCount)
        Assert.Equal(CommunicationResult.Cancelled, record.Communication)
        Assert.Equal(PostingResult.Cancelled, record.Posting)
        Assert.Equal(1, (fixture.Store.Snapshot fixture.Config.SessionId).Attempts)
        Assert.Empty(fixture.First.Requests)
        Assert.Equal<AttemptEvent list>(
            [ AttemptEvent.Reserved(record.GenerationId, 1); AttemptEvent.Finished(record.GenerationId, CommunicationResult.Cancelled) ],
            fixture.Tracker.Attempts)
    }

    [<Fact>]
    let ``T22 connection registry resolves separate instances and rejects missing connections`` () =
        let first = MockAdapter(say "First") :> IProviderAdapter
        let second = MockAdapter(say "Second") :> IProviderAdapter
        let registry = AdapterRegistry [ firstConnection, first; secondConnection, second ] :> IAdapterRegistry
        match registry.Resolve firstConnection, registry.Resolve secondConnection with
        | Ok actualFirst, Ok actualSecond ->
            Assert.Same(first, actualFirst)
            Assert.Same(second, actualSecond)
            Assert.NotSame(actualFirst, actualSecond)
        | _ -> failwith "Registered connections must resolve."
        match registry.Resolve(ConnectionId(guid 999)) with
        | Error _ -> ()
        | Ok _ -> failwith "Missing connections must not silently fall back."

    [<Fact>]
    let ``participant model override is fixed for the configured session`` () = task {
        let fixture = standard (say "Default must not run") (say "Override answer")
        let overrideBinding = { ConnectionId = secondConnection; ModelId = ModelId "override-model" }
        let configured =
            { fixture.Config with
                Participants =
                    fixture.Config.Participants |> List.map (fun p ->
                        if p.Character.Id = firstId then { p with ModelOverride = Some overrideBinding } else p) }
        let registry = AdapterRegistry [ firstConnection, fixture.First :> IProviderAdapter; secondConnection, fixture.Second :> IProviderAdapter ]
        let runner = reconfigure fixture configured registry
        // Constructing settings for a later session must not alter the current session.
        let nextSession = { configured with Participants = fixture.Config.Participants }
        Assert.Equal(None, nextSession.Participants.Head.ModelOverride)
        let! record = runner.RunTurn(firstId, CancellationToken.None)
        Assert.Equal(secondConnection, record.ConnectionId)
        Assert.Equal(ModelId "override-model", record.ModelId)
        Assert.Equal(ModelId "override-model", (Assert.Single fixture.Second.Requests).ModelId)
        Assert.Empty(fixture.First.Requests)
    }

    [<Fact>]
    let ``T13 structured logs contain only metadata even when provider errors contain sensitive text`` () = task {
        let sensitive = "synthetic-key-marker-3189 synthetic-body-marker-4367 synthetic-persona-marker-8472"
        let fixture = standard (reply (Error(ChatError.Unexpected sensitive))) (say "Unused")
        fixture.Store.AddHumanMessage(fixture.Config.SessionId, "Human", "synthetic-body-marker-4367") |> ignore
        let! record = fixture.Runner.RunTurn(firstId, CancellationToken.None)
        let log = SafeRequestLogger.format record
        use parsed = JsonDocument.Parse log
        Assert.Equal("Failed:Unexpected", parsed.RootElement.GetProperty("communication").GetString())
        Assert.Equal(1, parsed.RootElement.GetProperty("attempts").GetInt32())
        for marker in sensitive.Split(' ') do Assert.DoesNotContain(marker, log)
        Assert.DoesNotContain("Instructions", log)
        Assert.DoesNotContain("History", log)
    }

    [<Fact>]
    let ``Slice 1 does not execute tools or send a followup attempt`` () = task {
        let response = ToolCalls([ { Id = "synthetic-call"; Name = "get_active_drafts"; ArgumentsJson = "{}" } ], Usage.unknown)
        let fixture = standard (reply (Ok response)) (say "Unused")
        let! record = fixture.Runner.RunTurn(firstId, CancellationToken.None)
        Assert.Equal(CommunicationResult.Completed, record.Communication)
        Assert.Equal(PostingResult.Rejected RejectReason.InvalidFormat, record.Posting)
        Assert.Equal(1, record.AttemptCount)
        Assert.Equal(0, record.ToolRoundTrips)
        Assert.Single(fixture.First.Requests) |> ignore
        Assert.Empty(fixture.Events.Messages)
    }

    [<Fact>]
    let ``mock request capture is explicitly opt in`` () = task {
        let fixture = standard (say "Answer") (say "Unused")
        let uncaptured = MockAdapter(say "Answer")
        let registry = AdapterRegistry [ firstConnection, uncaptured :> IProviderAdapter ]
        let runner = reconfigure fixture fixture.Config registry
        let! record = runner.RunTurn(firstId, CancellationToken.None)
        Assert.Equal(PostingResult.Posted 1L, record.Posting)
        Assert.Empty(uncaptured.Requests)
    }

    [<Fact>]
    let ``notification failure preserves committed post and generation audit`` () = task {
        let fixture = standard (say "Committed answer") (say "Unused")
        let registry = AdapterRegistry [firstConnection, fixture.First :> IProviderAdapter]
        let events = { new IMessageEvents with member _.Publish _ = failwith "Synthetic notification failure" }
        let runner = ConversationOrchestrator(fixture.Config, fixture.Store,
                        ApiGateway(fixture.Store, registry, fixture.Tracker), fixture.Tracker,
                        events, fixture.Clock, fixture.Ids)
        let! record = runner.RunTurn(firstId, CancellationToken.None)
        Assert.Equal(PostingResult.Posted 1L, record.Posting)
        Assert.Equal(NotificationResult.Failed, record.Notification)
        Assert.Equal(record, Assert.Single fixture.Tracker.Records)
        let snapshot = fixture.Store.Snapshot fixture.Config.SessionId
        Assert.Equal(1, snapshot.AiPosts)
        Assert.Equal(0, snapshot.ActiveGenerations)
    }

    [<Fact>]
    let ``unregistered connection consumes no attempt`` () = task {
        let fixture = standard (say "Unused") (say "Unused")
        let runner = reconfigure fixture fixture.Config (AdapterRegistry [])
        let! record = runner.RunTurn(firstId, CancellationToken.None)
        Assert.Equal(PostingResult.Failed, record.Posting)
        Assert.Equal(0, record.AttemptCount)
        Assert.Equal(0, (fixture.Store.Snapshot fixture.Config.SessionId).Attempts)
        Assert.Empty(fixture.Tracker.Attempts)
        Assert.Empty(fixture.First.Requests)
    }
