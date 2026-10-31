# AI Provider Integration仕様書 v0.1

Oct 31, 2026 · @muneaki kanda

## 1. 位置づけ

本書は、AI Conversation Studioが各社AIへ接続し、モデルを選び、リクエストを組み立て、応答を受け取るまでの層を定める。会話エンジン（誰に生成機会を与え、何を投稿するか）は全体仕様 v0.2 が扱い、本書はその下の通信基盤を扱う。

- 状態：ドラフト。ちゃぴ子先生とオパ子先生のレビュー合意を反映した初版
- 関連文書：AI Conversation Studio 設計仕様書 v0.2
- 合意後、全体仕様の次版へ章として統合する

**全体仕様 v0.2 から置き換える箇所**

| v0.2 の箇所 | 本書での扱い |
| --- | --- |
| §6 「履歴変換はAdapterで実施」 | History FormatterとProvider Adapterに分離（§7・§8） |
| §7.3 `ChatRequest` / `IChatProvider` | 通信契約として再定義（§8） |
| §11-6 レート制限・再試行 | 方針を確定（§10）。具体値は未決のまま |

## 2. 基本原則（確定）

1. Provider / Model / Characterを分離し、アプリケーション層は特定ProviderのSDKに依存しない。
2. モデル一覧はProviderから動的取得を基本とし、モデルIDの手動登録も常に許可する。一覧取得に失敗しても登録済みモデルは使える。
3. モデルIDを登録できることと、そのモデルを正しく呼び出せることは別問題として扱う。
4. モデルが利用不可でも、アプリが黙って別モデルへ代替しない。
5. APIの再試行と会話エンジンの再生成は別物とする。再生成はしない（v0.2 §4.5と一貫）。
6. API通信の成功とメッセージ投稿の成功は別イベントとして記録する。
7. APIキーは平文の設定ファイル・通常ログに残さない。

## 3. コンポーネント構成

通信基盤は10のコンポーネントに分ける。要求は会話エンジンから下へ組み立てられ、応答はResponse Normalizerで判定されてから会話エンジンへ戻る。

&#91;embedded content: AI Integration Layer · 10コンポーネント\]

左列の設定・管理系はAdapterに接続情報・認証情報・モデル設定を渡す。Generation Trackerは全段の状態を記録する。

| コンポーネント | 責務 | 節 |
| --- | --- | --- |
| Provider Registry | 接続設定とProvider種別の管理 | §4 |
| Credential Manager | 認証情報の安全な保存・取得 | §4 |
| Model Catalog | モデル一覧と能力情報の管理 | §5.1 |
| Parameter Schema | モデル設定項目の定義 | §5.2 |
| Prompt Composer | 人格・ルーム指示・出力規約・ツール説明の合成 | §7.1 |
| History Formatter | 会話履歴の論理的な変換 | §7.2 |
| Provider Adapter | API固有のリクエスト・レスポンス変換 | §8 |
| Response Normalizer | PASS・発言本文・出力形式の正規化 | §9 |
| API Gateway | 呼び出し枠、レート制限、再試行、キャンセル | §10 |
| Generation Tracker | 通信状態と使用量の記録 | §11 |

## 4. Provider管理

接続（Connection）を単位として管理し、同じProvider種別を複数登録できる。個人用と仕事用のAPIキー、異なるエンドポイントを分けられる。

| 項目 | 内容 |
| --- | --- |
| 接続名 | 例：「OpenAI メイン」 |
| Provider種別 | OpenAI / Anthropic / Google / xAI / OpenAICompatible |
| 接続先URL | OpenAICompatibleでは必須。他は既定値 |
| 認証情報参照 | Credential Managerのキー。値そのものは保持しない |
| 互換性設定 | OpenAICompatibleのみ。ストリーミング・Tool Calling等の対応可否 |

**Credential Manager**：APIキーはOSの資格情報ストアに保存し、設定ファイルには参照キーだけを書く。ログ出力時はマスクする。

