# FSharp.Coreの依存関係・ロックファイル調査

調査日：2026-11-01。対象：main `9feee0d`、.NET SDK 10.0.403 / Linux x64。
Windows SDK同梱パッケージの現物比較およびWindows上の実行は未実施。

## 1. 10.1.403はどこで決まるか

修正前はプロジェクトにFSharp.Coreの明示PackageReferenceがなく、F# SDKが暗黙に追加していた。
`global.json` はSDKの最低バージョンを10.0.100、rollForwardをlatestFeatureとしているため、
この環境ではインストールされた10.0.403が選択される。

評価されたSDKファイル：

- `sdk/10.0.403/FSharp/Microsoft.FSharp.Core.NetSdk.props`：`FSCorePackageVersion=10.1.403`。
- `Microsoft.FSharp.NetSdk.props`：DisableImplicitFSharpCoreReferenceが未設定のとき暗黙のPackageReferenceを追加。
  FSharpCoreImplicitPackageVersionがあればそちらを使い、なければFSCorePackageVersionを使う。
- `Microsoft.FSharp.NetSdk.targets`：DisableImplicitLibraryPacksFolderがtrueでなければ
  `FSharp/library-packs` をRestoreAdditionalProjectSourcesへ追加する。

修正前の評価結果はFSharpCoreImplicitPackageVersionが空、PackageReference.Versionが10.1.403、
RestoreAdditionalProjectSourcesがこのSDKのlibrary-packsだった。
ロックの `requested: [10.1.403, )` は暗黙の下限指定、`resolved: 10.1.403` は実際の解決結果を表す。

```sh
dotnet msbuild src/AIConversationStudio.Domain/AIConversationStudio.Domain.fsproj -getProperty:NETCoreSdkVersion,FSCorePackageVersion,FSharpCoreImplicitPackageVersion,DisableImplicitLibraryPacksFolder,RestoreAdditionalProjectSources -getItem:PackageReference
```

## 2. Linuxで生成されたハッシュの由来

元の5つのロックファイルに共通するFSharp.CoreのcontentHashは、実際にLinux SDK同梱nupkgから計算した値と一致した。
旧共有キャッシュの `.nupkg.metadata` にも取得元としてSDKのlibrary-packsが記録されていた。

| 配布物 | NuGetのcontentHash |
| --- | --- |
| SDK 10.0.403 Linux x64同梱 FSharp.Core.10.1.403.nupkg | `TWtcvx8ZryidjqFb84j8iu2akDNpFr8pSWBbuFDMI5xRZrfCjJXsyOkO/bH5TTElSupHxgZTYi1I5Fd7InqlDQ==` |
| nuget.org FSharp.Core 10.1.403 | `SpKxPt90eSC3pfSKGLnSyucXXKVWokv1KVMTy2uQG36seOgJg14qUxjlcaWjZNWgxJtG5MeH9WpI6PQg4OR4tA==` |

取得した公式配布物：
`https://api.nuget.org/v3-flatcontainer/fsharp.core/10.1.403/fsharp.core.10.1.403.nupkg`

`NuGet.Packaging.PackageArchiveReader.GetContentHash` をSDK同梱のNuGet DLLから呼び出して比較した。
`contentHash` とnupkg全体にSHA-512を掛けた値（キャッシュの `.nupkg.sha512`）は、
この署名付きパッケージでは一致しない。比較対象を混同してはならない。

| 配布物 | バイト数 | nupkgアーカイブ全体のSHA-512（Base64） |
| --- | ---: | --- |
| SDK同梱版 | 3,049,858 | `SJuScg76FNYGJv1ttGP9HOAmbCINsLd5CZ58tEZy51inpeWPfqZlexa3Z0BVfmyiKvKqVoJEnfoIHFOQUXE6PQ==` |
| nuget.org版 | 3,064,861 | `dOBTNGB//u1UeGWn+x4nbxS2lZq4hrsAmIPxUxc81E9lVqpLIduor2RvTi2etVJRUnPi+gQLT0PtPhkEBoPN6w==` |

ロックのcontentHashを書き換えて検証を通したわけではない。配布元とバージョンの管理方式を変更した後、
空のキャッシュからNuGetに再解決・ロック再生成をさせた。

## 3. SDK版とnuget.org版の差がrestoreへ及ぼす影響

両方ともIDはFSharp.Core、バージョンは10.1.403、nuspecのrepository commitは
`5a2162bea6845eabc1fc721624a43568c65db3b1` だが、同じ配布物ではない。
ZIPのファイル名一覧は一致する一方、nuspec、XML、DLL、リソースDLL、署名のバイト列が異なる。
単なるZIP再圧縮やリポジトリ署名追加だけの差とは言えない。APIや実行時挙動の差までは調査していない。

例：`lib/netstandard2.1/FSharp.Core.dll` のSHA-256：

- SDK版：`c33076a19fe2ce2887d0f8098667f4966cbd3586345fca9d52f988cd0b91ea59`
- nuget.org版：`1af305b7625c30a54d141caf805c5b0b5a8f6166fb5cef22e3570f55d33152bd`

