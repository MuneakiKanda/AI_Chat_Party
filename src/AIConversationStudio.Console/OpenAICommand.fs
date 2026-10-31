module AIConversationStudio.Console.OpenAICommand

open System
open System.Net.Http
open System.Threading
open AIConversationStudio.Domain
open AIConversationStudio.Application
open AIConversationStudio.Infrastructure

let usage = """OpenAI opt-in smoke test (Windows):
  --store-credential <reference>       Store a key using concealed interactive input.
  --openai --model <id> --credential-ref <reference> [--history named|script]
           [--timeout-seconds <positive number>] [--confirm-api-call]
Without --confirm-api-call, only endpoint/model are displayed; no request is sent.
No default timeout is chosen. Ctrl+C requests cancellation, not a provider-side guarantee."""

type private Options = { Model: string; Reference: string; Format: HistoryFormat; Timeout: TimeSpan option; Confirm: bool }

let private parse args =
    let rec loop options = function
        | [] when not (String.IsNullOrWhiteSpace options.Model) && not (String.IsNullOrWhiteSpace options.Reference) -> Ok options
        | "--model" :: value :: rest -> loop { options with Model = value } rest
        | "--credential-ref" :: value :: rest -> loop { options with Reference = value } rest
        | "--history" :: "named" :: rest -> loop { options with Format = HistoryFormat.NamedRoleMessages } rest
        | "--history" :: "script" :: rest -> loop { options with Format = HistoryFormat.ScriptTranscript } rest
        | "--confirm-api-call" :: rest -> loop { options with Confirm = true } rest
        | "--timeout-seconds" :: value :: rest ->
            match Double.TryParse(value, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
            | true, seconds when Double.IsFinite seconds && seconds > 0. && seconds <= float Int32.MaxValue / 1000. ->
                loop { options with Timeout = Some(TimeSpan.FromSeconds seconds) } rest
            | _ -> Error "Invalid timeout."
        | _ -> Error "Supply a model ID and credential reference; see --help."
    loop { Model = ""; Reference = ""; Format = HistoryFormat.NamedRoleMessages; Timeout = None; Confirm = false } args

let private storeCredential reference =
    if not (OperatingSystem.IsWindows()) then
        eprintfn "Windows Credential Manager is required; no plaintext fallback is available."
        2
    elif Console.IsInputRedirected then
        eprintfn "Use an interactive terminal for concealed credential entry."
        2
    else
        printf "API key (hidden): "
        let chars = ResizeArray<char>()
        let mutable reading = true
        let mutable aborted = false
        while reading do
            let key = Console.ReadKey(true)
            match key.Key with
            | ConsoleKey.Enter -> reading <- false
            | ConsoleKey.Escape -> reading <- false; aborted <- true
            | ConsoleKey.Backspace when chars.Count > 0 -> chars.RemoveAt(chars.Count - 1)
            | _ when not (Char.IsControl key.KeyChar) -> chars.Add key.KeyChar
            | _ -> ()
        printfn ""
        if aborted then chars.Clear(); 130
        else
            let buffer = chars.ToArray()
            chars.Clear()
            let result = (WindowsCredentialStore() :> ICredentialStore).Write(reference, String buffer)
            Array.Clear buffer
            match result with
            | Ok () -> printfn "Credential saved in Windows Credential Manager."; 0
            | Error _ -> eprintfn "Credential storage failed."; 2

let private run options = task {
    printfn "Endpoint: %O" OpenAIHttp.endpoint
    printfn "Model: %s" options.Model
    printfn "History: %A | API Attempt limit: 100 | one explicit generation" options.Format
    if not options.Confirm then
        printfn "Preview only. Add --confirm-api-call to authorize one potentially billable request."
        return 0
    else
        let clock = SystemClock() :> IClock
        let ids = RandomIds() :> IIdGenerator
        let session, room = SessionId(ids.NewId()), RoomId(ids.NewId())
        let connection = { Id = ConnectionId(ids.NewId()); CredentialRef = options.Reference; Endpoint = OpenAIHttp.endpoint }
        use http = OpenAIHttp.createClient()
        let adapter = OpenAIAdapter(connection, WindowsCredentialStore(), http, ?requestTimeout = options.Timeout) :> IProviderAdapter
        let participant = {
            Character = { Id = CharacterId(ids.NewId()); Name = "先生"; PersonaMarkdown = "簡潔に答える先生。"
                          DefaultModel = { ConnectionId = connection.Id; ModelId = ModelId options.Model } }
            ModelOverride = None; AdditionalInstructions = None
        }
        let store = InMemoryStore(clock) :> IConversationStore
        store.CreateSession(session, room, SessionLimits.defaults)
        store.AddHumanMessage(session, "あなた", "日本語で短く挨拶してください。") |> ignore
        let tracker = GenerationTracker()
        let gateway = ApiGateway(store, AdapterRegistry [connection.Id, adapter], tracker)
        let config = { SessionId = session; RoomId = room; Participants = [participant]
                       RoomInstructions = "接続確認のため、一度だけ返答してください。"; HistoryFormat = options.Format }
        let runner = ConversationOrchestrator(config, store, gateway, tracker, MessageEvents(), clock, ids)
        use stop = new EmergencyStopController(store, session)
        let handler = ConsoleCancelEventHandler(fun _ args -> args.Cancel <- true; stop.Stop())
        Console.CancelKeyPress.AddHandler handler
        try
            let! record = runner.RunTurn(participant.Character.Id, stop.Token)
            // Safe metadata includes error classification, never raw provider error messages.
            Console.Error.WriteLine(SafeRequestLogger.format record)
            match record.Posting with
            | PostingResult.Posted sequence ->
                let entry = (store.Snapshot session).Entries |> List.find (fun entry -> entry.Sequence = sequence)
                printfn "%s：%s" entry.SpeakerName entry.Content
            | _ -> printfn "No message posted: %A" record.Posting
            let snapshot = store.Snapshot session
            printfn "Attempts: %d / 100" snapshot.Attempts
            if snapshot.State = SessionRunState.Running then store.Stop(session, StopReason.UserStopped)
            return if stop.Token.IsCancellationRequested then 130 elif record.Posting = PostingResult.Failed then 1 else 0
        finally Console.CancelKeyPress.RemoveHandler handler
}

let execute args =
    try
        match args with
        | ["--store-credential"; reference] -> storeCredential reference
        | "--openai" :: rest ->
            match parse rest with
            | Ok options -> (run options).GetAwaiter().GetResult()
            | Error message -> eprintfn "%s" message; 2
        | _ -> eprintfn "%s" usage; 2
    with _ ->
        eprintfn "OpenAI command failed; details suppressed to protect credentials."
        1
