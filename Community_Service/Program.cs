
using Community_Service.Migration;
using Community_Service.Services;
using DotNetEnv;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using System.Data.Common;
using System.Text;

namespace Community_Service
{
    public class Program
    {
        public static void Main(string[] args)
        {
            Env.Load();

            
            string conStr = Environment.GetEnvironmentVariable("CONNECTION_STRING") ?? throw new ArgumentNullException("CONNECTION_STRING not found");
            var sqlSecret = Environment.GetEnvironmentVariable("SQL_SECRET") ??         throw new ArgumentNullException("SQL_SECRET not found");
            var jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET") ??         throw new ArgumentNullException("JWT_SECRET not found");
            
            var origin = Environment.GetEnvironmentVariable("ORIGIN") ?? throw new ArgumentNullException("ORIGIN not found");
            var isReflectionEnabled = Environment.GetEnvironmentVariable("GRPC_REFLECTION_ENABLE") 
                ?? throw new ArgumentNullException("GRPC_REFLECTION_ENABLE not found");

            var generator = new IdGen.IdGenerator(1000);
            var builder = WebApplication.CreateBuilder(args);
            
            CreateDb.CreateDatabase(conStr, sqlSecret);

            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(
                    policy => policy.WithOrigins(origin)
                            .AllowAnyMethod().AllowAnyHeader()
                            .WithExposedHeaders("Grpc-Status", "Grpc-Message", "Grpc-Encoding", "Grpc-Accept-Encoding")
                            .AllowCredentials()
                    );
            });

            builder.Services.AddAuthorization();
            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
                {
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret))
                };
            });

            // Add services to the container.
            builder.Services.AddGrpc();
            builder.Services.AddTransient<DbConnection>(sp => new NpgsqlConnection(conStr));
            builder.Services.AddSingleton<IdGen.IdGenerator>(sp => generator);


            if (isReflectionEnabled != null && isReflectionEnabled == "true")
            {
                builder.Services.AddGrpcReflection();
            }

            var app = builder.Build();

            app.UseRouting();
            app.UseCors();

            app.UseAuthentication();
            app.UseAuthorization();


            app.UseGrpcWeb();

            app.MapGrpcReflectionService();
            app.MapGrpcService<ServerService>().EnableGrpcWeb();
            app.MapGrpcService<ChannelService>().EnableGrpcWeb();

            app.Run();
        }
    }
}