# Codeer.LowCode.Blazor.Connectors.NetSuite - Project Guide

## プロジェクト概要

Codeer.LowCode.Blazor (CLB) のモジュールを Oracle NetSuite のレコードに結び付けるコネクター。
NuGet パッケージ `Codeer.LowCode.Blazor.Connectors.NetSuite.Server` / `Codeer.LowCode.Blazor.Connectors.NetSuite.Designer` / `Codeer.LowCode.Blazor.Connectors.NetSuite` (画面の添付ファイルのフィールド) (MIT) として配布する。

- **ターゲット**: .NET 8.0 / C# (Nullable 有効, ImplicitUsings 有効)
- **本体リポジトリ**: `C:\tfs\codeer\Codeer.LowCode.Blazor` (参照用、直接編集しない)。本体は NuGet パッケージで参照する
- **作りの手本**: `C:\GitHub\Codeer.LowCode.Blazor.Extras`
- **利用者向けドキュメント**: `Docs/NetSuite.md`
- **TODO**: `Docs/TODO.md` (これから対応する予定のもの。TODO はこのファイルに置いて Git で管理する)
- **調査資料**: `Docs/01_netsuite_api_inventory.md` (API 調査) / `Docs/02_netsuite_specification_draft.md` (仕様案) / `Docs/03_research_request.md` (追加調査の依頼) / `Docs/04_research_request_design.md` (作りの分岐を決めるための調査の依頼) / `Docs/05_design_research_results.md` (04 の調査の回答) / `Docs/06_sandbox_verification_plan.md` (Sandbox で確かめることの計画) / `Docs/07_connector_review.md` (2026-11-16 の外部レビュー。6 件の指摘。対応状況は末尾)

## リポジトリ構成

```
Source/
├── Codeer.LowCode.Blazor.Connectors.NetSuite.slnx
├── Codeer.LowCode.Blazor.Connectors.NetSuite.Server/ # サーバー側ライブラリ (WPF・ASP.NET に依存しない)
│   ├── NetSuiteDbAccessor.cs          # IDbAccessor のデコレータ (公開の入口)
│   ├── NetSuiteConnection.cs          # 接続 1 つ: 認証・送信 (再試行・エラー分類)・定義の取得 (メタ情報の更新のときだけ)・SuiteQL・レコード操作
│   ├── NetSuiteMetadataFile.cs        # メタ情報のファイル (デザインのサーバー専用ファイル) の書き出しと読み込み。FromProject / FromDeployedDesign / UpdateAsync
│   ├── NetSuiteConnectionSettings.cs  # ConnectionString "Provider=NetSuite;..." の読み取り
│   ├── NetSuiteRecordDefinition.cs    # REST メタデータ (JSON Schema) → 列の定義
│   ├── SuiteQlTranslator.cs           # 本体の SQL → SuiteQL / レコード操作
│   ├── NetSuiteSublistJson.cs         # 明細の列の値 (行の JSON 配列) と REST の明細の本文の変換・更新の差分
│   ├── NetSuiteAttachments.cs         # 添付の列 (files) の値の解釈 (足す base64 / 外す deleted)・読んだファイルの JSON
│   ├── NetSuiteAttachmentFile.cs      # ダウンロードの結果 (GetAttachmentAsync が返す)
│   └── NetSuiteException.cs
├── Codeer.LowCode.Blazor.Connectors.NetSuite/   # 画面用 (Razor クラスライブラリ・net8)。取引の添付ファイルのフィールド (Kintone の添付と同じ作り)
│   ├── NetSuiteAttachmentsFieldDesign.cs / NetSuiteAttachmentsFieldData.cs / NetSuiteAttachmentsField.cs / NetSuiteAttachmentsFieldComponent.razor
│   └── NetSuiteFieldsInitializer.cs   # Initialize() = アセンブリを読み込む (クライアント・サーバーの起動時。デザイナは .Designer の Initialize が呼ぶ)
├── Codeer.LowCode.Blazor.Connectors.NetSuite.Designer/   # デザイナ用 (WPF)。AI 用の仕様ドキュメントの登録・メタ情報の更新メニュー・デザイナ用の IDbAccessor
│   ├── NetSuiteDesignerInitializer.cs # Initialize(env) = SpecDocs の登録 / Setup(env) = Tools のメニュー / CreateDbAccessor (開いているプロジェクトのメタ情報のファイルから読む)
│   └── SpecDocs/NetSuite.md           # AI が NetSuite のモジュールを設計するための仕様 (デザイン側のことだけを書く)
├── NetSuite.Mock/   # NetSuite のモック (ASP.NET Core・SQLite のインメモリ)。利用者が手元で起動して試す用。パッケージにしない
│   ├── MockNetSuite.cs                # 定義・データ・トークンを持ち、REST の要求に応える
│   ├── MockNetSuiteEndpoints.cs       # /services/rest/... の結び付け (MapMockNetSuite。テストは TestServer で同じものを立てる)
│   ├── MockSuiteQl.cs                 # SuiteQL を SQLite で動かす (文の確認・ROWNUM / NUMTODSINTERVAL の書き換え・Oracle の関数)
│   ├── MockRecordType.cs              # mock-data.json の読み取りと値の変換
│   ├── MockFileCabinet.cs             # File Cabinet (登録・属性・中身)・!attach / !detach・保存検索 (取引に付いたファイル。ページ送り)
│   ├── MockOidc.cs                    # アプリのログイン用の OpenID Connect のプロバイダ (discovery・認可の画面・認可コード + PKCE・JWKS・PS256 の ID トークン)。相手は employee の行
│   └── mock-data.json                 # 既定の定義と初期データ (架空のサンプル)。利用者向けの説明は Docs/NetSuiteMock.md
├── Codeer.LowCode.Blazor.Connectors.NetSuite.Test/   # NUnit。HTTP は偽物 (Harness/FakeNetSuite) とモック (Harness/MockHarness = モック + 本物のコネクター + 本体の ModuleDataIO)
│                                    # Extras.Server を参照するのはテストだけ (MockLoginAccountTest = Extras の LoginAccountStore の SQL がコネクターを通るか)
├── Example/                         # サンプル (パッケージにしない)。動かし方は Docs/Example.md
│   ├── LowCodeApp.Designer/         # コネクターを組み込んだデザイナ (WPF・net8)。結線の見本 (App.xaml.cs / DbAccessorFactory.cs) 兼 GUI テストの対象。Design の AppUser のため Extras.Designer も組み込む
│   ├── LowCodeApp.Server/ LowCodeApp.Client/ LowCodeApp.Client.Shared/   # Web アプリ (net10)。Starter の Cookie ホストを StarterTool export-app で書き出したもの
│   │                                # テンプレートとの違い: DataService.cs の NetSuiteDbAccessor の結線 (メタ情報は配られたデザインから・NetSuite プロパティ)・.Server と無印の ProjectReference・
│   │                                # 添付ファイル (NetSuiteFieldsInitializer の呼び出し 2 か所・Controllers/NetSuiteAttachmentsController.cs・ExceptionHandlerUtils の添付の文言)・appsettings (Main = SQLite / NetSuite)・パッケージの版 (公開済みの版)
│   │                                # NetSuite の接続文字列は appsettings に書かない (環境変数 ConnectionStrings__NetSuite)
│   ├── Design/                      # デザインのプロジェクト。Customer / Subsidiary / CustomRecordSample / Item / SalesOrder (+ 行 SalesOrderItem。添付ファイル Files は詳細と一覧) / SalesOrderLine (明細の表) / Employee (NetSuite)・AppUser (SQLite。メールの列 = 外部ログイン名)・ServerFiles/netsuite.NetSuite.metadata.json (モックから書き出したメタ情報。テストが一致を確かめる)
│   └── Data/main.sql                # ログインするユーザーの表と admin / admin・NetSuite でだけ入るユーザー (モックの従業員 1 のメール)
├── Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest/   # NUnit のブラウザテスト (ヘッドレス Chrome)。Example の Web アプリを別プロセスで起動して操作する
│   ├── Harness/MockServer.cs          # モックをこのプロセスの Kestrel で 127.0.0.1 の空きポートに立てる・モックのデータを SuiteQL で確かめる
│   ├── Harness/AppServer.cs           # 一時フォルダに鍵・SQLite・App.zip を作り、Example の Server を環境変数で設定を上書きして起動 (止めるのは自分が起動したプロセスだけ)
│   ├── PageObject/                    # 手書き (Example のデザイナには pageobject の CLI が無い)。モジュールのレイアウトを変えたら合わせる
│   └── Scenario/                      # 一覧 (リンク先の名前)・検索・作成/更新/削除・受注の明細の作成/変更/追加/削除と明細の表の検索 (SalesOrderTest)・受注の添付ファイル (SalesOrderAttachmentsTest)・NetSuite のアカウントでのログイン (NetSuiteLoginTest。モックを https で別に立てる)
└── Codeer.LowCode.Blazor.Connectors.NetSuite.DesignerTest/   # NUnit の GUI テスト。Example のデザイナを起動して Friendly で操作する
    ├── Harness/MockServer.cs          # モックを Kestrel で 127.0.0.1 の空きポートに立てる (要求を数える・同じポートで立て直す)。DeadPort = 届かない NetSuite
    ├── Harness/DesignProject.cs       # テストごとの一時フォルダ: 空のデザインプロジェクト (NetSuite のデータソースだけ) とその場で作った鍵
    └── Drivers/DesignerDriver.cs      # 起動 (引数で app.clprj を開く)・メニュー・トースト・MessageWindow・テーブルの定義・モジュール作成・デザインチェック
```

