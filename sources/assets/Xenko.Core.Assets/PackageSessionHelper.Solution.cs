// Copyright (c) Xenko contributors (https://xenko.com) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using Xenko.Core;
using Xenko.Core.Extensions;
using Xenko.Core.VisualStudio;
using Xenko.Core.Yaml;
using Xenko.Core.Yaml.Serialization;
using Version = System.Version;

namespace Xenko.Core.Assets
{
    internal partial class PackageSessionHelper
    {
        private const string XenkoPackage = "XenkoPackage";
        private static readonly string[] SolutionPackageIdentifier = new[] { XenkoPackage, "SiliconStudioPackage" };

        public static bool IsSolutionFile(string filePath)
        {
            return String.Compare(Path.GetExtension(filePath), ".sln", StringComparison.InvariantCultureIgnoreCase) == 0;
        }

        internal static bool IsPackage(Project2 project, out string packagePathRelative)
        {
            packagePathRelative = null;
            var packageFiles = Directory.GetFiles(Path.GetDirectoryName(project.FullPath), "*.xkpkg", SearchOption.TopDirectoryOnly);
            if (packageFiles.Length > 0)
            {
                packagePathRelative = packageFiles[0];
                return true;
            }
            return false;
        }
    }
}
