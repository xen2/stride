// Copyright (c) Xenko contributors (https://xenko.com) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xenko.Core;
using Xenko.Core.Extensions;
using Xenko.Core.IO;
using Xenko.Core.Presentation.Collections;
using Xenko.Core.Presentation.Dirtiables;
using Xenko.Core.Presentation.ViewModel;

namespace Xenko.Core.Assets.Editor.ViewModel
{
    public class ProjectCodeViewModel : SessionObjectViewModel
    {
        public ProjectCodeViewModel(ProjectViewModel project)
            : base(project.Session)
        {
        }

        public override string Name { get => "Code"; set => throw new NotImplementedException(); }

        public override bool IsEditable => false;

        public override string TypeDisplayName => "Project Code";

        protected override void UpdateIsDeletedStatus()
        {
            throw new NotImplementedException();
        }
    }

    // TODO: For the moment we consider that a project has only a single parent profile. Sharing project in several profile is not supported.
    public class ProjectViewModel : SessionObjectViewModel, IComparable<ProjectViewModel>
    {
        private readonly Project2 project;
        private bool isCurrentProject;
        private readonly ObservableList<DirtiableEditableViewModel> content = new ObservableList<DirtiableEditableViewModel>();
        private PackageViewModel package;

        public ProjectViewModel(SessionViewModel session, Project2 project, bool projectAlreadyInSession)
            : base(session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (project == null) throw new ArgumentNullException(nameof(project));
            this.project = project;

            isCurrentProject = project.Session.CurrentProject == project;

            // Use the property in order to create an action item
            InitialUndelete(false);

            if (project.Package != null)
            {
                package = new PackageViewModel(this, project.Package, projectAlreadyInSession);
                content.Add(package);
            }

            content.Add(Code = new ProjectCodeViewModel(this));
        }

        /// <summary>
        /// Gets the list of child item to be used to display in a hierachical view.
        /// </summary>
        /// <remarks>This collection usually contains categories and root folders.</remarks>
        public IReadOnlyObservableCollection<DirtiableEditableViewModel> Content => content;

        public override string Name { get { return project.FullPath.GetFileNameWithoutExtension(); } set { if (value != Name) throw new InvalidOperationException("The name of a project cannot be set"); } }

        public ProjectCodeViewModel Code { get; }

        public PackageViewModel Package
        {
            get => package;
            set
            {
                if (package != null)
                    content.Remove(package);

                SetValueUncancellable(ref package, value);

                project.Package = package.Package;

                if (package != null)
                    content.Add(package);
            }
        }

        // TODO CSPROJ=XKPKG
        public Guid Id => Guid.Empty;

        // TODO CSPROJ=XKPKG
        public ProjectType Type => ProjectType.Executable;

        public PlatformType Platform => PlatformType.Windows;

        public UFile ProjectPath => project.FullPath;

        public bool IsCurrentProject { get { return isCurrentProject; } set { SetValueUncancellable(ref isCurrentProject, value); } }

        /// <inheritdoc/>
        public override bool IsEditable => true;

        /// <inheritdoc/>
        public override bool IsEditing { get { return false; } set { } }

        /// <inheritdoc/>
        public override string TypeDisplayName => "Project";

        /// <inheritdoc/>
        public override IEnumerable<IDirtiable> Dirtiables => base.Dirtiables.Concat(Session.Dirtiables);

        /// <summary>
        /// Gets the root namespace for this project.
        /// </summary>
        public string RootNamespace => "RootNamespace"; // TODO CSPROJ=XKPKG

        /// <inheritdoc/>
        public int CompareTo(ProjectViewModel other)
        {
            if (other == null)
                return -1;

            var result = Type.CompareTo(other.Type);
            return result != 0 ? result : string.Compare(Name, other.Name, StringComparison.InvariantCultureIgnoreCase);
        }

        public void Delete()
        {
            using (var transaction = UndoRedoService.CreateTransaction())
            {
                string message = $"Delete project '{Name}'";
                IsDeleted = true;
                UndoRedoService.SetName(transaction, message);
            }
        }

        protected override void UpdateIsDeletedStatus()
        {
            if (IsDeleted)
            {
                Session.Projects.Remove(this);
            }
            else
            {
                Session.Projects.Add(this);
            }
        }
    }
}
