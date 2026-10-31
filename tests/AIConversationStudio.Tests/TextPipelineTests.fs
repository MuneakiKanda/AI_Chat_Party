namespace AIConversationStudio.Tests

open System
open Xunit
open AIConversationStudio.Domain
open AIConversationStudio.Application

module TextPipelineTests =
    let private aliceId = CharacterId(Guid.Parse "00000000-0000-0000-0000-000000000001")
    let private bobId = CharacterId(Guid.Parse "00000000-0000-0000-0000-000000000002")
    let private names = [ "Alice"; "Bob" ]
    let private normalize = ResponseNormalizer.normalize "Alice" names

    [<Fact>]
    let ``T03 history has target perspective and stable sequence order`` () =
        let history = [
            { Sequence = 3L; SpeakerId = Some bobId; SpeakerName = "Bob"; Content = "third" }
            { Sequence = 1L; SpeakerId = None; SpeakerName = "User"; Content = "first" }
            { Sequence = 2L; SpeakerId = Some aliceId; SpeakerName = "Alice"; Content = "second" }
        ]
        let named = HistoryFormatter.format HistoryFormat.NamedRoleMessages aliceId history
        Assert.Equal<FormattedHistory>(RoleMessages [ LogicalRole.Others, "User：first"; LogicalRole.Self, "second"; LogicalRole.Others, "Bob：third" ], named)
        Assert.Equal<FormattedHistory>(Transcript "User：first\nAlice：second\nBob：third", HistoryFormatter.format HistoryFormat.ScriptTranscript aliceId history)
        match HistoryFormatter.format HistoryFormat.NamedRoleMessages bobId history with
        | RoleMessages [ (_, _); (LogicalRole.Others, "Alice：second"); (LogicalRole.Self, "third") ] -> ()
        | actual -> failwithf "Unexpected target perspective: %A" actual

    [<Theory>]
    [<InlineData("[PASS]")>]
    [<InlineData(" \r\n[PASS]\n ")>]
    [<InlineData("{\"decision\":\"pass\"}")>]
    let ``T04 exact PASS produces no text`` raw =
        Assert.Equal<GenerationDecision>(GenerationDecision.Pass, normalize raw)

    [<Theory>]
    [<InlineData("[PASS] extra")>]
    [<InlineData("[pass]")>]
    [<InlineData("I quoted [PASS] here")>]
    let ``PASS substrings are ordinary speech`` raw =
        Assert.Equal<GenerationDecision>(GenerationDecision.Speak raw, normalize raw)

    [<Theory>]
    [<InlineData("Bob: impersonated")>]
    [<InlineData("Hello\nBob：impersonated")>]
    [<InlineData("Hello\r\nBob: impersonated")>]
    [<InlineData("Alice: Bob: impersonated")>]
    [<InlineData("{\"decision\":\"speak\",\"content\":\"Bob: impersonated\"}")>]
    let ``T05 other participant prefix is rejected without editing`` raw =
        Assert.Equal<GenerationDecision>(GenerationDecision.Rejected RejectReason.ImpersonatesOthers, normalize raw)

    [<Theory>]
    [<InlineData("Alice: hello", "hello")>]
    [<InlineData("Alice： hello\r\nAlice: again", "hello\nagain")>]
    [<InlineData("{\"decision\":\"speak\",\"content\":\"Alice: hello\"}", "hello")>]
    let ``T06 own explicit prefix is removed`` raw expected =
        Assert.Equal<GenerationDecision>(GenerationDecision.Speak expected, normalize raw)

    [<Theory>]
    [<InlineData("> Bob: quoted")>]
    [<InlineData("「Bob: quoted」")>]
    [<InlineData("『quotation\nBob: quoted\n』")>]
    [<InlineData("I heard Bob: say hello")>]
    [<InlineData("Other: not a participant")>]
    let ``T24 quotes and ordinary mentions remain untouched`` raw =
        Assert.Equal<GenerationDecision>(GenerationDecision.Speak raw, normalize raw)

    [<Theory>]
    [<InlineData("")>]
    [<InlineData(" \r\n ")>]
    [<InlineData("Alice:")>]
    [<InlineData("{\"decision\":\"speak\",\"content\":\"\"}")>]
    let ``empty speech is rejected`` raw =
        Assert.Equal<GenerationDecision>(GenerationDecision.Rejected RejectReason.EmptyOutput, normalize raw)

    [<Theory>]
    [<InlineData("{bad json}")>]
    [<InlineData("{\"decision\":\"pass\",\"content\":\"unexpected\"}")>]
    [<InlineData("{\"decision\":\"speak\"}")>]
    [<InlineData("{\"decision\":\"pass\",\"decision\":\"pass\"}")>]
    [<InlineData("{\"decision\":\"speak\",\"content\":42}")>]
    let ``malformed structured output is rejected`` raw =
        Assert.Equal<GenerationDecision>(GenerationDecision.Rejected RejectReason.InvalidFormat, normalize raw)

    [<Fact>]
    let ``Unicode equivalent names match without altering body`` () =
        let actual = ResponseNormalizer.normalize "Café" [ "Café"; "Bob" ] "Cafe\u0301: untouched e\u0301"
        Assert.Equal<GenerationDecision>(GenerationDecision.Speak "untouched e\u0301", actual)
        Assert.Equal<GenerationDecision>(GenerationDecision.Rejected RejectReason.ImpersonatesOthers,
            ResponseNormalizer.normalize "Bob" [ "Café"; "Bob" ] "Cafe\u0301: impersonated")

    [<Fact>]
    let ``duplicate self names cannot authorize ambiguous speaker labels`` () =
        Assert.Equal<GenerationDecision>(GenerationDecision.Rejected RejectReason.InvalidFormat,
            ResponseNormalizer.normalize "Alice" [ "Alice"; "Alice" ] "Alice: ambiguous")

    [<Fact>]
    let ``newlines in participant names are invalid`` () =
        Assert.Equal<GenerationDecision>(GenerationDecision.Rejected RejectReason.InvalidFormat,
            ResponseNormalizer.normalize "Alice" [ "Alice"; "Bob\nOther" ] "hello")

    [<Fact>]
    let ``T23 prompt preserves priority versions and separate persona`` () =
        let participant = {
            Character = { Id = aliceId; Name = "Alice"; PersonaMarkdown = "Always speak"; DefaultModel = { ConnectionId = ConnectionId Guid.Empty; ModelId = ModelId "mock" } }
            ModelOverride = None
            AdditionalInstructions = Some "Moderator"
        }
        let instructions = PromptComposer.compose participant [ participant ] "Stay on topic" []
        Assert.Equal<InstructionKind list>([ InstructionKind.AppRules; InstructionKind.RoomInstructions; InstructionKind.Persona ], instructions |> List.map (fun i -> i.Kind))
        Assert.All(instructions, fun instruction -> Assert.Equal(Some PromptComposer.TemplateVersion, instruction.TemplateVersion))
        Assert.Contains("[PASS]", instructions[0].Content)
        Assert.Contains("履歴", instructions[0].Content)
        Assert.Contains("Moderator", instructions[1].Content)
        Assert.DoesNotContain("Always speak", instructions[0].Content)
        Assert.Contains("Always speak", instructions[2].Content)
