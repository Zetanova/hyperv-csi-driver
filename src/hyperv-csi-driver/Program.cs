using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net;

namespace HypervCsiDriver
{
    public class Program
    {
        public static void Main(string[] args)
        {
            //required until powershell 7.2+
            AppContext.SetSwitch("System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization", true);

            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder
                        .UseKestrel(opt =>
                        {
                            var config = opt.ApplicationServices.GetRequiredService<IConfiguration>();
                            var logger = opt.ApplicationServices.GetRequiredService<ILogger<IWebHostBuilder>>();

                            var csiEP = config["CSI_ENDPOINT"];
                            if (string.IsNullOrEmpty(csiEP))
                            {
                                //todo debug switch to listen on TCP
                                logger.LogWarning("CSI_ENDPOINT required");


                                //opt.ListenLocalhost(5216, o =>
                                //{
                                //    o.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
                                //});
                                opt.Listen(IPAddress.Loopback, 5216, o =>
                                {
                                    o.UseConnectionLogging("CSI");

                                    //o.UseHttps("mycert.pfx", "pass1234");

                                    o.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
                                });
                            }
                            else
                            {
                                //unlink socket
                                if (File.Exists(csiEP))
                                {
                                    File.Delete(csiEP);
                                    logger.LogWarning($"socket '{csiEP}' unlinked");
                                }

                                opt.ListenUnixSocket(csiEP, o =>
                                {
                                    o.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;

                                    //o.UseConnectionLogging("CSI");
                                });
                            }
                        })
                        .UseStartup<Startup>();
                });
    }
}