パッケージは 3 つ (Extras と同じ分け方): サーバーは `.Server`、デザイナは `.Designer` (`.Server` と無印に依存)、画面 (Client) は無印 `Codeer.LowCode.Blazor.Connectors.NetSuite` (添付ファイルのフィールド)。
`.Server` は無印を参照しない (添付のフィールドは型の名前の文字で見る。Kintone と同じ)。無印の `InternalsVisibleTo` はテストだけ。

## ビルド・テスト

```bash
cd Source
MSBUILDDISABLENODEREUSE=1 dotnet build Codeer.LowCode.Blazor.Connectors.NetSuite.Server -m:1
MSBUILDDISABLENODEREUSE=1 dotnet test Codeer.LowCode.Blazor.Connectors.NetSuite.Test -m:1
```

`.Designer` は WPF なので dotnet CLI ではなく VS の MSBuild でビルドする (`MSBuild.exe <csproj> -restore -m:1`)。

### GUI テスト (DesignerTest)

`Example/LowCodeApp.Designer` と `DesignerTest` も WPF なので VS の MSBuild でビルドする (DesignerTest をビルドすると Example のデザイナも先にビルドされる)。
テストの実行は別コマンドで `dotnet test --no-build`。

```bash
cd Source
MSBUILDDISABLENODEREUSE=1 "C:/Program Files/Microsoft Visual Studio/2022/Professional/MSBuild/Current/Bin/MSBuild.exe" Codeer.LowCode.Blazor.Connectors.NetSuite.DesignerTest/Codeer.LowCode.Blazor.Connectors.NetSuite.DesignerTest.csproj -restore -m:1 -v:m -p:UseSharedCompilation=false
dotnet test Codeer.LowCode.Blazor.Connectors.NetSuite.DesignerTest/bin/Debug/net8.0-windows/Codeer.LowCode.Blazor.Connectors.NetSuite.DesignerTest.dll --no-build
```

- 実行するとデザイナの画面が実際に立ち上がる (テストごとに起動して閉じる)。操作中はマウス・キーボードに触らない
- CLB のライセンスを登録したマシンで動かす (起動時のライセンス確認を通る前提)
- モックはテストのプロセスの中で Kestrel に立てる (127.0.0.1 の空きポート・テストの間だけ)。デザインのプロジェクトと鍵はテストごとの一時フォルダ
- 1 回の起動に上限 (120 秒) があり、超えるとそのテストが起動したデザイナだけを止める (プロセス名で一括に止めない)
- WebView (Blazor) の中は WPF のラッパーで触れないので、テーブルからのモジュール作成とデザインチェックは対象プロセスの中の API を直接呼ぶ
  (作成は DB から取り込むときと同じ変換 `DbColumnFieldConverter.ConvertForModule` + デザイナの保存。Example は最小構成で、Designer.Standard の「DB から取り込む」メニューは無い)

### ブラウザのテスト (SeleniumTest)

