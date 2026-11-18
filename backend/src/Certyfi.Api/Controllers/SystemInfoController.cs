using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Certyfi.Api.Controllers;

// Public: which system this is. App:EnvironmentLabel ("TEST SYSTEM" on the
// test system, empty in production) makes the web app show a banner on every
// page, so the test system is never mistaken for production. The same build
// runs on both; only this setting differs.
[ApiController]
[Route("api/v1/system-info")]
[AllowAnonymous]
public class SystemInfoController : ControllerBase
{
    private readonly IConfiguration _configuration;

    public SystemInfoController(IConfiguration configuration) => _configuration = configuration;

    [HttpGet]
    public IActionResult Get()
    {
        var label = _configuration["App:EnvironmentLabel"];
        return Ok(new { environmentLabel = string.IsNullOrWhiteSpace(label) ? null : label.Trim() });
    }
}
