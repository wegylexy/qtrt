using Microsoft.Extensions.Configuration;
using System.Buffers;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("linux")]
[assembly: SupportedOSPlatform("windows")]
[assembly: SupportedOSPlatform("osx")]

var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", true, true)
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
for (ushort remotePort = ushort.Parse(configuration["forward:remote"] ?? "80"), localPort = ushort.Parse(configuration["forward:local"] ?? "5000"); ;)
{
    try
    {
        await using var quic = await QuicConnection.ConnectAsync(new()
        {
            ClientAuthenticationOptions = new()
            {
                ApplicationProtocols = new() { new("trt1") },
                RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true,
                TargetHost = "TRT"
            },
            DefaultCloseErrorCode = 0,
            DefaultStreamErrorCode = 1,
            IdleTimeout = TimeSpan.FromSeconds(30),
            MaxInboundBidirectionalStreams = 65535,
            MaxInboundUnidirectionalStreams = 0,
            RemoteEndPoint = new DnsEndPoint(configuration["server:host"] ?? "localhost", ushort.Parse(configuration["server:port"] ?? "7878"))
        }, cts.Token);
        {
            await using var control = await quic.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, cts.Token);
            var buffer = ArrayPool<byte>.Shared.Rent(2);
            try
            {
                MemoryMarshal.Cast<byte, ushort>(buffer.AsSpan(0, 2))[0] = remotePort;
                await control.WriteAsync(buffer.AsMemory(0, 2), true, cts.Token);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            await control.FlushAsync(cts.Token);
            _ = Task.Run(async () => // workaround until .NET supports QUIC's keepalive
            {
                try
                {
                    await using var keepAlive = await quic.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, cts.Token);
                    for (; ; )
                    {
                        await Task.Delay(10000, cts.Token);
                        keepAlive.WriteByte(0);
                    }
                }
                catch { }
            });
        }
        for (; ; )
        {
            var quicStream = await quic.AcceptInboundStreamAsync(cts.Token);
            _ = Task.Run(async () =>
            {
                try
                {
                    await using (quicStream)
                    {
                        using TcpClient client = new(AddressFamily.InterNetwork);
                        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, localPort), cts.Token);
                        await using var tcpStream = client.GetStream();
                        await Task.WhenAll(
                            quicStream.CopyToAsync(tcpStream, client.SendBufferSize, cts.Token)
                                .ContinueWith(_ => quicStream.Abort(QuicAbortDirection.Read, 1)
                                    , cts.Token, TaskContinuationOptions.NotOnRanToCompletion, TaskScheduler.Current),
                            tcpStream.CopyToAsync(quicStream, client.ReceiveBufferSize, cts.Token)
                                .ContinueWith(task =>
                                {
                                    if (task.IsCompletedSuccessfully)
                                    {
                                        quicStream.CompleteWrites();
                                    }
                                    else
                                    {
                                        quicStream.Abort(QuicAbortDirection.Write, 1);
                                    }
                                }, cts.Token)
                        );
                    }
                }
                catch { }
            });
        }
    }
    catch (OperationCanceledException ex) when (ex.CancellationToken == cts.Token)
    {
        break;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex);
        try
        {
            await Task.Delay(5000, cts.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }
}