```bash
cd Source
MSBUILDDISABLENODEREUSE=1 dotnet build Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest -m:1
dotnet test Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest --no-build
```

- ビルドすると Example の Server も同じ構成 (Debug / Release) でビルドされ、テストはその出力 (`bin/<構成>/net10.0`) を `dotnet` で起動する
- 一覧・検索はモックの初期データ (mock-data.json) を前提にしている
- 画面の変化は条件待ち (上限 30 秒 = `WebDriverManager.WaitTimeout`)。超えたらフリーズとして失敗にする。失敗するとスクリーンショットとサーバーの出力が残る
- ログインのテストはモックを https (ASP.NET Core の開発用証明書) で立てる。`dotnet dev-certs https --trust` 済みのマシンで動かす (OpenID Connect の discovery は https でしか読まれない)
- ログインの方式 2 (ユーザーモジュール = Employee) は `[Ignore]`。Example の `ExternalLoginUserResolver` などが `LoginAccountStore.Create(design, dataService.DbAccess)` に包む前の `DbAccessor` を渡すため
  (`DbAccess` の型を `IDbAccessor` にすると `ModuleDataController` の `DbAccess.ClearAsync()` がコンパイルできない)

## 設計: NetSuite を IDbAccessor として見せる

本体の `ModuleDataIO` は DB 処理 (`ModuleDataIOForDb`) を直接持っていて、データソースごとに処理を差し替える口は無い。
一方 `IDbAccessor` は公開のインターフェースで、ホストが実装を渡す。デザイナも `IDbAccessorFactory` 経由で同じものを使う。
そこで NetSuite 用のデータソースだけを横取りするデコレータ (`NetSuiteDbAccessor`) にした。**本体への変更は無い。**

```
ModuleDataIO (認可・インターセプタ・一覧/保存の組み立て = 本体のまま)
  └─ ModuleDataIOForDb が SQL を組み立てる (NetSuite 用データソースは Oracle 方言)
       └─ NetSuiteDbAccessor
            ├─ NetSuite のデータソース → SuiteQlTranslator → NetSuiteConnection (REST)
            └─ それ以外 → 包んだ IDbAccessor (DbAccessor)
```

この形で得られるもの:

- 認可 (UserRead / DataRead 条件・フィールド権限)・インターセプタ (監査・編集履歴)・サーバー側スクリプト・リンクの結合が、DB のモジュールと同じ経路で効く。
  ブラウザから来るのは通常の検索条件と保存データだけで、SuiteQL・URL・レコード種別を直接指定する口は無い
- デザイナの「テーブルからモジュール作成」・列のツリー・接続確認が、`GetCustomTableDefinitionsAsync` を返すだけで動く
- 設定は既存の DataSources に乗る (`ConnectionString` が `Provider=NetSuite;...`)。本体の設定の型もデザイナの UI も変えない

### SQL の読み替え (SuiteQlTranslator)

`GetDataSource` で `DataSourceType.Oracle` を返すので、本体は Oracle 方言で SQL を組む (SuiteQL は Oracle の書き方を受け付ける)。
本体が組む形だけを受け付け、それ以外 (`;` を含む文・知らない形の書き込み) は拒否する。

| 本体の SQL | NetSuite |
|---|---|
| `select ... from "t" left join ... where ... order by ... offset N rows fetch next M rows only` | SuiteQL。引用符を外す・ページング句は URL の limit / offset へ・日付と日時の列は `TO_CHAR` で決まった形に・ページングつきなら並びの最後に `主表.id` (明細の表は `主表.uniquekey`) |
| `BEGIN INSERT INTO "t"(...) VALUES (:p0,...) RETURNING "id" INTO :returning_id; END;` | `POST record/v1/t`。Location の末尾が Id (`args[":returning_id"]` にも入れる = 本体はそこから読む) |
| `update "t" set "c"=:p0,... where "id"=:pid0 [and ...]` | `PATCH record/v1/t/{id}`。追加条件があれば先に `select count(*)` で確かめる |
| `delete from "t" where "id"=:pid0 [and ...]` | `DELETE record/v1/t/{id}` |
| `BEGIN INSERT ...; INSERT ...; END;` (一括) | 1 件ずつ POST |

