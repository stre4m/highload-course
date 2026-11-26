using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.OpenApi;
using Npgsql;
using RabbitMQ.Client;
using SocialNetwork;
using SocialNetwork.OpenApi;
using SocialNetwork.Controllers;

AppContext.SetSwitch("Npgsql.DisableDateTimeInfinityConversions", true);

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Postgres");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("Set ConnectionStrings__Postgres before starting the application.");

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
var readConnectionString = builder.Configuration.GetConnectionString("PostgresRead");
if (!string.IsNullOrWhiteSpace(readConnectionString))
{
    builder.Services.AddKeyedSingleton("public-reads", (_, _) => NpgsqlDataSource.Create(readConnectionString));
}

builder.Services.AddSingleton(services =>
{
    var primary = services.GetRequiredService<NpgsqlDataSource>();
    var reader = services.GetKeyedService<NpgsqlDataSource>("public-reads") ?? primary;
    return new UserStore(primary, reader);
});
builder.Services.AddSingleton<PasswordHasher<string>>();
builder.Services.AddSingleton<PostStore>();
builder.Services.AddSingleton(services => new DialogStore(
    services.GetRequiredService<NpgsqlDataSource>(),
    builder.Configuration.GetSection("DialogShards")));
builder.Services.AddSingleton<FeedRefreshQueue>();
builder.Services.AddSingleton<FeedCache>();
builder.Services.AddHostedService<FeedRefreshWorker>();
var rabbitConnectionString = builder.Configuration.GetConnectionString("RabbitMq");
if (!string.IsNullOrWhiteSpace(rabbitConnectionString))
{
    builder.Services.AddSingleton<FeedDeliveryStore>();
    builder.Services.AddSingleton(_ => new FeedBroker(new ConnectionFactory
    {
        Uri = new Uri(rabbitConnectionString),
        AutomaticRecoveryEnabled = false,
        ClientProvidedName = $"social-network:{Environment.MachineName}"
    }));
    builder.Services.AddHostedService<FeedDeliveryWorker>();
    builder.Services.AddSingleton<FeedSocketSubscriptions>();
    builder.Services.AddHostedService(services => services.GetRequiredService<FeedSocketSubscriptions>());
}
var keyPath = builder.Configuration["DataProtection:KeyPath"];
if (!string.IsNullOrWhiteSpace(keyPath))
{
    builder.Services.AddDataProtection().SetApplicationName("SocialNetwork")
        .PersistKeysToFileSystem(new DirectoryInfo(keyPath));
}
builder.Services.Configure<PasswordHasherOptions>(options => options.IterationCount = 210_000);
builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)
    .ConfigureApiBehaviorOptions(options =>
    {
        options.SuppressMapClientErrors = true;
        options.InvalidModelStateResponseFactory = _ => new Microsoft.AspNetCore.Mvc.BadRequestResult();
    });
builder.Services.AddSingleton(services =>
{
    var hasher = services.GetRequiredService<PasswordHasher<string>>();
    var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    return new DummyPasswordHash(hasher.HashPassword(string.Empty, password));
});
builder.Services.AddAuthentication(BearerTokenDefaults.AuthenticationScheme)
    .AddBearerToken(options => options.BearerTokenExpiration = TimeSpan.FromHours(1));
builder.Services.AddAuthorization();
builder.Services.AddProblemDetails();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "Social Network API", Version = "v1" });
    options.AddSecurityDefinition("bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer"
    });
    options.OperationFilter<BearerSecurityOperationFilter>();
});

var app = builder.Build();
app.UseExceptionHandler();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseAuthentication();
app.UseAuthorization();
if (!string.IsNullOrWhiteSpace(rabbitConnectionString))
{
    app.UseWebSockets(new WebSocketOptions { KeepAliveTimeout = TimeSpan.FromSeconds(20) });
    app.MapGet("/post/feed/posted", FeedSocket.SubscribeAsync).RequireAuthorization().ExcludeFromDescription();
}
app.MapControllers();
app.Run();
