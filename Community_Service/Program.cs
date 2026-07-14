
using DotNetEnv;

namespace Community_Service
{
    public class Program
    {
        public static void Main(string[] args)
        {
            Env.Load();
            
            string constr = Environment.GetEnvironmentVariable("CONNECTION_STRING") ?? throw new ArgumentNullException("CONNECTION_STRING not found");
            var sqlSecret = Environment.GetEnvironmentVariable("SQL_SECRET") ??         throw new ArgumentNullException("SQL_SECRET not found");
            var jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET") ??         throw new ArgumentNullException("JWT_SECRET not found");
            
            var origin = Environment.GetEnvironmentVariable("ORIGIN") ?? throw new ArgumentNullException("ORIGIN not found");
            var grpcReflection = Environment.GetEnvironmentVariable("GRPC_REFLECTION_ENABLE") 
                ?? throw new ArgumentNullException("GRPC_REFLECTION_ENABLE not found");


            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(
                    policy => policy.WithOrigins(origin)
                            .AllowAnyMethod().AllowAnyHeader()
                            .WithExposedHeaders("Grpc-Status", "Grpc-Message", "Grpc-Encoding", "Grpc-Accept-Encoding")
                            .AllowCredentials()
                    );
            });

            // Add services to the container.
            builder.Services.AddGrpc();

            var app = builder.Build();


            app.Run();
        }
    }
}