- パラメータ: 文字は `?` + `params` (値はすべて JSON の文字列)。数値・真偽 (`'T'` / `'F'`)・日付 (`TO_DATE(...)`) は決まった書式のリテラルにして埋める
  (利用者の文字列は埋めない)。LIKE のエスケープ文字は本体の制御文字 (U+0001) から `\` に置き換える
- 表は、どの階層 (副問い合わせ・結合) でもメタ情報のファイルにあるもの (レコードと取引の明細の表) だけ。無ければ要求を送らずに `NetSuiteException` (`NotFound`。メタ情報の更新・デザインの配り直しを案内)
- 結果: SuiteQL は値を文字列で返し、null の列を省く。select の項目を読んで、本体が付けた別名をキーに、列の種類に合わせた型で返す (本体は別名の大文字・小文字まで一致で探す)
- 取引のレコード (salesorder など): from / join の表を `(select * from transaction where type = '<種別>') <別名>` に置き換える。
  種別は組み込みの対応表 (`SuiteQlTranslator._transactionTypes`。salesorder = SalesOrd ほか 16 種・大文字小文字を区別しない)。メタ情報の名前・定義・書き込みは REST のレコード名 (小文字) のまま
- 明細の列 (`NetSuiteFieldKind.Sublist`。下記): SuiteQL の select から外し、主表の id を必ず読む (本体が select していなければ `主表.id as nsxid` を足して結果からは外す)。
  行ごとに `GET record/v1/{type}/{id}?expandSubResources=true` を同時実行の枠の中で並べて送り、明細を行の JSON 配列 (文字列) にして返す。
  主表の select の項目に素の列として書かれたときだけ受け付け、結合先の表・条件・並び・集計に出たら `NetSuiteException` (`SublistNotSupported`)
- 明細の表 (`<取引>_<明細の列>`。下記): from / join の表を
  `(select tl.* from transactionline tl inner join transaction t on t.id = tl.transaction where t.type = '<種別>' and tl.mainline = 'F' and tl.taxline = 'F') <別名>` に置き換える。
  書き込み (insert / update / delete の対象) は `NetSuiteException` (`NotSupported`。読み取り専用・親の明細の列で書けと言う)

明細の表 (明細をまたぐ検索・集計用。読み取り専用):

- 名前は `<取引のレコード名>_<明細の列名>` の小文字 (`salesorder_item`)。親が取引の対応表にあり、その明細が列 (`Sublist`) のとき、メタ情報の更新で親と一緒にファイルに入る
- メタ情報の更新で、明細の表と同じ名前のレコードがあれば明細の表を採る (`_` で割らないので `customrecord_x` のような名前とは突き合わない)
- 列: `uniquekey` (文字列。Id に結ぶ)・`transaction` (参照。親)・`linesequencenumber` (整数)・行の項目 (REST 名の小文字。`line` は除く)
- `GetCustomTableDefinitionsAsync` には親の後ろに並べる。メタ情報のファイルには `Parent` / `Sublist` つきで保存する

明細の列 (親の列として見せる明細):

| 本体 | NetSuite |
|---|---|
| 読み出し (select の項目) | 上記。値は `[{"line":1,"item":"5","item_refName":"...","quantity":2,...}, ...]` |
| 作成 (INSERT の値) | POST の本文に `"<明細>":{"items":[...]}`。定義にある行の項目だけを変換 (参照 → `{"id"}`・時差の無い日時は UTC として `Z`)。`_refName`・定義に無いキー・`line`・null は送らない。空の配列 / null なら明細ごと省く |
| 更新 (UPDATE の値) | 今のレコードを GET して `line` で突き合わせる。変わった行は `line` + 変わった項目 (空にした項目は null)、`line` の無い行は新しい行。消えた行があれば `?replace=<明細>` にして残す行を全部並べる (変わっていない行は `{"line":n}`)。差が無ければ明細を送らず、何も変わらなければ PATCH しない (1 件更新として返す)。本体の項目と明細は 1 回の PATCH |

- 行の JSON のキーは REST の項目名そのまま。日付は `yyyy-MM-dd`、日時は REST の値のまま (`Z` つき)、参照はキーに id の文字列 + `<キー>_refName` に表示名。`line` はそのまま。行の中の入れ子 (サブレコード・明細)・links は入れない
- 比べるときは揃えてから: 参照は id、数値は decimal、日時は同じ瞬間か、日付は日
- 今の明細に無い `line` の行・同じ `line` の 2 つ目は新しい行として送る (別の画面で消された行を戻す = 後勝ち)
- 後勝ち: 読んでから保存するまでに NetSuite で変わった明細は、送った内容で上書きされる (送らなかった行・項目はそのまま。消えた行があるときは replace なので、その間に足された行も消える)
- **明細の列のあるモジュールに AutoSubmitField を置かない** (制限。Docs・SpecDocs に書いた。2026-11-20 に画面のモジュール + 本体の ModuleDataIO + モックで再現: 行を足して自動保存 → 項目を変えて自動保存で、明細が 1 → 2 → 3 行)。
  本体の軽い保存はレコードを読み直さず、列モードの ListField は保存後も行を変更ありのまま残して次も明細の全量を送る (本体 `ListField.AcceptChanges` のデータオーナーモード)。
  足した行は `line` を持たないので次の保存でまた新しい行になる。防げない理由: 行の同一性は NetSuite が付ける `line` だけで、それまでは「前に足した行の再送」と「同じ内容の行をもう 1 つ足した」を見分けられない
  (同じ値の今の行を探して省く推定は、利用者が同じ品目・数量の行を本当に 2 つ足したときに行を落とす)。行に識別子を持たせるには NetSuite 側に行の項目を足す必要がある

本体が組む SQL の形が変わるとここが壊れる。`Test/NetSuiteDbAccessorTest` は本体の `ModuleDataIO` を実際に通しているので、本体のパッケージを上げたらこのテストで確かめる。

### 取引の添付ファイル (files)

取引のレコードに付いた File Cabinet のファイルを、取引の仮想の列 `files` (`NetSuiteFieldKind.Attachments`) として見せる。使うのは標準の REST だけ (RESTlet・SOAP は使わない)。

| 本体 | NetSuite |
|---|---|
| 読み出し (select の項目) | SuiteQL からは外し (主表の id を読む。明細と同じ `nsxid`)、読んだ行の全部を **保存検索 1 回** (`GET query/v1/savedsearch/{id}/result?limit=&offset=` を `hasMore` が false になるまで) で引き当てる。値は `[{"id","name","size","type"}]` (size・type は列の設定があるときだけ)、無ければ null。結果は持っておかない |
| 作成 (INSERT の値) | レコードを POST してから、`base64` の要素を 1 つずつ `POST document/v1/file/` (multipart: `attributes` = `{"isLink":false,"fileName","folder":{"id"}}` + `data`。204 の `x-ns-link` の末尾がファイルの ID) → `POST record/v1/{type}/{id}/!attach/file/{fileId}`。`deleted` の要素は見ない |
| 更新 (UPDATE の値) | 本文を PATCH (変わった項目が無ければ送らないが、レコードがあることを確かめる) → 足す (作成と同じ) → 外す (`!detach`)。外すのは、保存の直前に保存検索で読んだ「そのレコードのファイル」に ID が一致するものだけ |
| 削除 | 何もしない (File Cabinet のファイルは残る) |
| ダウンロード | `NetSuiteDbAccessor.GetAttachmentAsync(io, designData, module, id, field, fileId)`: 型の名前・DbColumn が添付の列・Id の DbColumn が `id`・保存検索の設定を確かめ、`io.GetListAsync` (Id の一致・SelectFields はそのフィールド = 本体の行と項目の権限が効く) で読んだ値にその fileId があるときだけ `GET document/v1/file/{id}/content`。どこで失敗しても null |

- 列は取引の対応表にあるレコードだけ (明細の表・取引でないレコードには無い)。メタ情報のファイルには書かず、ファイルを読むときに足す (`AddAttachmentsField`。同じ名前の項目があれば足さない)。`RawDbTypeName` は `netsuite_files`、.NET の型は string
- 本体は値が null の項目を読んだデータに入れない (ファイルの無い取引の `Fields` には添付のキーが無い)
- 条件・並び・集計・結合先の表に出たらエラー (明細と同じ `SublistNotSupported` の形で "attachments column")
- 値の形が違う (配列でない・名前が無い・名前に制御文字・base64 でない・空のファイル・外す ID が数でない) ときは何も書かずに `InvalidRequest`。
  multipart の data のファイル名は `"`・`\`・制御文字を `_` にした名前 (本当の名前は attributes の `fileName`)。足すファイルがあるのに `AttachmentFolderId` が無ければ、何も書く前にエラー
