# Codeer.LowCode.Blazor.Connectors.NetSuite

[Codeer.LowCode.Blazor](https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Manual) のモジュールを、Oracle NetSuite のレコードに結び付けるコネクターです。

NetSuite をデータソースの 1 つとして登録すると、DB のテーブルと同じ手順でレコードからモジュールを作れます。
一覧・検索・詳細・保存といったいつもの操作が、そのまま NetSuite の読み書きになります。

- 一覧・検索は SuiteQL、作成・更新・削除は REST のレコード操作
- 認証は OAuth 2.0 Client Credentials (証明書を使う M2M)
- NetSuite 側へのスクリプトの配備や、外部の接続ドライバーは不要

> この版は NetSuite の実アカウントでの動作確認がまだ済んでいません。検証用のアカウントで動作を確かめてから使ってください。

## 使い方

アプリ (サーバー) に `Codeer.LowCode.Blazor.Connectors.NetSuite.Server`、デザイナに `Codeer.LowCode.Blazor.Connectors.NetSuite.Designer` を追加します。

データソースに NetSuite の接続を書き、

```json
{
  "Name": "NetSuite",
  "DataSourceType": "Oracle",
  "ConnectionString": "Provider=NetSuite;ServiceUrl=https://1234567.suitetalk.api.netsuite.com;ClientId=...;CertificateId=...;PrivateKeyPath=C:\\keys\\netsuite.pem;Records=customer,customrecord_sample"
}
```

アプリとデザイナの `IDbAccessor` を包みます。

```csharp
//アプリ (サーバー)
var netSuite = new NetSuiteDbAccessor(new DbAccessor(dataSources), NetSuiteConnection.FromDataSources(dataSources));

//デザイナ (レコードの定義は、Tools > NetSuite のメタ情報を更新 で保存したファイルから読む)
var netSuite = NetSuiteDesignerInitializer.CreateDbAccessor(new DbAccessor(dataSources), dataSources);
```

設定の項目・モジュールの作り方・動きと制限は [Docs/NetSuite.md](Docs/NetSuite.md) にあります。

NetSuite のアカウントが無くても、手元で動くモック (`dotnet run --project Source/NetSuite.Mock`) にアプリとデザイナをつないで試せます。使い方と、実物と違う点は [Docs/NetSuiteMock.md](Docs/NetSuiteMock.md) にあります。

コネクターを組み込んだ Web アプリとデザインのサンプルが `Source/Example` にあります。モックにつないで動かす手順は [Docs/Example.md](Docs/Example.md) にあります。

## ライセンス

MIT
