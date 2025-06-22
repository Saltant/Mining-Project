using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace PoW.MiningServer
{
    internal class Program
    {
        readonly static string jwtSecret = ".EG3N@m1mDc3IQ4A^4J^k36X~w!A+S&s";

        static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddSignalR();
            builder.Services.AddLogging(logging => logging.AddConsole());
            builder.Services.AddSingleton(sp => new MiningServer(
                Path.Combine(AppContext.BaseDirectory, "chain.db"),
                jwtSecret,
                sp.GetRequiredService<IHubContext<MiningHub>>(),
                sp.GetRequiredService<ILogger<MiningServer>>()
            ));
            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = false,
                        ValidateAudience = false,
                        ValidateLifetime = true,
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(jwtSecret))
                    };
                });
            var app = builder.Build();

            app.UseAuthentication();
            app.MapHub<MiningHub>("/miningHub");
            await app.RunAsync();
        }
    }
}