- 413 (10 MB 超) は `InvalidRequest` で「10 MB」と言う。`x-ns-link` が無ければ `WriteResultUnknown`
- 途中の失敗: 書き込みはやり直さず、登録したファイルも消さない。NetSuiteException 以外の例外 (取り消しを含む) も結果不明 (`WriteResultUnknown`) として同じエラーに包む。`NetSuiteException.RecordId` (取引の ID)・`UnattachedFileIds` (登録したが添付していない / 添付できたか分からないファイル)・メッセージに添付できたファイル・送っていない数。
  一括の追加ではそのレコードも `CreatedIds` に入れる (1 件目でも、続きがあれば一括の追加のエラーにして送っていない件数を言う)。Example の `ExceptionHandlerUtils` が「開き直して確かめ、足りない分だけ足し直す」の文言にする
- 保存検索の行: 親の列が無い行はエラー (列の名前の設定の誤りを 0 件にしない)・ファイルの ID の無い行は飛ばす・同じ (親, ファイル) は 1 つに。列は大文字小文字を区別せずに探す。値が `{id, refName}` なら名前・種類は refName、ID は id。
  失敗・`items` が無い・続きありで 0 行・1000 ページ超はエラー (0 件にしない)

画面のフィールド (無印のパッケージ) は Kintone の `KintoneAttachmentsField` の写し (fileKey → id・「消す」→「外す」)。`AcceptChanges` (読み直さない保存の後) も同じ: 送った追加は名前と大きさだけにし (ID が無いので外せない・ダウンロードできない)、送った外す分は消す。
送った追加を送信の間に画面で取り消していても、サーバーには付いたので名前だけの行として出す。

### 名前の対応

- 表名 (モジュールの DbTable) = REST のレコード名
- 列名 (DbColumn) = REST の項目名の小文字。SuiteQL にはそのまま使い、書き込みでは定義 (`NetSuiteFieldDefinition.RestName`) で REST の名前に戻す
- 取引のレコードも表名は REST のレコード名。SuiteQL の `transaction` 表と種別の条件はコネクターが読み替える (上記)。対応表に無い取引は SuiteQL で `<レコード名>` の表として送る
- 明細 = 親の列。列名は REST の明細名の小文字、.NET の型は string、`RawDbTypeName` は `CLOB`。行の項目は列にせず、行の JSON のキー (REST の名前のまま) にする
- 明細の表 = `<取引のレコード名>_<明細の列名>` (小文字)。列名は `transactionline` の列名 = 行の REST の項目名の小文字 (未検証。下記)
- 明細として扱うのは、行に `line` の項目がある明細だけ。メタデータはその場に書かれた形 (`{properties:{items:{type:array,items:{properties}}}}`) と `$ref` (…Collection → items → …Element) を受け、
  `$ref` は同じ metadata-catalog から読む (たどる回数に上限 4)。読めなければ列にしない。メタ情報のファイルにも行の項目を保存する (`Fields`。無い古いファイルも読める)

### 接続 (NetSuiteConnection)

- 長生きのオブジェクト (トークン・同時実行の枠を持つ)。`FromDataSources` は設定が同じなら同じ接続を返す。`NetSuiteDbAccessor` は要求ごとに作ってよい
- 定義は持たない。実行時の定義はメタ情報のファイル (下記) から読み、NetSuite に取りに行くのはメタ情報の更新 (`FetchRecordDefinitionsAsync`) のときだけ

### メタ情報のファイル (NetSuiteMetadataFile)

- `netsuite.<データソース名>.metadata.json` (名前は文字 (日本語なども含む)・数字と `_` `-` `.` 以外を `_` に。OS に依らない)。デザインのプロジェクトのサーバー専用ファイル (core の `WriteProjectServerFile` / `ReadProjectServerFile`)。
  App.zip に入って配られ、Web アプリは `GetServerFile` で読む (画面へ送るデザインには載らない)。中身はレコードと項目の定義だけ (接続の情報・鍵は入らない)
- 出所は 2 つ: `FromProject(プロジェクトのフォルダ)` (デザイナ) / `FromDeployedDesign(デザインの配置先 = DesignFileDirectory)` (Web アプリ)。どちらも必須 (NetSuite から直接読む口は無い)
- 書き出し `UpdateAsync(接続, プロジェクトのフォルダ)`: レコード種別の一覧 (`GET record/v1/metadata-catalog`) の全部から、SuiteQL で読めるレコード (`select id from <名前>` を 1 件で試す) と取引の対応表にある取引を取り込む。
  表名は一覧の名前の小文字。定義が読めない・SuiteQL に表が無い (400 / 403 / 404) ものは飛ばし、それ以外の失敗は止めて前のファイルを残す。名前順・明細の表は親の後ろ。同じファイルへの更新はプロセスの中で順に
- 読み込みの持ち方: 出所 + ファイルごとに解釈した結果を static に 1 つ持つ (変えない値なので並行に読んでよい)。配られたデザインは App.zip の更新日時と大きさが変わったときだけ開き直す (要求ごとに zip を開かない)。
  プロジェクトはファイルの中身を読んで前と同じなら解釈し直さない。アクセサは 1 回の操作の中で同じ中身を使う
- ファイルが無ければ表は 0 件。ファイルに無い表を求めるとエラー (デザイナ = メタ情報の更新を、Web アプリ = 更新してデザインを配り直すことを案内)
- 本体も表の定義を static に持つので、Web アプリで項目の変化を反映するには配り直しのあとアプリの再起動が要ることがある (未確認)
- 再試行: 読み出しは 429 / 502 / 503 / 504 / 通信失敗を 3 回まで。書き込みは 429 だけ。401 はトークンを取り直して 1 回。
  書き込みを送ったあとの通信失敗と、書き込みへの 503 以外の 5xx は `WriteResultUnknown` (実行されたか分からないので、やり直さない)
- 例外に秘密鍵・トークン・接続文字列を入れない

## 決めてあること (2026-11-13)

