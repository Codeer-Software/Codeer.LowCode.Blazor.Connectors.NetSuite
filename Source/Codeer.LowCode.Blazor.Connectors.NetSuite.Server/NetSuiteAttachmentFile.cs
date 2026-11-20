namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Server
{
    /// <summary>レコードに添付したファイル 1 つ (<see cref="NetSuiteDbAccessor.GetAttachmentAsync"/> の結果)。</summary>
    public sealed class NetSuiteAttachmentFile
    {
        internal NetSuiteAttachmentFile(string fileName, byte[] content)
        {
            FileName = fileName;
            Content = content;
        }

        /// <summary>ファイル名 (保存検索の結果の名前)。応答のヘッダに入れるときはホストが安全な形にする。</summary>
        public string FileName { get; }

        /// <summary>中身。</summary>
        public byte[] Content { get; }
    }
}
