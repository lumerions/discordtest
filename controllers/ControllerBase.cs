using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Controllers.ControllBase;

public abstract class BaseController : ControllerBase
{
    protected string UserId => User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    protected string UserName => User.FindFirst(ClaimTypes.Name)?.Value;

    public bool GetIdValue (ref int IdVar)
    {
        if (string.IsNullOrWhiteSpace(UserId)) return false;
        if (string.IsNullOrWhiteSpace(UserName)) return false;

        if (int.TryParse(UserId, out var IdValue))
        {
            IdVar = IdValue;
            return true;
        }

        return false;
    }
}