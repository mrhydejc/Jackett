﻿using Jackett.Common.Models.Config;
using Jackett.Common.Services;
using Jackett.Common.Services.Interfaces;
using Jackett.Common.Utils;
using NLog;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Jackett.Tray
{
    public partial class Main : Form
    {
        private IProcessService processService;
        private IServiceConfigService windowsService;
        private ITrayLockService trayLockService;
        private ISerializeService serializeService;
        private IConfigurationService configurationService;
        private ServerConfig serverConfig;
        private Process consoleProcess;
        private Logger logger;
        private bool closeApplicationInitiated;

        public Main()
        {
            Hide();
            InitializeComponent();

            RuntimeSettings runtimeSettings = new RuntimeSettings()
            {
                CustomLogFileName = "TrayLog.txt"
            };

            LogManager.Configuration = LoggingSetup.GetLoggingConfiguration(runtimeSettings);
            logger = LogManager.GetCurrentClassLogger();

            logger.Info("Starting Jackett Tray v" + EnvironmentUtil.JackettVersion);

            processService = new ProcessService(logger);
            windowsService = new WindowsServiceConfigService(processService, logger);
            trayLockService = new TrayLockService();
            serializeService = new SerializeService();
            configurationService = new ConfigurationService(serializeService, processService, logger, runtimeSettings);
            serverConfig = configurationService.BuildServerConfig(runtimeSettings);

            toolStripMenuItemAutoStart.Checked = AutoStart;
            toolStripMenuItemAutoStart.CheckedChanged += toolStripMenuItemAutoStart_CheckedChanged;

            toolStripMenuItemWebUI.Click += toolStripMenuItemWebUI_Click;
            toolStripMenuItemShutdown.Click += toolStripMenuItemShutdown_Click;

            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                toolStripMenuItemAutoStart.Visible = true;
            }

            if (!windowsService.ServiceExists())
            {
                // We are not installed as a service so just start the web server via JackettConsole and run from the tray.
                logger.Info("Starting server from tray");
                StartConsoleApplication();
            }

            Task.Factory.StartNew(WaitForEvent);
        }

        private void WaitForEvent()
        {
            trayLockService.WaitForSignal();
            CloseTrayApplication();
        }

        private void toolStripMenuItemWebUI_Click(object sender, EventArgs e)
        {
            Process.Start("http://127.0.0.1:" + serverConfig.Port);
        }

        private void toolStripMenuItemShutdown_Click(object sender, EventArgs e)
        {
            CloseTrayApplication();
        }

        private void toolStripMenuItemAutoStart_CheckedChanged(object sender, EventArgs e)
        {
            AutoStart = toolStripMenuItemAutoStart.Checked;
        }

        private string ProgramTitle
        {
            get
            {
                return Assembly.GetExecutingAssembly().GetName().Name;
            }
        }

        private bool AutoStart
        {
            get
            {
                return File.Exists(ShortcutPath);
            }
            set
            {
                if (value && !AutoStart)
                {
                    CreateShortcut();
                }
                else if (!value && AutoStart)
                {
                    File.Delete(ShortcutPath);
                }
            }
        }

        public string ShortcutPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Jackett.lnk");
            }
        }

        private void CreateShortcut()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                var appPath = Assembly.GetExecutingAssembly().Location;
                var shell = new IWshRuntimeLibrary.WshShell();
                var shortcut = (IWshRuntimeLibrary.IWshShortcut)shell.CreateShortcut(ShortcutPath);
                shortcut.Description = Assembly.GetExecutingAssembly().GetName().Name;
                shortcut.TargetPath = appPath;
                shortcut.Save();
            }
        }

        private void contextMenuStrip1_Opening(object sender, CancelEventArgs e)
        {
            if (windowsService.ServiceExists())
            {
                backgroundMenuItem.Visible = true;
                serviceControlMenuItem.Visible = true;
                toolStripSeparator1.Visible = true;
                toolStripSeparator2.Visible = true;

                if (windowsService.ServiceRunning())
                {
                    serviceControlMenuItem.Text = "Stop background service";
                    backgroundMenuItem.Text = "Jackett is running as a background service";
                    toolStripMenuItemWebUI.Enabled = true;
                }
                else
                {
                    serviceControlMenuItem.Text = "Start background service";
                    backgroundMenuItem.Text = "Jackett will run as a background service";
                    toolStripMenuItemWebUI.Enabled = false;
                }

                toolStripMenuItemShutdown.Text = "Close tray icon";
            }
            else
            {
                backgroundMenuItem.Visible = false;
                serviceControlMenuItem.Visible = false;
                toolStripSeparator1.Visible = false;
                toolStripSeparator2.Visible = false;
                toolStripMenuItemShutdown.Text = "Shutdown";
            }
        }

        private void serviceControlMenuItem_Click(object sender, EventArgs e)
        {
            var consolePath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "JackettConsole.exe");

            if (windowsService.ServiceRunning())
            {
                if (ServerUtil.IsUserAdministrator())
                {
                    windowsService.Stop();
                }
                else
                {
                    try
                    {
                        processService.StartProcessAndLog(consolePath, "--Stop", true);
                    }
                    catch
                    {
                        MessageBox.Show("Failed to get admin rights to stop the service.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
            else
            {
                if (ServerUtil.IsUserAdministrator())
                {
                    windowsService.Start();
                }
                else
                {
                    try
                    {
                        processService.StartProcessAndLog(consolePath, "--Start", true);
                    }
                    catch
                    {
                        MessageBox.Show("Failed to get admin rights to start the service.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void CloseTrayApplication()
        {
            closeApplicationInitiated = true;

            logger.Info("Close of tray application initiated");

            //Clears notify icon, otherwise icon will still appear on taskbar until you hover the mouse over
            notifyIcon1.Icon = null;
            notifyIcon1.Dispose();
            Application.DoEvents();

            if (consoleProcess != null && !consoleProcess.HasExited)
            {
                consoleProcess.StandardInput.Close();
                System.Threading.Thread.Sleep(1000);
                if (consoleProcess != null && !consoleProcess.HasExited)
                {
                    consoleProcess.Kill();
                }
            }

            Application.Exit();
        }

        private void StartConsoleApplication()
        {
            string applicationFolder = Path.GetDirectoryName(new Uri(Assembly.GetExecutingAssembly().CodeBase).LocalPath);

            var exePath = Path.Combine(applicationFolder, "JackettConsole.exe");

            var startInfo = new ProcessStartInfo()
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                FileName = exePath,
                RedirectStandardInput = true,
                RedirectStandardError = true
            };

            consoleProcess = Process.Start(startInfo);
            consoleProcess.EnableRaisingEvents = true;
            consoleProcess.Exited += ProcessExited;
            consoleProcess.ErrorDataReceived += ProcessErrorDataReceived;
        }

        private void ProcessErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            logger.Error(e.Data);
        }

        private void ProcessExited(object sender, EventArgs e)
        {
            logger.Info("Tray icon not responsible for process exit");

            if (!closeApplicationInitiated)
            {
                CloseTrayApplication();
            }
        }
    }
}