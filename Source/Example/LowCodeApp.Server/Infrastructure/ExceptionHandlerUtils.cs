using Codeer.LowCode.Blazor;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DbAccess;
using Codeer.LowCode.Blazor.Connectors.NetSuite.Server;
using Microsoft.AspNetCore.Diagnostics;
using System.Net;

namespace LowCodeApp.Server.Infrastructure
{
    /// <summary>
    /// 例外を利用者に返すときの方針。本体の例外 (LowCodeException。検証・楽観ロック・権限などの利用者向けの文言) はそのまま、
    /// 一意制約違反は利用者向けの文言に、それ以外 (DB の例外など。表名や値を含む) は開発時だけ原文、本番は固定の文言 + リクエスト Id (原文はログで引く)。
    /// NetSuite の失敗は本番では種類ごとの固定の文言 (結果が分からない・読み直しが要る・作成済みの internal ID など) + リクエスト Id。
    /// </summary>
    public static class ExceptionHandlerUtils
    {
        //未処理の例外 (一覧の読み出しなど)。原文は UseExceptionHandler がログに出す
        public static void UseExceptionHandlerSendToFront(this WebApplication app)
        {
            app.UseExceptionHandler(errorApp =>
            {
                errorApp.Run(async context =>
                {
                    var ex = context.Features.Get<IExceptionHandlerPathFeature>()?.Error;
                    if (ex == null) return;
                    context.Response.ContentType = "text/plain";
                    context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    await context.Response.WriteAsync(ToClientMessage(ex, context));
                });
            });
        }

        //保存の結果 (ModuleSubmitResult.ExceptionMessage)。保存の失敗は例外にならないので原文はここでログに出す
        public static List<ModuleSubmitResult> ToClientMessage(this List<ModuleSubmitResult> results, HttpContext context)
        {
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(ExceptionHandlerUtils));
            foreach (var ex in results.Select(e => e.Exception).OfType<Exception>().Distinct().Where(e => !IsUserFacing(e)))
            {
                logger.LogError(ex, "Submit failed. TraceIdentifier={TraceIdentifier}", context.TraceIdentifier);
            }
            foreach (var result in results.Where(e => e.Exception != null))
            {
                result.ExceptionMessage = ToClientMessage(result.Exception!, context);
            }
            return results;
        }

        //利用者の操作で普通に起きる失敗 (ログに残すものではない)
        static bool IsUserFacing(Exception ex) => ex is LowCodeException || DbErrors.IsUniqueViolation(ex);

        static string ToClientMessage(Exception ex, HttpContext context)
        {
            if (ex is LowCodeException) return ex.Message;
            if (DbErrors.IsUniqueViolation(ex)) return "同じ値のデータが既にあります。";
            if (context.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment()) return ex.GetMessages();
            if (Find<NetSuiteException>(ex) is { } netSuite) return ToClientMessage(netSuite, context);
            return $"サーバーでエラーが発生しました。(Id: {context.TraceIdentifier})";
        }

        //NetSuite の失敗は、利用者が次に何をすればよいかが分かる固定の文言 + リクエスト Id (原文はログで引く)。
        //一括の追加の途中で失敗したときは、作成済みの internal ID と、失敗した 1 件・送っていない残りを分けて伝える
        static string ToClientMessage(NetSuiteException ex, HttpContext context)
        {
            //レコードは保存したがファイルの添付が途中で失敗した (作成・更新の 1 件)
            if (ex.RecordId != null && ex.CreatedIds.Count == 0) return $"{AttachmentFailureText(ex)} (Id: {context.TraceIdentifier})";
            var text = ex.Kind switch
            {
                NetSuiteErrorKind.WriteResultUnknown => "NetSuite への保存の結果が分かりません。NetSuite で保存されたかを確かめてから、必要ならやり直してください。",
                NetSuiteErrorKind.Conflict => "NetSuite のデータが読み込んだ後に変わっています。読み込み直してからやり直してください。",
                NetSuiteErrorKind.InvalidRequest => "NetSuite が保存を受け付けませんでした。入力を確かめてください。" + (string.IsNullOrEmpty(ex.ErrorPath) ? string.Empty : $"(項目: {ex.ErrorPath})"),
                NetSuiteErrorKind.PermissionDenied => "NetSuite の権限が足りません。",
                NetSuiteErrorKind.RateLimited => "NetSuite が混み合っています。時間をおいてやり直してください。",
                //書き込みへの 503 は、NetSuite が処理を始めていないと言い切れる資料が無い。やり直す前に確かめてもらう
                NetSuiteErrorKind.ServiceUnavailable => "NetSuite が応答できない状態です。保存の操作だったときは、NetSuite で保存されていないことを確かめてから、時間をおいてやり直してください。",
                _ => "NetSuite との連携でエラーが発生しました。",
            };
            if (ex.RecordId != null)
            {
                //一括の追加で、最後に作ったレコードの添付が途中で失敗した
                text = $"この保存で {ex.CreatedIds.Count} 件は NetSuite に作成済みです (internal ID: {string.Join(", ", ex.CreatedIds)})。" +
                    AttachmentFailureText(ex) + "それより後は送っていません。作成済みのものを確かめてから、残りを保存してください。";
            }
            else if (ex.CreatedIds.Count != 0)
            {
                text += $" この保存で {ex.CreatedIds.Count} 件は NetSuite に作成済みです (internal ID: {string.Join(", ", ex.CreatedIds)})。" +
                    (ex.Kind == NetSuiteErrorKind.WriteResultUnknown ? "その次の 1 件は作成されたかどうか分かりません。" : "その次の 1 件は作成されていません。") +
                    "それより後は送っていません。作成済みのものを確かめてから、残りを保存してください。";
            }
            return $"{text} (Id: {context.TraceIdentifier})";
        }

        //添付の途中の失敗: レコードは保存済み。同じ保存をやり直すとファイルが二重になるので、確かめてから足りない分だけ足し直してもらう
        static string AttachmentFailureText(NetSuiteException ex)
        {
            var text = $"NetSuite のレコード (internal ID: {ex.RecordId}) は保存しましたが、ファイルの添付が途中で失敗しました。" +
                "レコードを開き直してファイルを確かめ、足りないファイルだけを足し直してください。";
            if (ex.UnattachedFileIds.Count != 0)
                text += $"File Cabinet に登録したがレコードに付いていない (または付いたか分からない) ファイルがあります (file ID: {string.Join(", ", ex.UnattachedFileIds)})。";
            return text;
        }

        static T? Find<T>(Exception? ex) where T : Exception
        {
            for (; ex != null; ex = ex.InnerException)
            {
                if (ex is T found) return found;
            }
            return null;
        }

        static string GetMessages(this Exception? ex)
        {
            var list = new List<string>();
            while (ex != null)
            {
                list.Add(ex.Message);
                ex = ex.InnerException;
            }
            return string.Join(Environment.NewLine, list);
        }
    }
}
