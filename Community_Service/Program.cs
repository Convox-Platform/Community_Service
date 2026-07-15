using System.Reflection;
using System.Security.Claims;
using System.Text;
using Community_Service.Data;
using Community_Service.Permissions;
using Community_Service.Services;
using Dapper;
using DbUp;
using DotNetEnv;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace Community_Service
{
    public class Program
    {
        public static void Main(string[] args)
        {
            Env.Load();

            var constr = Environment.GetEnvironmentVariable("CONNECTION_STRING")
                ?? throw new ArgumentNullException("CONNECTION_STRING not found");
            var jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET")
                ?? throw new ArgumentNullException("JWT_SECRET not found");
            var origin = Environment.GetEnvironmentVariable("ORIGIN")
                ?? throw new ArgumentNullException("ORIGIN not found");
            var permissionServiceUrl = Environment.GetEnvironmentVariable("PERMISSION_SERVICE_URL")
                ?? throw new ArgumentNullException("PERMISSION_SERVICE_URL not found");
            var grpcReflectionEnabled = string.Equals(
                Environment.GetEnvironmentVariable("GRPC_REFLECTION_ENABLED"),
                "true", StringComparison.OrdinalIgnoreCase);

            // Столбцы snake_case маппятся на PascalCase-свойства сущностей.
            DefaultTypeMap.MatchNamesWithUnderscores = true;
            // user_id (uint64) хранится в bigint через битовый проброс ulong <-> long.
            SqlMapper.AddTypeHandler(new UInt64TypeHandler());

            EnsureDatabase.For.PostgresqlDatabase(constr);
            var upgrader = DeployChanges.To
                .PostgresqlDatabase(constr)
                .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly())
                .LogToConsole()
                .Build();

            var migration = upgrader.PerformUpgrade();
            if (!migration.Successful)
            {
                Console.WriteLine(migration.Error);
                throw migration.Error;
            }

            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddSingleton(NpgsqlDataSource.Create(constr));
            builder.Services.AddScoped<CommunityRepository>();
            builder.Services.AddScoped<MemberRepository>();
            builder.Services.AddScoped<CategoryRepository>();
            builder.Services.AddScoped<ChannelRepository>();
            builder.Services.AddScoped<MeetingRepository>();
            builder.Services.AddScoped<IPermissionGuard, PermissionGuard>();

            builder.Services
                .AddGrpcClient<Permissions.Grpc.PermissionService.PermissionServiceClient>(o =>
                    o.Address = new Uri(permissionServiceUrl));

            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                    policy.WithOrigins(origin)
                        .AllowAnyMethod().AllowAnyHeader()
                        .WithExposedHeaders("Grpc-Status", "Grpc-Message", "Grpc-Encoding", "Grpc-Accept-Encoding")
                        .AllowCredentials());
            });

            builder.Services.AddGrpc();
            builder.Services.AddGrpcReflection();

            // Валидация JWT, выпущенного Auth_Service: тот же секрет, HMAC-SHA256,
            // без проверки issuer/audience. userId лежит в claim NameIdentifier.
            builder.Services
                .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.RequireHttpsMetadata = false;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
                        ValidateIssuer = false,
                        ValidateAudience = false,
                        ValidateLifetime = true,
                        ClockSkew = TimeSpan.FromMinutes(1),
                        NameClaimType = ClaimTypes.NameIdentifier
                    };
                });

            // Каждый вызов требует валидного токена по умолчанию;
            // публичные эндпоинты (health, reflection) помечаются AllowAnonymous.
            builder.Services.AddAuthorization(options =>
            {
                options.FallbackPolicy = new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .Build();
            });

            var app = builder.Build();

            app.UseRouting();
            app.UseCors();
            app.UseGrpcWeb();

            app.UseAuthentication();
            app.UseAuthorization();

            app.MapGrpcService<CommunityGrpcService>().EnableGrpcWeb();
            app.MapGrpcService<ChannelGrpcService>().EnableGrpcWeb();
            app.MapGrpcService<CategoryGrpcService>().EnableGrpcWeb();
            app.MapGrpcService<MeetingGrpcService>().EnableGrpcWeb();

            if (grpcReflectionEnabled)
            {
                app.MapGrpcReflectionService().AllowAnonymous();
            }

            app.MapGet("/", () => "Community Service is running.").AllowAnonymous();

            app.Run();
        }
    }
}
