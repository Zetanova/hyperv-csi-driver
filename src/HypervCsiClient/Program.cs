using System;
using System.Net.Http;
using System.Threading.Tasks;
using csi;
using Grpc.Net.Client;

namespace HypervCsiClient
{
    class Program
    {
        static async Task Main(string[] args)
        {
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

            var httpClientHandler = new HttpClientHandler();
            // Return `true` to allow certificates that are untrusted/invalid
            //httpClientHandler.ServerCertificateCustomValidationCallback =
            //    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            var httpClient = new HttpClient(httpClientHandler);

            using var channel = GrpcChannel.ForAddress("http://127.0.0.1:5216", new GrpcChannelOptions
            {
                HttpClient = httpClient
            });

            await Task.Delay(2000);

            var client = new Identity.IdentityClient(channel);

            var info = await client.GetPluginInfoAsync(new GetPluginInfoRequest { });

            Console.WriteLine($"Plugin Name: {info.Name}");
            Console.ReadLine();
        }
    }
}