署名はSDK版がMicrosoftのAuthor署名、nuget.org版がAuthor署名＋NuGet.orgのRepository署名。
`dotnet nuget verify --all` は両方とも終了コード0だったが、このネットワークでは失効確認先へ到達できず、
NU3018/NU3028（RevocationStatusUnknown）の警告が出た。失効状態まで完全に確認できたという意味ではない。
TLS、署名検証、ハッシュ検証を無効化する設定は追加していない。

NuGetのグローバルパッケージキャッシュはパッケージID／バージョン単位で再利用される。
同一ID／バージョンで内容が異なるフィードがあると、SDKの追加フィード、通常フィード、既存キャッシュの
どれを使ったかでcontentHashが変わり、locked-modeはNU1403で正しく拒否する。
複数ソースの列挙順だけで優先順位を保証することもできない。
Package Source Mappingは取得先の制限には有効だが、既存の同一ID／バージョンのキャッシュを
自動的に正しい配布物へ置換する仕組みではない。

したがって、問題を「WindowsとLinuxでハッシュ計算が違う」と説明するのは不正確。
異なる配布物を同じID／バージョンで選択していたことが、このLinux環境でも再現できる原因である。
Windows SDK同梱版がどちらと一致するかは今回実測していない。

### 再現結果

一時的な作業コピーと独立したキャッシュで実行した。既存ユーザーキャッシュは削除・上書きしていない。

| ロック | パッケージ供給元／キャッシュ | locked-mode結果 |
| --- | --- | --- |
| 元のSDK版ロック | 空キャッシュ＋SDK library-packsのみ | 成功 |
| 元のSDK版ロック | 空キャッシュ＋nuget.orgのみ（DisableImplicitLibraryPacksFolder=true） | NU1403 |
| 新しいnuget.org版ロック | 旧SDK版が入った共有キャッシュ | NU1403 |
| 新しいnuget.org版ロック | 空の専用キャッシュ＋nuget.org | 全ソリューションで成功 |

## 4. 採用する管理方式

**パッケージのバージョン、配布元、ロック、キャッシュの境界を合わせて管理する。**

- Directory.Build.propsでFSharpCoreImplicitPackageVersionを `[10.1.403]` に固定する。
  全F#プロジェクトに共通のSDK対応プロパティを用い、各fsprojへ参照を重複させない。
- DisableImplicitLibraryPacksFolder=trueでSDKのローカルフィード追加を無効にする。
- NuGet.Configでnuget.orgだけを宣言し、Package Source Mappingでもそこへマッピングする。
  本リポジトリには私有パッケージはない。追加する場合はソースとマッピングを明示的に変更する。
- NuGet.ConfigのglobalPackagesFolderを `.nuget/packages` とし、既存のSDK由来共有キャッシュから分離する。
  フォルダはgitignore対象。キャッシュに秘密情報を書き込む設定は含めない。
- 5つのpackages.lock.jsonを同じnuget.org配布物で再生成・コミットする。
  通常の開発・CIは `--locked-mode`。意図した依存関係更新時だけ `--force-evaluate` で再生成してレビューする。
- Windows/LinuxのCIマトリクスで、同じロックを用いたrestore・Release build・testを実行する。

| 選択肢 | 判断 |
| --- | --- |
| SDKだけを厳密に固定 | SDK同梱物とnuget.org物の選択差は解消しないため単独では不十分 |
| パッケージ版だけ固定 | 同一版の異なる配布物には不十分 |
| ソース統一＋明示版＋共通ロック | 採用。compiler SDK更新と依存パッケージ更新を分離できる |
| Central Package Management | 依存数が増えた場合は候補。今回はSDKの既存overrideプロパティで十分で、CPM自体が配布元の差を解消するわけではない |
| OS別ロック／検証無効化 | 今回は不要。異なる配布物の混在を固定化したり、整合性検査を弱めたりしない |

### キャッシュ移行

`NUGET_PACKAGES` を設定している場合はNuGet.ConfigのglobalPackagesFolderより優先される。
SDK版が入った共有キャッシュを指している場合は、環境変数を解除するか、新しい空の専用ディレクトリを
指定する。ユーザー全体のキャッシュ削除や、ロックハッシュの手編集は不要。

```sh
# リポジトリルートで実行。環境変数の上書きがない場合はNuGet.Configの専用キャッシュを使用する。
dotnet restore AIConversationStudio.sln --locked-mode

# 環境変数が残っている場合も、明示した空の専用キャッシュで検証できる。
dotnet restore AIConversationStudio.sln --locked-mode --packages .nuget/fresh-check
```

## 検証範囲

Linuxで新しい専用キャッシュによるlocked-modeの全ソリューション復元を実行した。
Windows実行結果は未確認であり、今回追加したwindows-latestのCI成功を確認してからマージする。
この調査だけをもってWindows実機で成功済みとはしない。

最終確認：nuget.orgの別の空キャッシュによる全ソリューションのlocked-mode復元成功、
Release build成功（警告0・エラー0）、既存128テスト成功（失敗0・スキップ0）。
NuGet.Configの通常経路でもlocked-modeが成功し、専用キャッシュの取得元がnuget.orgで、
全5ロックのcontentHashと一致することを確認した。
