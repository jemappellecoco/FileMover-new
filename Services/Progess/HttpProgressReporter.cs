using System.Net.Http.Json;
using FileMoverWeb.Models.Progress;

public sealed class HttpProgressReporter : IProgressReporter
{
    private readonly HttpClient _http;
    private readonly string _masterBase;

    public HttpProgressReporter(HttpClient http, IConfiguration cfg)
    {
        _http = http;
        _masterBase = (cfg["Cluster:MasterBaseUrl"] ?? "").Trim().TrimEnd('/');
    }

    public void Publish(ProgressReportDto dto)
    {
        // fire-and-forget
        _ = _http.PostAsJsonAsync($"{_masterBase}/api/progress/report", dto);
    }
}