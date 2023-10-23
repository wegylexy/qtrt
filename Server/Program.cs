using Microsoft.Extensions.Configuration;
using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

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
await using var quic = await QuicListener.ListenAsync(new()
{
    ApplicationProtocols = new() { new("trt1") },
    ConnectionOptionsCallback = (connection, hello, cancellationToken) =>
        ValueTask.FromResult<QuicServerConnectionOptions>(new()
        {
            DefaultCloseErrorCode = 0,
            DefaultStreamErrorCode = 1,
            IdleTimeout = TimeSpan.FromSeconds(30),
            MaxInboundBidirectionalStreams = 0,
            MaxInboundUnidirectionalStreams = 1,
            ServerAuthenticationOptions = new()
            {
                ApplicationProtocols = new() { new("trt1") },
                ServerCertificateSelectionCallback = (sender, hostName) =>
                {
                    // Creates key
                    using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                    // Requests
                    X500DistinguishedNameBuilder nameBuilder = new();
                    nameBuilder.AddCommonName("TRT");
                    CertificateRequest req = new(nameBuilder.Build(), ec, HashAlgorithmName.SHA256);
                    req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
                    req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
                    if (hostName != null)
                    {
                        SubjectAlternativeNameBuilder sanBuilder = new();
                        sanBuilder.AddDnsName(hostName);
                        req.CertificateExtensions.Add(sanBuilder.Build());
                    }
                    // Signs
                    var now = DateTime.UtcNow;
                    using var crt = req.CreateSelfSigned(now, now.AddDays(14));
                    // Exports
                    return new(crt.Export(X509ContentType.Pfx));
                }
            }
        }),
    ListenEndPoint = new(IPAddress.Parse(configuration["server:address"] ?? "::"), ushort.Parse(configuration["server:port"] ?? "7878"))
}, cts.Token);
Debug.WriteLine("Tunnel started");
try
{
    for (; ; )
    {
        var connection = await quic.AcceptConnectionAsync(cts.Token);
        var quicRemote = connection.RemoteEndPoint;
        _ = Task.Run(async () =>
        {
            try
            {
                await using (connection)
                {
                    await using var control = await connection.AcceptInboundStreamAsync(cts.Token);
                    if (control.Type != QuicStreamType.Unidirectional)
                    {
                        control.Abort(QuicAbortDirection.Both, 1);
                        await connection.CloseAsync(1, cts.Token);
                    }
                    else
                    {
                        int port;
                        {
                            var buffer = ArrayPool<byte>.Shared.Rent(2);
                            try
                            {
                                await control.ReadExactlyAsync(buffer.AsMemory(0, 2), cts.Token);
                                port = MemoryMarshal.Cast<byte, ushort>(buffer.AsSpan(0, 2))[0];
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(buffer);
                            }
                        }
                        if (port == 0 || !control.ReadsClosed.IsCompletedSuccessfully)
                        {
                            control.Abort(QuicAbortDirection.Read, 1);
                            await connection.CloseAsync(1, cts.Token);
                        }
                        else
                        {
                            await control.DisposeAsync();
                            Debug.WriteLine($"Tunnel to {quicRemote.Address} port {port} started");
                        }
                        _ = Task.Run(async () => // workaround until .NET supports QUIC's keepalive
                        {
                            try
                            {
                                await using var keepAlive = await connection.AcceptInboundStreamAsync(cts.Token);
                                while (keepAlive.ReadByte() != -1) { }
                            }
                            catch { }
                        });
                        try
                        {
                            TcpListener tcp = new(IPAddress.IPv6Any, port);
                            tcp.Start();
                            port = ((IPEndPoint)tcp.LocalEndpoint).Port;
                            for (; ; )
                            {
                                var client = await tcp.AcceptTcpClientAsync(cts.Token);
                                var tcpRemote = (IPEndPoint)client.Client.RemoteEndPoint!;
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        using (client)
                                        {
                                            Debug.WriteLine($"Tunnel from {tcpRemote.Address} to {quicRemote.Address} port {port} started");
                                            using var tcpStream = client.GetStream();
                                            await using var quicStream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cts.Token);
                                            await Task.WhenAll(
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
                                                    }, cts.Token),
                                                quicStream.CopyToAsync(tcpStream, client.SendBufferSize, cts.Token)
                                                    .ContinueWith(task => quicStream.Abort(QuicAbortDirection.Read, 1),
                                                        cts.Token, TaskContinuationOptions.NotOnRanToCompletion, TaskScheduler.Current)
                                            );
                                        }
                                    }
                                    catch { }
                                    finally
                                    {
                                        Debug.WriteLine($"Tunnel from {tcpRemote.Address} to {quicRemote.Address} port {port} stopped");
                                    }
                                });
                            }
                        }
                        finally
                        {
                            Debug.WriteLine($"Tunnel to {quicRemote.Address} port {port} stopped");
                        }
                    }
                }
            }
            catch { }
        }, default);
    }
}
catch (OperationCanceledException ex) when (ex.CancellationToken == cts.Token) { }
finally
{
    Debug.WriteLine("Tunnel stopped");
}
