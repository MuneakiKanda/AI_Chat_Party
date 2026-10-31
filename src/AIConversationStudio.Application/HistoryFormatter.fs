namespace AIConversationStudio.Application

open AIConversationStudio.Domain

/// History is data; only the target character's own messages use the Self role.
module HistoryFormatter =
    let format (format: HistoryFormat) (target: CharacterId) (history: ConversationEntry list) : FormattedHistory =
        let entries = history |> List.sortBy (fun entry -> entry.Sequence)
        let named (entry: ConversationEntry) = $"{entry.SpeakerName}：{entry.Content}"
        match format with
        | HistoryFormat.NamedRoleMessages ->
            entries
            |> List.map (fun entry ->
                if entry.SpeakerId = Some target then LogicalRole.Self, entry.Content
                else LogicalRole.Others, named entry)
            |> RoleMessages
        | HistoryFormat.ScriptTranscript ->
            entries |> List.map named |> String.concat "\n" |> Transcript
