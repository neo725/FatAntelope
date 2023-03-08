using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FatAntelope.CommandLine
{
    public class ParameterItems
    {
        public ParameterItems()
        {
            this.Configs = new List<ConfigItem>();
            this.Args = new Dictionary<string, string>();
        }

        public List<ConfigItem> Configs { get; set; }

        public Dictionary<string, string> Args { get; set; }
    }

    public class ConfigItem
    {
        public string SourceConfig { get; set; }

        public string TargetConfig { get; set; }

        public string OutputDiffConfig { get; set; }

        public string TransformerConfig { get; set; }

        //public string[] args
        //{
        //    get
        //    {

        //        var _ = new string[3];
        //    }
        //}
    }
}
