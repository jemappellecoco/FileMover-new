using System;
using System.IO;

namespace FileMoverWeb.Models
{
    public sealed class HistoryTask
    {
        // 基礎 History 欄位
        public int HistoryId { get; set; } // 改為大寫 H 以對齊 SQL
        public int FileId { get; set; }
        public string? Action { get; set; }
        public int Priority { get; set; }
        public int HistoryStatus { get; set; }
        public DateTime? CreateTime { get; set; }
        public string? filetype { get; set; }
        public string? AssignedNode { get; set; }

        // Storage 資訊
        public int FromStorageId { get; set; }
        public int ToStorageId { get; set; }
        public string? FromStorageName { get; set; } // 修正重點
        public string? ToStorageName { get; set; }   // 修正重點
        public string? FromLocation { get; set; }
        public string? ToLocation { get; set; }
        public string? FromType { get; set; }
        public string? ToType { get; set; }

        public string? FromGroup { get; set; }
        public string? ToGroup { get; set; }
        // 檔案細節 (由 PO 或 CM 決定)
        public string? UserBit { get; set; }
        public string? FileName { get; set; }
        public string? Extension { get; set; }
        public int? FileStatus { get; set; }
        public long? FileSize4F { get; set; }
        public long? FileSize7F { get; set; }
        // 唯讀邏輯屬性
        public string? FullFileName =>
            (string.IsNullOrWhiteSpace(UserBit) || string.IsNullOrWhiteSpace(Extension))
                ? null
                : $"{UserBit}.{Extension}".ToUpper();

        public string? FromFullPath =>
            string.IsNullOrWhiteSpace(FromLocation) || string.IsNullOrWhiteSpace(FullFileName)
                ? null
                : Path.Combine(FromLocation, FullFileName);

        public string? ToFullPath =>
            string.IsNullOrWhiteSpace(ToLocation) || string.IsNullOrWhiteSpace(FullFileName)
                ? null
                : Path.Combine(ToLocation, FullFileName);
    }
}