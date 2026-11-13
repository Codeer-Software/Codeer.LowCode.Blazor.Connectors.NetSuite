# Codeer.LowCode.Blazor.Connectors.NetSuite - Project Guide

## プロジェクト概要

Codeer.LowCode.Blazor (CLB) のモジュールを Oracle NetSuite のレコードに結び付けるコネクター。
NuGet パッケージ `Codeer.LowCode.Blazor.Connectors.NetSuite` (MIT) として配布する。

- **ターゲット**: .NET 8.0 / C# (Nullable 有効, ImplicitUsings 有効)
- **本体リポジトリ**: `C:\tfs\codeer\Codeer.LowCode.Blazor` (参照用、直接編集しない)。本体は NuGet パッケージで参照する
- **作りの手本**: `C:\GitHub\Codeer.LowCode.Blazor.Extras`
- **利用者向けドキュメント**: `Docs/NetSuite.md`
- **調査資料**: `Docs/01_netsuite_api_inventory.md` (API 調査) / `Docs/02_netsuite_specification_draft.md` (仕様案) / `Docs/03_research_request.md` (追加調査の依頼)

## リポジトリ構成

```
Source/
├── Codeer.LowCode.Blazor.Connectors.NetSuite.slnx
├── Codeer.LowCode.Blazor.Connectors.NetSuite/        # ライブラリ (WPF・ASP.NET に依存しない)
│   ├── NetSuiteDbAccessor.cs          # IDbAccessor のデコレータ (公開の入口)
│   ├── NetSuiteConnection.cs          # 接続 1 つ: 認証・送信 (再試行・エラー分類)・定義・SuiteQL・レコード操作
│   ├── NetSuiteConnectionSettings.cs  # ConnectionString "Provider=NetSuite;..." の読み取り
│   ├── NetSuiteRecordDefinition.cs    # REST メタデータ (JSON Schema) → 列の定義
│   ├── SuiteQlTranslator.cs           # 本体の SQL → SuiteQL / レコード操作
│   └── NetSuiteException.cs
├── Codeer.LowCode.Blazor.Connectors.NetSuite.Designer/   # デザイナ用 (WPF)。AI 用の仕様ドキュメントの登録・メタ情報の更新メニュー・デザイナ用の IDbAccessor
│   ├── NetSuiteDesignerInitializer.cs # Initialize(env) = SpecDocs の登録 + ファイルから読むモード / Setup(env) = Tools のメニュー / CreateDbAccessor
│   └── SpecDocs/NetSuite.md           # AI が NetSuite のモジュールを設計するための仕様 (デザイン側のことだけを書く)
├── NetSuite.Mock/   # NetSuite のモック (ASP.NET Core・SQLite のインメモリ)。利用者が手元で起動して試す用。パッケージにしない
│   ├── MockNetSuite.cs                # 定義・データ・トークンを持ち、REST の要求に応える
│   ├── MockNetSuiteEndpoints.cs       # /services/rest/... の結び付け (MapMockNetSuite。テストは TestServer で同じものを立てる)
│   ├── MockSuiteQl.cs                 # SuiteQL を SQLite で動かす (文の確認・ROWNUM / NUMTODSINTERVAL の書き換え・Oracle の関数)
│   ├── MockRecordType.cs              # mock-data.json の読み取りと値の変換
│   └── mock-data.json                 # 既定の定義と初期データ (架空のサンプル)。利用者向けの説明は Docs/NetSuiteMock.md
├── Codeer.LowCode.Blazor.Connectors.NetSuite.Test/   # NUnit。HTTP は偽物 (Harness/FakeNetSuite) とモック (Harness/MockHarness = モック + 本物のコネクター + 本体の ModuleDataIO)
├── Example/LowCodeApp.Designer/     # コネクターを組み込んだデザイナ (WPF)。結線の見本 (App.xaml.cs / DbAccessorFactory.cs) 兼 GUI テストの対象。パッケージにしない
└── Codeer.LowCode.Blazor.Connectors.NetSuite.DesignerTest/   # NUnit の GUI テスト。Example のデザイナを起動して Friendly で操作する
    ├── Harness/MockServer.cs          # モックを Kestrel で 127.0.0.1 の空きポートに立てる (要求を数える・同じポートで立て直す)。DeadPort = 届かない NetSuite
    ├── Harness/DesignProject.cs       # テストごとの一時フォルダ: 空のデザインプロジェクト (NetSuite のデータソースだけ) とその場で作った鍵
    └── Drivers/DesignerDriver.cs      # 起動 (引数で app.clprj を開く)・メニュー・トースト・MessageWindow・テーブルの定義・モジュール作成・デザインチェック
```

