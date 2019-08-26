using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.TemplateWizard;
using Xenko.VisualStudio.Commands;

namespace Xenko.VisualStudio
{
    class XenkoScriptsTemplateWizard : IWizard
    {
        public XenkoScriptsTemplateWizard()
        {

        }

        public void BeforeOpeningFile(ProjectItem projectItem)
        {
        }

        public void ProjectFinishedGenerating(Project project)
        {
        }

        public void ProjectItemFinishedGenerating(ProjectItem projectItem)
        {
        }

        public void RunFinished()
        {
        }

        public void RunStarted(object automationObject, Dictionary<string, string> replacementsDictionary, WizardRunKind runKind, object[] customParams)
        {
            var remote = XenkoCommandsProxy.GetProxy();
            var @namespace = replacementsDictionary["$rootnamespace$"];
            var name = replacementsDictionary["$safeitemname$"];
            remote?.GenerateScript(@namespace, name, XX);
        }

        public bool ShouldAddProjectItem(string filePath)
        {
            return true;
        }
    }
}
