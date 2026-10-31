namespace AIConversationStudio.Application

open AIConversationStudio.Domain

/// Connections, including connections to the same provider, have independent adapters.
type AdapterRegistry(adapters: (ConnectionId * IProviderAdapter) list) =
    let registry = Map.ofList adapters
    do
        if registry.Count <> adapters.Length then
            invalidArg (nameof adapters) "Duplicate connection IDs."
    interface IAdapterRegistry with
        member _.Resolve connectionId =
            match registry.TryFind connectionId with
            | Some adapter -> Ok adapter
            | None -> Error "No adapter registered for this connection."
