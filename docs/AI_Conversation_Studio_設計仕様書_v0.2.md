# AI Conversation Studio 設計仕様書（ドラフト v0.2）

- 作成日：2026-10-31
- 状態：構想・PoC設計段階（設計レビュー反映版）
- 改訂元：v0.1（2026-10-31）
- 改訂要旨：イベント駆動並列会話、候補選択、PASS、楽観的投稿、計測・停止制御を具体化
- 対象：F# × Avalonia UIによるデスクトップ用マルチAI仮想チャットアプリ

## 1. 目的と基本原則

複数のAIキャラクターが共通のチャットルームで会話するデスクトップアプリを構築する。ユーザーは会話を観察することも、途中参加することもできる。

**確定方針**

1. 特定のAIサービスに依存しない。Provider / Model / Characterを分離する。
2. 同じキャラクターは複数ルームに所属でき、複数ルームは同時実行できる。
3. ルーム一覧に未読件数バッジを表示する。
4. AIごとの「お休み」と復帰をサポートする。ルーム単位と全ルーム共通の休止を区別する。
5. 復帰時には休止中の会話履歴をコンテキストに取り込み、処理中の状態をUIに表示する。
6. 全画面からアプリを終了できる。緊急停止（実行停止）とアプリ終了（プロセス終了）は分離する。
7. 同じルーム内で他者に未投稿の下書きがあるかをAIが必要に応じて参照できるようにする。アプリから積極的な通知や待機指示はしない。
8. イベント駆動・並列発言をPoCの標準方式とし、PASSをAIの選択肢とする。ラウンドロビンは決定的テスト用に残す。
9. 1ルームにつき実行中セッションは最大1つ（PoC制約）。複数ルームの同時実行は許可する。
10. PoC初日からAPI使用量・候補選択・生成結果・停止理由を記録する。

**提案・未確定事項**：SQLite、Markdown人格設定、JSONアプリ設定、Avalonia.FuncUI/MVU、タスクトレイ常駐、会話要約、高度な発言者選択方式、金額ベースの料金上限などは実装候補であり、確定要件ではない。ターン数・API呼び出し回数による上限制御はPoC必須とする。

## 2. 用語・責務

| 概念 | 責務 |
|---|---|
| Provider | OpenAI / Anthropic / Google / xAI / ローカル推論等への接続設定 |
| Model | Provider内のモデル識別子と対応機能 |
| Character | 名前、人格、立ち絵、既定モデルなどを持つ再利用可能なAI人格 |
| Room | 参加キャラクターとルーム固有の設定を持つ会話空間 |
| RoomParticipant | CharacterとRoomの多対多関連。役割、ルーム内休止状態を保持 |
| ConversationSession | Room内の個別の会話実行単位 |
| Message | セッション内で確定・投稿された発言 |
| Draft | 生成開始後、まだ投稿されていない発言の状態。本文は他AIへ非公開 |
| CandidateScheduler | 発言候補の選択と開始遅延を決定 |
| ConversationOrchestrator | 会話進行、発言権限・投稿可否・停止条件を検証 |
| GenerationMetrics | API生成試行ごとの計測記録（PASSや破棄も含む） |
| RoomReadState | ユーザーのルーム別既読位置 |

```text
Provider 1 ── * Model
                   ↑
                   │ default model reference
                Character 1 ── * RoomParticipant * ── 1 Room
                                                       │
                                                       └── * ConversationSession
                                                                  └── * Message
```

Characterの人格設定は全ルーム共通、RoomParticipantの追加指示・役割はルーム固有とする。会話コンテキストと下書き状態はセッション単位で隔離し、他ルームへ自動共有しない。

## 3. 画面・機能要件

### 3.1 共通レイアウト

```text
┌──────────────────────────────────────────────────────────┐
│ AI Conversation Studio                         [緊急停止][終了] │
├───────────────────┬──────────────────────────────────────┤
│ ルーム一覧        │ 選択ルーム / 管理画面                  │
│ ＋ ルーム作成     │                                      │
│ 親睦会       [12] │ 発言タイムライン                      │
│ 技術討論会    [5] │ AI状態：参加中 / 下書き中 / お休み中   │
│ 哲学研究会        │                                      │
│                   │ 入力欄・送信・会話操作               │
│ AI管理            │                                      │
│ 設定              │                                      │
├───────────────────┴──────────────────────────────────────┤
│ 実行中ルーム数 / API状態 / エラー表示                     │
└──────────────────────────────────────────────────────────┘
```

