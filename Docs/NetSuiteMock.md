# NetSuite のモック

NetSuite の実アカウントが無くても、コネクターを組み込んだアプリとデザイナを動かして試すための、手元で動く NetSuite の代わりです。
自分のマシンで起動し、アプリとデザイナのデータソースをモックに向けると、次の操作を通しで試せます。

- デザイナで、レコードからモジュールを作る (項目の一覧・型・必須)
- 一覧・検索・並び・ページング・件数、リンク越しの表示と検索
- 集計 (件数・合計・平均・最小・最大、項目・リンク・日付の区切りごと)
- 新規・更新・空にする・削除と、そのときのエラー (必須の項目が無い・知らない項目・型の合わない値・無い参照先)
- NetSuite のアカウント (従業員) でのアプリのログイン (OpenID Connect。下記)
- 取引の添付ファイルの一覧・ダウンロード・追加・外す (File Cabinet・添付・保存検索。下記)

データはモックのプロセスの中 (メモリ) だけにあります。止めると、最初のデータに戻ります。

> **モックで動いても、実アカウントで動くとは限りません。** 下の「実物と同じとは限らない点」を読んでください。

## 起動

.NET 8 の SDK が要ります。リポジトリのフォルダで:

```
dotnet run --project Source/NetSuite.Mock
```

`http://localhost:5080` で待ち受けます。ASP.NET Core の開発用証明書 (`dotnet dev-certs https --trust`) があるときは `https://localhost:5081` でも待ち受けます (アプリのログインを試すときに使います)。
ポートやデータを変えるとき:

```
dotnet run --project Source/NetSuite.Mock -- --urls http://localhost:5090 --data C:\work\my-mock-data.json
```

| 引数 | 内容 |
|---|---|
| `--urls` | 待ち受ける URL (`;` 区切りで複数)。既定 `http://localhost:5080` と、開発用証明書があれば `https://localhost:5081` |
| `--data` | レコードの定義と初期データのファイル。既定はモックに付いている `mock-data.json` (架空のサンプル) |

終了は Ctrl+C です。

## データソースの設定

起動すると、コンソールにデータソースの設定が出ます。

```json
{
  "Name": "NetSuite",
  "DataSourceType": "Oracle",
  "ConnectionString": "Provider=NetSuite;ServiceUrl=http://localhost:5080;ClientId=mock-client;CertificateId=mock-cert;PrivateKeyPath=C:\\...\\mock-key.pem"
}
```

これをアプリの `appsettings.json` とデザイナの設定の `DataSources` にそのまま足します。コネクターの組み込みは [NetSuite.md](NetSuite.md) の「3. 組み込み」と同じです。
デザイナでは、モックを起動した状態でメニュー Tools > NetSuite のメタ情報を更新 を実行すると、モックのレコード (取引の明細の表を含む) がテーブルとして出ます。
アプリは、デザインと一緒に配られたメタ情報のファイルを読みます (更新したらデザインを配り直す)。

- `ServiceUrl` は本来 https だけですが、このマシン自身 (`localhost`・`127.0.0.1`) に限り http を使えます
- `ClientId`・`CertificateId` は何でもかまいません (モックは確かめません)
- `PrivateKeyPath` の鍵は、モックが最初の起動のときに実行ファイルの隣に作る使い捨ての鍵 (`mock-key.pem`) です。実アカウントの鍵とは関係ありません。リポジトリには入れません

## NetSuite のアカウントでログインする (OpenID Connect)

モックは NetSuite の OpenID Connect のプロバイダの代わりもします。アプリの外部ログイン (`OidcLogins`) の `Authority` をモックの https の URL にすると、
ログイン画面の「NetSuite」のボタンからモックのログイン画面に移り、従業員を選んでログインできます。設定の書き方は [NetSuite.md](NetSuite.md) の「NetSuite のアカウントでログインする」と同じです。

```json
{
  "OidcLogins": [
    {
      "Name": "NetSuite",
      "DisplayName": "NetSuite",
      "ClientId": "mock-client",
      "ClientSecret": "mock-secret",
      "Authority": "https://localhost:5081",
      "Scopes": [ "openid", "email" ],
      "LoginNameClaim": "email"
    }
  ]
}
```

