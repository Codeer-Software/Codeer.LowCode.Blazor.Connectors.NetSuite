# NetSuite のモック

NetSuite の実アカウントが無くても、コネクターを組み込んだアプリとデザイナを動かして試すための、手元で動く NetSuite の代わりです。
自分のマシンで起動し、アプリとデザイナのデータソースをモックに向けると、次の操作を通しで試せます。

- デザイナで、レコードからモジュールを作る (項目の一覧・型・必須)
- 一覧・検索・並び・ページング・件数、リンク越しの表示と検索
- 集計 (件数・合計・平均・最小・最大、項目・リンク・日付の区切りごと)
- 新規・更新・空にする・削除と、そのときのエラー (必須の項目が無い・知らない項目・型の合わない値・無い参照先)

データはモックのプロセスの中 (メモリ) だけにあります。止めると、最初のデータに戻ります。

> **モックで動いても、実アカウントで動くとは限りません。** 下の「実物と同じとは限らない点」を読んでください。

## 起動

.NET 8 の SDK が要ります。リポジトリのフォルダで:

```
dotnet run --project Source/NetSuite.Mock
```

`http://localhost:5080` で待ち受けます。ポートやデータを変えるとき:

```
dotnet run --project Source/NetSuite.Mock -- --urls http://localhost:5090 --data C:\work\my-mock-data.json
```

| 引数 | 内容 |
|---|---|
| `--urls` | 待ち受ける URL。既定 `http://localhost:5080` |
| `--data` | レコードの定義と初期データのファイル。既定はモックに付いている `mock-data.json` (架空のサンプル) |

終了は Ctrl+C です。

## データソースの設定

起動すると、コンソールにデータソースの設定が出ます。

```json
{
  "Name": "NetSuite",
  "DataSourceType": "Oracle",
  "ConnectionString": "Provider=NetSuite;ServiceUrl=http://localhost:5080;ClientId=mock-client;CertificateId=mock-cert;PrivateKeyPath=C:\\...\\mock-key.pem;Records=subsidiary,customer,customrecord_sample"
}
```

これをアプリの `appsettings.json` とデザイナの設定の `DataSources` にそのまま足します。コネクターの組み込みは [NetSuite.md](NetSuite.md) の「3. 組み込み」と同じです。
デザイナでは、モックを起動した状態でメニュー Tools > NetSuite のメタ情報を更新 を実行すると、モックのレコードがテーブルとして出ます。

- `ServiceUrl` は本来 https だけですが、このマシン自身 (`localhost`・`127.0.0.1`) に限り http を使えます
- `ClientId`・`CertificateId` は何でもかまいません (モックは確かめません)
- `PrivateKeyPath` の鍵は、モックが最初の起動のときに実行ファイルの隣に作る使い捨ての鍵 (`mock-key.pem`) です。実アカウントの鍵とは関係ありません。リポジトリには入れません
- `Records` には、データのファイルに書いたレコードが全部入ります。使うものだけに減らしてもかまいません

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

- `records` のキーがレコード名 (REST のレコード名。`Records` に書く名前・モジュールの DbTable)
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

- `required: true` の項目は、作成で省けず、更新で空にできません
- 行の `id` は省くと 1 から順に振られます。書くなら正の整数。以後の作成は、いちばん大きい id の次から振られます
- ファイルに誤り (知らない種類・無い参照先・必須の値が無い行など) があると、起動のときにどこが違うかを出して止まります
- コメント (`//`) と末尾のカンマは書けます

付いている `mock-data.json` は架空のサンプルです: `subsidiary` (3 行)、`customer` (20 行)、`customrecord_sample` (15 行。`customer` への参照を持つ)。

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
- **メタデータ**: JSON Schema は、コネクターが読む部分 (型・形式・参照・必須・表示名) だけを返します。明細・サブレコード・選択肢 (enum)・読み取り専用の印は出しません
- **書き込み**: 項目の値の検証は型と必須と参照先の有無だけです。NetSuite の業務ルール・既定値・自動で入る項目 (更新日時など)・読み取り専用の項目は再現しません。エラーのコード (`o:errorCode`) と文言はモックが決めたもので、NetSuite と同じではありません
- **認証**: トークンの要求は JWT の形と期限 (`exp`) だけを見ます。署名・Client ID・証明書・scope は確かめません。ロールの権限による制限もありません
- **呼び出しの制限**: 同時実行・呼び出し量の制限 (429)、障害 (5xx)、応答の遅れは起きません。コネクターのやり直しの動きはモックでは試せません
- **データの量**: 1 回の SuiteQL の結果の上限 (100,000 行) は再現しません
