module AIConversationStudio.Console.Program

open System
open System.Threading
open System.Threading.Tasks
open AIConversationStudio.Domain
open AIConversationStudio.Application
open AIConversationStudio.Infrastructure

type Options = { Theme: string; History: HistoryFormat; Scenario: string; Diagnostics: bool }

let usage = "AI Conversation Studio — Slice 1 Mock PoC\n  --theme <テーマ>  --history named|script\n  --scenario speak|pass|reject|timeout  --diagnostics\n外部APIを呼ばず、2キャラクターが順番に1回ずつ応答します。"

let parse args =
    let rec loop options = function
        | [] -> Ok options
        | "--theme" :: value :: rest when not (String.IsNullOrWhiteSpace value) -> loop { options with Theme = value } rest
        | "--history" :: "named" :: rest -> loop { options with History = HistoryFormat.NamedRoleMessages } rest
        | "--history" :: "script" :: rest -> loop { options with History = HistoryFormat.ScriptTranscript } rest
        | "--scenario" :: value :: rest when List.contains value ["speak"; "pass"; "reject"; "timeout"] -> loop { options with Scenario = value } rest
        | "--diagnostics" :: rest -> loop { options with Diagnostics = true } rest
        | _ -> Error "引数が不正です。--help で使い方を確認してください。"
    loop { Theme = "AIと一緒に学ぶには？"; History = HistoryFormat.NamedRoleMessages; Scenario = "speak"; Diagnostics = false } args

let run options = task {
    let clock = SystemClock() :> IClock
    let ids = RandomIds() :> IIdGenerator
    let sessionId, roomId = SessionId(ids.NewId()), RoomId(ids.NewId())
    let character name persona model =
        { Character = { Id = CharacterId(ids.NewId()); Name = name; PersonaMarkdown = persona
                        DefaultModel = { ConnectionId = ConnectionId(ids.NewId()); ModelId = ModelId model } }
          ModelOverride = None; AdditionalInstructions = None }
    let chapiko = character "ちゃぴ子先生" "親しみやすく、具体的な学び方を提案する先生。" "mock-chapiko"
    let opako = character "オパ子先生" "別の視点から考えを深める先生。" "mock-opako"
    let reply text =
        MockAdapter(fun _ ct -> task {
            ct.ThrowIfCancellationRequested()
            return Ok(Text(text, Usage.unknown))
        }) :> IProviderAdapter
    let second =
        match options.Scenario with
        | "pass" -> reply "[PASS]"
        | "reject" -> reply "ちゃぴ子先生：これは他の参加者を装った応答です。"
        | "timeout" -> MockAdapter(fun _ _ -> Task.FromResult(Error ChatError.TimedOutAfterSend)) :> IProviderAdapter
        | _ -> reply "オパ子先生：その考えに加えて、自分の言葉で説明してみると理解を確かめられますね。"
    let registry =
        AdapterRegistry [
            chapiko.Character.DefaultModel.ConnectionId, reply "ちゃぴ子先生：まずは小さな問いを決めて、一緒に試してみましょう。"
            opako.Character.DefaultModel.ConnectionId, second
        ] :> IAdapterRegistry
    let store = InMemoryStore(clock) :> IConversationStore
    store.CreateSession(sessionId, roomId, SessionLimits.defaults)
    let tracker = GenerationTracker()
    let events = MessageEvents()
    let gateway = ApiGateway(store, registry, tracker)
    let configuration = { SessionId = sessionId; RoomId = roomId; Participants = [chapiko; opako]
                          RoomInstructions = "テーマについて対話してください。"; HistoryFormat = options.History }
    let orchestrator = ConversationOrchestrator(configuration, store, gateway, tracker, events, clock, ids)
    use cancellation = new CancellationTokenSource()
    let handler = ConsoleCancelEventHandler(fun _ args ->
        args.Cancel <- true
        store.Stop(sessionId, StopReason.EmergencyStop)
        cancellation.Cancel())
    Console.CancelKeyPress.AddHandler handler
    try
        printfn "AI Conversation Studio | Mock / %A" options.History
        store.AddHumanMessage(sessionId, "あなた", options.Theme) |> ignore
        printfn "あなた：%s" options.Theme
        for participant in configuration.Participants do
            let! record = orchestrator.RunTurn(participant.Character.Id, cancellation.Token)
            match record.Posting with
            | PostingResult.Posted sequence ->
                let entry = (store.Snapshot sessionId).Entries |> List.find (fun e -> e.Sequence = sequence)
                // Confirmed conversation is UI output; diagnostics never contain its body.
                printfn "%s：%s" entry.SpeakerName entry.Content
            | PostingResult.Passed -> printfn "%s：発言を見送りました（PASS）。" participant.Character.Name
            | result -> printfn "%s：投稿なし（%A）。" participant.Character.Name result
            if options.Diagnostics then Console.Error.WriteLine(SafeRequestLogger.format record)
        let snapshot = store.Snapshot sessionId
        printfn "完了：API Attempt %d / 100、AI投稿 %d / 50" snapshot.Attempts snapshot.AiPosts
        if snapshot.State = SessionRunState.Running then store.Stop(sessionId, StopReason.UserStopped)
        return if cancellation.IsCancellationRequested then 130 else 0
    finally
        Console.CancelKeyPress.RemoveHandler handler
}

[<EntryPoint>]
let main args =
    if Array.contains "--help" args then
        printfn "%s" usage
        0
    else
        match parse (Array.toList args) with
        | Error error -> eprintfn "%s" error; 2
        | Ok options -> (run options).GetAwaiter().GetResult()