- **ルーム管理**：作成・編集・削除、参加キャラクター選択、会話モード、テーマ、実行制限。
- **AI管理**：Provider接続、Model選択、Character作成・編集・削除、人格設定Markdown、立ち絵、全ルーム共通の休止。
- **設定**：API認証情報、保存先、表示、実行数・料金関連の制限（詳細は未確定）。APIキーを平文JSONへ保存しない。
- **チャット**：観察型・参加型、ユーザーの途中参加、セッション履歴、開始・一時停止・再開・停止。
- **ルーム一覧**：未読バッジ必須。最新発言プレビュー・実行状態は推奨。
- **共通操作**：どの画面からも緊急停止およびアプリ終了が可能。

### 3.2 既読・未読

- 単一ローカルユーザーを前提に、ルーム単位で最後に閲覧したメッセージ位置を保持する（UserId列は設けない）。
- ユーザー自身の発言や一時的な処理インジケーターは未読に含めない。
- 過去ログ閲覧中は新着を自動既読にせず、最新位置が表示された時点で既読を進める。
- 複数セッションがある場合も、ルーム内で単調増加するメッセージ連番を使う設計を推奨する。

## 4. 会話・実行制御

### 4.1 並行実行

各ルームは独立した実行状態・コンテキストを持ち、複数ルームの同時実行を許可する。共通のAPIゲートウェイでProvider別のレート制限、同時接続数、キャンセルを管理する。発言者選択は§4.4のイベント駆動方式をPoC標準とする。AIの状況判断を過剰に代行しない。ルーム内の生成は最大2件並行、投稿はルーム単位の逐次コミットで確定する。

### 4.2 お休みと復帰

- **ルーム内休止**：該当RoomParticipantだけを新規発言対象から外す。
- **グローバル休止**：該当Characterをすべてのルームで新規発言対象から外す。
- **お休み**：進行中の生成は原則完了を待つが、休止確定後に完成した下書きは投稿しない（破棄・計測）。新規生成も抑止する。休止位置をルームごとのメッセージ連番として記録する。
- **即時停止**：進行中のAPI処理にキャンセルを要求し、遅れて返った結果は投稿しない。
- **復帰**：休止中の履歴を取得し、必要なコンテキストを構築してから発言候補に戻す。グローバル休止解除では全ルーム同時に要約せず、各ルームで次の発言機会に遅延キャッチアップする。
- **休止の起点**：ルーム内休止はRoomParticipantにRestedAtSequenceを保持する。グローバル休止は休止開始時点の各ルーム連番を別テーブルでスナップショットし、ルーム内休止との重複時にはより古い未読開始点を採用する。
- **投稿直前の再判定**：生成開始時に加え、投稿コミット直前にもルーム休止・グローバル休止・緊急停止・セッション状態を検証する。
- 復帰処理中は「○○が会話履歴を読み込んでいます…」等を表示する。DB読み出しとAI要約・コンテキスト準備は区別する。
- 読み込み中表示は一時的な状態であり、履歴メッセージとして永続化しない。復帰イベント自体の保存は任意。
- コンテキスト長を超える場合の要約・直近N件方式は拡張候補。

### 4.3 緊急停止とアプリ終了

- **緊急停止**：全ルームの新規API発行を禁止し、実行中リクエストをキャンセル、遅延結果の投稿を拒否。再開はユーザーの明示操作が必要。
- **通常終了**：新規処理受付停止、キャンセル、状態保存、アプリ終了。
- **強制終了**：正常終了が進まない場合の最終手段。未保存データ損失の可能性を明示する。
- UIの終了ボタンは常時アクセス可能な共通シェルに配置する。OS側のウィンドウ閉じる操作とタスクトレイ常駐の関係は未確定。

### 4.4 発言候補選択・生成開始（PoC標準）

