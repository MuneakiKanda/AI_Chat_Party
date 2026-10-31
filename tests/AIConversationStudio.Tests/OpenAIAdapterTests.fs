namespace AIConversationStudio.Tests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open AIConversationStudio.Domain
open AIConversationStudio.Application
open AIConversationStudio.Infrastructure

module OpenAIAdapterTests =
    let private syntheticKey = "synthetic-only-secret-DO-NOT-LOG-6721"
    type private StubHandler(send: HttpRequestMessage -> CancellationToken -> Task<HttpResponseMessage>) =
        inherit HttpMessageHandler()
        override _.SendAsync(request, ct) = send request ct
    type private FailingContent(error: exn) =
        inherit HttpContent()
        override _.TryComputeLength(length: byref<int64>) = length <- 0L; false
        override _.SerializeToStreamAsync(_: Stream, _: TransportContext) = Task.FromException error

    let private credentials result = {
        new ICredentialStore with
            member _.Read(_, _) = Task.FromResult result
            member _.Write(_, _) = failwith "Not used."
    }
    let private connection = { Id = ConnectionId(Guid.Parse "00000000-0000-0000-0000-000000000001")
                               CredentialRef = "test-reference"; Endpoint = OpenAIHttp.endpoint }
    let private request = {
        GenerationId = GenerationId(Guid.Parse "00000000-0000-0000-0000-000000000002")
        ModelId = ModelId "configured-model"
        Instructions = [
            { Kind = InstructionKind.Persona; Content = "synthetic-persona"; TemplateVersion = Some "test" }
            { Kind = InstructionKind.RoomInstructions; Content = "synthetic-room"; TemplateVersion = Some "test" }
            { Kind = InstructionKind.AppRules; Content = "synthetic-app"; TemplateVersion = Some "test" }
        ]
        History = RoleMessages [LogicalRole.Others, "Human：topic"; LogicalRole.Self, "own reply"; LogicalRole.Others, "Other：reply"]
        Tools = []; Parameters = Map.empty
    }
    let private response status body =
        new HttpResponseMessage(enum<HttpStatusCode> status, Content = new StringContent(body, Encoding.UTF8, "application/json"))
    let private success = """{"choices":[{"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":11,"completion_tokens":7}}"""
    let private client handler = new HttpClient(new StubHandler(handler), true, Timeout = Timeout.InfiniteTimeSpan)
    let private adapter http = OpenAIAdapter(connection, credentials (Ok syntheticKey), http) :> IProviderAdapter
    let private sent result =
        match result with Ok(Text(text, usage)) -> text, usage | _ -> failwithf "Unexpected result: %A" result

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``wire payload preserves instruction layers and own versus other roles`` transcript = task {
        let mutable calls = 0
        use http = client (fun message _ -> task {
            calls <- calls + 1
            Assert.Equal(OpenAIHttp.endpoint, message.RequestUri)
            Assert.Equal(HttpMethod.Post, message.Method)
            Assert.Equal("Bearer", message.Headers.Authorization.Scheme)
            Assert.Equal(syntheticKey, message.Headers.Authorization.Parameter)
            let! body = message.Content.ReadAsStringAsync()
            Assert.DoesNotContain(syntheticKey, body)
            use json = JsonDocument.Parse body
            let root = json.RootElement
            Assert.Equal("configured-model", root.GetProperty("model").GetString())
            Assert.False(root.GetProperty("stream").GetBoolean())
            let messages = root.GetProperty("messages").EnumerateArray() |> Seq.toArray
            Assert.Equal("system", messages[0].GetProperty("role").GetString())
            Assert.Equal("synthetic-app", messages[0].GetProperty("content").GetString())
            Assert.Equal("developer", messages[1].GetProperty("role").GetString())
            let developer = messages[1].GetProperty("content").GetString()
            Assert.True(developer.IndexOf("synthetic-room", StringComparison.Ordinal) < developer.IndexOf("synthetic-persona", StringComparison.Ordinal))
            Assert.DoesNotContain("Human", developer)
            let history = messages[2..]
            let roles = history |> Array.map (fun m -> m.GetProperty("role").GetString())
            if transcript then
                Assert.Equal<string[]>([|"user"|], roles)
                Assert.Contains("Other：reply", history[0].GetProperty("content").GetString())
            else
                Assert.Equal<string[]>([|"user"; "assistant"; "user"|], roles)
                Assert.Equal("Other：reply", history[2].GetProperty("content").GetString())
            return response 200 success
        })
        let input = if transcript then { request with History = Transcript "Human：topic\nSelf：own reply\nOther：reply" } else request
        let! result = (adapter http).Send(input, CancellationToken.None)
        let text, usage = sent result
        Assert.Equal("hello", text)
        Assert.Equal(Some 11L, usage.InputTokens)
        Assert.Equal(Some 7L, usage.OutputTokens)
        Assert.Equal(1, calls)
    }

    [<Theory>]
    [<InlineData(401, "invalid_api_key", "auth")>]
    [<InlineData(403, "permission_denied", "auth")>]
    [<InlineData(429, "rate_limit_exceeded", "rate")>]
    [<InlineData(429, "insufficient_quota", "quota")>]
    [<InlineData(404, "model_not_found", "model")>]
    [<InlineData(404, "unknown", "transport")>]
    [<InlineData(500, "server_error", "transport")>]
    [<InlineData(302, "redirect", "transport")>]
    let ``http errors are sanitized classified and never retried`` status code expected = task {
        let mutable calls = 0
        use http = client (fun _ _ ->
            calls <- calls + 1
            let body = JsonSerializer.Serialize {| error = {| code = code; message = syntheticKey |} |}
            let result = response status body
            result.Headers.RetryAfter <- Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds 9.)
            Task.FromResult result)
        let! result = (adapter http).Send(request, CancellationToken.None)
        let error = match result with Error error -> error | _ -> failwith "Expected error"
        match expected, error with
        | "auth", ChatError.AuthenticationFailed
        | "quota", ChatError.QuotaExceeded _
        | "model", ChatError.ModelNotAvailable _
        | "transport", ChatError.TransportFailed -> ()
        | "rate", ChatError.RateLimited delay -> Assert.Equal(Some(TimeSpan.FromSeconds 9.), delay)
        | _ -> failwithf "Unexpected classification: %A" error
        Assert.DoesNotContain(syntheticKey, sprintf "%A" error)
        Assert.Equal(1, calls)
    }

    [<Theory>]
    [<InlineData("not json")>]
    [<InlineData("{}")>]
    [<InlineData("{\"choices\":[]}")>]
    [<InlineData("{\"choices\":[{\"message\":{\"content\":null},\"finish_reason\":\"stop\"}]}")>]
    [<InlineData("{\"choices\":[{\"message\":{\"content\":\"partial\"},\"finish_reason\":\"length\"}]}")>]
    let ``malformed or incomplete response is never posted as final text`` body = task {
        use http = client (fun _ _ -> Task.FromResult(response 200 body))
        let! result = (adapter http).Send(request, CancellationToken.None)
        Assert.Equal(Error ChatError.InvalidResponse, result)
    }

    [<Theory>]
    [<InlineData(0)>]
    [<InlineData(1)>]
    [<InlineData(2)>]
    [<InlineData(3)>]
    let ``transport exceptions never expose credentials or invent send certainty`` mode = task {
        let error: exn =
            match mode with
            | 0 -> new HttpRequestException(HttpRequestError.NameResolutionError, syntheticKey, null)
            | 1 -> new HttpRequestException(HttpRequestError.ConnectionError, syntheticKey, null)
            | 2 -> new OperationCanceledException(syntheticKey)
            | _ -> new InvalidOperationException(syntheticKey)
        use http = client (fun _ _ -> Task.FromException<HttpResponseMessage> error)
        let! result = (adapter http).Send(request, CancellationToken.None)
        let expected =
            match mode with
            | 0 -> ChatError.ConnectionFailedBeforeSend "Name resolution failed."
            | 1 -> ChatError.TransportFailed
            | 2 -> ChatError.TimeoutSendStateUnknown
            | _ -> ChatError.Unexpected "OpenAI request failed."
        Assert.Equal(Error expected, result)
        Assert.DoesNotContain(syntheticKey, sprintf "%A" result)
    }

    [<Fact>]
    let ``timeout reading body after response headers is after send`` () = task {
        use http = client (fun _ _ ->
            let result = new HttpResponseMessage(HttpStatusCode.OK)
            result.Content <- new FailingContent(new OperationCanceledException(syntheticKey))
            Task.FromResult result)
        let! result = (adapter http).Send(request, CancellationToken.None)
        Assert.Equal(Error ChatError.TimedOutAfterSend, result)
    }

    [<Fact>]
    let ``caller cancellation is distinguished from transport timeout`` () = task {
        use cancellation = new CancellationTokenSource()
        use http = client (fun _ _ ->
            cancellation.Cancel()
            Task.FromException<HttpResponseMessage>(new OperationCanceledException(syntheticKey)))
        let! result = (adapter http).Send(request, cancellation.Token)
        Assert.Equal(Error ChatError.Cancelled, result)
    }

    [<Fact>]
    let ``credential failures are safe and never send HTTP`` () = task {
        let mutable calls = 0
        use http = client (fun _ _ -> calls <- calls + 1; Task.FromResult(response 200 success))
        let failing = { new ICredentialStore with
                        member _.Read(_, _) = Task.FromException<Result<string, CredentialError>>(new Exception(syntheticKey))
                        member _.Write(_, _) = Error CredentialError.AccessFailed }
        let! result = (OpenAIAdapter(connection, failing, http) :> IProviderAdapter).Send(request, CancellationToken.None)
        Assert.DoesNotContain(syntheticKey, sprintf "%A" result)
        Assert.True(Result.isError result)
        let! missing = (OpenAIAdapter(connection, credentials (Error CredentialError.NotFound), http) :> IProviderAdapter).Send(request, CancellationToken.None)
        Assert.Equal(Error ChatError.CredentialUnavailable, missing)
        Assert.Equal(0, calls)
    }

    [<Fact>]
    let ``unsupported native credential platform fails closed`` () = task {
        if not (OperatingSystem.IsWindows()) then
            let store = WindowsCredentialStore() :> ICredentialStore
            let! result = store.Read("synthetic", CancellationToken.None)
            Assert.Equal(Error CredentialError.UnsupportedPlatform, result)
            Assert.Equal(Error CredentialError.UnsupportedPlatform, store.Write("synthetic", syntheticKey))
    }

    [<Theory>]
    [<InlineData("http://api.openai.com/v1/chat/completions")>]
    [<InlineData("https://other.example/v1/chat/completions")>]
    [<InlineData("https://api.openai.com/v1/chat/completions?key=secret")>]
    let ``invalid destinations are rejected without echoing connection data`` endpoint =
        use http = client (fun _ _ -> failwith "Must not send")
        let error = Assert.Throws<ArgumentException>(fun () -> OpenAIAdapter({ connection with Endpoint = Uri endpoint }, credentials (Ok syntheticKey), http) |> ignore)
        Assert.DoesNotContain("secret", error.Message)

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``emergency stop prevents new attempts and discards delayed HTTP response`` passCancellation = task {
        let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let release = TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously)
        let mutable notified = false
        use http = client (fun _ token ->
            token.Register(fun () -> notified <- true) |> ignore
            entered.SetResult()
            release.Task)
        let clock = { new IClock with member _.UtcNow = DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero) }
        let store = InMemoryStore(clock) :> IConversationStore
        let session, room = SessionId(Guid.NewGuid()), RoomId(Guid.NewGuid())
        store.CreateSession(session, room, SessionLimits.defaults)
        let tracker = GenerationTracker()
        let events = MessageEvents()
        let character = { Character = { Id = CharacterId(Guid.NewGuid()); Name = "Synthetic"; PersonaMarkdown = "persona"
                                        DefaultModel = { ConnectionId = connection.Id; ModelId = request.ModelId } }
                          ModelOverride = None; AdditionalInstructions = None }
        let config = { SessionId = session; RoomId = room; Participants = [character]
                       RoomInstructions = "test"; HistoryFormat = HistoryFormat.NamedRoleMessages }
        let ids = { new IIdGenerator with member _.NewId() = Guid.NewGuid() }
        let runner = ConversationOrchestrator(config, store, ApiGateway(store, AdapterRegistry [connection.Id, adapter http], tracker), tracker, events, clock, ids)
        use stop = new EmergencyStopController(store, session)
        let pending = runner.RunTurn(character.Character.Id, if passCancellation then stop.Token else CancellationToken.None)
        do! entered.Task
        stop.Stop()
        Assert.Equal(passCancellation, notified)
        Assert.Equal(AttemptReservation.NotRunning StopReason.EmergencyStop, store.TryReserve session)
        let! blocked = runner.RunTurn(character.Character.Id, stop.Token)
        Assert.Equal(0, blocked.AttemptCount)
        release.SetResult(response 200 success)
        let! record = pending
        Assert.Empty((store.Snapshot session).Entries)
        Assert.Empty(events.Messages)
        Assert.Equal(1, (store.Snapshot session).Attempts)
        Assert.Equal(0, (store.Snapshot session).ActiveGenerations)
        Assert.Equal(SessionRunState.Stopped StopReason.EmergencyStop, (store.Snapshot session).State)
        Assert.Equal(2, tracker.Records.Length)
        if not passCancellation then
            Assert.Equal(CommunicationResult.Completed, record.Communication)
            Assert.Equal(PostingResult.Discarded PostRejection.EpochChanged, record.Posting)
        else Assert.Equal(PostingResult.Cancelled, record.Posting)
    }

    [<Theory>]
    [<InlineData(0)>]
    [<InlineData(1)>]
    let ``adapter errors and diagnostics cannot retain synthetic key or provider error text`` mode = task {
        use http = client (fun _ _ ->
            if mode = 0 then Task.FromResult(response 401 (JsonSerializer.Serialize {| error = {| message = syntheticKey; code = "invalid_api_key" |} |}))
            else Task.FromException<HttpResponseMessage>(new HttpRequestException(syntheticKey)))
        let clock = { new IClock with member _.UtcNow = DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero) }
        let store = InMemoryStore(clock) :> IConversationStore
        let session, room = SessionId(Guid.NewGuid()), RoomId(Guid.NewGuid())
        store.CreateSession(session, room, SessionLimits.defaults)
        let tracker = GenerationTracker()
        let participant = { Character = { Id = CharacterId(Guid.NewGuid()); Name = "Synthetic"
                                          PersonaMarkdown = "synthetic-private-persona"
                                          DefaultModel = { ConnectionId = connection.Id; ModelId = request.ModelId } }
                            ModelOverride = None; AdditionalInstructions = None }
        let config = { SessionId = session; RoomId = room; Participants = [participant]
                       RoomInstructions = "test"; HistoryFormat = HistoryFormat.NamedRoleMessages }
        let ids = { new IIdGenerator with member _.NewId() = Guid.NewGuid() }
        store.AddHumanMessage(session, "Human", "synthetic-private-conversation") |> ignore
        let runner = ConversationOrchestrator(config, store, ApiGateway(store, AdapterRegistry [connection.Id, adapter http], tracker), tracker, MessageEvents(), clock, ids)
        let! record = runner.RunTurn(participant.Character.Id, CancellationToken.None)
        Assert.Equal(PostingResult.Failed, record.Posting)
        let log = SafeRequestLogger.format record
        use _json = JsonDocument.Parse log
        for text in [log; sprintf "%A" record; sprintf "%A" tracker.Attempts] do
            Assert.DoesNotContain(syntheticKey, text)
            Assert.DoesNotContain("synthetic-private-persona", text)
            Assert.DoesNotContain("synthetic-private-conversation", text)
        Assert.Equal(1, record.AttemptCount)
        Assert.Equal(0, record.RetryCount)
    }

    [<Fact>]
    let ``gateway does not label uncancelled OperationCanceledException as user cancellation`` () = task {
        let clock = { new IClock with member _.UtcNow = DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero) }
        let store = InMemoryStore(clock) :> IConversationStore
        let session = SessionId(Guid.NewGuid())
        store.CreateSession(session, RoomId(Guid.NewGuid()), SessionLimits.defaults)
        let tracker = GenerationTracker()
        let provider = MockAdapter(fun _ _ -> Task.FromException<Result<ChatResponse, ChatError>>(new OperationCanceledException(syntheticKey))) :> IProviderAdapter
        let gateway = ApiGateway(store, AdapterRegistry [connection.Id, provider], tracker)
        let! result = gateway.Send(session, connection.Id, request, CancellationToken.None)
        Assert.Equal(CommunicationResult.Failed ChatError.TimeoutSendStateUnknown, result.Communication)
        Assert.DoesNotContain(syntheticKey, sprintf "%A" tracker.Attempts)
    }

    [<Fact>]
    let ``unknown token usage stays missing`` () = task {
        use http = client (fun _ _ -> Task.FromResult(response 200 """{"choices":[{"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}]}"""))
        let! result = (adapter http).Send(request, CancellationToken.None)
        let _, usage = sent result
        Assert.Equal(Usage.unknown, usage)
    }
