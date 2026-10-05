using System;
using System.IO;
using System.Text;

namespace MatePadToolbox.Services
{
    /// <summary>
    /// 全局日志服务：把应用从启动到关闭产生的一切日志写入 log\ 目录。
    /// 线程安全，可从任意线程调用；写失败时静默降级，不影响主流程。
    /// </summary>
    public static class LogService
    {
        private static readonly object Gate = new();
        private static string? _file;

        static LogService()
        {
            try
            {
                Directory.CreateDirectory(AppConfig.LogDir);
                _file = Path.Combine(AppConfig.LogDir, $"run_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                Write("========== 应用启动 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ==========");
            }
            catch
            {
                _file = null; // 无法写日志时静默降级，不影响主流程
            }
        }

        /// <summary>记录一条普通日志。</summary>
        public static void Info(string message)
            => Write($"[{DateTime.Now:HH:mm:ss.fff}] {message}");

        /// <summary>记录一条错误日志。</summary>
        public static void Error(string message)
            => Write($"[{DateTime.Now:HH:mm:ss.fff}][错误] {message}");

        /// <summary>记录一条异常（含堆栈）。</summary>
        public static void Exception(string context, Exception? ex)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append($"[{DateTime.Now:HH:mm:ss.fff}][异常] {context}");
                if (ex != null)
                {
                    sb.AppendLine();
                    sb.Append(ex);
                }
                Write(sb.ToString());
            }
            catch { /* 忽略 */ }
        }

        /// <summary>记录应用退出原因。</summary>
        public static void Shutdown(string reason)
            => Write($"========== 应用退出（{reason}） " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ==========");

        private static void Write(string line)
        {
            try
            {
                lock (Gate)
                {
                    if (_file == null) return;
                    File.AppendAllText(_file, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { /* 忽略写日志异常 */ }
        }
    }
}