パッケージは 2 つ (Extras と Extras.Designer と同じ分け方): サーバーは本体パッケージ、デザイナは `.Designer` (本体パッケージに依存) を参照する。

## ビルド・テスト

```bash
cd Source
MSBUILDDISABLENODEREUSE=1 dotnet build Codeer.LowCode.Blazor.Connectors.NetSuite -m:1
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
| `select ... from "t" left join ... where ... order by ... offset N rows fetch next M rows only` | SuiteQL。引用符を外す・ページング句は URL の limit / offset へ・日付と日時の列は `TO_CHAR` で決まった形に・ページングつきなら並びの最後に `主表.id` |
| `BEGIN INSERT INTO "t"(...) VALUES (:p0,...) RETURNING "id" INTO :returning_id; END;` | `POST record/v1/t`。Location の末尾が Id (`args[":returning_id"]` にも入れる = 本体はそこから読む) |
| `update "t" set "c"=:p0,... where "id"=:pid0 [and ...]` | `PATCH record/v1/t/{id}`。追加条件があれば先に `select count(*)` で確かめる |
| `delete from "t" where "id"=:pid0 [and ...]` | `DELETE record/v1/t/{id}` |
| `BEGIN INSERT ...; INSERT ...; END;` (一括) | 1 件ずつ POST |

- パラメータ: 文字は `?` + `params` (値はすべて JSON の文字列)。数値・真偽 (`'T'` / `'F'`)・日付 (`TO_DATE(...)`) は決まった書式のリテラルにして埋める
  (利用者の文字列は埋めない)。LIKE のエスケープ文字は本体の制御文字 (U+0001) から `\` に置き換える
- 表は、どの階層 (副問い合わせ・結合) でも `Records` に書かれたレコードだけ
- 結果: SuiteQL は値を文字列で返し、null の列を省く。select の項目を読んで、本体が付けた別名をキーに、列の種類に合わせた型で返す (本体は別名の大文字・小文字まで一致で探す)

本体が組む SQL の形が変わるとここが壊れる。`Test/NetSuiteDbAccessorTest` は本体の `ModuleDataIO` を実際に通しているので、本体のパッケージを上げたらこのテストで確かめる。

### 名前の対応

- 表名 (モジュールの DbTable) = REST のレコード名
- 列名 (DbColumn) = REST の項目名の小文字。SuiteQL にはそのまま使い、書き込みでは定義 (`NetSuiteFieldDefinition.RestName`) で REST の名前に戻す
- SuiteQL の表名・列名が REST と違うレコード (取引 = `transaction` 表など) は扱えない。対応するなら、レコードごとの対応表を足す (未設計)

### 接続 (NetSuiteConnection)

- 長生きのオブジェクト (トークン・定義・同時実行の枠を持つ)。`FromDataSources` は設定が同じなら同じ接続を返す。`NetSuiteDbAccessor` は要求ごとに作ってよい
- 定義はプロセスの間持つ (本体も表の定義を static に持つので、短い期限を付けても本体側に古い定義が残る)。NetSuite の項目を変えたらアプリを再起動
- 再試行: 読み出しは 429 / 502 / 503 / 504 / 通信失敗を 3 回まで。書き込みは 429 だけ。401 はトークンを取り直して 1 回。
  書き込みを送ったあとの通信失敗と、書き込みへの 503 以外の 5xx は `WriteResultUnknown` (実行されたか分からないので、やり直さない)
- 例外に秘密鍵・トークン・接続文字列を入れない

## 決めてあること (2026-11-13)

仕様の自然さで決めた。蒸し返すときは理由ごと見直す。

- **保存はデータソースごとに確定する。1 回の保存に NetSuite のレコードが複数ある・DB と混ざる要求を、書く前に拒否しない。**
  本体の `DbAccessor` はデータソースごとに別のトランザクションを順にコミットするので、DB を 2 つ使う保存も全体では原子的でない。
  NetSuite は「1 件ごとに確定するデータソース」として同じ並びに置く。拒否すると、NetSuite のレコード + DB の編集履歴・監査のような普通の組み合わせが使えなくなる。
  補償 (失敗したら作ったレコードを消す・値を戻す) はしない (補償自体が失敗しうる・消したレコードは同じ ID で戻せない)
- **`Records` は必須。** データソース = そのアプリが扱うレコードの集まり。デザイナは全テーブルの定義を読むので、アカウントの全レコード種別を対象にしない
- **`DataSourceType` は `Oracle` と書かせる。** 本体の enum に NetSuite を足さない (本体は NetSuite を知らないまま = IDbAccessor の差し替えだけで成り立つ形を保つ)
- **モジュールの Id は常に internal ID。** 外部 ID を鍵にした Upsert は提供しない
- **集計は DB のモジュールと同じに動かす** (本体の `AggregateAsync` の SQL を読み替える)
- **OptimisticLockingField は対象外。** REST に条件つきの更新が無い。Id 以外の条件が付いた更新・削除は、先に読んで確かめるだけ (確かめてから書くまでの間は防げない)
- **定義はプロセスの間持つ。** 本体が表の定義を static に持つのに揃える
- **デザイナはメタ情報をファイルから読む** (ユーザー指示 2026-11-13)。プロジェクトのフォルダの `netsuite.<データソース名>.metadata.json` (`NetSuiteMetadataFile`)。
  NetSuite に取りに行くのは Tools > NetSuite のメタ情報を更新 のときだけ。ファイルが無ければ表は 0 件 (自動では取りに行かない)。
  狙いは「プロジェクトを開くたびの重い取得を無くす」「オフライン・鍵なしでもデザインできる」。headless の CLI も同じファイルを読む (更新の CLI は無い)。
  サーバー (実行時) はファイルを使わない (デザインのファイルはデプロイされない)
- **必須の項目も、列の定義は null 可で見せる。** NOT NULL にすると本体 (`DbDataConverter`) が空の値を型の既定値 (0・0001-01-01・false) に置き換えて書いてしまう。必須は NetSuite が保存時に確かめる
- **Starter (ホストのテンプレート) には組み込まない。** 特定の外部サービス向けなので、使うアプリが 2 行足す
- **見せ方を決めてあり、実装は調査待ちのもの**
  - 取引のレコード: 利用者が書く表名は常に REST のレコード名。SuiteQL の表・種別の条件との違いはコネクターが吸収する
  - 明細・サブレコード: 親とは別の表 (子のレコード) として見せる。読み出しは SuiteQL、書き込みは親のレコードへの REST 要求
  - 選択肢: 相手がレコードなら参照 (いまと同じ)。固定の選択肢なら候補の一覧を読める表として見せる

## 守ること

- 本体 (core) を変えない。足りない口があれば、変える前にユーザーに相談する
- ホスト (Starter) に置くべきもの (Controller・DI の要求) をライブラリに持ち込まない。組み込みは `IDbAccessor` を包む 1 行
- 実アカウントで確かめていないことを「確認済み」と書かない
- 公開リポジトリ。秘密鍵・Client ID・アカウント ID などの実物をコード・テスト・ドキュメントに書かない (テストの鍵はテストの中で生成する)

## 未検証・未実装 (2026-11-13 時点)

**NetSuite の実アカウントには一度もつないでいない。** テストは HTTP の偽物が相手。
実際の応答・構文について裏が取れていない前提は `Docs/03_research_request.md` に質問として並べてある (回答が来たらコードの前提と突き合わせる)。特に不確かなもの:

- JWT の `scope` クレームの形 (配列で送っている)
- メタデータの JSON Schema での参照・選択肢・明細の書かれ方 (`NetSuiteRecordDefinition.GetKind` の判定)
- SuiteQL の結果の形 (null の省略・値が文字列・キーの大文字小文字) と、`TO_CHAR` した日時の時刻帯 (UTC として扱っている)
- REST の項目名の小文字 = SuiteQL の列名、が成り立つ範囲
- 集計で本体が書く Oracle の式 (`TRUNC` / `ADD_MONTHS` / `NUMTODSINTERVAL`・派生表) を SuiteQL が受け付けるか
- 書き込みへの 5xx のうち、実行されていないと言えるもの (いまは 503 だけをそう扱っている)

未実装:

- 明細・サブレコード・複数選択、固定の選択肢の候補、取引のレコード (見せ方は「決めてあること」。調査の回答待ち)
- 読み取り専用の項目の判別 (いまは NetSuite がエラーを返すのに任せている。メタデータで見分けられるかは調査の回答待ち)
- 対象外と決めたもの: 外部 ID の Upsert、保存検索・Dataset・Action・Transform・ファイル