**OpenAICompatible**：Ollama、LM Studio等のローカル推論を含む、OpenAI互換を名乗るエンドポイント用の種別。互換性には実装差があるため、標準OpenAI Adapterをそのまま流用せず、対応機能を設定・検証できる構造にする。

```fsharp
type ProviderKind =
    | OpenAI | Anthropic | Google | XAI
    | OpenAICompatible of baseUrl: string

type ProviderConnection = {
    Id: ConnectionId
    Name: string
    Kind: ProviderKind
    CredentialRef: string   // OS資格情報ストアのキー
}
```

## 5. Model Catalog

モデルは「Providerから取得したID」と「アプリが保持する設定」を組み合わせて管理する。PoCではモデルIDを設定ファイルに手書きし、動的取得と統合UIはP1とする。

### 5.1 能力情報

各能力は3値（対応・非対応・不明）で持つ。複数の情報源が食い違うときの採用順は次のとおり。

**ユーザー手動設定 ＞ 接続テスト結果 ＞ アプリ組み込み情報 ＞ Provider取得情報**

- この順位はアプリがどの値を採用するかの順位であり、実際のAPIの制約は変えられない。
- 接続テスト結果は取得日時を持ち、古い結果を無条件に信用しない。
- 組み込み情報や取得情報の更新で、ユーザー手動設定を上書きしない。
- 接続テストは課金を伴うため、ユーザーの明示操作でのみ実行する。

```fsharp
type Support = Supported | Unsupported | Unknown

type CapabilitySource =
    | UserSetting | ConnectionTest of testedAt: DateTimeOffset
    | BuiltIn | ProviderListing

type ModelCapabilities = {
    Streaming: Support
    ToolCalling: Support
    StructuredOutput: Support
}
```

### 5.2 パラメータ定義

設定画面は機能フラグから組み立てず、Adapterが宣言するパラメータ定義から生成する。各社固有の推論パラメータもこの仕組みで扱う。値の最終検証はAdapter側でも行う（設定ファイルの直接編集に備える）。

```fsharp
type ParameterType =
    | Boolean
    | Integer of min: int option * max: int option
    | Decimal of min: decimal option * max: decimal option
    | Choice of string list

type ParameterValue =
    | BoolValue of bool | IntValue of int
    | DecimalValue of decimal | ChoiceValue of string

type ParameterDefinition = {
    Key: string
    DisplayName: string
    ParameterType: ParameterType
    Required: bool
    DefaultValue: ParameterValue option
}
```

## 6. Characterとモデルの紐付け

Characterは既定の接続とモデルを持ち、RoomParticipantで上書きできる。同じキャラクターを別モデルで動かす比較実験を可能にするため。

- 使用モデルは生成のたびに「RoomParticipantの上書き → Characterの既定」の順で解決する。
- 各生成のモデルIDは計測ログに記録する。
- PoCではモデル変更をセッションの区切りに限定する。会話途中の変更はPoC後に検討する。

同じモデルIDでも接続（APIキー・エンドポイント）が異なれば別の実行先になるため、モデルの実行先は接続IDとモデルIDの組で特定する。全体仕様 v0.2 の `ProviderId` は、本書では接続を指す `ConnectionId` として読み替える。

```fsharp
type ModelBinding = {
    ConnectionId: ConnectionId
    ModelId: ModelId
}

type Character = {
    Id: CharacterId
    Name: string
    DefaultModel: ModelBinding
    // 人格設定・立ち絵などはv0.2 §7.1のとおり
}

type RoomParticipant = {
    CharacterId: CharacterId
    ModelOverride: ModelBinding option
    // 役割・休止状態などはv0.2 §7.1のとおり
}
```

### 6.1 利用不可時の扱い

モデルや接続が使えないとき、別モデルへ自動代替しない。ユーザーによる休止とは区別して、理由付きの状態としてUIに表示する。

```fsharp
type ParticipantAvailability =
    | Available
    | UserResting
    | ModelUnavailable of reason: string       // 廃止・不存在など恒久的
    | ConnectionUnavailable of reason: string  // 認証・接続・一時制限
```

