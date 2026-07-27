using Npgsql;
using Internal.Database;
using Middleware.Csrf;

namespace Middleware.Authenication;

public class AuthenicationMiddleware
{
    private readonly RequestDelegate next_;
    private readonly DatabaseHandler DBHandler;

    public AuthenicationMiddleware (DatabaseHandler DBHandler_, RequestDelegate next__)
    {
        next_ = next__;
        DBHandler = DBHandler_;
    } 

    public async Task InvokeAsync (HttpContext context)
    {
        var JwtAuthenicationToken = context.Request.Cookies["jwt"];
        var ResponseError = new CsrfMiddlewareResponseError
        {
            success = false,
            message = "Session is not valid."
        };

        if (string.IsNullOrEmpty(JwtAuthenicationToken))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(ResponseError);
            return;
        }

      //  var Conn = await DBHandler.GetConnection();
       // await using var CheckAuth = new NpgsqlCommand("", Conn);
// TODO finish this 
        await next_(context);
    }
}