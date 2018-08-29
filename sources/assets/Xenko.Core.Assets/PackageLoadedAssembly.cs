// Copyright (c) Xenko contributors (https://xenko.com) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System.Reflection;

namespace Xenko.Core.Assets
{
    /// <summary>
    /// Represents an assembly that is loaded at runtime by the package.
    /// </summary>
    public class PackageLoadedAssembly
    {
        /// <summary>
        /// Gets the path of the assembly.
        /// </summary>
        /// <value>
        /// The path.
        /// </value>
        public string Path { get; set; }

        /// <summary>
        /// Gets or sets the loaded assembly. Could be null if not properly loaded.
        /// </summary>
        /// <value>
        /// The assembly.
        /// </value>
        public Assembly Assembly { get; set; }

        public PackageLoadedAssembly(string path)
        {
            Path = path;
        }
    }
}
