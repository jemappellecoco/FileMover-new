// Services/ISlotWorkerFactory.cs
namespace FileMoverWeb.Services
{
    /// <summary>
    /// 建立 SlotWorker 的工廠（把 SlotWorker 跟你的業務服務解耦）
    /// </summary>
    public interface ISlotWorkerFactory
    {
        SlotWorker Create(int slotIndex);
    }
}
