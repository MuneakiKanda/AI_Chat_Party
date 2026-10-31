# AI Conversation Studio 実装設計書 v0.2

- 作成日：2026-10-31
- 状態：**レビュー反映版**（実装開始用・詳細値は未決）
- 対象：F# コンソールPoC、Vertical Slice 1〜3を中心とする実装設計
- 参照：`AI Conversation Studio 設計仕様書 v0.2`、`AI Provider Integration仕様書 v0.1`
- 本書の優先順位：参照仕様で確定した振る舞いを優先し、本書独自の実装判断は「提案」と明記する。

## 1. 目的・非目的

### 1.1 目的
異なるProviderを使う2キャラクターが1ルームで会話するまでを、外部API接続前にモックで検証可能な構造として実装する。通信成功と投稿成功を分離し、PASS・拒否・失敗・キャンセル・予算超過を区別して記録する。

### 1.2 PoCで守る原則
1. `Provider / Model / Character`を分離し、実行先は`ConnectionId + ModelId`で特定する。
2. Provider固有SDKへの依存はInfrastructure内に閉じ込める。
3. `PromptComposer`、`HistoryFormatter`、`ResponseNormalizer`を独立させる。
4. すべてのProvider API呼び出しは`ApiGateway`で**事前にAttempt枠を原子的に予約**する。セッション上限は初期値100。
5. 同一セッションのAI投稿上限は50。人間の投稿は含めない。
6. APIの再試行と会話の再生成を混同しない。**会話の自動再生成はしない**。
7. モデル利用不可時の無断フォールバックを禁止する。
8. 本番ログにはAPIキー・人格Markdown・会話本文を記録しない。詳細ペイロードの捕捉は合成データを使うテスト、または明示的に有効化したデバッグ環境に限定する。
9. 時刻・遅延・ID・乱数・Adapterを差し替え、決定的な自動テストを可能にする。

### 1.3 初期PoCで実装しないもの
- モデル一覧動的取得UI、能力統合UI、パラメータ定義からのUI生成。
- Avalonia、DBマイグレーション、本格的なTool Calling（ただし契約は先に用意）。
- ルーム間並行実行、休止・復帰・緊急停止の完全実装（後続Slice）。
- 会話途中のモデル切替（セッション境界でのみ変更）。
- 具体的な再試行回数やProvider固有レート制限の高度な制御（未決事項）。

## 2. ソリューション構成と依存方向

```text
AIConversationStudio.sln
├─ src/
│  ├─ AIConversationStudio.Domain/
│  │  ├─ Ids.fs
│  │  ├─ Conversation.fs
│  │  ├─ Generation.fs
│  │  └─ Budget.fs
│  ├─ AIConversationStudio.Application/
│  │  ├─ Contracts.fs
│  │  ├─ PromptComposer.fs
│  │  ├─ HistoryFormatter.fs
│  │  ├─ ResponseNormalizer.fs
│  │  ├─ AdapterRegistry.fs
│  │  ├─ SessionLifecycle.fs
│  │  ├─ ApiGateway.fs
│  │  ├─ GenerationTracker.fs
│  │  └─ ConversationOrchestrator.fs
│  ├─ AIConversationStudio.Infrastructure/
│  │  ├─ Providers/MockAdapter.fs
│  │  ├─ Providers/OpenAIAdapter.fs
│  │  ├─ Providers/SecondProviderAdapter.fs
│  │  ├─ Security/OsCredentialStore.fs
│  │  ├─ Storage/InMemoryStore.fs
│  │  └─ Diagnostics/SafeRequestLogger.fs
│  └─ AIConversationStudio.Console/
│     ├─ Configuration.fs
│     └─ Program.fs
└─ tests/
   ├─ AIConversationStudio.Domain.Tests/
   ├─ AIConversationStudio.Application.Tests/
   └─ AIConversationStudio.Infrastructure.Tests/
```

依存方向：

```text
Console ───────► Application ◄────── Infrastructure
  │                  │                     │
  └──────────────────┴─────────────────────► Domain
```

