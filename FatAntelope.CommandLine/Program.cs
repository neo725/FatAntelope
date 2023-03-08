using FatAntelope.Writers;
using Microsoft.Web.XmlTransform;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;

namespace FatAntelope.CommandLine
{
    class Program
    {
        private enum ExitCode : int
        {
            Success = 0,
            InvalidParameters = 1,
            NoDifference = 2,
            RootNodeMismatch = 3,
            UnknownError = 100
        }

        static int Main(string[] args)
        {
            var version = Assembly.GetEntryAssembly().GetName().Version;
            Console.WriteLine(
                  "==================\n"
                + string.Format(" FatAntelope v{0}.{1}\n", version.Major, version.Minor)
                + "==================\n");

            var batchInput = new List<string[]>();
            if (args.Length == 1)
            {
                // test is file
                var batchFilename = GetFullFilename(args[0]);
                if (File.Exists(batchFilename) == true)
                {
                    var lines = File.ReadAllLines(batchFilename);
                    foreach (var line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line) == false)
                        {
                            var arguments = line.Split(' ');
                            if (arguments[0].Equals("./FatAntelope.exe", StringComparison.CurrentCultureIgnoreCase))
                            {
                                arguments = arguments.Skip(1).ToArray();
                            }
                            batchInput.Add(arguments);
                        }
                    }
                }
            }