1. 投稿イベントをルームごとのキューへ投入する。生成中に発生した複数の投稿イベントは次ラウンド用に1つの起動契機へ集約する（**履歴本文は捨てない**）。
2. Schedulerが候補を最大 **K=2** 人選択する。休止中・復帰準備中・すでに生成中のキャラは除外。直前の話者は原則除外し、名前・別名による指名／言及を優先し、残りは発言間隔の長い順とする。候補が空になる場合は直前話者の再選択を許す。
3. 第1候補は即時、第2候補は **0〜3秒** の遅延後に生成開始する。遅延後に必ず最新履歴と休止状態を再取得する。遅延はキャンセル可能、テスト時は乱数シード固定可能とする。
4. 各AIは1回の生成で `Speak(content)` または `Pass` を返す。PASSは投稿イベントを発生させない。構造化出力を優先し、非対応時は厳密な `[PASS]` 規約をフォールバックとする。PASS判明時のストリーム打ち切りは可能な範囲で行うが、課金削減を保証しない。
5. Schedulerは生成機会のみを与え、AIはSpeak/Passを決め、Orchestratorは投稿権限・停止条件・競合ポリシーを検証する。
6. テスト・比較用にRoundRobinモードを残す。決定的テストではモデル応答と時計もモック化する。

**初期起動**：ユーザーがルームを開始した際は起動イベントを1件投入し、以後は投稿イベントで進行する。PASSだけで終わったラウンドは追加の自動起動を行わず、停止・待機とする。ユーザー投稿は通常の新着イベントとして扱う。

### 4.5 楽観的投稿と重複観測

- 下書きは生成開始時の `BasedOnSequence` を保持する。新着があっても原則投稿を許可し、単純な新着件数だけでは即破棄しない。
- **PoCの緩い安全弁**：生成開始から60秒以上、または新着5件以上の場合は破棄する（再生成しない）。時間は生成開始から投稿判定まで。設定変更可能。
- 投稿時は休止・緊急停止・セッション停止・ターン上限・世代IDを再検証する。検証と連番採番・投稿保存は同一トランザクション／逐次コミット境界で行う。
- `BasedOnSequence` は参照履歴の末尾、`ReplyToMessageId` は明示された返信対象。前者から後者を推測してはならない。明示的な返信先がある場合のみUIに「↩ #12への発言」等を表示する。
- 同じBasedOnSequenceから投稿された件数と本文類似度をPoCで観測する。初期は文字列類似度でよく、意味的類似度は後続検討。

### 4.6 実行上限・停止理由（PoC必須）

| パラメータ | PoC初期値 | 意味 |
|---|---:|---|
| 1イベント候補数 | 2 | Schedulerが選ぶ最大人数 |
| 1ルーム同時生成数 | 2 | 同時にAPI生成中となる最大数 |
| 1ルーム実行中セッション | 1 | 別セッションの同時実行は不可 |
| 1セッション最大投稿数 | 50 | AI投稿を上限に数える。ユーザー投稿は除外 |
| 1セッション最大API呼び出し数 | 100 | PASS・失敗・破棄も含めて生成呼び出しを数える |
| 第2候補開始遅延 | 0〜3秒 | 待機後に履歴再取得 |
| 下書き最大経過 | 60秒 | 以上で投稿拒否 |
| 下書き開始後の許容新着 | 4件 | 5件以上で投稿拒否 |
| ユーザー無操作時停止 | 有効 | 時間閾値は設定項目・値は未決定 |

- API呼び出し枠は**発行前に原子的に予約**し、並行生成でも100回を超えない。予約を使わず終了後にカウントする実装は禁止する。
- 上限到達時は新規発行を禁止する。既に発行済みの生成は完了可能だが、最大投稿数と投稿権限を再検証する。API上限到達後の進行は停止状態とし、自動的に次ラウンドを開始しない。
- 停止理由（ユーザー操作／投稿上限／API呼び出し上限／無操作／緊急停止／エラー）をUIとログに表示する。無操作タイマーの起点・閾値は未決事項。
- Provider別レート制限と金額ベースの予算制御は別途検討する。上限は安全弁であり費用を保証するものではない。

