namespace AIConversationStudio.Infrastructure

open System
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks

[<RequireQualifiedAccess>]
type CredentialError = NotFound | UnsupportedPlatform | AccessFailed

/// Secrets stay in Infrastructure. Configuration contains only a lookup reference.
type ICredentialStore =
    abstract Read: string * CancellationToken -> Task<Result<string, CredentialError>>
    abstract Write: string * string -> Result<unit, CredentialError>

module private WindowsCredentials =
    [<Struct; StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)>]
    type Credential =
        val mutable Flags: uint32
        val mutable Type: uint32
        val mutable TargetName: string
        val mutable Comment: string
        val mutable LastWritten: System.Runtime.InteropServices.ComTypes.FILETIME
        val mutable CredentialBlobSize: uint32
        val mutable CredentialBlob: nativeint
        val mutable Persist: uint32
        val mutable AttributeCount: uint32
        val mutable Attributes: nativeint
        val mutable TargetAlias: string
        val mutable UserName: string

    [<DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)>]
    extern bool read(string target, uint32 kind, uint32 flags, nativeint& credential)
    [<DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)>]
    extern bool write(Credential& credential, uint32 flags)
    [<DllImport("advapi32.dll", EntryPoint = "CredFree")>]
    extern void free(nativeint credential)

/// Windows Generic Credentials, current user, persisted on this machine only.
/// Unsupported OSes fail closed; there is no environment/file fallback.
type WindowsCredentialStore() =
    let target reference = "AIConversationStudio/" + reference
    interface ICredentialStore with
        member _.Read(reference, ct) = task {
            ct.ThrowIfCancellationRequested()
            if not (OperatingSystem.IsWindows()) then return Error CredentialError.UnsupportedPlatform
            elif String.IsNullOrWhiteSpace reference then return Error CredentialError.NotFound
            else
                try
                    let mutable pointer = nativeint 0
                    if not (WindowsCredentials.read(target reference, 1u, 0u, &pointer)) then
                        return Error(if Marshal.GetLastPInvokeError() = 1168 then CredentialError.NotFound else CredentialError.AccessFailed)
                    else
                        try
                            let credential = Marshal.PtrToStructure<WindowsCredentials.Credential> pointer
                            if credential.CredentialBlobSize = 0u || credential.CredentialBlobSize % 2u <> 0u then
                                return Error CredentialError.AccessFailed
                            else
                                return Ok(Marshal.PtrToStringUni(credential.CredentialBlob, int credential.CredentialBlobSize / 2))
                        finally
                            WindowsCredentials.free pointer
                with _ -> return Error CredentialError.AccessFailed
        }
        member _.Write(reference, secret) =
            if not (OperatingSystem.IsWindows()) then Error CredentialError.UnsupportedPlatform
            elif String.IsNullOrWhiteSpace reference || String.IsNullOrWhiteSpace secret then Error CredentialError.AccessFailed
            else
                try
                    let blob = Marshal.StringToCoTaskMemUni secret
                    try
                        let mutable credential = WindowsCredentials.Credential()
                        credential.Type <- 1u
                        credential.TargetName <- target reference
                        credential.CredentialBlobSize <- uint32 (secret.Length * 2)
                        credential.CredentialBlob <- blob
                        credential.Persist <- 2u
                        credential.UserName <- "OpenAI"
                        if WindowsCredentials.write(&credential, 0u) then Ok () else Error CredentialError.AccessFailed
                    finally Marshal.ZeroFreeCoTaskMemUnicode blob
                with _ -> Error CredentialError.AccessFailed
