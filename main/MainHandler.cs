using System;
using System.Threading.Tasks;

public class MainHandler
{
    public async Task<Dictionary<string, string>> ProfileInfo(string Name)
    {
        var ProfileInformation = new Dictionary<string, string>
        {
            {"ProfileName", Name}
        };

        return ProfileInformation;
    }
}