## 5. 下書き検知・AIへの参照提供

### 5.1 最小仕様

- 対象は**現在のルーム内に存在する他者の未投稿下書き**。
- アプリがAPI生成開始を検知してDraft Registryに登録する。生成済みでも未投稿なら登録を維持する。
- 投稿・キャンセル・失敗・セッション終了で該当レコードを解消する。
- AIは説明書に記載された読み取り専用ツール `get_active_drafts` を任意のタイミングで呼び出す。
- AIへ自動通知しない。下書き中なら待つ等の行動指示もしない。
- 返却するのは下書きの存在・作成者・開始時刻等のみ。本文は公開しない。
- 自身の下書きは結果から除外する。
- ツール非対応モデルには原則として自動注入しない。ルームごとに明示的にオプトインした場合のみ、生成開始時の下書き状態スナップショットをプロンプトへ注入できる。

**重要な制約**：API内部の思考や未公開テキストを直接観測するのではなく、アプリが管理する生成・未投稿状態を検知する。参照結果はツール呼び出し時点のスナップショットであり、継続的なリアルタイム監視ではない。

### 5.2 AI向け説明書の例

```markdown
# ルーム内の下書き状態
このルームでは、他の参加者が未投稿の発言を準備している場合があります。
必要なら `get_active_drafts` を呼び出して、現在のルームで誰が
下書き中か確認できます。下書き本文は取得できません。
情報をどのように会話へ反映するかは、あなたが判断してください。
```

### 5.3 ツール応答例

```json
{
  "roomId": "room-001",
  "activeDrafts": [
    {
      "characterId": "character-chapiko",
      "name": "ちゃぴ子先生",
      "startedAt": "2026-10-31T11:30:00+09:00"
    }
  ]
}
```

## 6. アーキテクチャ案

```text
Avalonia UI (F#)
  ├── Common Shell / Room List / Chat / AI Admin / Settings
  └── ViewModel または MVU
              │
Application (F#)
  ├── RoomExecutionManager
  ├── CandidateScheduler / EventCoalescer
  ├── ConversationOrchestrator
  ├── CharacterManager
  ├── DraftRegistry
  ├── GenerationMetricsCollector
  ├── ReadStateService
  └── EmergencyStopController
              │
Domain (F#)
  ├── Provider / Model / Character
  ├── Room / RoomParticipant / Session / Message
  └── 状態遷移・ポリシー
              │
Infrastructure
  ├── Provider Adapters (OpenAI / Anthropic / Gemini / xAI / Local)
  ├── SQLite (候補)
  ├── Markdown / JSON (候補)
  └── OS Credential Store
```

- Provider Adapterはモデル差分（ツール呼び出し、ストリーミング、キャンセル、パラメータ）を吸収する。複数AIの履歴は内部で話者ID付きメッセージとして保持し、Providerのrole制約に従って、他者の発言を話者名付きのuser相当コンテンツにまとめる等の変換をAdapterで実施する。ロールを無条件に1:1転写しない。
- アプリケーション層は特定Provider SDKに依存しない。
- Room単位の実行制御にMailboxProcessorを使う案がある。外部API待機中にルーム全体の状態処理を塞がないよう、完了結果をメッセージとして戻す。
- UI更新はイベント/状態購読によって行い、会話エンジンはAvaloniaに依存しない。

## 7. F#サンプルコード（概念実装）

以下は設計の骨格を示すサンプル。単一ファイルでの完成アプリではなく、永続化、認証、並行制御、エラー処理、UIバインディング等は別途実装する。

### 7.1 ドメイン型

```fsharp
open System

type ProviderId = ProviderId of Guid
type ModelId = ModelId of string
type CharacterId = CharacterId of Guid
type RoomId = RoomId of Guid
type SessionId = SessionId of Guid

type ConversationMode = Observation | Interactive
type RoomRole = Participant | Moderator | Observer
type ParticipationState = Active | Resting | CatchingUp
type ActivityState = Idle | ReadingHistory | SummarizingHistory | GeneratingResponse

type Character = {
    Id: CharacterId
    Name: string
    ProviderId: ProviderId
    ModelId: ModelId
    PersonalityFile: string
    AvatarPath: string option
    GloballyResting: bool
}

type RoomParticipant = {
    RoomId: RoomId
    CharacterId: CharacterId
    Role: RoomRole
    State: ParticipationState
    AdditionalInstructions: string option
    RestedAtSequence: int64 option
}

type Room = {
    Id: RoomId
    Name: string
    Participants: RoomParticipant list
    Mode: ConversationMode
}

let canGenerate (character: Character) (participant: RoomParticipant) =
    not character.GloballyResting && participant.State = Active
```