- `Domain`は他の自作プロジェクトに依存しない。
- `Application`は`Domain`に依存し、`IProviderAdapter`、`IAttemptBudget`、`IMessageStore`などの**ポートを定義**する。
- `Infrastructure`は`Application`のポートを実装する。`Application`は`Infrastructure`を参照しない。
- `Console`はComposition Rootとして実装を組み立てる。
- **コンポーネントの分離とプロジェクトの分離は同義ではない**。PoCでは機能単位のモジュール分離を基本とする。
- `MockAdapter`はInfrastructureに置くが、テスト用捕捉機能は本番のAdapter契約に混ぜず、Mock固有の公開プロパティで提供する。

## 3. 主要なドメイン型（F#設計スケッチ）

以下は契約と意図を示す**設計スケッチ**であり、コンパイル済みコードではない。名前・型は実装時に一貫して整える。

```fsharp
type ConnectionId = ConnectionId of System.Guid
type CharacterId = CharacterId of System.Guid
type RoomId = RoomId of System.Guid
type SessionId = SessionId of System.Guid
type GenerationId = GenerationId of System.Guid
type ModelId = ModelId of string

type ModelBinding = {
    ConnectionId: ConnectionId
    ModelId: ModelId
}

type Character = {
    Id: CharacterId
    Name: string
    PersonaMarkdown: string
    DefaultModel: ModelBinding
}

type RoomParticipant = {
    CharacterId: CharacterId
    ModelOverride: ModelBinding option
    AdditionalInstructions: string option
}

type HistoryFormat = NamedRoleMessages | ScriptTranscript
type LogicalRole = Self | Others

type ConversationEntry = {
    Sequence: int64
    SpeakerId: CharacterId option
    SpeakerName: string
    Content: string
}

type FormattedHistory =
    | RoleMessages of (LogicalRole * string) list
    | Transcript of string

type GenerationDecision =
    | Speak of string
    | Pass
    | Rejected of string
```

**実行時のModelBinding**は、生成開始前に`RoomParticipant.ModelOverride`→`Character.DefaultModel`の順で解決し、生成単位で固定する。セッション中の設定変更は次セッションまで反映しない（PoC）。

**`BasedOnSequence`**はAPIに送る履歴スナップショットの末尾Sequenceとし、生成結果の投稿時に鮮度判定に使用する。`ReplyToMessageId`とは独立した値とする。

## 4. Applicationのポートと責務

### 4.1 Provider Adapter

```fsharp
type ChatRequest = {
    GenerationId: GenerationId
    ModelId: ModelId
    Instructions: InstructionSegment list
    History: FormattedHistory
    Tools: ToolDefinition list
    Parameters: Map<string, ParameterValue>
}

type ChatResponse =
    | Text of raw: string * usage: Usage
    | ToolCalls of ToolCall list * usage: Usage

type IProviderAdapter =
    abstract member Parameters: ModelId -> ParameterDefinition list
    abstract member Send:
        ChatRequest * System.Threading.CancellationToken
        -> System.Threading.Tasks.Task<Result<ChatResponse, ChatError>>
```

`ChatRequest`に秘密情報は入れない。`ConnectionId`はGatewayの呼び出しコンテキストで別途渡し、Adapterの接続インスタンスまたは解決された接続設定に結びつける。`CredentialRef`を使い、APIキーそのものは送信直前に安全に取得する。

### 4.2 接続ごとのAdapter解決（レビュー反映）

`IAdapterRegistry`が`ConnectionId`から`IProviderAdapter`を解決する。AdapterはCredentialRefを持つ**接続単位**のインスタンスとする。GatewayはConnectionIdを基準にAdapter解決・レート制限・Trackerへの記録を統一する。

```fsharp
type IAdapterRegistry =
    abstract member Resolve:
        ConnectionId -> Result<IProviderAdapter, string>
```

Adapterが認証情報そのものを保持する必要はない。接続設定の`CredentialRef`を保持し、資格情報は送信時に安全に取得する。

### 4.3 Prompt Composer

入力：キャラクター人格Markdown、ルーム指示、参加者一覧と在席状況、Speak/PASS規約、利用可能なツール定義。

出力：優先度付き`InstructionSegment list`と、各セグメントのテンプレート版。

```fsharp
type InstructionKind = AppRules | RoomInstructions | Persona | ToolGuide

type InstructionSegment = {
    Kind: InstructionKind
    Content: string
    TemplateVersion: string option
}
```

