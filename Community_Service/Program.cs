using System.Reflection;
using System.Security.Claims;
using System.Text;
using Community_Service.Data;
using Community_Service.Events;
using Community_Service.Ids;
using Community_Service.Invites;
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
            var origins = (Environment.GetEnvironmentVariable("ORIGINS") ?? origin)
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var permissionServiceUrl = Environment.GetEnvironmentVariable("PERMISSION_SERVICE_URL")
                ?? throw new ArgumentNullException("PERMISSION_SERVICE_URL not found");
            var presenceServiceUrl = Environment.GetEnvironmentVariable("PRESENCE_SERVICE_URL")
                ?? throw new ArgumentNullException("PRESENCE_SERVICE_URL not found");
            var voiceServiceUrl = Environment.GetEnvironmentVariable("VOICE_SERVICE_URL")
                ?? throw new ArgumentNullException("VOICE_SERVICE_URL not found");
            var messageServiceUrl = Environment.GetEnvironmentVariable("MESSAGE_SERVICE_URL")
                ?? throw new ArgumentNullException("MESSAGE_SERVICE_URL not found");
            var amqpUrl = Environment.GetEnvironmentVariable("AMQP_URL")
                ?? throw new ArgumentNullException("AMQP_URL not found");
            var workerIdValue = Environment.GetEnvironmentVariable("SNOWFLAKE_WORKER_ID") ?? "0";
            if (!int.TryParse(workerIdValue, out var workerId) ||
                workerId is < 0 or > SnowflakeIdGenerator.MaxWorkerId)
            {
                throw new ArgumentOutOfRangeException(
                    "SNOWFLAKE_WORKER_ID",
                    workerIdValue,
                    $"SNOWFLAKE_WORKER_ID must be an integer between 0 and {SnowflakeIdGenerator.MaxWorkerId}");
            }
            var rabbitMqExchange = Environment.GetEnvironmentVariable("RABBITMQ_EXCHANGE")
                ?? RabbitMqOptions.DefaultExchange;
            var grpcReflectionEnabled = string.Equals(
                Environment.GetEnvironmentVariable("GRPC_REFLECTION_ENABLED"),
                "true", StringComparison.OrdinalIgnoreCase);

            // Столбцы snake_case маппятся на PascalCase-свойства сущностей.
            DefaultTypeMap.MatchNamesWithUnderscores = true;
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
            builder.Services.AddSingleton(new SnowflakeIdGenerator(workerId));
            builder.Services.AddSingleton<IInviteCodeGenerator, InviteCodeGenerator>();
            builder.Services.AddScoped<CommunityRepository>();
            builder.Services.AddScoped<MemberRepository>();
            builder.Services.AddScoped<CategoryRepository>();
            builder.Services.AddScoped<ChannelRepository>();
            builder.Services.AddScoped<MeetingRepository>();
            builder.Services.AddScoped<RecordingActivityRepository>();
            builder.Services.AddScoped<InviteRepository>();
            builder.Services.AddSingleton<OutboxWriter>();
            builder.Services.AddSingleton<OutboxStore>();
            builder.Services.AddSingleton(new RabbitMqOptions
            {
                AmqpUrl = new Uri(amqpUrl),
                ExchangeName = rabbitMqExchange
            });
            builder.Services.AddHostedService<RabbitMqOutboxPublisher>();
            builder.Services.AddHostedService<RecordingStatusConsumer>();
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddScoped<IPermissionGuard, PermissionGuard>();
            builder.Services.AddScoped<IMeetingRecordingClient, MeetingRecordingClient>();
            builder.Services.AddScoped<IMeetingActivityClient, MeetingActivityClient>();
            builder.Services.AddScoped<IRecordingActivityClient, RecordingActivityClient>();

            builder.Services
                .AddGrpcClient<Permissions.Grpc.PermissionService.PermissionServiceClient>(o =>
                    o.Address = new Uri(permissionServiceUrl));
            builder.Services
                .AddGrpcClient<Presence.Grpc.PresenceService.PresenceServiceClient>(o =>
                    o.Address = new Uri(presenceServiceUrl));
            builder.Services
                .AddGrpcClient<Voice.Grpc.MeetingRecordingService.MeetingRecordingServiceClient>(o =>
                    o.Address = new Uri(voiceServiceUrl));
            builder.Services
                .AddGrpcClient<Messages.Grpc.MessageService.MessageServiceClient>(o =>
                    o.Address = new Uri(messageServiceUrl));

            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                    policy.WithOrigins(origins)
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