### 7.2 下書きレジストリ

ルーム・セッション・キャラクターをキーにし、他セッションの下書きが混ざらないようにする。実装ではスレッド安全な辞書を使用する。

```fsharp
open System
open System.Collections.Concurrent

type DraftInfo = {
    RoomId: RoomId
    SessionId: SessionId
    CharacterId: CharacterId
    Name: string
    StartedAt: DateTimeOffset
    BasedOnSequence: int64
    GenerationId: Guid
}

type DraftRegistry() =
    let drafts = ConcurrentDictionary<SessionId * CharacterId, DraftInfo>()

    member _.Start(draft: DraftInfo) =
        drafts.TryAdd((draft.SessionId, draft.CharacterId), draft)

    member _.Finish(sessionId: SessionId, characterId: CharacterId, generationId: Guid) =
        let key = (sessionId, characterId)
        match drafts.TryGetValue key with
        | true, current when current.GenerationId = generationId ->
            // keyと値の両方が一致するときだけ削除する
            (drafts :> System.Collections.Generic.ICollection<_>)
                .Remove(System.Collections.Generic.KeyValuePair(key, current))
        | _ -> false

    member _.GetOthers(roomId: RoomId, sessionId: SessionId, requester: CharacterId) =
        drafts.Values
        |> Seq.filter (fun d ->
            d.RoomId = roomId &&
            d.SessionId = sessionId &&
            d.CharacterId <> requester)
        |> Seq.sortBy (fun d -> d.StartedAt)
        |> Seq.toList
```

`Start` / `Finish` の成功・失敗は呼び出し元で確認する。生成IDとkey/value一致による条件付き削除で、古いリクエストの完了通知が新しい下書きを削除しないようにする。開始のTryAddが失敗した場合も呼び出し側で処理する。

### 7.3 Provider非依存のチャットポート

```fsharp
open System.Threading

type ChatRequest = {
    ModelId: ModelId
    Instructions: string
    History: (string * string) list
}

type ChatResult = { Content: string }

type IChatProvider =
    abstract Generate:
        ChatRequest * CancellationToken -> System.Threading.Tasks.Task<ChatResult>
```

ツール呼び出しに対応する実装では、Providerごとのツール仕様を共通のアプリ内ツール呼び出しに変換する。`get_active_drafts`のRoomId、SessionId、呼び出し元CharacterIdは**モデルが指定した値を信用せず**、アプリが保持する実行コンテキストから確定する。

### 7.4 AIが参照する読み取り専用ツール

```fsharp
type ToolExecutionContext = {
    RoomId: RoomId
    SessionId: SessionId
    Requester: CharacterId
}

type RoomTools(registry: DraftRegistry) =
    member _.GetActiveDrafts(ctx: ToolExecutionContext) =
        registry.GetOthers(ctx.RoomId, ctx.SessionId, ctx.Requester)
        |> List.map (fun d ->
            {| CharacterId = d.CharacterId
               Name = d.Name
               StartedAt = d.StartedAt |})
```

### 7.5 会話エンジンの追加ドメイン型

