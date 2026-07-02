using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Internal.WebSocketController;
using Internal.Database;
using Internal.Redis;
using Internal.MessageHandler;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddAuthentication(options => {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,

            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Main:JWTKEY"] ?? throw new Exception("JWTKey missing"))
            )
        };
});

builder.Services.AddSingleton<WebSocketChannelIdConnections>();
builder.Services.AddSingleton<RedisHandler>();
builder.Services.AddSingleton<DatabaseHandler>();
builder.Services.AddSingleton<WebSocketSessionManager>();
builder.Services.AddOpenApi();
builder.Services.AddControllers();
builder.Services.AddAuthentication();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseWebSockets();
app.MapControllers();
app.UseHttpsRedirection();
app.Run();
