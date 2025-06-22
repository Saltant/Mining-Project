using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace PoW.MiningPool
{
    internal class Program
    {
        static readonly string jwtSecret = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJwb29sSWQiOiJwb29sMSIsIm5iZiI6MTc0NjM1ODAyOCwiZXhwIjoxNzc3ODk0MDI4LCJpYXQiOjE3NDYzNTgwMjh9.DhBQ29ZyFG_htZL9KE_DSYYXFNvPSkIfxfPRAbqolFY";
        static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddSignalR();
            builder.Services.AddLogging(logging => logging.AddConsole());
            builder.Services.AddSingleton(sp => new MiningPool(
                "http://localhost:5000/miningHub",
                "pool1",
                jwtSecret,
                sp.GetRequiredService<IHubContext<PoolHub>>(),
                sp.GetRequiredService<ILogger<MiningPool>>()
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

            app.Services.GetService<MiningPool>();

            app.UseAuthentication();
            app.MapHub<PoolHub>("/poolHub");
            await app.RunAsync("http://192.168.0.101:12000");
        }
    }
}