`ChatRequest.Instructions`も`string`から`InstructionSegment list`へ変更する。AdapterはProviderのsystem/developer等の利用可能な指示レイヤーへ割り当てる。単一レイヤーしかないProviderでは順序と明確な区切りを維持する。**上位指示を確実に強制できるという保証はしない**。

指示の優先順位：**アプリ実行規約 ＞ ルーム指示 ＞ 人格設定**。履歴は指示ではなくデータとして区切って渡す。テンプレート版をGenerationログに記録する。Provider固有のsystem/developer等への割当はAdapterの責務。

### 4.4 History Formatter

入力：確定済みの会話履歴、対象CharacterId、`HistoryFormat`。

出力：`FormattedHistory`。

- `NamedRoleMessages`：自身の発言のみ`Self`、他者・ユーザーは話者名付きの`Others`。
- `ScriptTranscript`：全員の発言を明示的な話者ラベル付きの台本としてまとめる。
- 同一の履歴を両方式で再現できるよう、Formatterは副作用を持たない純粋関数とする。
- **重要**：`Self/Others`は論理ロールであり、Providerの実際の`assistant/user`への変換はAdapterで行う。Providerの連続ロール制約をAdapterが吸収する。

### 4.5 Response Normalizer

入力：Adapterの最終レスポンスと対象キャラクター名。

出力：`Speak / Pass / Rejected`。

- 構造化出力が使える場合はそれを優先し、非対応モデルでは厳密な`[PASS]`を認識する。
- 自分の名前の明白な行頭プレフィックスのみ除去する。
- 他者なりすましは、**行頭が「他の参加者名＋`：`または`:`」で始まる行**に限定して判定する。`>`で始まる引用行、かぎ括弧内の引用、本文中の名前は対象外。参加者一覧から名前を照合する。空出力・不正形式も`Rejected`として記録し、**自動再生成しない**。誤検知件数を計測し、判定範囲の拡張は検証後に判断する。
- 引用文中の名前や通常の本文を安易に削除しない。
- ストリーミングの断片をNormalizerに確定本文として渡さない。

### 4.6 Generation Tracker

1回の発言機会を`Generation`、Provider APIへの個別リクエストを`API Attempt`、ローカルツール実行を`Tool Execution`として別々に識別する。

最小記録項目：
`GenerationId / SessionId / RoomId / CharacterId / ConnectionId / ModelId / PromptVersion / HistoryFormat / BasedOnSequence / StartTime / EndTime / GenerationState / AttemptCount / ToolRoundTrips / RetryCount / Usage / NormalizationResult / PostingResult / DiscardReason`。

通信終了（成功・失敗・キャンセル）と会話上の結末（投稿・PASS・拒否・破棄・失敗）は**別フィールド**にする。トークン数不明は欠損を許容する。推定費用は別計算とする。

## 5. API Gatewayと予算管理

### 5.1 初期上限

| 対象 | PoC初期値 | 数え方 |
| --- | ---: | --- |
| セッションのAPI Attempt | 100 | 予約したAttemptを含む。PASS、失敗、再試行、ツール後の再送も対象 |
| セッションのAI投稿 | 50 | 実際に確定したAI投稿のみ。人間の投稿は対象外 |
| ルーム内の同時生成 | 2 | 後続の並列Sliceで実装 |
| 生成あたりのツール往復 | 2 | Tool Calling実装時に有効化 |

### 5.2 原子的予約契約

```fsharp
type AttemptReservation =
    | Reserved of attemptNumber: int
    | LimitReached of limit: int

type IAttemptBudget =
    abstract member TryReserve:
        SessionId -> AttemptReservation
```

- `TryReserve`は**同一SessionIdについて線形化可能**な操作とし、残り1枠への同時アクセスで成功は1件だけ。
- 最初のSliceではロック等で保護したインメモリ実装を使用。永続化する段階ではトランザクションによる予約へ置き換える。
- **上限判定は予約件数を基準**とし、予約後に送信前キャンセルとなっても原則として枠は戻さない（PoCの保守的な計数方針・レビュー対象）。
- `AttemptReserved`と`AttemptSent`を別イベントとして記録し、実際の送信件数と予約消費を区別する。
- `LimitReached`時はAdapterを呼ばず、停止理由`MaxApiCallsReached`をOrchestratorへ通知する。
- Mock Adapterでも同じGatewayを通し、実際には課金しないが予約数は加算する。
- 将来、複数プロセスから同一セッションを動かす場合はインメモリ実装では不十分。PoCは単一プロセスを前提とする。

