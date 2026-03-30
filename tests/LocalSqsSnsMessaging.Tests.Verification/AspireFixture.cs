using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using TUnit.Core.Interfaces;

namespace LocalSqsSnsMessaging.Tests.Verification;

public sealed class AspireFixture : IAsyncInitializer, IAsyncDisposable
{
    private const string DockerImage = "ghcr.io/justeattakeaway/jet-stack:latest";

    private string? _containerId;

    public int? LocalStackPort { get; private set; }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async Task InitializeAsync()
    {
        var port = GetAvailablePort();

        // Start the container
        using var runProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                ArgumentList =
                {
                    "run", "-d", "--rm",
                    "-p", $"{port}:4566",
                    DockerImage
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        runProcess.Start();
        _containerId = (await runProcess.StandardOutput.ReadToEndAsync()).Trim();
        var stderr = await runProcess.StandardError.ReadToEndAsync();
        await runProcess.WaitForExitAsync();

        if (runProcess.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to start jet-stack container. Exit code: {runProcess.ExitCode}. Stderr: {stderr}");
        }

        LocalStackPort = port;

        // Wait for the container to become healthy
        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri($"http://localhost:{port}"),
            Timeout = TimeSpan.FromSeconds(1)
        };

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
#pragma warning disable CA2234
                using var response = await httpClient.GetAsync("/");
#pragma warning restore CA2234
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
#pragma warning disable CA1031
            catch
            {
                // Container not ready yet
            }
#pragma warning restore CA1031

            await Task.Delay(100);
        }

        throw new TimeoutException($"jet-stack container did not become healthy within 30 seconds on port {port}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_containerId is not null)
        {
            var stopProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    ArgumentList = { "kill", _containerId },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };

            stopProcess.Start();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await stopProcess.WaitForExitAsync(cts.Token);
            stopProcess.Dispose();
        }
    }
}
