namespace Middleware.Csrf;

public class CsrfMiddlewareResponse
{
    public bool success;
}

public class CsrfMiddlewareResponseError : CsrfMiddlewareResponse
{
    public string message;
}

public class CsrfMiddleware
{
    private readonly RequestDelegate next_;

    public CsrfMiddleware (RequestDelegate next__)
    {
        next_ = next__;
    }

    public async Task InvokeAsync (HttpContext context)
    {
        var CsrfToken = context.Request.Cookies["x-csrf-token"];
        var CsrfHeaderToken = context.Request.Headers["x-csrf-token"];

        var ResponseError = new CsrfMiddlewareResponseError
        {
            success = false,
            message = "Csrf Token Validation Error."
        };

        if (string.IsNullOrEmpty(CsrfHeaderToken))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(ResponseError);
            return;
        }

        if (string.IsNullOrEmpty(CsrfToken))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(ResponseError);
            return;
        }

        var CsrfHeaderValue = CsrfHeaderToken.ToString();

        if (CsrfHeaderValue != CsrfToken) {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(ResponseError);
            return;
        }

        await next_(context);
    }
}