### 5.3 セッションの停止状態（レビュー反映）

```fsharp
type SessionRunState =
    | Running
    | Draining of StopReason
    | Stopped of StopReason
```

- 最後のAPI Attempt枠を予約した時点で、**新規予約を閉じる**。理由`MaxApiCallsReached`を記録し、進行中の処理を残して`Draining`へ移行する。
- `Draining`中は新しいAttemptを予約しない。すでに予約済み・実行中のGenerationは完了可能だが、投稿時には50件上限や休止などの条件を改めて確認する。Tool Callingの追加往復も新規予約できないため、予算不足として安全に終了する。
- 進行中Generationがゼロになったら`Stopped`へ移行する。停止理由は保持する。
- `EmergencyStop`は別経路：新規予約を禁止し、進行中のCancellationTokenをキャンセルし、世代IDを更新して**遅延応答の投稿を拒否**する。キャンセルを無視するProvider応答も投稿できない。
- 上限50件到達時の扱いも、進行中の生成を含めて投稿上限を越えないよう`TryCommit`で保証する。
- 状態遷移、予約数、進行中件数は同一SessionIdに対して整合するよう同期する。Gatewayの予約とSession状態の検査を別々の競合可能な操作にしない。

### 5.4 再試行とTool Calling

- Slice 1：再試行なし。Gatewayは予算予約・Adapter呼び出し・Attempt記録を行う。
- Slice 2：接続前失敗と一時的なレート制限のみ、仕様の分類に従い条件付き再試行。具体的な回数・待機時間は未決で、**決定までは自動再試行を無効**とする。
- 送信後タイムアウト、ストリーム中断、認証失敗、クォータ不足、モデル不存在は自動再試行しない。
- Tool Callingを有効化したら、各追加API送信も**新しいAttempt予約が必要**。ツール実行そのものはAPI Attemptに含めない。
- ツール往復上限2に達したときは**`ToolLimitExceeded`で失敗終了**する。追加のAPI Attemptで最終応答を要求しない。発生頻度を記録し、PoC後に再検討する。
- レート制限はまずConnection単位で管理し、Provider側の組織・モデル単位制限への対応は後続とする。

## 6. Vertical Slice 1：Mockで1往復

### 6.0 投稿の原子的契約（Slice 1から実装）

```fsharp
type PostCommand = {
    SessionId: SessionId
    RoomId: RoomId
    GenerationId: GenerationId
    ExpectedEpoch: int64
    BasedOnSequence: int64
    GeneratedAt: System.DateTimeOffset
    Content: string
}

type PostRejection =
    | MaxMessagesReached
    | ParticipantResting
    | SessionNotAcceptingPosts
    | EpochChanged
    | StaleGeneration
    | DuplicateGeneration

type PostCommitResult =
    | Committed of sequence: int64
    | Rejected of PostRejection

type IMessageStore =
    abstract member TryCommit: PostCommand -> PostCommitResult
```

`TryCommit`は、**Sequence採番・AI投稿上限50・休止状態・セッション世代ID・鮮度（60秒または後続5投稿以上）・同一Generationの重複防止・保存**を一つの原子的操作で実施する。事前にOrchestratorで確認してもよいが、その確認は最終的な権威ではない。Slice 1はロック付きインメモリStore、SQLite移行時は`BEGIN IMMEDIATE`を用いた単一書き込みトランザクションで同じ契約を満たす。

`GeneratedAt`は名称を含めて実装時に整理する。鮮度60秒は**生成開始時刻**を起点とするため、生成開始時刻を確実に保持すること。`ExpectedEpoch`は緊急停止や休止状態変更に対する世代検査に使用する。

`TryCommit`とセッションの`Draining`は両立させる。**Draining中の既存Generationは投稿可能**であり、新規生成だけを禁止する。`Stopped`または緊急停止後は投稿不可。

