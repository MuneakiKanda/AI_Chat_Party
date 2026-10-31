namespace AIConversationStudio.Application

open System
open System.Text
open System.Text.Json
open AIConversationStudio.Domain

module ResponseNormalizer =
    let private canonicalName (name: string) = name.Normalize(NormalizationForm.FormC)

    let private invalidName (name: string) =
        String.IsNullOrWhiteSpace name || name.IndexOfAny([| '\r'; '\n' |]) >= 0

    let private normalizeText (selfName: string) (participantNames: string list) (raw: string) =
        let self = canonicalName selfName
        let names = participantNames |> List.map canonicalName
        let ambiguousSelf = names |> List.filter ((=) self) |> List.length |> fun count -> count > 1
        let others = names |> List.filter ((<>) self) |> Set.ofList
        let mutable rejection = None
        let mutable quoteDepth = 0
        let lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
        let normalized =
            lines
            |> Array.map (fun line ->
                let quotedLine = line.TrimStart().StartsWith(">", StringComparison.Ordinal)
                let mutable result = line
                if not quotedLine && quoteDepth = 0 then
                    let colon = line.IndexOfAny([| ':'; '：' |])
                    if colon >= 0 then
                        let prefix = canonicalName (line.Substring(0, colon))
                        if others.Contains prefix then
                            rejection <- Some RejectReason.ImpersonatesOthers
                        elif prefix = self then
                            if ambiguousSelf then rejection <- Some RejectReason.InvalidFormat
                            else
                                result <- line.Substring(colon + 1).TrimStart()
                                // Removing our own label must not reveal a forged participant label.
                                let innerColon = result.IndexOfAny([| ':'; '：' |])
                                if innerColon >= 0 && others.Contains(canonicalName (result.Substring(0, innerColon))) then
                                    rejection <- Some RejectReason.ImpersonatesOthers
                // Japanese quotations can span multiple lines. Quoted speaker names are data.
                if not quotedLine then
                    for ch in line do
                        match ch with
                        | '「' | '『' -> quoteDepth <- quoteDepth + 1
                        | '」' | '』' -> quoteDepth <- max 0 (quoteDepth - 1)
                        | _ -> ()
                result)
            |> String.concat "\n"
            |> fun value -> value.Trim()
        match rejection with
        | Some reason -> GenerationDecision.Rejected reason
        | None when String.IsNullOrWhiteSpace normalized -> GenerationDecision.Rejected RejectReason.EmptyOutput
        | None -> GenerationDecision.Speak normalized

    /// Structured output uses exactly {decision:"pass"} or {decision:"speak",content:string}.
    /// Plain output recognizes only the complete [PASS] marker (surrounding whitespace allowed).
    let normalize (selfName: string) (participantNames: string list) (raw: string) : GenerationDecision =
        if invalidName selfName || List.exists invalidName participantNames then
            GenerationDecision.Rejected RejectReason.InvalidFormat
        elif String.IsNullOrWhiteSpace raw then
            GenerationDecision.Rejected RejectReason.EmptyOutput
        else
            let text = raw.Trim()
            if text = "[PASS]" then GenerationDecision.Pass
            elif text.StartsWith("{", StringComparison.Ordinal) then
                try
                    use json = JsonDocument.Parse text
                    let root = json.RootElement
                    if root.ValueKind <> JsonValueKind.Object then
                        GenerationDecision.Rejected RejectReason.InvalidFormat
                    else
                        let properties = root.EnumerateObject() |> Seq.toList
                        let uniqueNames = properties |> List.map (fun p -> p.Name) |> Set.ofList
                        let tryString name =
                            properties
                            |> List.tryFind (fun p -> p.Name = name)
                            |> Option.bind (fun p -> if p.Value.ValueKind = JsonValueKind.String then Some(p.Value.GetString()) else None)
                        if uniqueNames.Count <> properties.Length then
                            GenerationDecision.Rejected RejectReason.InvalidFormat
                        else
                            match tryString "decision", tryString "content", properties.Length with
                            | Some "pass", None, 1 -> GenerationDecision.Pass
                            | Some "speak", Some content, 2 -> normalizeText selfName participantNames content
                            | _ -> GenerationDecision.Rejected RejectReason.InvalidFormat
                with :? JsonException -> GenerationDecision.Rejected RejectReason.InvalidFormat
            else normalizeText selfName participantNames text
