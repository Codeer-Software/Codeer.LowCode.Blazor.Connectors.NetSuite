using Codeer.LowCode.Blazor.Repository.Data;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite
{
    /// <summary>
    /// 添付のフィールドのデータ。Value はファイルの JSON の配列の文字 (ファイルが無ければ null)。
    /// 読んだ値は [{"id","name","size","type"}...] (size・type は接続の保存検索に列があるときだけ)。
    /// 保存で送る値には、足すファイル {"name","contentType","base64"} と外すファイル {"id","deleted":true} が入る。
    /// </summary>
    public class NetSuiteAttachmentsFieldData() : ValueFieldDataBase<string>(typeof(NetSuiteAttachmentsFieldData).FullName!), ICloneable<NetSuiteAttachmentsFieldData>
    {
        public NetSuiteAttachmentsFieldData Clone() => (NetSuiteAttachmentsFieldData)MemberwiseClone();
    }
}
