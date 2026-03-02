using FileMoverWeb.Models.Progress;

public interface IProgressReporter
{
    void Publish(ProgressReportDto dto);
}