仕様の自然さで決めた。蒸し返すときは理由ごと見直す。

- **保存はデータソースごとに確定する。1 回の保存に NetSuite のレコードが複数ある・DB と混ざる要求を、書く前に拒否しない。**
  本体の `DbAccessor` はデータソースごとに別のトランザクションを順にコミットするので、DB を 2 つ使う保存も全体では原子的でない。
  NetSuite は「1 件ごとに確定するデータソース」として同じ並びに置く。拒否すると、NetSuite のレコード + DB の編集履歴・監査のような普通の組み合わせが使えなくなる。
  補償 (失敗したら作ったレコードを消す・値を戻す) はしない (補償自体が失敗しうる・消したレコードは同じ ID で戻せない)
- **メタ情報はファイルに一本化し、`Records` (扱うレコードの指定) は無くした** (ユーザー指示 2026-11-13)。メタ情報の更新でアカウントのレコードを全部取り込む (遅くてよい)。
  対応表に無い取引は SuiteQL で読めないので出さない
- **`DataSourceType` は `Oracle` と書かせる。** 本体の enum に NetSuite を足さない (本体は NetSuite を知らないまま = IDbAccessor の差し替えだけで成り立つ形を保つ)
- **モジュールの Id は常に internal ID。** 外部 ID を鍵にした Upsert は提供しない
- **集計は DB のモジュールと同じに動かす** (本体の `AggregateAsync` の SQL を読み替える)
- **OptimisticLockingField は対象外。** REST に条件つきの更新が無い。Id 以外の条件が付いた更新・削除は、先に読んで確かめるだけ (確かめてから書くまでの間は防げない)
- **デザイナも Web アプリもメタ情報をファイルから読む** (ユーザー指示 2026-11-13)。プロジェクトのサーバー専用ファイル `ServerFiles/netsuite.<データソース名>.metadata.json` (`NetSuiteMetadataFile`)。
  NetSuite に取りに行くのは Tools > NetSuite のメタ情報を更新 のときだけ。ファイルが無ければ表は 0 件 (自動では取りに行かない)。
  狙いは「プロジェクトを開くたびの重い取得を無くす」「オフライン・鍵なしでもデザインできる」「実行時に定義を取りに行かない」。headless の CLI も同じファイルを読む (更新の CLI は無い)。
  Web アプリはデザインと一緒に配られたファイルを読む (メタ情報を変えたらデザインを配り直す)
- **必須の項目も、列の定義は null 可で見せる。** NOT NULL にすると本体 (`DbDataConverter`) が空の値を型の既定値 (0・0001-01-01・false) に置き換えて書いてしまう。必須は NetSuite が保存時に確かめる
- **Starter (ホストのテンプレート) には組み込まない。** 特定の外部サービス向けなので、使うアプリが 2 行足す
- **取引のレコード: 利用者が書く表名は常に REST のレコード名。** SuiteQL の `transaction` 表・種別の条件との違いはコネクターが吸収する
- **明細 (サブリスト) は親の列として見せる。** 本体の列モードの一覧 (`ListFieldDesign.DbColumn` = 明細の列) で親のモジュールの中に並べ、行の JSON と REST の明細の本文の変換はコネクターがする。
  明細をまたぐ検索・集計は、読み取り専用の明細の表 `<取引>_<明細の列>` (`salesorder_item`。SuiteQL の `transactionline` を読む) を別に見せて行う。
  書き込みは親の明細の列だけ (明細の表に書くとエラー)。取引でないレコードの明細には表を作らない (SuiteQL で読む表が無い)
- **明細の表は `SuiteQlTranslator._lineSublists` に載せた取引の `item` だけ** (2026-11-20。レビュー指摘 2)。`transactionline` は明細の種類で行を分けずに読むので、
  明細の種類が 1 つに決まる取引だけにした。品目と経費の両方を持つ取引 (発注・仕入請求など)・仕訳・ほかの明細は表にしない (親の明細の列としては読み書きできる)。
  前の版が書いた余計な明細の表はファイルから読まない (`NetSuiteMetadataFile.Parse`)
- **明細の同時編集** (2026-11-20。レビュー指摘 1): 読んだ行に、読んだときの `line` の並びを `_readLines` として入れる (本体は結び付けていないキーを保存まで残す)。
  保存のとき、読んだ後にほかで足された行は残す (消す行があるときの replace に `{"line":n}` で付ける)・ほかで消された行を残した / 変えた保存は `Conflict` (何も送らない)・
  同じ `line` が 2 つあれば入力のエラー。同じ行の同じ項目は後勝ち。読んだときの行が分からない (読んだ明細が空・全部消した) ときは、要求の行が明細の全部になる (制限として文書に書いた)
- **選択肢: 相手がレコードなら参照 (いまと同じ)。固定の選択肢はメタ情報のファイルの項目の `Choices: [{Value}]` に持ち、SpecDocs で AI に Enum の定義へ写させる** (2026-11-20。Pleasanter / Kintone と同じ見せ方)。
  読むのはメタデータの文字の項目の `enum` と、選択の項目 (`{properties:{id, refName}}`。`$ref` の先も) の `properties.id.enum`。表示名はメタデータに無いので `DisplayText` は持たない。
  選択の項目は Reference として `{"id"}` で書く。実行時には使わない (値の確かめは NetSuite に任せる)。
  本体の `DbColumnDefinition` に選択肢を持つ口が無いので、デザイナのテーブルの定義には出ない (Pleasanter と同じく AI がファイルを読む)
- **読み取り専用の項目はメタデータの `readOnly: true` で見分け、ファイルに `ReadOnly: true` で持つ** (2026-11-20)。作成では送らない・更新の対象に入っていたら送る前にエラー (Pleasanter と同じ。
  作成で送らないのは、レコードのコピーが作成日時などを写しても保存できるようにするため)。明細の行の項目は、足した行でも今ある行でも送らない (エラーにしない)。明細は行ごと全部の値が送られてくるので、
  触ったのか、ほかの人・NetSuite の再計算で変わったのかを見分けられない (変わっていた値をエラーにすると、触っていない利用者の保存が通らなくなる)
