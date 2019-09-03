// Copyright (c) Xenko contributors (https://xenko.com) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Threading;
using System.Threading.Tasks;
using Xenko.Core;
using Xenko.Engine.Design;
using Xenko.Engine.Processors;

namespace Xenko.Engine
{
    /// <summary>
    /// A script which can be implemented as an async microthread.
    /// </summary>
    [DefaultEntityComponentProcessor(typeof(ScriptProcessor2), ExecutionMode = ExecutionMode.Runtime)]
    public abstract class Script : ScriptComponent
    {
        /// <summary>
        /// Called once, as a microthread
        /// </summary>
        /// <returns></returns>
        public abstract ValueTask Execute();
    }
}