            if (batchInput.Count > 0)
            {
                batchInput.ForEach(input => TransformFile(input));

                Console.WriteLine("");
                Console.WriteLine("- Finished batch!\n");
                return (int)ExitCode.Success;
            }
            else
            {
                return TransformFile(args);
            }
        }

        private static int TransformFile(string[] args)
        {
            if (args == null || args.Length < 3 || args.Length > 4)
            {
                Console.WriteLine($"args: {string.Join(" ", args)}");
                Console.WriteLine("");
                Console.WriteLine(
                      "Error: Unexpected number of paramters.\n"
                    + "Usage: FatAntelope source-file target-file output-file [transformed-file]\n"
                    + "  source-file : (Input) The original config file\n"
                    + "  target-file : (Input) The final config file\n"
                    + "  output-file : (Output) The output config transform patch file\n"
                    + "  transformed-file : (Optional Output) The config file resulting from applying the output-file to the source-file\n"
                    + "                     This file should be semantically equal to the target-file.\n");

                return (int)ExitCode.InvalidParameters;
            }

            Console.WriteLine("- Building xml trees . . .\n");
            var tree1 = BuildTree(args[0]);
            var tree2 = BuildTree(args[1]);

            var missingAppSettings = GetMissingNode(tree2, tree1, @"/configuration/appSettings", "key");

            tree2 = SortNode(tree1, tree2, @"/configuration/appSettings");

            Console.WriteLine("- Comparing xml trees . . .\n");
            XDiff.Diff(tree1, tree2);
            if (tree1.Root.Match == MatchType.Match && tree2.Root.Match == MatchType.Match && tree1.Root.Matching == tree2.Root)
            {
                Console.WriteLine("Warning: No difference found!\n");
                return (int)ExitCode.NoDifference;
            }

            if (tree1.Root.Match == MatchType.NoMatch || tree2.Root.Match == MatchType.NoMatch)
            {
                Console.Error.WriteLine("Error: Root nodes must have the same name!\n");
                return (int)ExitCode.RootNodeMismatch;
            }

            Console.WriteLine("- Writing XDT transform . . .\n");

            var appJson = ConfigurationManager.AppSettings["app_settings_json"]?.ToString();
            var settingJson = ParseJson(appJson);

            var writer = new XdtDiffWriter(settingJson);
            var patch = writer.GetDiff(tree2);
            patch.Save(FormatFilename(args[2]));

            if (args.Length > 3)
            {
                Console.WriteLine("- Applying transform to source . . .\n");
                var source = new XmlTransformableDocument();
                source.Load(args[0]);

                var transform = new XmlTransformation(patch.OuterXml, false, null);
                transform.Apply(source);

                source.Save(args[3]);
            }

            if (missingAppSettings != null)
            {
                Console.WriteLine("- Missing node in /configuration/appSettings . . .\n");
                Console.WriteLine(missingAppSettings.Document.InnerXml);
            }

            Console.WriteLine("- Finished successfully!\n");
            return (int)ExitCode.Success;
        }

        private XmlDocument Transform(XmlDocument sourceXml, XmlDocument patchXml)
        {
            var source = new XmlTransformableDocument();
            source.LoadXml(sourceXml.OuterXml);

            var patch = new XmlTransformation(patchXml.OuterXml, false, null);
            patch.Apply(source);

            return source;
        }

        public static XTree BuildTree(string fileName)
        {
            if (fileName.StartsWith("/"))
            {
                fileName = FormatFilename(fileName);
            }

            var doc = new XmlDocument();
            doc.Load(fileName);

            return new XTree(doc);
        }

        public static XmlNode GetTree(XTree tree, string path)
        {
            var node = tree.Document.SelectSingleNode(path);
            return node;
        }

        public static XTree GetMissingNode(XTree treeMaster, XTree treeSlave, string xpath, string attributeName = "key")
        {
            var xdocSource =
                XDocument.Parse(treeMaster.Document.OuterXml);
            var xdocTarget =
                XDocument.Parse(treeSlave.Document.OuterXml);

            var elmSource = xdocSource.XPathSelectElement(xpath);
            var elmTarget = xdocTarget.XPathSelectElement(xpath);

            var elmCopy = new XElement(elmTarget.Name);
            var missingCount = 0;

            foreach (var elm in elmSource.XPathSelectElements("./*"))
            {
                var keyName = elm.Attribute(attributeName).Value;

                var findElement =
                    elmTarget.Elements().LastOrDefault(
                        x => x.Attribute(attributeName).Value.Equals(keyName, StringComparison.CurrentCultureIgnoreCase));

                if (findElement == null)
                {
                    // missing node
                    elmCopy.Add(XElement.Parse(elm.ToString()));
                    missingCount += 1;
                }
                //else
                //{
                //    // node exists
                //    elmCopy.Add(XElement.Parse(findElement.ToString()));
                //}
            }

            //xdocTarget.XPathSelectElement(xpath).ReplaceWith(elmCopy);

            //var xdoc = new XmlDocument();
            //xdoc.LoadXml(xdocTarget.ToString());
            //return new XTree(xdoc);

            if (missingCount == 0)
            {
                return null;
            }

            var xdoc = new XmlDocument();
            xdoc.LoadXml(elmCopy.ToString());
            return new XTree(xdoc);
        }

        public static XTree SortNode(XTree treeSource, XTree treeTarget, string xpath, string attributeName = "key")
        {
            var xdocSource =
                XDocument.Parse(treeSource.Document.OuterXml);
            var xdocTarget =
                XDocument.Parse(treeTarget.Document.OuterXml);

            var elmSource = xdocSource.XPathSelectElement(xpath);
            var elmTarget = xdocTarget.XPathSelectElement(xpath);

            var elmCopy = new XElement(elmTarget.Name);

            foreach (var elm in elmSource.XPathSelectElements("./*"))
            {
                var keyName = elm.Attribute(attributeName).Value;

                var findElement =
                    elmTarget.Elements().LastOrDefault(
                        x => x.Attribute(attributeName).Value.Equals(keyName, StringComparison.CurrentCultureIgnoreCase));

                if (findElement != null)
                {
                    elmCopy.Add(XElement.Parse(findElement.ToString()));
                }
            }

            xdocTarget.XPathSelectElement(xpath).ReplaceWith(elmCopy);

            var xdoc = new XmlDocument();
            xdoc.LoadXml(xdocTarget.ToString());
            return new XTree(xdoc);
        }

        private static ParameterItems GetArguments(string[] args)
        {
            var parameters = new ParameterItems();

            var configMode = false;
            var plusI = 0;

            for (var i = 0; i < args.Length; i++)
            {
                var configItem = new ConfigItem();
                for (var j = 0; j < 3; j++)
                {
                    if (plusI > 0 && configMode == false)
                    {
                        i += plusI;
                        plusI = 0;
                    }
                    if (j + i > args.Length) break;

                    if (args[i].StartsWith("--"))
                    {
                        configMode = false;
                        plusI = 0;
                        var parameter = args[i].Split(new[] { ':' });
                        if (parameter.Length == 1)
                        {
                            parameters.Args.Add(parameter[0], string.Empty);
                        }
                        else
                        {
                            parameters.Args.Add(parameter[0], parameter[1]);
                        }
                        break;
                    }

                    if (j == 0)
                    {
                        configMode = true;
                        plusI = 1;
                        configItem.SourceConfig = args[j + i].Trim();
                    }
                    else if (j == 1)
                    {
                        plusI += 1;
                        configItem.TargetConfig = args[j + i].Trim();
                    }
                    else if (j == 2)
                    {
                        configMode = false;

                        configItem.OutputDiffConfig = args[j + i].Trim();
                        parameters.Configs.Add(configItem);

                        i += plusI;
                        plusI = 0;
                    }
                }
            }

            return parameters;
        }

        private static string FormatFilename(string fileName)
        {
            if (fileName.StartsWith("/"))
            {
                fileName = fileName.Replace("/", "\\");
                fileName = $"{fileName.Substring(1, 1)}:\\{fileName.Substring(2)}";
            }

            return fileName;
        }

        private static string GetFullFilename(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename))
            {
                return null;
            }

            var assembly = Assembly.GetExecutingAssembly();
            var currentLocation = assembly.Location;
            var currentPath = Path.GetDirectoryName(currentLocation);

            var fullfilename = Path.Combine(currentPath, filename);
            if (File.Exists(fullfilename) == false)
            {
                return null;
            }

            return fullfilename;
        }

        private static AppSettingJson ParseJson(string jsonFile)
        {
            var settingJson = new AppSettingJson();

            jsonFile = GetFullFilename(jsonFile);

            if (File.Exists(jsonFile) == false)
            {
                return settingJson;
            }

            DefaultContractResolver contractResolver = new DefaultContractResolver
            {
                NamingStrategy = new SnakeCaseNamingStrategy()
            };

            var jsonContent = File.ReadAllText(jsonFile);
            settingJson =
                JsonConvert.DeserializeObject<AppSettingJson>(jsonContent, new JsonSerializerSettings
                {
                    ContractResolver = contractResolver,
                    Formatting = Newtonsoft.Json.Formatting.Indented
                });

            return settingJson;
        }
    }
}
