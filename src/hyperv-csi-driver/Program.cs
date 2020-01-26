using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HypervCsiDriver
{
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                //.ConfigureHostConfiguration((config) =>
                //{
                //    config.AddEnvironmentVariables();
                //    config.AddCommandLine(args);
                //})
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
                                    o.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
                                });
                            } 
                            else
                            {
                                opt.ListenUnixSocket(csiEP, o => 
                                {
                                    o.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;

                                    o.UseConnectionLogging("CSI");
                                    
                                    //o.Use(next => 
                                    //{
                                    //    return async (ctx) => 
                                    //    {
                                            
                                    //        await (next);
                                    //    };    
                                    //});
                                });
                            }
                        })
                        .UseStartup<Startup>();                        
                });
    }
}
