using System.Collections.Generic;

namespace FileMoverWeb.Models.History
{
    public sealed class PagedResult<T>
    {
        public int total { get; set; }
        public int page { get; set; }
        public int take { get; set; }
        public int totalPages { get; set; }
        public List<T> rows { get; set; } = new();
    }
}