```fsharp
type GenerationDecision = Speak of content: string | Pass

type SchedulingMode = EventDriven | RoundRobin

type CandidateReason = Mentioned | LeastRecentlySpoken | Fallback

type GenerationOutcome =
    | Published of messageSequence: int64
    | Passed
    | Discarded of reason: string
    | Failed of error: string
    | Cancelled

type StopReason =
    | UserRequested
    | MaxMessagesReached
    | MaxApiCallsReached
    | InactivityTimeout
    | EmergencyStop
    | ExecutionError of string

type Message = {
    Id: Guid
    RoomId: RoomId
    SessionId: SessionId
    Sequence: int64
    AuthorId: CharacterId option
    BasedOnSequence: int64 option
    ReplyToMessageId: Guid option
    Content: string
}

type GenerationMetrics = {
    GenerationId: Guid
    RoomId: RoomId
    SessionId: SessionId
    CharacterId: CharacterId
    ProviderId: ProviderId
    ModelId: ModelId
    CandidateReason: CandidateReason
    StartDelayMs: int
    DurationMs: int64 option
    InputTokens: int64 option
    OutputTokens: int64 option
    BasedOnSequence: int64
    Outcome: GenerationOutcome
}
```

`GenerationMetrics`はAPIを呼ぶ試行ごとに作成し、開始時にレコードを確保して終了時にOutcomeを更新する。トークン数・所要時間は取得できない場合に欠損を許容する。加えて候補選択ログ（候補外・遅延中止も含む）とセッション停止ログを保存する。推定費用はモデル別単価・キャッシュ利用・課金規則を考慮する別計算値とする。

### 7.6 未読件数（概念SQL）

```sql
-- Messages: RoomId, Sequence, AuthorKind, CountsAsUnread, ...
-- RoomReadState: RoomId, LastReadSequence
SELECT COUNT(*)
FROM Messages AS m
LEFT JOIN RoomReadState AS r ON r.RoomId = m.RoomId
WHERE m.RoomId = @roomId
  AND m.Sequence > COALESCE(r.LastReadSequence, 0)
  AND m.CountsAsUnread = 1;
```

`Sequence`はルーム内の確定投稿順で単調増加する。既読位置の更新は単調増加（古い位置へ戻さない）とする。

### 7.7 復帰時の処理フロー（擬似コード）

```fsharp
// 実装時はResult型、排他制御、キャンセルを追加する。
// 1. RoomParticipantをCatchingUpに変更
// 2. ActivityStateをReadingHistoryにしてUIへ通知
// 3. RestedAtSequence（グローバル休止ではルーム別スナップショット）から履歴取得
// 4. 必要ならSummarizingHistoryに変更して要約
// 5. 次回生成に使用するコンテキストを構築
// 6. 休止解除が有効なままならActiveへ変更。グローバル復帰は次の発言機会まで遅延可
// 7. UIの一時インジケーターを消す
```

## 8. イベントと整合性

```fsharp
type RoomEvent =
    | MessagePosted of RoomId * SessionId * int64
    | DraftStarted of RoomId * SessionId * CharacterId
    | DraftFinished of RoomId * SessionId * CharacterId
    | ParticipantRested of RoomId * CharacterId
    | ParticipantResumed of RoomId * CharacterId
    | SessionPaused of RoomId * SessionId
    | EmergencyStopped
```

- メッセージを永続化してから`MessagePosted`を通知する。ルーム内Sequenceはトランザクション内で専用カウンタを更新して採番し、`MAX(Sequence)+1`による競合を避ける。
- Draft Registryは実行状態の一時的な投影であり、アプリ異常終了時の残骸を復元しない。
- 下書き開始時のメッセージ連番を`BasedOnSequence`として必ず記録する。投稿競合は§4.5の楽観的投稿方式と安全弁に従う。
- 緊急停止後の遅延レスポンスは実行世代ID等で無効化し、投稿を防止する。
- APIキャンセルはリクエスト送信後の課金まで取り消せることを意味しない。

## 9. PoCの実装順序（提案）

1. **縦切りPoC**：2キャラクター・1ルーム・1セッションでAPI接続、イベント駆動Scheduler、PASS、投稿、履歴表示。最大投稿数・最大API呼び出し数・無操作停止の仕組み、生成計測ログ、停止理由表示を初日から実装する（無操作の具体的閾値は設定時に確定）。
2. **AI管理とルーム管理**：Provider/Model/Character、RoomParticipant、複数ルーム登録。
3. **並行実行と未読**：K=2、開始遅延、イベント集約、複数Room Runner、ルーム一覧バッジ、既読位置、楽観的投稿・古さ安全弁。
4. **休止・復帰**：ルーム内/グローバル休止、履歴再構築、処理中インジケーター。
5. **Draft RegistryとAIツール**：生成ID付き下書き検知、`get_active_drafts`、モデルによる任意参照、PASS率・重複度の比較。
6. **停止・運用**：緊急停止、全画面終了、キャンセル、永続化、障害復旧、停止理由の検証。
7. **体験改善**：立ち絵、ストリーミング、金額ベースの料金制御、履歴要約、必要ならタスクトレイ常駐。