### 6.1 処理フロー

```text
Console: テーマ入力 / テスト用固定シナリオ
    │
    ▼
ConversationOrchestrator
    ├─ Session/Character/ModelBindingを確定
    ├─ 現在の会話Sequenceを取得（BasedOnSequence）
    ├─ PromptComposer → Instructions + TemplateVersion
    └─ HistoryFormatter → FormattedHistory
    │
    ▼
ApiGateway
    ├─ Session状態を確認しIAttemptBudget.TryReserve(SessionId)（原子的）
    ├─ IAdapterRegistry.Resolve(ConnectionId)
    ├─ GenerationTracker: AttemptReserved
    ├─ Adapter.Send(ChatRequest)
    └─ GenerationTracker: AttemptSent/Completed/Failed
    │
    ▼
MockAdapter（受信ChatRequestをテスト内で捕捉）
    │
    ▼
ResponseNormalizer
    ├─ Speak ───► IMessageStore.TryCommit(PostCommand)
    │                ├─ Committed(seq) → MessagePosted
    │                └─ Rejected(reason) → DiscardReason記録
    ├─ Pass ────► PASSとして記録、投稿しない
    └─ Rejected ► 拒否理由を記録、投稿しない
    │
    ▼
GenerationTracker: 通信結果と投稿結果を確定
```

**`TryCommit`が成功した後にのみ`MessagePosted`を発行**する。Slice 1は単一ルーム・逐次生成だが、後続のイベント駆動化でこの順序を変えない。

### 6.2 MockAdapter

テストごとに以下を注入できるようにする：
`Text / [PASS] / 他者なりすまし / 空出力 / 送信前接続失敗 / 送信後タイムアウト / 遅延応答 / キャンセル`。

Mockは`ChatRequest`を**テスト用インメモリコレクション**に保存する。捕捉内容はファイルや通常ログに出力しない。並行テストに備え、スレッド安全なコレクションまたはロックを使う。

### 6.3 最小の会話シナリオ

1. ユーザーがテーマを投稿（Sequence 1）。
2. ちゃぴ子先生のMockが発言（Sequence 2）。
3. オパ子先生への`ChatRequest`を捕捉し、Sequence 2のちゃぴ子先生の発言が`Others`として渡されていることを検証する。
4. オパ子先生のMockが発言（Sequence 3）。
5. `MessageStore`にはユーザー＋AI2件の確定投稿だけが残る。
6. `GenerationTracker`には各GenerationのAttempt、Normalizer結果、投稿結果が残る。

### 6.4 ログと検証の分離

| 種類 | 収録する内容 | 運用 |
| --- | --- | --- |
| 通常の構造化ログ | ID、状態、時間、エラー分類、使用量、モデルID、プロンプト版 | 本文・秘密情報なし |
| Mockの捕捉リクエスト | `ChatRequest`全体（合成データ） | テストプロセス内のみ |
| Adapterペイロード検証 | 実送信直前のProvider形式（合成データ） | HTTPスタブで検証 |
| 詳細デバッグログ | 指示・履歴・ペイロード | 明示的有効化、保存先・保持期間を制限、認証ヘッダー等は必ず除去 |

**送信形式の検証**は、Mockの論理リクエスト検証だけでは不十分。実Adapterに差し込むHTTPスタブで、他者の発言がProviderのassistantロールに誤配置されていないことを検証する。

## 7. 決定的テストのための依存性

```fsharp
type IClock =
    abstract member UtcNow: System.DateTimeOffset

type IIdGenerator =
    abstract member NewId: unit -> System.Guid

type IAsyncDelay =
    abstract member Delay:
        System.TimeSpan * System.Threading.CancellationToken
        -> System.Threading.Tasks.Task

type IRandomSource =
    abstract member NextDelay:
        minInclusive: System.TimeSpan * maxInclusive: System.TimeSpan
        -> System.TimeSpan
```

- テスト用時計は任意時刻へ進められる。
- `IAsyncDelay`は仮想時間で待機を完了できる。実`Task.Delay`をテストに残さない。
- IDは固定列から供給できる。
- スケジューラの乱数も固定シード・固定列で再現する。
- テスト用依存性はComposition Rootで注入し、アプリケーションコード内で`Guid.NewGuid()`や`DateTimeOffset.UtcNow`を直接呼ばない。

