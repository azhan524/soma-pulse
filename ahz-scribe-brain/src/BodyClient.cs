using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AhzScribe.Contracts;

namespace AhzScribe.Brain
{
    /// <summary>
    /// Brain-side client for ahz-scribe-body. It has no file/shell/desktop API.
    /// </summary>
    public sealed class BodyClient : IActionExecutor, ICapabilitySource, IDisposable
    {
        public static readonly string DefaultPipeName = Environment.GetEnvironmentVariable("AHZ_BODY_PIPE") ?? "ahz-scribe-body";
        readonly string _pipeName;
        readonly JsonSerializerOptions _json;

        public BodyClient(string pipeName = null)
        {
            _pipeName = string.IsNullOrEmpty(pipeName) ? DefaultPipeName : pipeName;
            _json = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Converters = { new JsonStringEnumConverter() }
            };
        }

        public async Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken ct)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            BodyReply reply = await SendAsync(new BodyEnvelope { Action = "execute", Request = request }, ct).ConfigureAwait(false);
            if (reply.Result == null) throw new IOException(reply.Error ?? "body returned no result");
            return reply.Result;
        }

        public async Task<List<CapabilityDescriptor>> GetCapabilitiesAsync(CancellationToken ct)
        {
            BodyReply reply = await SendAsync(new BodyEnvelope { Action = "capabilities" }, ct).ConfigureAwait(false);
            if (!reply.Ok) throw new IOException(reply.Error ?? "body capability query failed");
            return reply.Capabilities ?? new List<CapabilityDescriptor>();
        }

        async Task<BodyReply> SendAsync(BodyEnvelope env, CancellationToken ct)
        {
            NamedPipeClientStream pipe = null;
            StreamWriter w = null;
            StreamReader r = null;
            try
            {
                pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(3000, ct).ConfigureAwait(false);

                string line = JsonSerializer.Serialize(env, _json);

                w = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                r = new StreamReader(pipe, Encoding.UTF8);
                await w.WriteLineAsync(line).ConfigureAwait(false);
                string response = await r.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(response)) throw new IOException("body returned empty response");
                var reply = JsonSerializer.Deserialize<BodyReply>(response, _json);
                if (reply == null) throw new IOException("body returned invalid reply");
                return reply;
            }
            finally
            {
                try { if (w != null) w.Dispose(); } catch { }
                try { if (r != null) r.Dispose(); } catch { }
                try { if (pipe != null) pipe.Dispose(); } catch { }
            }
        }

        public void Dispose() { }
    }
}