- **一括の追加の途中失敗は `NetSuiteException.CreatedIds` に作成済みの internal ID を入れる** (2026-11-20。レビュー指摘 3)。メッセージにも作成済み・失敗した 1 件 (結果不明ならそう書く)・送っていない件数。
  Example の `ExceptionHandlerUtils` が本番で Kind ごとの日本語の定型文 + 作成済みの ID + リクエスト Id にする。書き込みへの 503 は `ServiceUnavailable` のままにし、Example の文言で「保存されていないことを確かめてから」と書く
  (処理が始まっていないと言い切れる資料が無い)
- **アプリのログインに NetSuite のアカウントを使うのは、Extras の汎用 OpenID Connect (`OidcLogins` に 1 件)。** コネクターに NetSuite 専用のログインのクラスを作らない・ホストのコードを変えない。
  方式 1 = ユーザーモジュールは DB のまま、メールを契約の `ExternalLoginName` と突き合わせる。方式 2 = ユーザーモジュールを NetSuite の `employee` にする (`LoginName` = メール。パスワード・有効フラグ・セッションの印なし。
  セッションの印は Extras が Id の列の型を DB に直接つないで調べるので使えない)。方式 2 はホストが `LoginAccountStore` に `NetSuiteDbAccessor` で包んだものを渡す必要がある。
  Example は `DataService.ModuleDbAccess` (包んだもの) を `ModuleDataIO`・`LoginAccountStore`・`PasswordLogin`・Extras の機能に渡し、一時ファイル・`ClearAsync`・Dispose は具象の `DbAccess` のまま (2026-11-20。Extras の変更は要らなかった)

- **取引の添付ファイル (2026-11-20 オーナー方針)**: 標準の REST だけ。外すは `!detach` だけで、File Cabinet のファイルを消さない・差し替えない。親の削除でもファイルに何もしない。
  一覧は利用者が NetSuite に作った保存検索を最後のページまで実行してサーバーで絞る (一覧の 1 ページで 1 回・キャッシュなし)。保存検索が無ければ足すだけ。
  読み直さない保存の後は名前だけにして、一覧を通らないダウンロードの道を作らない。ダウンロードは保存検索にその親とそのファイルの行があるときだけ。
  クライアントの ID・大きさは信じない (外す ID は保存検索と突き合わせる・大きさはサーバーで base64 から決める)。途中の失敗はやり直さず、孤立したファイルを自動で消さない
- **添付の列は取引だけ** (2026-11-20): 保存検索は接続に 1 つで、取引の internal ID は取引の種類をまたいで重ならないので、結果の親の ID だけで取引を決められる。取引でないレコードは ID が種類ごとに振られるので混ざる
- **保存検索の列の名前は接続文字列に書かせる** (2026-11-20): REST の結果で結合した列 (File の列) がどの名前で返るかの資料が無い。親の列だけ資料の例 (`internalid`) を既定にし、ファイルの ID と名前は必須
- **保存検索に無いファイルの ID を外す指定は黙って無視する** (2026-11-20): 別の画面で外された・ほかのレコードのファイル。保存の失敗にしない

## 守ること

- 本体は core のリポで変える (このリポから直接編集しない)。足りない口はユーザーに相談
- ホスト (Starter) に置くべきもの (Controller・DI の要求) をライブラリに持ち込まない。組み込みは `IDbAccessor` を包む 1 行
- 実アカウントで確かめていないことを「確認済み」と書かない
- 公開リポジトリ。秘密鍵・Client ID・アカウント ID などの実物をコード・テスト・ドキュメントに書かない (テストの鍵はテストの中で生成する)

## 未検証・未実装 (2026-11-13 時点。2026-11-20 に更新)

**NetSuite の実アカウントには一度もつないでいない。** テストは HTTP の偽物が相手。
実際の応答・構文について裏が取れていない前提は `Docs/03_research_request.md` に質問として並べてある (回答が来たらコードの前提と突き合わせる)。特に不確かなもの:

- JWT の `scope` クレームの形 (配列で送っている)
- メタデータの JSON Schema での参照・選択肢・明細の書かれ方 (`NetSuiteRecordDefinition.GetKind` の判定)。
  固定の選択肢 = 文字の項目の `enum` / 選択の項目の `properties.id.enum`、読み取り専用 = `readOnly: true` は、Oracle のヘルプ (REST の metadata は JSON Schema / OpenAPI 3.0) と検索の要約から決めた。
  Oracle のブログ・コミュニティは読めなかった (403 / タイムアウト)。実物の metadata-catalog で書かれ方を確かめる。
  根拠にした資料 (Oracle NetSuite のヘルプ。`https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/` の下): メタデータ `chapter_1540810168.html`・OpenAPI 3.0 のメタデータ `section_1545126526.html`・
  明細とサブレコードの形 `section_1545141947.html`・選択と参照の形 `section_1545142037.html`・複数選択の形 `section_1545142093.html`・列挙の値の形 `section_1545142146.html`
- SuiteQL の結果の形 (null の省略・値が文字列・キーの大文字小文字) と、`TO_CHAR` した日時の時刻帯 (UTC として扱っている)
- REST の項目名の小文字 = SuiteQL の列名、が成り立つ範囲
- 集計で本体が書く Oracle の式 (`TRUNC` / `ADD_MONTHS` / `NUMTODSINTERVAL`・派生表) を SuiteQL が受け付けるか
- 書き込みへの 5xx のうち、実行されていないと言えるもの (いまは 503 だけをそう扱っている)
- アプリのログイン (OpenID Connect。公開の仕様とモックだけで確かめた): ID トークンの `iss` (`https://system.netsuite.com` という情報) が discovery の `issuer` と違うと ASP.NET Core の検証で落ちる。
  PS256 の署名・`at_hash` の検証 (落ちるという未確認の報告あり)・返るクレーム (`email` が入るか・`email_verified` が無いか)・`response_mode=form_post` を受け付けるか。
  `iss` や `at_hash` で落ちたら、`OidcLoginProvider` を継承したクラスで `Configure` を上書きして検証を合わせる (そのときはコネクターかアプリのどちらに置くかを決める)

明細と取引のレコードは、モック (NetSuite.Mock) を相手にしか確かめていない。実アカウントで裏が取れていないもの:

