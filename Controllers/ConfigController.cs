using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace FileMoverWeb.Controllers
{
    [ApiController]
    [Route("api/config")]
    public class ConfigController : ControllerBase
    {
        private readonly IWebHostEnvironment _env;

        public ConfigController(IWebHostEnvironment env)
        {
            _env = env;
        }

    }
}
