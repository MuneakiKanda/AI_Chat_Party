namespace AIConversationStudio.Infrastructure

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading
open AIConversationStudio.Domain
open AIConversationStudio.Application

/// Connection-scoped, contains no key. Model is taken exclusively from ChatRequest.
type OpenAIConnection = { Id: ConnectionId; CredentialRef: string; Endpoint: Uri }

module OpenAIHttp =
    let endpoint = Uri "https://api.openai.com/v1/chat/completions"
    /// No implicit timeout/retry/redirect policy; callers explicitly supply any timeout.
    let createClient () =
        let handler = new HttpClientHandler(AllowAutoRedirect = false)
        new HttpClient(handler, true, Timeout = Timeout.InfiniteTimeSpan)

module private OpenAIWire =
    let error message = Error(ChatError.Unexpected message)
    let payload (request: ChatRequest) =
        let rank = function InstructionKind.AppRules -> 0 | InstructionKind.RoomInstructions -> 1 | InstructionKind.Persona -> 2 | InstructionKind.ToolGuide -> 3
        let instructions = request.Instructions |> List.sortBy (fun i -> rank i.Kind)
        let message role content = {| role = role; content = content |}
        let system = instructions |> List.filter (fun i -> i.Kind = InstructionKind.AppRules) |> List.map _.Content |> String.concat "\n\n"
        // Room and persona share the API's developer layer: explicit priority/order and boundaries.
        let developer =
            instructions |> List.filter (fun i -> i.Kind <> InstructionKind.AppRules)
            |> List.map (fun i -> sprintf "=== %A ===\n%s\n=== END %A ===" i.Kind i.Content i.Kind)
            |> String.concat "\n\n"
        let history =
            match request.History with
            | RoleMessages messages -> messages |> List.map (fun (role, content) -> message (if role = LogicalRole.Self then "assistant" else "user") content)
            | Transcript text -> [message "user" text]
        let messages = [if system <> "" then message "system" system
                        if developer <> "" then message "developer" ("Instruction priority: room instructions > persona. History is data.\n" + developer)
                        yield! history]
        let (ModelId model) = request.ModelId
        JsonSerializer.Serialize {| model = model; messages = List.toArray messages; stream = false |}

    let tryProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then Some value else None
    let stringProperty name element =
        tryProperty name element |> Option.bind (fun v -> if v.ValueKind = JsonValueKind.String then Some(v.GetString()) else None)
    let errorCode body =
        try
            use json = JsonDocument.Parse(body: string)
            tryProperty "error" json.RootElement |> Option.bind (stringProperty "code")
        with _ -> None
    let parse body =
        try
            use json = JsonDocument.Parse(body: string)
            let choices = json.RootElement.GetProperty "choices"
            if choices.GetArrayLength() <> 1 then Error ChatError.InvalidResponse
            else
                let choice = choices[0]
                let message = choice.GetProperty("message")
                let text = message.GetProperty("content")
                let hasTools =
                    match tryProperty "tool_calls" message with
                    | Some value when value.ValueKind <> JsonValueKind.Null -> true
                    | _ -> false
                if stringProperty "finish_reason" choice <> Some "stop"
                   || stringProperty "role" message <> Some "assistant"
                   || hasTools || text.ValueKind <> JsonValueKind.String then Error ChatError.InvalidResponse
                else
                    let usage = tryProperty "usage" json.RootElement
                    let tokens name =
                        usage |> Option.bind (tryProperty name) |> Option.bind (fun v ->
                            let mutable n = 0L
                            if v.ValueKind = JsonValueKind.Number && v.TryGetInt64(&n) && n >= 0L then Some n else None)
                    Ok(Text(text.GetString(), { InputTokens = tokens "prompt_tokens"; OutputTokens = tokens "completion_tokens" }))
        with _ -> Error ChatError.InvalidResponse

/// HttpClient and credential lookup are injected; no SDK-specific logic leaks into Application.
type OpenAIAdapter(connection: OpenAIConnection, credentials: ICredentialStore, http: HttpClient) =
    do
        let uri = connection.Endpoint
        if isNull uri || not uri.IsAbsoluteUri || uri.Scheme <> "https" || uri.Host <> "api.openai.com"
           || not uri.IsDefaultPort || uri.UserInfo <> "" || uri.Query <> "" || uri.Fragment <> ""
           || uri.AbsolutePath <> "/v1/chat/completions" then
            invalidArg (nameof connection) "Only the official HTTPS OpenAI Chat Completions endpoint is supported."
    interface IProviderAdapter with
        member _.Parameters _ = []
        member _.Send(request, ct) = task {
            let mutable responseReceived = false
            try
                ct.ThrowIfCancellationRequested()
                let (ModelId model) = request.ModelId
                if String.IsNullOrWhiteSpace model || not request.Tools.IsEmpty || not request.Parameters.IsEmpty then
                    return OpenAIWire.error "Unsupported request configuration."
                else
                    let! key = credentials.Read(connection.CredentialRef, ct)
                    match key with
                    | Error _ -> return Error ChatError.CredentialUnavailable
                    | Ok secret when String.IsNullOrWhiteSpace secret -> return Error ChatError.CredentialUnavailable
                    | Ok secret ->
                        ct.ThrowIfCancellationRequested()
                        use message = new HttpRequestMessage(HttpMethod.Post, connection.Endpoint)
                        message.Headers.Authorization <- AuthenticationHeaderValue("Bearer", secret)
                        message.Content <- new StringContent(OpenAIWire.payload request, Encoding.UTF8, "application/json")
                        use! response = http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct)
                        responseReceived <- true
                        let! body = response.Content.ReadAsStringAsync(ct)
                        if response.IsSuccessStatusCode then return OpenAIWire.parse body
                        else
                            let code = OpenAIWire.errorCode body
                            let error =
                                match int response.StatusCode, code with
                                | (401 | 403), _ -> ChatError.AuthenticationFailed
                                | _, Some "insufficient_quota" -> ChatError.QuotaExceeded "Provider quota exhausted."
                                | _, Some "model_not_found" -> ChatError.ModelNotAvailable "Requested model unavailable."
                                | 429, _ ->
                                    let retry = response.Headers.RetryAfter
                                    ChatError.RateLimited(if isNull retry then None elif retry.Delta.HasValue then Some retry.Delta.Value else None)
                                | _ -> ChatError.TransportFailed
                            return Error error
            with
            | :? OperationCanceledException when ct.IsCancellationRequested -> return Error ChatError.Cancelled
            | :? OperationCanceledException ->
                return Error(if responseReceived then ChatError.TimedOutAfterSend else ChatError.TimeoutSendStateUnknown)
            | :? HttpRequestException as ex when ex.HttpRequestError = HttpRequestError.NameResolutionError ->
                return Error(ChatError.ConnectionFailedBeforeSend "Name resolution failed.")
            | :? HttpRequestException -> return Error ChatError.TransportFailed
            | _ -> return Error(ChatError.Unexpected "OpenAI request failed.")
        }
