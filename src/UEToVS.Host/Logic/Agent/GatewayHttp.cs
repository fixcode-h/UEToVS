using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UEToVS.Host.Logic.Agent
{
    /// <summary>A parsed HTTP/1.1 request (the gateway's minimal server; see <see cref="AgentGateway"/>).</summary>
    public sealed class GatewayRequest
    {
        public string Method { get; set; }
        /// <summary>Path without the query string.</summary>
        public string Path { get; set; }
        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body { get; set; } = Array.Empty<byte>();

        public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
        public string BodyText => Encoding.UTF8.GetString(Body);
    }

    /// <summary>
    /// Just enough HTTP/1.1 for VS's OpenAI / Ollama clients on loopback: one request per connection, Content-Length or
    /// chunked request bodies, responses either with a length or close-delimited (streaming). Kept free of HttpListener
    /// on purpose: http.sys needs a URL ACL (admin) for non-localhost prefixes, a raw socket on 127.0.0.1 needs nothing.
    /// </summary>
    public static class GatewayHttp
    {
        public const int MaxHeaderBytes = 64 * 1024;
        public const int MaxBodyBytes = 64 * 1024 * 1024;

        /// <summary>Reads one request; null when the peer closed before sending a request line.</summary>
        public static async Task<GatewayRequest> ReadRequestAsync(Stream stream, CancellationToken ct)
        {
            var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                int n = await stream.ReadAsync(chunk, 0, chunk.Length, ct).ConfigureAwait(false);
                if (n <= 0)
                {
                    if (buffer.Length == 0) return null;
                    throw new InvalidDataException("connection closed inside the request headers");
                }
                await buffer.WriteAsync(chunk, 0, n, ct).ConfigureAwait(false);
                headerEnd = IndexOfHeaderEnd(buffer.GetBuffer(), (int)buffer.Length);
                if (headerEnd < 0 && buffer.Length > MaxHeaderBytes) throw new InvalidDataException("request headers too large");
            }
            byte[] all = buffer.ToArray();
            string head = Encoding.ASCII.GetString(all, 0, headerEnd);
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var first = lines[0].Split(' ');
            if (first.Length < 2) throw new InvalidDataException("bad request line");
            var req = new GatewayRequest { Method = first[0].ToUpperInvariant() };
            string target = first[1];
            int q = target.IndexOf('?');
            req.Path = q >= 0 ? target.Substring(0, q) : target;
            for (int i = 1; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf(':');
                if (c <= 0) continue;
                req.Headers[lines[i].Substring(0, c).Trim()] = lines[i].Substring(c + 1).Trim();
            }

            // Body bytes already read past the header block.
            var rest = new MemoryStream();
            int bodyStart = headerEnd + 4;
            await rest.WriteAsync(all, bodyStart, all.Length - bodyStart, ct).ConfigureAwait(false);
            string te = req.Header("Transfer-Encoding");
            if (te != null && te.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
                req.Body = await ReadChunkedAsync(stream, rest, ct).ConfigureAwait(false);
            else
            {
                long length = 0;
                if (req.Header("Content-Length") is string cl && !long.TryParse(cl, NumberStyles.None, CultureInfo.InvariantCulture, out length))
                    throw new InvalidDataException("bad Content-Length");
                if (length > MaxBodyBytes) throw new InvalidDataException("request body too large");
                while (rest.Length < length)
                {
                    int n = await stream.ReadAsync(chunk, 0, chunk.Length, ct).ConfigureAwait(false);
                    if (n <= 0) throw new InvalidDataException("connection closed inside the request body");
                    await rest.WriteAsync(chunk, 0, n, ct).ConfigureAwait(false);
                }
                var body = rest.ToArray();
                if (body.Length > length) Array.Resize(ref body, (int)length);
                req.Body = body;
            }
            return req;
        }

        private static int IndexOfHeaderEnd(byte[] b, int length)
        {
            for (int i = 0; i + 3 < length; i++)
                if (b[i] == '\r' && b[i + 1] == '\n' && b[i + 2] == '\r' && b[i + 3] == '\n') return i;
            return -1;
        }

        private static async Task<byte[]> ReadChunkedAsync(Stream stream, MemoryStream pending, CancellationToken ct)
        {
            // Simple approach: keep reading until the terminating "0\r\n\r\n" is in the buffer, then decode it.
            var chunk = new byte[8192];
            var body = new MemoryStream();
            byte[] data = pending.ToArray();
            int pos = 0;
            while (true)
            {
                int lineEnd = IndexOf(data, pos, (byte)'\r', (byte)'\n');
                if (lineEnd < 0) { data = await MoreAsync(stream, data, chunk, ct).ConfigureAwait(false); continue; }
                string sizeText = Encoding.ASCII.GetString(data, pos, lineEnd - pos);
                int semi = sizeText.IndexOf(';');
                if (semi >= 0) sizeText = sizeText.Substring(0, semi);
                if (!int.TryParse(sizeText.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int size) || size < 0)
                    throw new InvalidDataException("bad chunk size");
                if (size == 0) return body.ToArray();
                int start = lineEnd + 2;
                while (data.Length < start + size + 2) data = await MoreAsync(stream, data, chunk, ct).ConfigureAwait(false);
                await body.WriteAsync(data, start, size, ct).ConfigureAwait(false);
                if (body.Length > MaxBodyBytes) throw new InvalidDataException("request body too large");
                pos = start + size + 2;
            }
        }

        private static async Task<byte[]> MoreAsync(Stream stream, byte[] data, byte[] chunk, CancellationToken ct)
        {
            int n = await stream.ReadAsync(chunk, 0, chunk.Length, ct).ConfigureAwait(false);
            if (n <= 0) throw new InvalidDataException("connection closed inside a chunked body");
            var next = new byte[data.Length + n];
            Buffer.BlockCopy(data, 0, next, 0, data.Length);
            Buffer.BlockCopy(chunk, 0, next, data.Length, n);
            return next;
        }

        private static int IndexOf(byte[] b, int from, byte x, byte y)
        {
            for (int i = from; i + 1 < b.Length; i++) if (b[i] == x && b[i + 1] == y) return i;
            return -1;
        }

        /// <summary>Status line + headers. <paramref name="contentLength"/> null = close-delimited body (streaming).</summary>
        public static Task WriteHeadAsync(Stream stream, int status, string reason, string contentType, long? contentLength, CancellationToken ct)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(string.IsNullOrEmpty(reason) ? "Status" : reason).Append("\r\n");
            if (!string.IsNullOrEmpty(contentType)) sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            if (contentLength.HasValue) sb.Append("Content-Length: ").Append(contentLength.Value.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("Cache-Control: no-cache\r\n");
            sb.Append("Connection: close\r\n\r\n");
            var bytes = Encoding.ASCII.GetBytes(sb.ToString());
            return stream.WriteAsync(bytes, 0, bytes.Length, ct);
        }

        public static async Task WriteJsonAsync(Stream stream, int status, string json, CancellationToken ct)
        {
            var body = Encoding.UTF8.GetBytes(json);
            await WriteHeadAsync(stream, status, ReasonOf(status), "application/json; charset=utf-8", body.Length, ct).ConfigureAwait(false);
            await stream.WriteAsync(body, 0, body.Length, ct).ConfigureAwait(false);
        }

        public static string ReasonOf(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 500: return "Internal Server Error";
                case 502: return "Bad Gateway";
                case 504: return "Gateway Timeout";
                default: return "Status";
            }
        }
    }
}
