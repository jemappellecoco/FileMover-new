using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace FileMoverWeb.Controllers
{
    [ApiController]
    [Route("api/config/ftp")]
    public sealed class FtpConfigController : ControllerBase
    {
        private readonly IConfiguration _cfg;
        private readonly IWebHostEnvironment _env;

        private static readonly object _fileLock = new();

        public FtpConfigController(IConfiguration cfg, IWebHostEnvironment env)
        {
            _cfg = cfg;
            _env = env;
        }

        [HttpGet]
        public IActionResult Get()
        {
            var ic = _cfg.GetSection("Ftp:IC")
                .Get<Dictionary<string, FtpDto>>() ?? new();

            return Ok(ic);
        }

        [HttpPost]
        public IActionResult Save([FromBody] Dictionary<string, FtpDto> data)
        {
            if (!IsMaster())
                return Forbid();

            data ??= new();

            // 基本驗證（避免寫出爛設定）
            foreach (var (key, v) in data)
            {
                if (string.IsNullOrWhiteSpace(key))
                    return BadRequest("Key 不可為空");

                if (v == null || string.IsNullOrWhiteSpace(v.Host))
                    return BadRequest($"[{key}] Host 不可為空");

                if (v.Port <= 0) v.Port = 21;
                v.BasePath ??= "/";
                v.User ??= "";
                v.Pass ??= "";
            }

            var path = Path.Combine(_env.ContentRootPath, "ftpsettings.json");

            var json = JsonSerializer.Serialize(new
            {
                Ftp = new { IC = data }
            }, new JsonSerializerOptions { WriteIndented = true });

            lock (_fileLock)
            {
                System.IO.File.WriteAllText(path, json);
            }

            return Ok(new { ok = true, path });
        }

        private bool IsMaster()
            => string.Equals(_cfg["Cluster:Role"], "Master", StringComparison.OrdinalIgnoreCase);

        public sealed class FtpDto
        {
            public string Host { get; set; } = "";
            public int Port { get; set; } = 21;
            public string BasePath { get; set; } = "/";
            public string User { get; set; } = "";
            public string Pass { get; set; } = "";
        }
    }
}
