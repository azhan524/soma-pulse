using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AhzScribe.Face
{
    // Private lifecycle helper: a second process asks the resident Face to show itself.
    public static class ActivationServer
    {
        public static string PipeName
        {
            get { return "AhzScribe_Face_Activate_v1" + SingleInstance.InstanceSuffix; }
        }
        public static void Start(Action show)
        {
            Task.Run(async delegate
            {
                while (true)
                {
                    try
                    {
                        using (var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None))
                        {
                            await server.WaitForConnectionAsync().ConfigureAwait(false);
                            using (var reader = new StreamReader(server, Encoding.UTF8))
                            {
                                string msg = await reader.ReadLineAsync().ConfigureAwait(false);
                                if (!string.IsNullOrEmpty(msg)) show();
                            }
                        }
                    }
                    catch { try { await Task.Delay(500).ConfigureAwait(false); } catch { } }
                }
            });
        }

        public static bool Activate()
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    client.Connect(1200);
                    using (var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true })
                        writer.WriteLine("show");
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
