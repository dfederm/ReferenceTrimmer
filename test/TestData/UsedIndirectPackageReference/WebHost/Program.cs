namespace WebHost
{
    using System.IO;
    using System.Net;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.AspNetCore.Http;

    public class Program
    {
        public static Task Main()
        {
            return new WebHostBuilder()
                .UseKestrel(options => options.Listen(IPAddress.Loopback, 5001))
                .UseContentRoot(Directory.GetCurrentDirectory())
                .Configure(app =>
                    app.Run(async context =>
                    {
                        await context.Response.WriteAsync("Hello World!");
                    }))
                .Build()
                .RunAsync();
        }
    }
}