一時的な制限（レート制限）と恒久的な問題（モデル廃止）を区別し、前者は自動復帰、後者はユーザーの対応を待つ。

## 7. リクエスト構築

リクエストはProvider非依存の論理形式で組み立て、API固有形式への変換はAdapterに任せる。History FormatterはAdapterの外に置き、同じ変換方式を異なるProviderで比較できるようにする。

### 7.1 Prompt Composer

指示部（system相当）を合成する。材料は次のとおり。

- Characterの人格設定（Markdown）
- RoomParticipantの追加指示・役割
- 参加者一覧と各自の在席状況
- Speak / PASSの出力規約
- 利用可能なツールの説明書（`get_active_drafts`等）

PASS率や会話の質はこの文面に強く左右されるため、テンプレートにバージョン番号を付け、計測ログに記録する。

**指示の優先順位**：アプリの実行規約（Speak / PASS規約・ツール規約）＞ ルーム固有の指示 ＞ キャラクター人格設定。人格設定に「必ず発言する」とあっても、PASS規約は無効化されない。

**履歴はデータとして扱う**：会話履歴やユーザー発言の中に命令文が現れても、実行規約としては解釈しない。指示部と履歴部は区切りを明示して分ける。

### 7.2 History Formatter

内部の会話は話者ID付きで保持し、生成ごとに対象キャラ視点の論理形式へ変換する。他者の発言をProviderのassistantロールへ1:1で写さない。

| 方式 | 内容 |
| --- | --- |
| NamedRoleMessages | 自分の発言をassistant、他者とユーザーの発言を話者名付きのuser相当にまとめる |
| ScriptTranscript | 会話全体を台本形式の1つのuser相当メッセージにする |

どちらが会話の質に有利かはPoCで比較する。方式はルーム設定で差し替え可能にする。

```fsharp
type HistoryFormat = NamedRoleMessages | ScriptTranscript

type ConversationEntry = {
    SpeakerId: CharacterId option   // Noneはユーザー
    SpeakerName: string
    Content: string
    Sequence: int64
}

type LogicalRole = Self | Others

type FormattedHistory =
    | RoleMessages of (LogicalRole * string) list
    | Transcript of string
```

## 8. Provider Adapter と通信契約

Adapterは論理リクエストをAPI固有形式へ変換し、応答を共通形式へ戻す。ロールの連続制約、ツール定義の形式、ストリーミング、キャンセル、使用量の取り出しといったProvider差分はここで吸収する。

```fsharp
type ChatRequest = {
    GenerationId: Guid
    ModelId: ModelId
    Instructions: string            // Prompt Composerの出力
    History: FormattedHistory       // History Formatterの出力
    Tools: ToolDefinition list
    Parameters: Map<string, ParameterValue>
}

type Usage = { InputTokens: int64 option; OutputTokens: int64 option }

type ChatError =
    | ConnectionFailedBeforeSend of string
    | RateLimited of retryAfter: TimeSpan option
    | QuotaExceeded of string
    | AuthenticationFailed
    | ModelNotAvailable of string
    | TimedOutAfterSend
    | StreamInterrupted
    | Cancelled
    | Unexpected of string

type ChatResponse =
    | Text of raw: string * Usage
    | ToolCalls of ToolCall list * Usage

type IProviderAdapter =
    abstract Parameters: ModelId -> ParameterDefinition list
    abstract Send:
        ChatRequest * CancellationToken
        -> Task<Result<ChatResponse, ChatError>>
```

- エラーは種別で返し、再試行の判断はAPI Gatewayが行う（§10）。Adapterは自分で再試行しない。
- ツール呼び出しのRoomId・SessionId・呼び出し元は、モデルの指定値ではなくアプリの実行コンテキストから確定する（v0.2 §7.3と同じ）。
- PoCではストリーミングを使う場合も、本文はユーザーに途中表示しない。画面は「○○が入力中…」とし、本文は投稿確定後に表示する。PASSや正規化による拒否は生成完了まで確定しないため。

