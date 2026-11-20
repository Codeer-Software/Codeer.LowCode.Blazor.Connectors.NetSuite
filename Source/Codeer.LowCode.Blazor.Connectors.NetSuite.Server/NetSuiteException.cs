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
        /// <summary>読んだ後に NetSuite で変わっていて、そのまま書くとほかの変更を壊すので送らなかった (読み直してからやり直す)。</summary>
        Conflict,
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

        /// <summary>
        /// 1 回の一括の追加で、このエラーの前に作れたレコードの internal ID (作った順)。作成済みのレコードは残っている。
        /// このエラーになったレコードは作られていない (<see cref="Kind"/> が <see cref="NetSuiteErrorKind.WriteResultUnknown"/> なら作られたかどうか分からない)、
        /// その後ろのレコードは送っていない。一括の追加でなければ空。
        /// </summary>
        public IReadOnlyList<string> CreatedIds { get; } = Array.Empty<string>();

        /// <summary>
        /// 添付ファイルの処理 (ファイルの登録・添付・外す) の途中で失敗したとき、保存 (作成・更新) まで済んだレコードの internal ID。
        /// レコードの項目は保存済みで、添付はこのエラーの前に済んだ分だけが反映されている。添付の処理の失敗でなければ null。
        /// </summary>
        public string? RecordId { get; }

        /// <summary>
        /// 添付ファイルの処理の途中で失敗したとき、File Cabinet に登録したがレコードに添付できなかった (<see cref="Kind"/> が
        /// <see cref="NetSuiteErrorKind.WriteResultUnknown"/> なら添付できたか分からない) ファイルの internal ID。登録したファイルは自動では消さない。
        /// </summary>
        public IReadOnlyList<string> UnattachedFileIds { get; } = Array.Empty<string>();

        public NetSuiteException(string message, NetSuiteErrorKind kind = NetSuiteErrorKind.NotSupported, Exception? innerException = null)
            : base(message, innerException)
        {
            Kind = kind;
        }

        //一括の追加の途中の失敗 (失敗の中身は innerException と同じ。作れたレコードの ID を足す)
        internal NetSuiteException(string message, NetSuiteException innerException, IReadOnlyList<string> createdIds)
            : base(message, innerException)
        {
            Kind = innerException.Kind;
            StatusCode = innerException.StatusCode;
            ErrorCode = innerException.ErrorCode;
            ErrorPath = innerException.ErrorPath;
            CreatedIds = createdIds;
            RecordId = innerException.RecordId;
            UnattachedFileIds = innerException.UnattachedFileIds;
        }

        //添付の処理の途中の失敗 (失敗の中身は innerException と同じ。保存済みのレコードと添付できなかったファイルを足す)
        internal NetSuiteException(string message, NetSuiteException innerException, string recordId, IReadOnlyList<string> unattachedFileIds)
            : base(message, innerException)
        {
            Kind = innerException.Kind;
            StatusCode = innerException.StatusCode;
            ErrorCode = innerException.ErrorCode;
            ErrorPath = innerException.ErrorPath;
            RecordId = recordId;
            UnattachedFileIds = unattachedFileIds;
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
