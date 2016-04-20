using System.Collections.Generic;
using System.Linq;

namespace Microsoft.DotNet.Tools.Build
{
    internal class IncrementalResult
    {
        public static IncrementalResult DoesNotNeedRebuild = new IncrementalResult(false, "", Enumerable.Empty<string>());

        public bool NeedsRebuild { get; }
        public string Reason { get; }
        public IEnumerable<string> Items { get; }

        private IncrementalResult(bool needsRebuild, string reason, IEnumerable<string> items)
        {
            NeedsRebuild = needsRebuild;
            Reason = reason;
            Items = items;
        }

        public IncrementalResult(string reason)
            : this(true, reason, Enumerable.Empty<string>())
        {
        }

        public IncrementalResult(string reason, IEnumerable<string> items )
            : this(true, reason, items)
        {
        }
    }
}