- メタデータでの明細の書かれ方 (その場の形か `$ref` か・…Collection / …Element の名前・行に `line` が載るか)
- `?replace=<明細>` の動き (送らなかった行が消える・残す行を `{"line":n}` だけで送ってよいか)
- `line` だけの要素で行が変わらずに残るか
- SuiteQL が `(select * from transaction where type = '...') 別名` の派生表を受け付けるか (結合・集計・ページングの中でも)
- 取引の種別の文字列 (`SalesOrd` など対応表の 41 種。2026-11-20 に足した 25 種は公開の記事の一覧から取った値で、REST のレコード名との対応も確かめていない)
- 明細の表にした取引 (`_lineSublists`) で、`transactionline` の mainline / taxline でない行が品目の明細の行だけになるか (請求書の請求可能な経費・時間の明細などが混ざらないか)
- 明細の `_readLines` の前提: `line` が行を消しても使い回されない・読んだ後に足された行は読んだときより大きい番号になる、とは仮定していない (並びの集まりで見る) が、`line` が行の同一性を保つことは前提
- `expandSubResources=true` の応答の形 (行の参照が `{id, refName}`・日時が `Z` つき)
- 明細の表: 行の REST の項目名の小文字 = `transactionline` の列名 (`item`・`quantity`・`expectedshipdate` など)
- 明細の表: `mainline = 'F' and taxline = 'F'` で対象の明細の行だけになるか (取引に明細が複数あるとき・送料や小計などの行が混ざらないか)
- 明細の表: `transactionline` の数量・金額の符号 (取引の種別によって REST と逆になる行があるという情報がある)
- 明細の表: `uniquekey` が行ごとに一意で、行を足し消ししても変わらないか (Id とページングの並びの最後に使っている)

未実装 (何が分かれば実装できるか):

- 複数選択: メタデータでの書かれ方 (`items` の配列か) と、REST で書く本文の形 (`{"items":[{"id"}]}` か) が実物で分かれば、列 (文字列の並び) と書き込みを決められる
- サブレコード (住所など): 公開の資料どうしで PATCH で直接変えられるかが食い違う。実物で「親の PATCH に入れ子で書けるか」「行の中のサブレコード (在庫の詳細) の形」が分かれば実装できる
- 取引でないレコードの明細をまたぐ検索・集計: SuiteQL でその明細を読む表の名前・親への結び付きの列・行のキーが分かれば、明細の表と同じ形で足せる
- 明細の表の対象を増やす (経費の明細・発注など): `transactionline` で品目の行と経費の行を分ける列 (`itemtype` など) が分かれば、`_lineSublists` に条件ごと足せる
- 取引の追加: 一覧に無い取引は、REST のレコード名と `transaction.type` の値の組が分かれば `_transactionTypes` に 1 行足す
- 固定の選択肢の表示名: メタデータに無い。SuiteQL で選択肢の表示名を読む表が分かれば取り込める (いまは Enum に人が書く)
- 対象外と決めたもの: 外部 ID の Upsert、保存検索 (添付の一覧に使う以外)・Dataset・Action・Transform、File Cabinet のファイルの削除・差し替え、取引でないレコードの添付

取引の添付ファイルはモックだけで確かめた (`AttachmentsTest`・`AttachmentsFieldTest`・Selenium の `SalesOrderAttachmentsTest`)。実物で確かめること (`Docs/07_netsuite_connector_addendum.md` の §7):

- F01 登録→添付→取得: 日本語名の PDF で、作成 204 と `x-ns-link`・添付 204・NetSuite の画面の添付・取得したバイト列のハッシュが一致するか
- F02 共有・解除・削除: 同じファイルを A と B に付けて A から外したとき A からだけ消えるか (コネクターは本体を消さない)
- F03 既存一覧・ページング: 保存検索の REST の結果が画面の添付と一致し、ほかの親のファイルが混ざらないか。`limit` / `offset` を `savedsearch/{id}/result` が受け付けるか・ページをまたいで欠け・重なりが無いか。
  結果の列の名前 (結合した File の列がどう返るか)・大きさの列の単位 (バイトか KB か)・種類の列の形
- F04 権限: 連携用のロールに要る権限 (File Cabinet・取引の編集・保存検索の実行)・scope は `rest_webservices` だけで足りるか。読めないフォルダのファイルのダウンロードが 403 になるか
- F05 サイズ: 10 MB 前後の境界 (multipart の分を含むか)・413 の形・リンクのファイル (`isLink`) が保存検索に出たときの中身の取得
- F06 障害からの再開: 登録の応答の消失・添付の拒否・添付の応答の消失で、`RecordId` / `UnattachedFileIds` と文言が照合に足りるか
- F07 環境別: 受注以外の取引 (商談など) で保存検索・添付・外すが同じに動くか

## レビュー (Docs/07) の対応状況

- 6 (メタ情報の更新): 対応済み。書く前に解釈し直して確かめる・置き換えは core の `WriteProjectServerFile` (書き終えてから置き換える)・同じファイルへの更新はプロセスの中で順に・読む側は中身 (配られたデザインは App.zip) の変化で読み直す
- 1 (明細の削除の推定): 対応済み (2026-11-20)。「決めてあること」の明細の同時編集。受入試験の 3 つ (A の数量変更と B の新規行が残る・B が消した行を A が変えても戻らない (Conflict)・明示した削除は指定の行だけ) をモックで確かめた (`MockSublistTest`)
- 2 (明細の表が同じ行の集まりになる): 対応済み (2026-11-20)。明細の表を、明細の種類が 1 つに決まる取引の `item` だけにした。Sandbox での照合 (REST の件数・金額と表の一覧・合計) は未実施
- 3 (部分成功と結果不明): 対応済み (2026-11-20)。`CreatedIds`・メッセージに作成済み / 失敗 / 未送信・Example の本番の定型文。
  503 は ServiceUnavailable のまま (文言で照合を求める)。受入試験の Production 設定の Example での実行は未実施 (Selenium は開発設定)
- 4 (方式 2 の結線): 対応済み (2026-11-20)。Selenium の方式 2 の `[Ignore]` を外して成功
- 5 (Id 以外の条件): 文書で対応 (2026-11-20。Docs/NetSuite.md の保存・SpecDocs のやってはいけないこと)。挙動は従来どおり (先に読んで確かめてから Id で書く。`NetSuiteDbAccessorTest` の楽観ロックつきの更新のテスト)。
  承認と編集を並行したときの順序の確認は Sandbox 待ち
