using Microsoft.AspNetCore.Hosting;

namespace Controllers.Environment;

public interface IEnvironmentController
{
    string GetEnvironmentPath ();
}

public class EnvironmentService : IEnvironmentController 
{   
    private readonly IWebHostEnvironment env;
    
    public EnvironmentService (IWebHostEnvironment env_) 
    {
        env = env_;
    }

    public string GetEnvironmentPath () 
    {
        return env.ContentRootPath;
    }
}