## 8. Slice別実装計画

| Slice | 主要成果 | 必須テスト・完了条件 |
| --- | --- | --- |
| 1：Mock | Domain、Composer、Formatter、Normalizer、最小Gateway、Tracker、メモリStore、Console | 1往復、PASS、拒否、上限100、履歴視点、捕捉リクエスト、決定的時計/ID |
| 2：Provider 1社 | 実Adapter、OS資格情報ストア、送信形式スタブ、通信エラー分類、**最小緊急停止** | 実APIで1応答。**上限100は既に適用済み**。Ctrl+Cで新規予約停止・進行中キャンセル・遅延投稿拒否。秘密情報非記録 |
| 3：Provider 2社 | 第2Adapter、接続ごとの設定 | 異なるProviderの2キャラ会話、各Adapterのロール変換テスト |
| 4：並列会話 | Scheduler、イベント集約、K=2、0〜3秒遅延、楽観的投稿、**無操作時停止** | 競合投稿、BasedOnSequence、60秒/5件鮮度、50投稿上限、無操作時停止 |
| 5：休止とツール | 休止・復帰、緊急停止のUI統合、Generation epochの拡張、Tool Calling統合 | 休止後の誤投稿なし、遅延応答の破棄、ツール往復上限 |
| 6：UI | Avalonia、ルーム一覧、未読、参加者状態、停止理由 | 通信・投稿状態の表示、設定と停止操作 |

**Slice 1は順次生成のハーネス**であり、v0.2のイベント駆動会話を置き換えるものではない。Slice 4で正式なスケジューラへ移行する。

## 9. 受け入れテスト詳細

| ID | テスト | 期待結果 |
| --- | --- | --- |
| T01 | Mockで2キャラ1往復 | ユーザー1件＋AI2件が順番どおり確定 |
| T02 | 2人目への履歴 | 1人目の発言が`Others`として含まれる |
| T03 | `NamedRoleMessages` / `ScriptTranscript` | 同一履歴を両方式で構築可能 |
| T04 | Mockが`[PASS]` | 投稿0件、PASS結果とAttempt1件を記録 |
| T05 | Mockが他者になりすます | 投稿せず`Rejected`と理由を記録 |
| T06 | Mockが自身の名前を行頭に付ける | 正規化して投稿 |
| T07 | Attempt残数1で並列予約2件 | 成功1件・拒否1件、合計100以下 |
| T08 | Attempt上限到達 | Adapter未呼び出し、`MaxApiCallsReached` |
| T09 | Mockで送信後タイムアウト | 再生成・自動再試行なし、Attempt消費 |
| T10 | キャンセル | 応答を投稿せず、通信/会話結果を別記録 |
| T11 | 時計を60秒以上進める | 後続Sliceの鮮度判定が決定的 |
| T12 | 実AdapterのHTTPスタブ | Providerロールと話者ラベルが正しく変換 |
| T13 | 構造化ログ | APIキー・会話本文・人格Markdownを含まない |
| T14 | モデル利用不可 | 自動代替せず、理由付き状態へ遷移 |
| T15 | 生成中の後続投稿（Slice 4） | 5件/60秒閾値に従い採否を決定 |
| T16 | ストリーム断片（Slice 2以降） | 本文は確定前にUIへ表示しない |
| T17 | 50件AI投稿上限（Slice 4） | 51件目は確定せず停止理由を記録 |
| T18 | 予約後・送信前キャンセル | AttemptReservedとAttemptSentを区別、上限不変 |
| T19 | `TryCommit`の競合 | 最後の投稿枠への同時コミットで成功は1件 |
| T20 | `Draining`への移行 | 新規予約禁止、既存Generationは投稿上限内で完了可能 |
| T21 | Ctrl+C緊急停止（Slice 2） | 新規予約禁止・進行中キャンセル・遅延応答の投稿拒否 |
| T22 | Adapter Registry | 同じProvider種別でも接続IDごとに別Adapter/資格情報参照 |
| T23 | 指示セグメント | 優先順位・各版を保持し、Provider別割当を検証 |
| T24 | Normalizerの引用 | 行頭他者名は拒否、引用行・かぎ括弧内・文中は保持 |
| T25 | ToolLimitExceeded | 2往復後に追加API Attemptなしで失敗終了 |

