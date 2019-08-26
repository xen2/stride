using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xenko.Assets.Scripts;
using Xenko.Core.Assets;
using Xenko.Core.Assets.Templates;
using Xenko.Core.Reflection;

namespace Xenko.Assets.Templates
{
    public static class ScriptTemplateGeneratorHelper
    {
        public static string GenerateScript(TemplateDescription desc, string @namespace, string name)
        {
            var scriptFile = Path.ChangeExtension(desc.FullPath, ScriptSourceFileAsset.Extension);

            var scriptContent = File.ReadAllText(scriptFile);
            scriptContent = scriptContent.Replace("##Namespace##", @namespace);
            scriptContent = scriptContent.Replace("##Scriptname##", name);

            return scriptContent;
        }
    }
}