- **https が要ります。** ASP.NET Core の OpenID Connect は、プロバイダの設定 (discovery) を https でしか読みません。開発用証明書を信頼済みにしてからモックを起動してください (コンソールに Authority が出ます)
- `ClientId`・`ClientSecret` は空でなければ何でもかまいません (モックは確かめません)
- ログイン画面に出るのは、データのファイルの `employee` の行のうち、メール (`email`) があり、無効 (`isInactive`) でない従業員です。キャンセルすると `access_denied` で戻ります
- 認可コード + PKCE (S256) だけを受け付けます。scope は `openid` と `email` だけ (`email` を付けたときだけ ID トークンに `email` が入ります)
- ID トークンは PS256 で署名します (鍵は起動のたびに作り直す使い捨て)。中身は `iss` (モックの URL)・`sub` (`<ロールの ID>;<従業員の id>`。ロールは `1111` 固定)・`aud`・`exp`・`iat`・`auth_time`・`nonce`・`at_hash`・`email` です。名前のクレームはありません
- 認可コードは 1 回だけ・5 分で切れます。`redirect_uri`・クライアント ID は認可の要求と同じでないと通りません

## データのファイル (mock-data.json)

レコードごとに、項目の定義と最初の行を書きます。

```json
{
  "records": {
    "subsidiary": {
      "fields": {
        "name": { "type": "string", "required": true, "title": "Name" },
        "country": "string"
      },
      "rows": [
        { "id": 1, "name": "Headquarters", "country": "JP" }
      ]
    },
    "customer": {
      "fields": {
        "companyName": { "type": "string", "required": true, "title": "Company Name" },
        "creditLimit": "number",
        "startDate": "date",
        "subsidiary": { "type": "reference", "record": "subsidiary", "required": true }
      },
      "rows": [
        { "id": 1, "companyName": "Alpha Trading", "creditLimit": 50000, "startDate": "2024-04-01", "subsidiary": { "id": "1" } }
      ]
    }
  }
}
```

- `records` のキーがレコード名 (REST のレコード名。小文字にしたものがメタ情報の名前・モジュールの DbTable)
- `fields` のキーが項目名 (REST の項目名。`companyName` のような形)。モジュールの DbColumn はこれを小文字にしたもの (`companyname`)
- 項目は `{ "type": ..., "required": ..., "title": ... }` か、種類だけを `"string"` のように書く
- `id` は書かない (全レコードに自動で付く internal ID)

| type | 値の書き方 (rows と REST の要求) |
|---|---|
| `string` | `"文字"` |
| `boolean` | `true` / `false` |
| `integer` | `12` |
| `number` | `12.5` |
| `date` | `"2026-03-04"` |
| `dateTime` | `"2026-03-04T05:06:07Z"` (UTC。`+09:00` のような時差つきも可) |
| `reference` | `{ "id": "3" }`。`"record"` に参照先のレコード名を書く。rows では `3` や `"3"` とも書ける |
| `select` | 固定の選択肢。`"values": { "_pDF": "PDF", ... }` に値と表示名を書く。読むと `{ "id": "_pDF", "refName": "PDF" }`、書くのは `{ "id": "_pDF" }` か `{ "refName": "PDF" }`。rows では `"_pDF"` とも書ける。メタデータには値だけが `id` の `enum` として出る |

- `required: true` の項目は、作成で省けず、更新で空にできません
- `readOnly: true` の項目は、メタデータに `"readOnly": true` が出ます (モックは書き込みを拒みません。拒まないので、コネクターが送らないことを確かめられます)
- 行の `id` は省くと 1 から順に振られます。書くなら正の整数。以後の作成は、いちばん大きい id の次から振られます
- ファイルに誤り (知らない種類・無い参照先・必須の値が無い行など) があると、起動のときにどこが違うかを出して止まります
- コメント (`//`) と末尾のカンマは書けます

### 取引と明細

```json
"salesOrder": {
  "transactionType": "SalesOrd",
  "fields": {
    "entity": { "type": "reference", "record": "customer", "required": true },
    "tranDate": { "type": "date", "required": true }
  },
  "sublists": {
    "item": {
      "required": true,
      "fields": {
        "item": { "type": "reference", "record": "item", "required": true },
        "quantity": "number"
      }
    }
  },
  "rows": [
    { "id": 1, "entity": { "id": "1" }, "tranDate": "2026-04-01", "item": { "items": [ { "item": { "id": "1" }, "quantity": 10 } ] } }
  ]
}
```

- `transactionType` を書いたレコードは取引です。SuiteQL の `transaction` 表に、取引のレコード全部の行が `type` (この値) の列つきで並びます (項目はどれかの取引にあるもの全部。無い項目は空)。
  取引のレコードでは `type` を項目名に使えず、`transaction` という名前のレコードは作れません。
  取引のレコード名 (`salesorder` など) は SuiteQL の表としては読めません (NetSuite と同じ。コネクターは取引の対応表にある取引だけを `transaction` 表に読み替える)