## 10. 仕様との対応表

| 参照仕様 | 本書での実装位置 | 注意点 |
| --- | --- | --- |
| 全体v0.2 §4.4 候補選択 | Slice 4 Scheduler | Slice 1は逐次ハーネス |
| 全体v0.2 §4.5 楽観的投稿 | Orchestrator / Slice 4 | BasedOnSequenceとReplyToは独立 |
| 全体v0.2 §4.6 実行上限 | Gateway + MessageStore | API上限はAttempt予約、投稿上限は確定投稿 |
| 全体v0.2 §5 Draft Registry | Slice 5 | PoC初期の会話には不要 |
| 全体v0.2 §8 イベント整合性 | MessageStore / Orchestrator | 永続化成功後にMessagePosted |
| Integration v0.1 §4〜6 | Configuration / Binding / Credentials | モデル無断代替禁止 |
| Integration v0.1 §7 | Composer / Formatter | 指示優先順位、履歴をデータとして隔離 |
| Integration v0.1 §8 | Application port / Adapter | Provider固有ロール変換はAdapter |
| Integration v0.1 §9 | Normalizer | PASSと拒否を区別 |
| Integration v0.1 §10 | Gateway | すべてのAPI Attemptを事前予約 |
| Integration v0.1 §11 | Tracker | 通信結果と投稿結果を分離 |

## 11. オパ子先生レビューへの回答（8件すべて採用）

| 論点 | 反映結果 |
| --- | --- |
| 予約後キャンセル | 予約枠は返却せず、Reserved/Sentを別記録（§5.2） |
| ConnectionId | `IAdapterRegistry`を追加、接続単位でAdapter解決（§4.2） |
| 停止理由 | `Running → Draining → Stopped`、緊急停止は別経路（§5.3） |
| 投稿原子性 | `IMessageStore.TryCommit`をSlice 1から実装（§6.0） |
| ツール往復上限 | 2往復で`ToolLimitExceeded`、追加Attemptなし（§5.4） |
| 指示の境界 | `InstructionSegment list`へ変更（§4.3） |
| Normalizer誤検知 | 他者名＋コロンの行頭のみ拒否、引用を除外（§4.5） |
| Slice境界 | 緊急停止をSlice 2へ、無操作停止をSlice 4へ（§8） |

### 実装時の追加確認

- `Running → Draining`と最終枠予約を同じ同期境界で処理する。
- `TryCommit`はDraining中の既存Generationを許可し、緊急停止のepoch変更後は拒否する。
- 指示セグメントはProviderのロール制約に合わせて変換するが、モデルが優先順位を守ること自体は保証できない。
- Normalizerの名前判定は、参加者名の重複・別名・改行・Unicode表記揺れをテストで扱う。

## 12. 未決事項・変更管理

- 再試行回数・待機時間・タイムアウトの数値。
- Prompt Composerテンプレートのファイル/DB管理方式。
- Provider別SDK/HTTPクライアント選択、認証ストアのOS別実装。
- Model Catalog、能力判定、接続テスト、OpenAICompatible互換設定の詳細。
- Tool Calling上限到達時はPoCでは`ToolLimitExceeded`。将来の代替動作は未決。
- Sessionの停止・一時停止・drainの厳密な状態遷移。
- ログの保持期間、詳細デバッグログの保護と削除方針。
- 永続化・クラッシュ復旧・予約計数の再開仕様。
- 複数ルーム/複数セッションのレート制限と予算配分。

## 13. 変更履歴

| 版 | 日付 | 内容 |
| --- | --- | --- |
| v0.1 | 2026-10-31 | 初版。F#ソリューション構成、ポート、最小Gateway、Mock捕捉、決定的テスト、Slice計画、仕様対応表、レビュー論点 |
| v0.2 | 2026-10-31 | オパ子先生の8点のレビューを反映。AdapterRegistry、Draining、TryCommit、指示セグメント、Normalizer狭域判定、Slice境界変更 |
