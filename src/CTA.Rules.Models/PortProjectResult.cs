using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CTA.Rules.Models
{
    public class PortProjectResult : ProjectResult
    {
        public HashSet<string> References { get; set; }
        public HashSet<string> DownloadedFiles { get; set; }
        public PortProjectResult(string projectPath)
        {
            ProjectFile = projectPath;
            References = new HashSet<string>();
            DownloadedFiles = new HashSet<string>();
        }
    }
}
