using Microsoft.AspNetCore.Mvc;

namespace Certyfi.Api.Controllers;

[ApiController]
[Route("health")]
public class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { status = "ok", timeUtc = DateTime.UtcNow });
}