## 10. 検証・受け入れ条件

- 同じCharacterを2ルームに配置し、両ルームで並行会話できる。
- ルームAの休止がルームBに影響しない。グローバル休止は両方に反映される。
- 復帰時に休止中の履歴がコンテキストに含まれ、準備中インジケーターが表示される。
- 背景ルームにAI投稿があると未読件数が増え、最新位置を閲覧すると減る。
- `get_active_drafts`が同一セッション内の他者の未投稿下書きのみ返し、本文を返さない。
- モデルがツールを呼び出さなくてもアプリが自動通知や待機指示を挿入しない。
- 緊急停止後に新規生成が始まらず、遅延した結果も投稿されない。
- すべての画面から終了操作へアクセスできる。
- APIキーが平文の設定ファイルや通常ログに記録されない。
- 1投稿イベントから選択される候補が2人以下であり、同一ルームの生成中API呼び出しが2件以下である。
- 第2候補は遅延後に最新履歴を取得し、休止中なら生成しない。
- PASSは投稿されず、PASSのみのラウンドから無限に再起動しない。
- 100回目のAPI予約以降に新規API発行が行われず、停止理由がUIに表示される。
- 投稿上限50を超える並行投稿が成立しない。
- 投稿時の休止・緊急停止チェックとSequence採番が競合に対して原子的である。
- 古いGenerationIdの完了通知で新しい下書きが消えない。
- 60秒以上または新着5件以上の下書きは投稿されず、破棄理由が記録される。
- 同一BasedOnSequenceからの重複投稿、PASS率、投稿1件当たりAPI呼び出し数、所要時間、トークン数を集計できる。
- 返信先が明示されない投稿にReplyToMessageIdを推測で付けない。
- RoundRobin＋モックAI＋固定時計で候補選択の再現テストが可能である。

## 11. 未決事項・PoC後の検証項目

**PoCの基本方針は§4で確定済み。** 次は未決事項として残す。

1. Avalonia標準MVVMとAvalonia.FuncUI/MVUの選択。
2. ユーザー無操作時停止の具体的閾値、操作の定義、バックグラウンド実行との関係。
3. 会話履歴の圧縮・要約方式、モデル別トークン予算、長時間休止のキャッチアップ方法。
4. ツール非対応モデルのルーム別オプトイン設定のUIと初期値（既定は注入なし）。
5. タスクトレイ常駐、OSログイン時自動起動、ウィンドウ閉鎖時の挙動。
6. Provider別レート制限、タイムアウト・再試行、金額ベースの予算上限。
7. SQLiteスキーマ、マイグレーション、バックアップ・エクスポート形式。
8. 観察型と参加型をセッション途中で切り替える際のUI挙動。
9. 重複度の測定手法と許容基準、開始遅延0〜3秒の有効性、投稿の古さ閾値の再調整。
10. グローバル休止の対象ルーム増減時における起点スナップショットの厳密な扱い。
11. 生成完了済みの下書きを一時保持する機能の要否。PoCでは生成結果を直ちに投稿判定する。

## 12. 変更履歴

| 版 | 日付 | 内容 |
|---|---|---|
| v0.1 | 2026-10-31 | 基本構想、ルーム・AI管理、休止・復帰、Draft Registryの初版 |
| v0.2 | 2026-10-31 | イベント駆動並列会話、K=2、PASS、遅延、楽観的投稿、安全弁、計測ログ、停止理由、競合対策、PoC受け入れ条件 |

---

本書は合意事項・PoC暫定値・未決事項を区別した設計ドラフト。F#コードは責務とデータ形状を説明する概念例であり、そのまま実行可能な完成実装を保証しない。外部AI APIの対応機能・料金体系は実装時にProviderごとに確認する。
