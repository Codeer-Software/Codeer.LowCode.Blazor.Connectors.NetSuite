using System.Net;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Server
{
    /// <summary>NetSuite との連携の失敗の種類。</summary>
    public enum NetSuiteErrorKind
    {
        /// <summary>設定の誤り・このコネクターが扱えない操作。</summary>
        NotSupported,
        /// <summary>認証に失敗した (トークンが取れない・401)。</summary>
        Authentication,
        /// <summary>NetSuite 側の権限不足 (403)。</summary>
        PermissionDenied,
        /// <summary>入力・業務ルールのエラー (400 など)。</summary>
        InvalidRequest,
        /// <summary>対象が無い (404)。</summary>
        NotFound,
        /// <summary>同時実行・呼び出し量の制限 (429)。</summary>
        RateLimited,
        /// <summary>NetSuite 側・通信の障害。</summary>
        ServiceUnavailable,
        /// <summary>書き込みを送ったあと応答を受け取れなかった。NetSuite 側で処理されたかどうかは分からない。</summary>
        WriteResultUnknown,
    }

    /// <summary>NetSuite との連携の失敗。</summary>
    public class NetSuiteException : Exception
    {
        public NetSuiteErrorKind Kind { get; }

        /// <summary>HTTP ステータス (応答を受け取っていれば)。</summary>
        public HttpStatusCode? StatusCode { get; }

        /// <summary>NetSuite のエラーコード (o:errorCode)。</summary>
        public string ErrorCode { get; } = string.Empty;

        /// <summary>エラーの対象の項目 (o:errorPath)。</summary>
        public string ErrorPath { get; } = string.Empty;

        public NetSuiteException(string message, NetSuiteErrorKind kind = NetSuiteErrorKind.NotSupported, Exception? innerException = null)
            : base(message, innerException)
        {
            Kind = kind;
        }

        public NetSuiteException(string message, NetSuiteErrorKind kind, HttpStatusCode statusCode, string errorCode, string errorPath)
            : base(message)
        {
            Kind = kind;
            StatusCode = statusCode;
            ErrorCode = errorCode;
            ErrorPath = errorPath;
        }
    }
}
