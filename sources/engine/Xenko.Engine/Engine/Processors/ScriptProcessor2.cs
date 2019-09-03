// Copyright (c) Xenko contributors (https://xenko.com) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using Xenko.Core;

namespace Xenko.Engine.Processors
{
    /// <summary>
    /// Manage scripts
    /// </summary>
    public sealed class ScriptProcessor2 : EntityProcessor<Script>
    {
        private ScriptSystem2 scriptSystem;

        public ScriptProcessor2()
        {
            // Script processor always running before others
            Order = -100000;
        }

        protected internal override void OnSystemAdd()
        {
            scriptSystem = Services.GetService<ScriptSystem2>();
        }

        /// <inheritdoc/>
        protected override void OnEntityComponentAdding(Entity entity, Script component, Script associatedData)
        {
            // Add current list of scripts
            scriptSystem.Add(component);
        }

        /// <inheritdoc/>
        protected override void OnEntityComponentRemoved(Entity entity, Script component, Script associatedData)
        {
            scriptSystem.Remove(component);
        }
    }
}