## 9. Response Normalizer

Adapterが返した本文を、会話エンジンが扱う `Speak` / `Pass` / 拒否のいずれかに変換する。他者を装った発言への対処は、履歴変換ではなくここで行う。

| 処理 | PoCの方針 |
| --- | --- |
| PASS判定 | 構造化出力を優先。非対応モデルは厳密な `[PASS]` 規約 |
| 自分の名前の前置き | 行頭の「ちゃぴ子先生：」等、自分の名前だけは除去して正規化 |
| 他者を装った出力 | 編集せず、不正形式として拒否 |
| 空・形式不明の出力 | 拒否 |

- 拒否はPASSとは別の破棄理由として記録し、再生成しない。
- 引用や通常の会話を壊さないよう、文字列の削除は明確なケースに限る。

```fsharp
type NormalizedResult =
    | NormalizedSpeak of content: string
    | NormalizedPass
    | Rejected of reason: RejectReason

and RejectReason =
    | ImpersonatesOthers
    | EmptyOutput
    | InvalidFormat of string
```

## 10. API Gateway

すべてのAPI呼び出しはGatewayを通し、呼び出し枠の予約、同時実行、レート制限、再試行、キャンセルをここで管理する。

**数え方の単位**

| 単位 | 定義 | 上限との関係 |
| --- | --- | --- |
| Generation | 発言機会を与えられてから、投稿・PASS・破棄・失敗・キャンセルで終わるまで | 候補選択の単位 |
| API Attempt | Provider APIへの個々のリクエスト | **上限100はこれに適用** |
| Tool Execution | アプリ内部でのツール実行（`get_active_drafts` 等） | 外部呼び出しではないため数えない |

例：1回のGenerationでツールを2回呼び、その都度結果をモデルへ返すと、API Attemptは3回になる。全体仕様 v0.2 §4.6の「API呼び出し数」はAPI Attemptとして読み替える。

### 10.1 再試行

再試行は「リクエストが処理されていないことが確実、または待てば解決する」場合に限る。再試行もAPI呼び出し上限（v0.2 §4.6、初期値100）に数え、枠は発行前に予約する。

| エラー | PoCの方針 |
| --- | --- |
| 送信前の接続失敗 | 条件付きで再試行可 |
| 一時的なレート制限 | Retry-After等を考慮して待機後に再試行 |
| クォータ・残高不足 | 再試行しない |
| 認証エラー | 再試行しない |
| モデル不存在・利用不可 | 再試行しない（§6.1の状態へ） |
| 送信後タイムアウト | 自動再試行しない |
| ストリーム途中切断 | 自動再試行しない |
| キャンセル | 再試行しない |

再試行回数とタイムアウトの具体値は未決（§14）。

### 10.2 レート制限

制御はProvider種別ではなく接続（APIキー）単位を基本とする。Provider側には組織・プロジェクト・モデル単位の制限もあるため、PoCは接続単位で実装し、将来は複数階層を扱える構造にしておく。

### 10.3 ツール呼び出しのループ上限

1回の生成の中でツールを呼ぶと、APIの往復が増える。

- 1生成あたりの最大ツール往復数：**2**（PoC初期値）
- 各往復もAPI呼び出し上限に数える
- 上限に達したら、ツール結果を返さずに最終応答を要求するか、生成を失敗として終える（どちらにするかは未決）
- ツール実行中に休止・緊急停止が起きたら、結果を返さずキャンセルする

## 11. Generation Tracker

API呼び出しの状態は会話メッセージとは独立して追跡する。通信の結果と会話の結果は別物で、「通信は成功したがPASS」「応答は返ったが投稿は拒否」を正しく区別するため。

```fsharp
type GenerationState =
    | Queued
    | Delayed          // 第2候補の開始遅延中
    | Sending
    | Streaming
    | ToolExecuting
    | Completed
    | Failed of ChatError
    | Cancelled
```

