using FileMoverWeb.Models.Progress;
using FileMoverWeb.Services;
public sealed class HubProgressReporter : IProgressReporter
{
    private readonly ProgressHub _hub;

    public HubProgressReporter(ProgressHub hub)
    {
        _hub = hub;
    }

    public void Publish(ProgressReportDto dto)
    {
        _hub.Publish(dto);
    }
}