- 取引の id は取引のレコード全部で通しで振られ、種類が違っても重なりません (rows に同じ id を書くと起動のときに止まります)
- `sublists` のキーが明細名、`fields` が行の項目 (書き方はレコードの項目と同じ。`line` は使えません)。`required: true` の明細は、作成で行を 0 行にできず、更新で全部の行を消せません
- rows の行の明細は `"明細名": { "items": [ ... ] }`。行の番号 (`line`) は書かず、親ごとに 1 から振られます

REST での明細の動き:

- **作成** (POST): 明細を `"明細名": { "items": [ ... ] }` で送ります。行に `line` は書けません
- **更新** (PATCH): `line` のある行はその行の項目を更新し、`line` の無い行は足します。今の行に無い `line` は 400 で、そのときは何も変わりません。
  `?replace=明細名` を付けると、送らなかった行を消します。消した行の番号は使い回しません
- **読み出し** (GET): `?expandSubResources=true` のときだけ行を返します (`line`・項目・links。参照は `{ "id", "refName" }`、refName は参照先のレコードの最初の文字列の項目)。付けなければ明細は links だけです
- **メタデータ**: 明細は `$ref` (`<レコード名>-<明細名>Collection`) で書き、その先 (…Collection → items → `<レコード名>-<明細名>Element`) も同じ metadata-catalog から読めます
- **SuiteQL の `transactionline` 表**: 取引の明細の行を全部並べた表です。列は `uniquekey` (行のキー。モックの全部の明細で通しで振り、使い回しません)・
  `transaction` (親の取引の id)・`id` と `linesequencenumber` (どちらも行の番号 `line`)・`mainline` と `taxline` (いつも `F`)・行の項目の小文字 (どれかの明細にあるもの全部。無い項目は空)。
  行の項目のうち、名前がこれらの列と同じもの (`id` など) は `transactionline` には出ません

付いている `mock-data.json` は架空のサンプルです: `subsidiary` (3 行)、`customer` (20 行)、`customrecord_sample` (15 行。`customer` への参照を持つ)、
`item` (5 行)、`salesOrder` (4 行。取引 `SalesOrd`。必須の明細 `item` を持つ)、
`employee` (5 行。ログインできるのは 3 人。1 人は無効、1 人はメールなし)。

### File Cabinet と添付ファイル

```json
"fileCabinet": {
  "folders": [ { "id": 100, "name": "Attachments" } ],
  "files": [ { "id": 500, "name": "見積書.txt", "folder": 100, "fileType": "PLAINTEXT", "text": "..." } ],
  "attachments": [ { "record": "salesOrder", "id": 1, "file": 500 } ]
},
"savedSearches": {
  "customsearch_clb_transaction_files": {
    "columns": { "record": "internalid", "file": "file_internalid", "name": "file_name", "size": "file_documentsize", "type": "file_filetype" }
  }
}
```

- `folders`: フォルダ。`"restricted": true` を付けると、連携用のロールがそのフォルダに登録できず中身を読めない (403) フォルダになります
- `files`: ファイル。中身は `text` (UTF-8)。`size` を書くと中身と関係なくその大きさのファイルになります (10 MB を超えるファイルを試すとき)
- `attachments`: レコードに付いているファイル (レコード名・id・ファイルの id)
- `savedSearches`: 保存検索。モックの保存検索は「取引に付いているファイル」の 1 種類だけで、1 行が取引とファイルの組です (ファイルの無い取引は出ません)。`columns` は結果の列の名前

REST での動き:

- **登録** (`POST document/v1/file/`): multipart の `attributes` (`{"fileName", "folder": {"id"}}`) と `data`。204 で、`x-ns-link` の末尾がファイルの id。10 MB を超えると 413
- **属性** (`GET document/v1/file/{id}`)・**中身** (`GET document/v1/file/{id}/content`): 読めないフォルダは 403、10 MB を超えるファイルの中身は 413
- **添付・外す** (`POST record/v1/{レコード}/{id}/!attach/file/{ファイル}`・`!detach`): 本文なしで 204。外してもファイルは残ります。レコードを消すと、そのレコードへの添付は無くなります (ファイルは残ります)
- **保存検索** (`GET query/v1/savedsearch/{id}/result?limit=&offset=`): 行は取引の id・ファイルの id の順。`links` (`next`)・`count`・`hasMore`・`items`・`offset`・`totalResults` を返します。種類の列は `{ "id", "refName" }`

付いている `mock-data.json` では、受注 1 (`First order`) にファイル 500 (`見積書.txt`) と 501 (`spec.csv`) が付いています。フォルダは 100、保存検索は `customsearch_clb_transaction_files` です。
起動したときにコンソールに出るデータソースの設定には、このフォルダと保存検索が入っています。

