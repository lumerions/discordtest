using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Controllers.ControllBase;

public abstract class BaseController : ControllerBase
{
    protected string UserId => User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    protected string UserName => User.FindFirst(ClaimTypes.Name)?.Value;
}