using System.Text;

namespace TokenMonitor.Core.Proxy;

/// <summary>
/// SSE 行读取器（02-§1.5）：逐行读取上游流，行缓冲上限 maxLineBytes（对齐原 scanner.Buffer(1KB, 8MB)），
/// 超限抛 LineTooLongException（→ 按漏抓登记）；支持 \n 与 \r\n（转发时归一为 \n，保持原实现语义）。
/// </summary>
internal sealed class LineStreamReader(Stream stream, int maxLineBytes) : IDisposable
{
    internal sealed class LineTooLongException : Exception
    {
        public LineTooLongException(string message) : base(message) { }
    }

    private readonly byte[] _buf = new byte[64 * 1024];
    private int _bufLen;
    private int _bufPos;
    private byte[] _line = new byte[1024];
    private int _lineLen;
    private bool _eof;

    /// <summary>读取一行（不含行尾）。流结束返回 null；取消抛 OperationCanceledException；超长行抛 LineTooLongException。</summary>
    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            // 在缓冲中找 '\n'
            for (var i = _bufPos; i < _bufLen; i++)
            {
                if (_buf[i] != (byte)'\n') continue;
                var len = i - _bufPos;
                EnsureLineCapacity(_lineLen + len);
                Array.Copy(_buf, _bufPos, _line, _lineLen, len);
                _lineLen += len;
                _bufPos = i + 1;
                return TakeLine();
            }
            // 缓冲无换行：把剩余部分并入行缓冲
            var rest = _bufLen - _bufPos;
            if (rest > 0)
            {
                EnsureLineCapacity(_lineLen + rest);
                Array.Copy(_buf, _bufPos, _line, _lineLen, rest);
                _lineLen += rest;
                _bufPos = _bufLen;
            }
            if (_eof) return _lineLen > 0 ? TakeLine() : null; // 尾行无换行
            if (_lineLen > maxLineBytes) throw new LineTooLongException($"SSE 行超过 {maxLineBytes} 字节上限");
            await FillAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task FillAsync(CancellationToken ct)
    {
        if (_bufPos == _bufLen)
        {
            _bufPos = 0;
            _bufLen = 0;
        }
        else if (_bufPos > 0)
        {
            Array.Copy(_buf, _bufPos, _buf, 0, _bufLen - _bufPos);
            _bufLen -= _bufPos;
            _bufPos = 0;
        }
        var read = await stream.ReadAsync(_buf.AsMemory(_bufLen, _buf.Length - _bufLen), ct).ConfigureAwait(false);
        if (read == 0)
        {
            _eof = true;
            return;
        }
        _bufLen += read;
    }

    private string TakeLine()
    {
        var len = _lineLen;
        _lineLen = 0;
        if (len > 0 && _line[len - 1] == (byte)'\r') len--; // \r\n 归一
        return Encoding.UTF8.GetString(_line, 0, len);
    }

    private void EnsureLineCapacity(int required)
    {
        if (_line.Length >= required) return;
        var newSize = _line.Length * 2;
        while (newSize < required) newSize *= 2;
        Array.Resize(ref _line, newSize);
    }

    public void Dispose() => stream.Dispose();
}
