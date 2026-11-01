// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.IO;
using System.Threading.Tasks;
using Stride.Core.BuildEngine;
using Stride.Core.IO;
using Stride.Core.Serialization;
using Stride.Core.Serialization.Contents;

namespace Stride.Assets.Effect
{
    /// <summary>
    /// Writes the companion of a replaced shader, which holds the full name of its replacer: <c>shaders/X.sdsl/replacedby</c>.
    /// </summary>
    internal sealed class SaveReplacerCommand(string location, string replacer) : IndexFileCommand
    {
        private string location = location;
        private string replacer = replacer;

        public override string Title => $"Replacer {replacer} > {location}";

        protected override void ComputeParameterHash(BinarySerializationWriter writer)
        {
            base.ComputeParameterHash(writer);
            writer.Write(location);
            writer.Write(replacer);
        }

        protected override Task<ResultStatus> DoCommandOverride(ICommandContext commandContext)
        {
            using var stream = MicrothreadLocalDatabases.DatabaseFileProvider.OpenStream(location, VirtualFileMode.Create, VirtualFileAccess.Write, VirtualFileShare.Write);
            using var writer = new StreamWriter(stream);
            writer.Write(replacer);
            return Task.FromResult(ResultStatus.Successful);
        }

        public override string ToString() => Title;
    }
}
