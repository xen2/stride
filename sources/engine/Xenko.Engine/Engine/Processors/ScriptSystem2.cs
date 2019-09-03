// Copyright (c) Xenko contributors (https://xenko.com) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Xenko.Core;
using Xenko.Core.Collections;
using Xenko.Core.Diagnostics;
using Xenko.Core.Scripting;
using Xenko.Core.Serialization.Contents;
using Xenko.Games;

namespace Xenko.Engine.Processors
{
    /// <summary>
    /// The script system handles scripts scheduling in a game.
    /// </summary>
    public sealed class ScriptSystem2 : GameSystemBase
    {
        internal static readonly Logger Log = GlobalLogger.GetLogger(nameof(ScriptSystem2));

        /// <summary>
        /// Gets the scheduler.
        /// </summary>
        /// <value>The scheduler.</value>
        public Scheduler Scheduler { get; private set; }

        public void Add(Script component)
        {
            Scheduler.Add(component.Execute);
        }

        public void Remove(Script component)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="GameSystemBase" /> class.
        /// </summary>
        /// <param name="registry">The registry.</param>
        /// <remarks>The GameSystem is expecting the following services to be registered: <see cref="IGame" /> and <see cref="ContentManager" />.</remarks>
        public ScriptSystem2(IServiceRegistry registry)
            : base(registry)
        {
            Enabled = true;
            Visible = true;
            Scheduler = new Scheduler();
        }

        protected override void Destroy()
        {
            Scheduler = null;

            base.Destroy();
        }

        public override void Update(GameTime gameTime)
        {
            // Run current micro threads
            Scheduler.Run(KnownSyncPoints.UpdateStart);
        }

        public override void Draw(GameTime gameTime)
        {
            // Run current micro threads
            Scheduler.Run(KnownSyncPoints.DrawStart);
        }
    }
}
