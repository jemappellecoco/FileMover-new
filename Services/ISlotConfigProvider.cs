// Services/ISlotConfigProvider.cs
using System.Threading;
using System.Threading.Tasks;
using FileMoverWeb.Services;
namespace FileMoverWeb.Services
{
    /// <summary>
    /// 提供「目前這台機器允許啟用的 slot 數量」來源（DB / config / master 分配都可以換實作）
    /// </summary>
    public interface ISlotConfigProvider
    {
        Task<int> GetEnabledSlotsAsync(CancellationToken ct);
    }
}