## 実物と同じとは限らない点

このモックは、**コネクターが前提にしている NetSuite の振る舞いどおりに応える**ものです。NetSuite の実際の応答を写したものではありません。
コネクターの前提そのものが実物と違っていれば、モックでは動いて実アカウントでは動きません。実アカウントで使う前に、検証用のアカウントで確かめてください。

- **SuiteQL**: モックは受け取った SuiteQL を SQLite で実行します。コネクターが送る形 (Oracle の書き方。`TO_CHAR`・`TO_DATE`・`TRUNC`・`ADD_MONTHS`・`NUMTODSINTERVAL`・`ROWNUM`) は動くようにしてありますが、
  NetSuite の SuiteQL が受け付ける構文・関数の範囲とは違います。モックで通る文が NetSuite で通るとは限らず、その逆もあります
- **大文字・小文字**: 文字の比較・部分一致 (LIKE) の大文字と小文字の扱いは、NetSuite と違うことがあります (モックの部分一致は英字の大文字と小文字を区別しません)
- **並び**: 空の値の並び順は Oracle の既定 (昇順で最後・降順で最初) に合わせていますが、文字の並び順 (照合順序) は NetSuite と違うことがあります
- **結果の形**: 値はすべて文字列・空の列は省く・真偽は `T` / `F`・キーは小文字、はコネクターの前提どおりに返しています。`TO_CHAR` しない日付・日時は `2026-03-04` / `2026-03-04 05:06:07` の形で返します (NetSuite ではアカウントの表示形式になると考えられます)
- **時刻帯**: 日時はすべて UTC として持ち、UTC のまま返します。NetSuite が SuiteQL で返す時刻帯は確かめられていません
- **項目と名前**: SuiteQL の表名 = レコード名、列名 = 項目名の小文字、が常に成り立ちます。NetSuite の標準のレコードでは名前が違う項目があり得ます。項目の定義はデータのファイルに書いたものだけで、NetSuite の標準のレコードの項目とは関係ありません
- **メタデータ**: JSON Schema は、コネクターが読む部分 (型・形式・参照・必須・表示名・明細の `$ref`) だけを返します。サブレコード・選択肢 (enum)・読み取り専用の印は出しません。明細の書かれ方 (`$ref` の名前・形) はコネクターの前提どおりで、NetSuite の実際の形は確かめられていません
- **取引と明細**: `transaction` 表は取引のレコードを並べたものです。NetSuite の `transaction` 表の列・`type` の値とは違うことがあります。
  `transactionline` 表は明細の行だけを並べたものです。NetSuite の `transactionline` には本体の行 (`mainline = 'T'`)・税の行なども入り、列名が REST の項目名の小文字と同じとは限りません。数量・金額の符号も確かめられていません。
  明細の `replace`・`line` の振り方・`line` だけの要素の扱いは、コネクターの前提どおりに動かしています
- **書き込み**: 項目の値の検証は型と必須と参照先の有無だけです。NetSuite の業務ルール・既定値・自動で入る項目 (更新日時など)・読み取り専用の項目は再現しません。エラーのコード (`o:errorCode`) と文言はモックが決めたもので、NetSuite と同じではありません
- **認証**: トークンの要求は JWT の形と期限 (`exp`) だけを見ます。署名・Client ID・証明書・scope は確かめません。ロールの権限による制限もありません
- **ログイン (OpenID Connect)**: 公開されている仕様 (PKCE・scope・PS256・`sub` の形) に合わせたものです。実物は ID トークンの `iss` が discovery の `issuer` と違う (`https://system.netsuite.com`) という情報があり、
  `at_hash` の扱い・返るクレームも確かめられていません。モックの `iss` は discovery の `issuer` (モックの URL) と同じで、`email_verified` は返しません。ログイン画面・ロールの選択・二要素認証は NetSuite のものとは違います
- **呼び出しの制限**: 同時実行・呼び出し量の制限 (429)、障害 (5xx)、応答の遅れは起きません。コネクターのやり直しの動きはモックでは試せません
- **データの量**: 1 回の SuiteQL の結果の上限 (100,000 行) は再現しません
- **添付ファイル**: 保存検索の結果の列の名前 (特に File の列の名前)・大きさの単位・ページ送り (`limit` / `offset`) を受け付けるかは、コネクターの前提どおりにしたもので、実物では確かめられていません。
  保存検索の条件・並びはモックでは決まっていて、NetSuite で作った保存検索の中身は再現しません。ファイルの種類はファイル名の拡張子で決めています。リンクのファイル (`isLink: true`) は登録できません
