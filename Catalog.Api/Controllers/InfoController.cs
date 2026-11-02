using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Catalog.Api.Controllers;

/// <summary>
/// Identifica a versao do codigo e o pod que respondeu. Usado para acompanhar um deploy:
/// durante o rolling update, chamadas seguidas mostram a versao antiga e a nova se
/// alternando (pods diferentes) ate so restar a nova — sem nenhuma resposta de erro.
/// </summary>
[ApiController]
[Route("api/games/info")]
public class InfoController : ControllerBase
{
    /// <summary>Versao do servico. Altere aqui para demonstrar o deploy continuo.</summary>
    public const string Versao = "4.0.0";

    private readonly IWebHostEnvironment _env;

    public InfoController(IWebHostEnvironment env) => _env = env;

    [HttpGet]
    [AllowAnonymous]
    public IActionResult Get() => Ok(new
    {
        servico = "catalog-api",
        versao = Versao,
        pod = Environment.MachineName,
        ambiente = _env.EnvironmentName,
        horaUtc = DateTime.UtcNow
    });
}
