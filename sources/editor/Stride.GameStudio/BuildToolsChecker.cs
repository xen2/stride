using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Stride.Core;
using Stride.Core.VisualStudio;

namespace Stride.GameStudio
{
    static class BuildToolsChecker
    {
        private static readonly string[] NecessaryVS2019Workloads = new[] { "Microsoft.VisualStudio.Workload.ManagedDesktop", "Microsoft.VisualStudio.Workload.NetCoreTools", "Microsoft.NetCore.ComponentGroup.DevelopmentTools.2.1" };
        private static readonly string[] NecessaryBuildTools2019Workloads = new[] { "Microsoft.VisualStudio.Workload.MSBuildTools", "Microsoft.VisualStudio.Workload.NetCoreBuildTools", "Microsoft.Net.Component.4.6.1.TargetingPack" };
        private const bool AllowVisualStudioOnly = true; // Somehow this doesn't work well yet, so disabled for now

        public static void CheckVisualStudioAndBuildTools()
        {
            var vsInstallerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft Visual Studio\Installer\vs_installer.exe");

            // Check if an old version of VS2017 is installed because some people had to update before everything was working
            // https://github.com/stride3d/stride/issues/673#issuecomment-621155230
            foreach (var vs2017Instance in VisualStudioVersions.AllAvailableVisualStudioInstances.Where(x => x.Version.Major == 15).ToList())
            {
                if (vs2017Instance.Version.Minor < 9)
                {
                    UpdateVisualStudioToLatest(vsInstallerPath, vs2017Instance);
                }
            }

            // Check if there is any VS2019 installed with necessary workloads
            var matchingVisualStudioInstallation = VisualStudioVersions.AvailableVisualStudioInstances.FirstOrDefault(x => NecessaryVS2019Workloads.All(workload => x.PackageVersions.ContainsKey(workload)));
            if (AllowVisualStudioOnly && matchingVisualStudioInstallation != null)
            {
                if (!matchingVisualStudioInstallation.Complete)
                    MessageBox.Show("We detected Visual Studio 2019 was already installed but is not in a complete state.\r\nYou probably have to reboot, otherwise Stride projects won't properly compile.", "Information", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                // Check if there is actually a VS2019+ installed
                var existingVisualStudio2019Install = VisualStudioVersions.AvailableVisualStudioInstances.FirstOrDefault(x => x.PackageVersions.ContainsKey("Microsoft.VisualStudio.Component.CoreEditor"));
                if (AllowVisualStudioOnly && existingVisualStudio2019Install != null && File.Exists(vsInstallerPath))
                {
                    // First, check if a Visual Studio update is needed
                    // Note: not necessary since VS2019, still keeping code for when we'll need a specific VS2019 version
                    if (existingVisualStudio2019Install.Version.Major == 16 && existingVisualStudio2019Install.Version.Minor < 0)
                    {
                        UpdateVisualStudioToLatest(vsInstallerPath, existingVisualStudio2019Install);
                    }

                    // Second, check workloads
                    {
                        var vsInstallerExitCode = PrerequisiteRunner.RunProgramAndAskUntilSuccess("Visual Studio", vsInstallerPath, $"modify --noUpdateInstaller --passive --norestart --installPath \"{existingVisualStudio2019Install.InstallationPath}\" {string.Join(" ", NecessaryVS2019Workloads.Select(x => $"--add {x}"))}", DialogBoxTryAgainVS);
                        if (vsInstallerExitCode != 0)
                        {
                            var errorMessage = $"Visual Studio 2019 install failed with error {vsInstallerExitCode}";
                            MessageBox.Show(errorMessage, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            throw new InvalidOperationException(errorMessage);
                        }
                    }

                    // Refresh existingVisualStudio2019Install.Complete and check if restart is needed
                    VisualStudioVersions.Refresh();
                    existingVisualStudio2019Install = VisualStudioVersions.AvailableVisualStudioInstances.FirstOrDefault(x => x.InstallationPath == existingVisualStudio2019Install.InstallationPath);
                    if (existingVisualStudio2019Install != null && !existingVisualStudio2019Install.Complete)
                        MessageBox.Show("Visual Studio 2019 install needs a computer restart.\r\nIf you don't restart, Stride projects likely won't compile.", "Information", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    // Otherwise, fallback to vs_buildtools standalone detection and install
                    var buildTools = VisualStudioVersions.AvailableBuildTools.Where(x => x.PackageVersions.ContainsKey("Microsoft.VisualStudio.Workload.MSBuildTools")).ToList();
                    var matchingBuildTool = buildTools.FirstOrDefault(x => NecessaryBuildTools2019Workloads.All(workload => x.PackageVersions.ContainsKey(workload)));
                    string buildToolsCommandLine = null;

                    if (matchingBuildTool == null)
                    {
                        if (buildTools.Count > 0)
                        {
                            // Incomplete installation
                            buildToolsCommandLine = $"modify --wait --passive --norestart --installPath \"{buildTools.First().InstallationPath}\" {string.Join(" ", NecessaryBuildTools2019Workloads.Select(x => $"--add {x}"))}";
                        }
                        else
                        {
                            // Not installed yet
                            buildToolsCommandLine = $"--wait --passive --norestart {string.Join(" ", NecessaryBuildTools2019Workloads.Select(x => $"--add {x}"))}";
                        }
                    }

                    if (buildToolsCommandLine != null)
                    {
                        // Run vs_buildtools again
                        PrerequisiteRunner.RunProgramAndAskUntilSuccess("Build Tools", "vs_buildtools.exe", buildToolsCommandLine, DialogBoxTryAgainVS);
                    }
                }
            }
        }

        private static void UpdateVisualStudioToLatest(string vsInstallerPath, IDEInfo existingVisualStudioInstall)
        {
            try
            {
                // Not sure why, but it seems VS Update is sometimes sending Ctrl+C to our process...
                Console.CancelKeyPress += Console_IgnoreControlC;
                var vsInstallerExitCode = PrerequisiteRunner.RunProgramAndAskUntilSuccess("Visual Studio", vsInstallerPath, $"update --noUpdateInstaller --passive --norestart --installPath \"{existingVisualStudioInstall.InstallationPath}\"", DialogBoxTryAgainVS);
                if (vsInstallerExitCode != 0)
                {
                    var errorMessage = $"{existingVisualStudioInstall.DisplayName} update failed with error {vsInstallerExitCode}";
                    MessageBox.Show(errorMessage, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    throw new InvalidOperationException(errorMessage);
                }
            }
            finally
            {
                Console.CancelKeyPress -= Console_IgnoreControlC;
            }
        }

        private static bool TryDownloadAndExecuteSetup(string vsSetupUrl)
        {
            try
            {
                var downloadFile = Path.Combine(Path.GetTempPath(), $"vs_Setup_{Guid.NewGuid()}.exe");
                var webClient = new WebClient();
                webClient.DownloadFile(vsSetupUrl, downloadFile);
                var process = Process.Start(downloadFile, "--update --wait --passive");
                process.WaitForExit();
                File.Delete(downloadFile);
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }

            return true;
        }

        private static bool DialogBoxTryAgainVS(string programName, Process process)
        {
            var additionalErrorLines = FindVisualStudioInstallerErrors();
            var updateError = additionalErrorLines.FirstOrDefault(x => x.Contains("UpdateRequiredException"));
            if (updateError != null)
            {
                // Second part of error message should be a download URL, let's parse it
                var downloadUrl = Regex.Match(updateError, @"https://.*\.exe\b");
                var webClient = new WebClient();
                if (downloadUrl.Success && TryDownloadAndExecuteSetup(downloadUrl.Value))
                {
                    return true;
                }
                else
                {
                    Process.Start(process.StartInfo.FileName);
                    var result2 = MessageBox.Show($"It seems Visual Studio Installer needs to self-update.\r\nWe're running it for you, please click Update, and then click OK on this dialog box only once the Update is done.", "Error", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
                    return result2 == DialogResult.OK;
                }
            }
            var additionalErrors = string.Join(Environment.NewLine, additionalErrorLines);
            if (additionalErrors.Length > 0)
                additionalErrors = "\r\n\r\nAdditional details from log files:\r\n\r\n" + additionalErrors;

            var result = MessageBox.Show($"The installation of {programName} returned with code {process.ExitCode}.\r\nDo you want to try it again?{additionalErrors}", "Error", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            return result == DialogResult.Yes;
        }

        private static string[] FindVisualStudioInstallerErrors()
        {
            var results = new List<string>();
            var processStartTime = Process.GetCurrentProcess().StartTime;

            // Find all the %TEMP%\dd_*.log files created after this program started
            var tempFiles = Directory.GetFiles(Path.GetTempPath(), "dd_*.log");
            foreach (var tempFile in tempFiles)
            {
                try
                {
                    var fi = new FileInfo(tempFile);
                    if (fi.LastWriteTime >= processStartTime)
                    {
                        var tempFileLines = File.ReadAllLines(tempFile);
                        foreach (var tempFileLine in tempFileLines)
                        {
                            if ((fi.Name.StartsWith("dd_client_") && tempFileLine.Contains(": Error :")) // dd_client
                                || (fi.Name.StartsWith("dd_setup_") && fi.Name.EndsWith("_errors.log"))) // dd_setup_*_errors
                            {
                                results.Add(tempFileLine);
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore errors
                }
            }
            return results.ToArray();
        }

        private static void Console_IgnoreControlC(object sender, ConsoleCancelEventArgs e)
        {
            if (e.SpecialKey == ConsoleSpecialKey.ControlC && !e.Cancel)
                e.Cancel = true;
        }
    }
}
