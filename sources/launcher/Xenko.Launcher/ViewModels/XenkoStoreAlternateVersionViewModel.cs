using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xenko.Core;
using Xenko.Core.Annotations;
using Xenko.Core.Packages;
using Xenko.Core.Presentation.Commands;
using Xenko.Core.Presentation.ViewModel;

namespace Xenko.LauncherApp.ViewModels
{
    internal sealed class XenkoStoreAlternateVersionViewModel : DispatcherViewModel
    {
        private XenkoStoreVersionViewModel xenkoVersion;
        private NugetServerPackage serverPackage;
        private NugetLocalPackage localPackage;

        public XenkoStoreAlternateVersionViewModel([NotNull] XenkoStoreVersionViewModel xenkoVersion)
            : base(xenkoVersion.ServiceProvider)
        {
            this.xenkoVersion = xenkoVersion;

            SetAsActiveCommand = new AnonymousCommand(ServiceProvider, () =>
            {
                xenkoVersion.UpdateLocalPackage(localPackage, null);
                if (localPackage == null)
                {
                    // If it's a non installed version, offer same version for serverPackage so that it offers to install this specific version
                    xenkoVersion.UpdateServerPackage(serverPackage, null);
                }
                else
                {
                    // Otherwise, offer latest version for update
                    xenkoVersion.UpdateServerPackage(xenkoVersion.LatestServerPackage, null);
                }

                if (!xenkoVersion.SetAsActiveCommand.IsEnabled && xenkoVersion.Launcher.ActiveVersion == xenkoVersion)
                {
                    // Non existing version, disable global active version
                    xenkoVersion.Launcher.ActiveVersion = null;
                }
                else if (xenkoVersion.SetAsActiveCommand.IsEnabled)
                {
                    // Existing version, set as active
                    xenkoVersion.Launcher.ActiveVersion = xenkoVersion;
                }
            });
        }

        /// <summary>
        /// Gets the command that will set the associated version as active.
        /// </summary>
        public CommandBase SetAsActiveCommand { get; }

        public string FullName
        {
            get
            {
                if (localPackage != null)
                    return $"{localPackage.Id} {localPackage.Version} (installed)";
                return $"{serverPackage.Id} {serverPackage.Version}";
            }
        }

        public PackageVersion Version => localPackage?.Version ?? serverPackage.Version;

        internal void UpdateLocalPackage(NugetLocalPackage package)
        {
            OnPropertyChanging(nameof(FullName), nameof(Version));
            localPackage = package;
            OnPropertyChanged(nameof(FullName), nameof(Version));
        }

        internal void UpdateServerPackage(NugetServerPackage package)
        {
            OnPropertyChanging(nameof(FullName), nameof(Version));
            serverPackage = package;
            OnPropertyChanged(nameof(FullName), nameof(Version));
        }
    }
}