- 通信の終了状態（上記）と会話上の結果（v0.2 §7.5の `GenerationOutcome`：投稿・PASS・破棄・失敗・キャンセル）を両方記録する。
- v0.2の `GenerationMetrics` に次の項目を追加する：接続ID、Prompt Composerのテンプレート版、History Formatterの方式、ツール往復数、再試行回数、Normalizerの拒否理由。
- トークン数や所要時間が取れない場合は欠損を許容する。推定費用は別計算とする。

## 12. PoC範囲と優先度

PoCの目標は、設定ファイルでモデルIDを指定した2種類のProviderを接続し、2キャラクターを会話させることである。優先度は「縦切りPoCに何が必要か」で決める。

| 優先度 | 機能 | 理由 |
| --- | --- | --- |
| P0 | API通信契約（§8） | AI呼び出しの基盤 |
| P0 | APIキー管理（§4） | 認証情報の安全な扱い |
| P0 | Prompt Composer・History Formatter（§7） | 複数AIの会話成立に必須 |
| P0 | Response Normalizer（§9） | PASSと発言の判定 |
| P0 | 最小限のモデル設定（設定ファイル） | 使用モデルの明示 |
| P0 | エラー分類・キャンセル・再試行方針（§10.1） | 実行制御との整合 |
| P0 | 使用量と通信状態の記録（§11） | コスト上限と実験ログ |
| P1 | モデル一覧の動的取得 | 管理UIの利便性 |
| P1 | 能力情報の統合（§5.1） | 設定ミスの防止 |
| P1 | パラメータ定義からのUI生成（§5.2） | Provider差分の吸収 |
| P1 | 接続テスト | 利用可能性の確認 |
| P1 | Tool Callingの共通化（§10.3） | `get_active_drafts` の前提 |
| P2 | モデル廃止検知・更新支援 | 長期運用 |

## 13. 受け入れ条件（PoC）

- 異なる2つのProviderのキャラクターが、同じルームで会話できる。
- 他者の発言がProviderのassistantロールに写されていないことを、送信リクエストのログで確認できる。
- History Formatterの方式をルーム設定で切り替えられ、どちらの方式でも会話が成立する。
- APIキーが設定ファイル・通常ログに平文で現れない。
- 他者を装った出力は投稿されず、拒否理由が記録される。自分の名前の前置きは除去されて投稿される。
- 送信後タイムアウト・クォータ不足・認証エラーで自動再試行が起きない。
- 再試行とツール往復がAPI呼び出し上限に数えられ、上限を超えない。
- モデルが利用不可のとき、別モデルで代替されず、理由付きの状態がUIに表示される。
- ストリーミング中に本文がユーザーに表示されず、PASS・拒否時に「書きかけて消える発言」が出ない。
- 各生成について、接続・モデル・テンプレート版・履歴方式・通信状態・会話上の結果を集計できる。

## 14. 未決事項

1. 再試行回数・待機時間・タイムアウトの具体値。
2. ツール往復上限に達したときの扱い（最終応答を要求するか、失敗とするか）。
3. History Formatterの既定方式（PoCの比較結果で決める）。
4. Prompt Composerのテンプレート管理方法（ファイルかDBか、版の付け方）。
5. 会話途中のモデル変更を許すか、許す場合の履歴と人格の引き継ぎ。
6. 複数階層のレート制限（組織・プロジェクト・モデル単位）の表現方法。
7. 接続テストで確認する項目と、1回のテストにかけてよい費用。
8. OpenAICompatibleの互換性設定の項目と既定値。
9. モデル一覧の自動更新の頻度と、廃止モデルの検知方法。

## 15. 変更履歴

| 版 | 日付 | 内容 |
| --- | --- | --- |
| v0.1 | 2026-10-31 | 初版。Provider管理、Model Catalog、Prompt Composer、History Formatter、Adapter契約、Response Normalizer、API Gateway、Generation Tracker、PoC優先度。ちゃぴ子先生レビュー反映：指示の優先順位、API Attemptの定義、ModelBinding |
