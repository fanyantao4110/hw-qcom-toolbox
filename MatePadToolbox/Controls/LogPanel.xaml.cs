using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using MatePadToolbox.Services;
using System;
using System.Collections.Generic;
using System.Text;

namespace MatePadToolbox.Controls
{
    /// <summary>
    /// 通用日志输出面板：线程安全，可从任意线程追加文本。
    /// 内部对 UI 刷新做了合批与去重：无论后台线程多快堆入日志，
    /// UI 线程都以批次方式渲染，避免大量日志导致界面卡顿。
    /// 同时把每条日志写入 LogService（落盘到 log 目录）。
    /// </summary>
    public sealed partial class LogPanel : UserControl
    {
        private const int MaxLines = 3000;

        // 缓冲：跨线程追加进队列，UI 线程批次合并刷新
        private readonly object _sync = new();
        private readonly Queue<string> _pending = new();
        private bool _flushScheduled;
        private readonly StringBuilder _buffer = new();
        private int _lineCount;

        public LogPanel()
        {
            this.InitializeComponent();
        }

        /// <summary>追加一行日志（任何线程安全）。</summary>
        public void Append(string line)
        {
            // 始终落盘（日志文件记录全部日志，不丢失）
            try { LogService.Info(string.IsNullOrEmpty(line) ? "(空行)" : line); }
            catch { /* 忽略日志落盘异常 */ }

            lock (_sync)
            {
                _pending.Enqueue(line ?? string.Empty);
                if (_flushScheduled) return;
                _flushScheduled = true;
            }

            var queue = DispatcherQueue;
            try
            {
                queue.TryEnqueue(FlushOnce);
            }
            catch
            {
                lock (_sync) _flushScheduled = false;
            }
        }

        /// <summary>清空日志。</summary>
        public void Clear()
        {
            lock (_sync)
            {
                _pending.Clear();
                _buffer.Clear();
                _lineCount = 0;
            }
            var queue = DispatcherQueue;
            if (queue.HasThreadAccess)
            {
                LogText.Text = string.Empty;
            }
            else
            {
                queue.TryEnqueue(() => LogText.Text = string.Empty);
            }
        }

        /// <summary>
        /// 一次合并刷新：把队列里攒下的行一次性追加到缓冲区并渲染。
        /// 若批次处理期间又有新行，会再次触发，从而把高频日志压缩成
        /// 低频的成批 UI 更新，避免界面卡顿。
        /// </summary>
        private void FlushOnce()
        {
            List<string> batch;
            lock (_sync)
            {
                _flushScheduled = false;
                if (_pending.Count == 0) return;
                batch = new List<string>(_pending);
                _pending.Clear();
            }

            foreach (var line in batch)
            {
                _buffer.Append(line).Append('\n');
            }
            _lineCount += batch.Count;

            // 仅在超出上限时才丢弃最旧行（一次性精确截断，避免每次全量 Split/Join）
            if (_lineCount > MaxLines)
            {
                int drop = _lineCount - MaxLines;
                DropFirstLines(drop);
            }

            LogText.Text = _buffer.ToString();
            try
            {
                ScrollArea.ChangeView(null, ScrollArea.ScrollableHeight, null, disableAnimation: true);
            }
            catch { /* 忽略滚动异常 */ }
        }

        /// <summary>从缓冲区头部丢弃指定行数（超过上限时调用，按换行符精确切分）。</summary>
        private void DropFirstLines(int count)
        {
            if (count <= 0 || _buffer.Length == 0) return;
            int removedNewlines = 0;
            int index = 0;
            while (index < _buffer.Length && removedNewlines < count)
            {
                if (_buffer[index] == '\n') removedNewlines++;
                index++;
            }
            if (index >= _buffer.Length) index = _buffer.Length;
            _buffer.Remove(0, index);
            _lineCount -= removedNewlines;
            if (_lineCount < 0) _lineCount = 0;
        }
    }
}
