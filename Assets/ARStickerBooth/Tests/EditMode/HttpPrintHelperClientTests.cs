using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using PhotoBooth.Booth.Printing;

namespace PhotoBooth.Booth.Tests.EditMode
{
    public sealed class HttpPrintHelperClientTests
    {
        [Test]
        public async Task PrintAsync_WhenBridgeReturnsSuccess_MapsSuccessfulResult()
        {
            using var server = await TestPrintBridgeServer.StartAsync(200, "{\"success\":true,\"retryable\":false,\"message\":\"Print job submitted.\",\"printer_name\":\"XP-420B\",\"operation_id\":\"op-1\"}");
            var client = new HttpPrintHelperClient(server.BaseUrl, 2);

            var result = await client.PrintAsync(new PrintJobRequest
            {
                JobId = "JOB-1",
                ImagePath = "/tmp/final.png",
                PrinterName = "XP-420B",
                Copies = 1
            });

            Assert.That(result.Success, Is.True);
            Assert.That(result.Retryable, Is.False);
            Assert.That(result.PrinterName, Is.EqualTo("XP-420B"));
            Assert.That(result.OperationId, Is.EqualTo("op-1"));
        }

        [Test]
        public async Task PrintAsync_WhenBridgeReturnsRetryableFailure_MapsRetryableResult()
        {
            using var server = await TestPrintBridgeServer.StartAsync(502, "{\"success\":false,\"retryable\":true,\"message\":\"printer offline\",\"printer_name\":\"XP-420B\",\"operation_id\":null}");
            var client = new HttpPrintHelperClient(server.BaseUrl, 2);

            var result = await client.PrintAsync(new PrintJobRequest
            {
                JobId = "JOB-1",
                ImagePath = "/tmp/final.png",
                PrinterName = "XP-420B",
                Copies = 1
            });

            Assert.That(result.Success, Is.False);
            Assert.That(result.Retryable, Is.True);
            Assert.That(result.Message, Is.EqualTo("printer offline"));
        }

        [Test]
        public async Task PrintAsync_WhenBridgeReturnsHardFailure_MapsHardFailureResult()
        {
            using var server = await TestPrintBridgeServer.StartAsync(403, "{\"success\":false,\"retryable\":false,\"message\":\"Printer is not allowed by this bridge.\",\"printer_name\":\"Other\",\"operation_id\":null}");
            var client = new HttpPrintHelperClient(server.BaseUrl, 2);

            var result = await client.PrintAsync(new PrintJobRequest
            {
                JobId = "JOB-1",
                ImagePath = "/tmp/final.png",
                PrinterName = "Other",
                Copies = 1
            });

            Assert.That(result.Success, Is.False);
            Assert.That(result.Retryable, Is.False);
            Assert.That(result.Message, Is.EqualTo("Printer is not allowed by this bridge."));
        }

        private sealed class TestPrintBridgeServer : IDisposable
        {
            private readonly HttpListener listener;
            private readonly Task serveTask;

            private TestPrintBridgeServer(HttpListener listener, Task serveTask, string baseUrl)
            {
                this.listener = listener;
                this.serveTask = serveTask;
                BaseUrl = baseUrl;
            }

            public string BaseUrl { get; }

            public static Task<TestPrintBridgeServer> StartAsync(int statusCode, string responseBody)
            {
                var port = FindFreePort();
                var baseUrl = $"http://127.0.0.1:{port}";
                var listener = new HttpListener();
                listener.Prefixes.Add($"{baseUrl}/");
                listener.Start();
                var serveTask = Task.Run(async () =>
                {
                    var context = await listener.GetContextAsync();
                    var bytes = Encoding.UTF8.GetBytes(responseBody);
                    context.Response.StatusCode = statusCode;
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                    context.Response.Close();
                });

                return Task.FromResult(new TestPrintBridgeServer(listener, serveTask, baseUrl));
            }

            public void Dispose()
            {
                listener.Close();
                try
                {
                    serveTask.Wait(TimeSpan.FromSeconds(1));
                }
                catch
                {
                    // Test cleanup only.
                }
            }

            private static int FindFreePort()
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                return port;
            }
        }
    }
}
