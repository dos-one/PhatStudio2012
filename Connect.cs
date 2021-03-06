//
// CONNECT.CS 
//
// Copyright (c) 2009 PhatStudio development team (Jeremy Stone et al)
// 
// This file is part of PhatStudio.
//
// PhatStudio is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// PhatStudio is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with PhatStudio. If not, see <http://www.gnu.org/licenses/>.
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using EnvDTE;
using EnvDTE80;
using Extensibility;
using Microsoft.VisualStudio.CommandBars;

namespace PhatStudio
{    
    public class Commands : IDTExtensibility2, IDTCommandTarget
	{
        // index of files in this solution
        static private FileIndex fileIndex = new FileIndex();

        private DTE2 applicationObject;
        private EnvDTE.SolutionEvents solutionEvents;
        private DTEEvents DTEEvents;
        private List<ProjectItemsEvents> projectItemsEventsList = new List<ProjectItemsEvents>();
        private List<ProjectItem> pendingRemovedItems = new List<ProjectItem>();
        private AddIn addInInstance;
		private CommandBarPopup subMenu;
        private bool promptForKeyBinding;
        private Window toolWindow;
        private OpenFileControl toolWindowControl;
        private const string guidToolWindow = "{2E595C18-CBCC-42bf-B045-3ECE4813A59E}";
        private const string ExplorerVisibleKey = "PhatStudioExplorerVisible";
        static private string[] extensionsToExclude = { ".bmp", ".dll", ".exclude", ".gif", ".jpg", 
                                                       ".pdb", ".pdf", ".png", ".swf",".wav",".zip" };

		public Commands()
		{
			Properties.Settings.Default.SettingsLoaded += SettingsLoadedEventHandler;
			Properties.Settings.Default.SettingsSaving += SettingsSavingEventHandler;
		}

        private void SettingsLoadedEventHandler(object sender, System.Configuration.SettingsLoadedEventArgs e) 
		{
			UpdateExcludedExtensions();
		}

        private void SettingsSavingEventHandler(object sender, System.ComponentModel.CancelEventArgs e) 
		{
			UpdateExcludedExtensions();
		}

		public void UpdateExcludedExtensions()
		{
			string[] newExtensionsToExclude = new string[Properties.Settings.Default.ExcludedExtensions.Count];
			Properties.Settings.Default.ExcludedExtensions.CopyTo(newExtensionsToExclude, 0);

			Array.Sort(newExtensionsToExclude);

			// Update only if excluded extension list has changed since last update
			if (newExtensionsToExclude != extensionsToExclude)
			{
				extensionsToExclude = newExtensionsToExclude;
				ParseSolutionFiles();
			}
		}

		/// <summary>Implements the OnConnection method of the IDTExtensibility2 interface. Receives notification that the Add-in is being loaded.</summary>
		/// <param term='application'>Root object of the host application.</param>
		/// <param term='connectMode'>Describes how the Add-in is being loaded.</param>
		/// <param term='addInInst'>Object representing this Add-in.</param>
		/// <seealso class='IDTExtensibility2' />
		public void OnConnection(object application, ext_ConnectMode connectMode, object addInInst, ref Array custom)
		{
			applicationObject = (DTE2)application;
			addInInstance = (AddIn)addInInst;

			CommandBarPopup subMenu = CreateSubMenu("view", "PhatStudio", "PhatStudio");

			bool commandAdded = false;
			if (subMenu != null)
			{
				// add commands for our addin
				commandAdded |= RegisterCommand(subMenu, "Settings", "PhatStudio Settings", "Configure PhatStudio");
				commandAdded |= RegisterCommand(subMenu, "SwitchFile", "PhatStudio Switch File", "Switch between source/header file");
				commandAdded |= RegisterCommand(subMenu, "ViewExplorer", "PhatStudio Explorer", "Explore with PhatStudio");
				commandAdded |= RegisterCommand(subMenu, "OpenFile", "PhatStudio Open File", "Open file with PhatStudio");
			}

			// if we added any commands that didn't previously exist, this is first run or we were reset.  Note to ourselves
			// to prompt to ask to bind to default keys
			if (commandAdded)
			{
				promptForKeyBinding = true;
			}

            // Register for IDE events we care about
            RegisterForEvents();

            // If we've just started and the solution is already open, build an index of the files in the solution.
            // Otherwise we'll do it when the solution is opened.
            if (applicationObject.Solution != null && applicationObject.Solution.IsOpen == true)
            {
                ParseSolutionFiles();
            }

			// create the explorer winow if it was previously visible
			if (Properties.Settings.Default.ExplorerVisible)
			{
				CreateToolWindow();
			}
			else
			{
				Window toolWindow;
				try
				{
					toolWindow = applicationObject.Windows.Item(guidToolWindow);
					if (toolWindow != null)
					{
						toolWindow.Visible = false;
					}
				}
				catch { };
			}
		}

        /// <summary>
        /// Registers for all events we need to handle
        /// </summary>
        void RegisterForEvents()
        {
            // cache pointers to wrapped COM interfaces we are going to subscribe to, otherwise they will free themselves
            // and we won't receive events from them
            solutionEvents = applicationObject.Events.SolutionEvents;
            DTEEvents = applicationObject.Events.DTEEvents;
            projectItemsEventsList.Add(applicationObject.Events.SolutionItemsEvents);

            // to get project events (like file add and remove), we have to subscribe to events for every possible project type.  Whee!
            string[] eventNames = { "VBProjectItemsEvents", "CSharpProjectItemsEvents", 
                "VJSharpProjectItemsEvents", "eVBProjectItemsEvents", "eCSharpProjectItemsEvents",
                "WebSiteItemsEvents" };

            foreach (string eventName in eventNames)
            {
                try
                {
                    // see if this project type is installed
                    ProjectItemsEvents projectItemsEvents = (ProjectItemsEvents)applicationObject.Events.GetObject(eventName);
                    if (projectItemsEvents != null)
                    {
                        // subscribe to events for this project type
                        projectItemsEventsList.Add(projectItemsEvents);
                    }
                }
                catch
                {
                    // this project type not installed
                }
            }

            // register for global events
            DTEEvents.OnStartupComplete += new _dispDTEEvents_OnStartupCompleteEventHandler(DTEEvents_OnStartupComplete);
            DTEEvents.OnBeginShutdown += new _dispDTEEvents_OnBeginShutdownEventHandler(DTEEvents_OnBeginShutdown);

            // register for solution events
            solutionEvents.Opened += new _dispSolutionEvents_OpenedEventHandler(SolutionEvents_Opened);
            solutionEvents.BeforeClosing += new _dispSolutionEvents_BeforeClosingEventHandler(SolutionEvents_BeforeClosing);
            solutionEvents.ProjectAdded += new _dispSolutionEvents_ProjectAddedEventHandler(SolutionEvents_ProjectAdded);
            solutionEvents.ProjectRemoved += new _dispSolutionEvents_ProjectRemovedEventHandler(SolutionEvents_ProjectRemoved);

            // register for project events, for all installed project types
            foreach (ProjectItemsEvents projectItemsEvents in projectItemsEventsList)
            {
                projectItemsEvents.ItemAdded += new _dispProjectItemsEvents_ItemAddedEventHandler(ProjectItemsEvents_ItemAdded);
                projectItemsEvents.ItemRemoved += new _dispProjectItemsEvents_ItemRemovedEventHandler(ProjectItemsEvents_ItemRemoved);
                projectItemsEvents.ItemRenamed += new _dispProjectItemsEvents_ItemRenamedEventHandler(ProjectItemsEvents_ItemRenamed);
            }
        }

		private CommandBarPopup CreateSubMenu(string parentName, string name, string caption)
		{
			//Find the MenuBar command bar, which is the top-level command bar holding all the main menu items:
			Microsoft.VisualStudio.CommandBars.CommandBar menuBarCommandBar;
			menuBarCommandBar = ((Microsoft.VisualStudio.CommandBars.CommandBars)applicationObject.CommandBars)["MenuBar"];

			CommandBarPopup menuPopup = null;
			foreach (CommandBarControl menuControl in menuBarCommandBar.Controls)
			{
				if (menuControl.Type == MsoControlType.msoControlPopup
					&& ((CommandBarPopup)menuControl).CommandBar.Name.Equals(parentName, StringComparison.InvariantCultureIgnoreCase))
				{
					menuPopup = (CommandBarPopup)menuControl;
				}
			}

			if (menuPopup == null)
				return null;

			subMenu = FindSubMenu(menuPopup, name);

			if (subMenu != null)
				return null;			// return null if it already exists

			try
			{
				subMenu = (CommandBarPopup)menuPopup.Controls.Add(
					 MsoControlType.msoControlPopup, System.Type.Missing, System.Type.Missing,
					 menuPopup.Controls.Count + 1, true);			// insert after last entry

				// Change some commandbar popup properties
				subMenu.CommandBar.Name = name;		// causes ArgumentException if commandbar with same name already exists
				subMenu.Caption = caption;

				// Make visible the commandbar popup
				subMenu.Visible = true;
			}
			catch (ArgumentException)
			{
				// popup menu already exists
			}

			return subMenu;
		}

		private CommandBarPopup FindSubMenu(CommandBarPopup menuPopup, string name)
		{
			foreach (object obj in menuPopup.Controls)
			{
				if (obj is CommandBarPopup)
				{
					CommandBarPopup menu = (CommandBarPopup)obj;
					if (menu.CommandBar.Name == name)
					{
						return menu;
					}
				}
			}

			return null;
		}

		// We could use 
		// commands.Item("PhatStudio.Commands.<commandName>", -1);
		// instead, but it causes an exception if the command does not exist
		private Command FindCommand(Commands2 commands, string commandName)
		{
			foreach (Command cmd in commands)
			{
				if (cmd.Name.Equals(commandName, StringComparison.OrdinalIgnoreCase))
				{
					return cmd;
				}
			}

			return null;
		}

        /// <summary>
        /// Registers Visual Studio command.  Returns true if this is a new command, false if it already existed.
        /// </summary>
        bool RegisterCommand(CommandBarPopup menuPopup, string commandName, string commandFriendlyName, string commandDescription)
        {
            bool ret = false;

            Commands2 commands = (Commands2)applicationObject.Commands;

            try
            {
				// try to find previously existing command first
				Command command = FindCommand(commands, addInInstance.ProgID+ "." + commandName);

				// if found, we have to delete it and create it again
				// otherwise the icons won't work.
				if (command == null)
				{
					object[] contextGUIDS = new object[] { };

					//Add a command to the Commands collection:
					command = commands.AddNamedCommand2(addInInstance, commandName, commandFriendlyName,
						commandDescription, false, 1, ref contextGUIDS,
						(int)vsCommandStatus.vsCommandStatusSupported + (int)vsCommandStatus.vsCommandStatusEnabled,
						(int)vsCommandStyle.vsCommandStylePictAndText, vsCommandControlType.vsCommandControlTypeButton);

					ret = true;
				}

                //Add a control for the command to the tools menu:
                if ((command != null) && (menuPopup != null))
                {
                    command.AddControl(menuPopup.CommandBar, 1);
                }
            }
            catch (System.ArgumentException)
            {
            }

            return ret;
        }

		/// <summary>Implements the Exec method of the IDTCommandTarget interface. This is called when the command is invoked.</summary>
		/// <param term='commandName'>The name of the command to execute.</param>
		/// <param term='executeOption'>Describes how the command should be run.</param>
		/// <param term='varIn'>Parameters passed from the caller to the command handler.</param>
		/// <param term='varOut'>Parameters passed from the command handler to the caller.</param>
		/// <param term='handled'>Informs the caller if the command was handled or not.</param>
		/// <seealso class='Exec' />
		public void Exec(string commandName, vsCommandExecOption executeOption, ref object varIn, ref object varOut, ref bool handled)
        {
            handled = false;
            if (executeOption == vsCommandExecOption.vsCommandExecOptionDoDefault)
            {
                if (commandName == "PhatStudio.Commands.OpenFile")
                {
                    // open the modal open file dialog
                    OpenFileDlg openFileDlg = new OpenFileDlg();
                    openFileDlg.Init(applicationObject, fileIndex);
                    openFileDlg.ShowDialog();
                    handled = true;
                }
                else if (commandName == "PhatStudio.Commands.ViewExplorer")
                {
                    // show the explorer tool window
                    CreateToolWindow();
                    handled = true;
                }
				else if (commandName == "PhatStudio.Commands.SwitchFile")
				{
					SwitchFile.SwitchToRelated(applicationObject.ActiveDocument);
					handled = true;
				}
				else if (commandName == "PhatStudio.Commands.Settings")
				{
					ConfigDlg configDlg = new ConfigDlg();
					configDlg.ShowDialog();
					handled = true;
				}            
			}
        }

		/// <summary>Implements the QueryStatus method of the IDTCommandTarget interface. This is called when the command's availability is updated</summary>
		/// <param term='commandName'>The name of the command to determine state for.</param>
		/// <param term='neededText'>Text that is needed for the command.</param>
		/// <param term='status'>The state of the command in the user interface.</param>
		/// <param term='commandText'>Text requested by the neededText parameter.</param>
		/// <seealso class='Exec' />
		public void QueryStatus(string commandName, vsCommandStatusTextWanted neededText, ref vsCommandStatus status, ref object commandText)
        {
            if (neededText == vsCommandStatusTextWanted.vsCommandStatusTextWantedNone)
            {
                if (commandName == "PhatStudio.Commands.OpenFile" || 
					commandName == "PhatStudio.Commands.ViewExplorer" ||
					commandName == "PhatStudio.Commands.Settings")
                {
                    status = vsCommandStatus.vsCommandStatusSupported | vsCommandStatus.vsCommandStatusEnabled;
                    return;
                }
				else if(commandName == "PhatStudio.Commands.SwitchFile")
				{
					status = vsCommandStatus.vsCommandStatusSupported;

					try
					{
						// accessing applicationObject.ActiveDocument can cause 
						// a TypeInitializationException under some (rare) circumstances
						if (SwitchFile.SwitchPossible(applicationObject.ActiveDocument))
						{
							status = vsCommandStatus.vsCommandStatusSupported | vsCommandStatus.vsCommandStatusEnabled;
						}
					}
					catch (TypeInitializationException)
					{

					}

					return;
				}
            }
        }

        /// <summary>
        /// Asks user if we should bind FileOpen to default key.  (For first run only.)
        /// </summary>
        void PromptForKeyBinding()
        {
			DialogResult result = System.Windows.Forms.MessageBox.Show(
				"Welcome to PhatStudio!\n" +
				"\n" + 
				"Would you like to bind PhatStudio commands?\n" +
				"You can change the key binding any time in the Tools->Options->Keyboard dialog.\n" +
				"\n" + 
				"Open File command to the key ALT+O\n" + 
				"Switch File command to the key ALT+S",
				"PhatStudio", System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
            {
                try
                {
                    EnvDTE.Commands commands = applicationObject.Commands;
             
					Command command_open = commands.Item("PhatStudio.Commands.OpenFile", -1);
                    command_open.Bindings = new object[] { "Global::ALT+O" };

					Command command_switch = commands.Item("PhatStudio.Commands.SwitchFile", -1);
					command_switch.Bindings = new object[] { "Global::ALT+S" };
                }
                catch
                {
                }
            }
		}

        /// <summary>
        /// Creates a tool window for our Explorer window
        /// </summary>
        void CreateToolWindow()
        {
            object programmableObject = null;

            Windows2 windows2 = (Windows2)applicationObject.Windows;
            Assembly asm = Assembly.GetExecutingAssembly();
            try
            {
                // create the tool window and tell it to host our open file control
                toolWindow = windows2.CreateToolWindow2(addInInstance, asm.Location,
                    "PhatStudio.OpenFileControl",
                    "PhatStudio Explorer", guidToolWindow, ref programmableObject);

                // get our control out of the tool window
                OpenFileControl openFileControl = (OpenFileControl)programmableObject;
                // init our control
                openFileControl.Init(applicationObject, fileIndex, false);
                toolWindow.Visible = true;
                toolWindowControl = (OpenFileControl)toolWindow.Object;

                // do some black magic to set our icon on the tool window tab that appears when docked               
                Bitmap bitmap = Properties.Resources.AppIcon;
                ImageToPictureDispConverter imageConverter = new ImageToPictureDispConverter();
                stdole.IPictureDisp pictureDisp = imageConverter.GetIPictureDispFromImage(bitmap);
                toolWindow.SetTabPicture(pictureDisp);
                imageConverter.Dispose();
                toolWindowControl.Visible = true;
            }
            catch { }
        }

        /// <summary>
        /// Enumerate and index all files in current solution
        /// </summary>
        void ParseSolutionFiles()
        {
            // Debug.WriteLine("PhatStudio: starting file index");
            // int start = Environment.TickCount;

            // clear existing index
            fileIndex.RemoveAll();
			fileIndex.SetSolutionDir(null);

			if (applicationObject == null)
				return;

            Solution solution = applicationObject.Solution;
            if (solution != null)
            {
				if (!String.IsNullOrEmpty(solution.FullName))
				{
					try
					{
						string dir = Path.GetDirectoryName(solution.FullName);
						fileIndex.SetSolutionDir(dir);
					}
					catch (ArgumentException)
					{
					}
					catch (System.IO.PathTooLongException)
					{
					}
				}

                // enumerate all projects
                Projects projects = solution.Projects;
                if (projects != null)
                {
                    foreach (Project project in projects)
                    {
                        ProjectItems projectItems = project.ProjectItems;
                        if (projectItems != null)
                        {
                            AddProjectItems(projectItems);
                        }
                    }
                }
            }

            // int end = Environment.TickCount;
            // Debug.WriteLine(String.Format("PhatStudio: parsed & indexed solution files in {0} ms", end - start));
        }

        /// <summary>
        /// Adds all ProjectItems in the specified collection
        /// </summary>
        public void AddProjectItems(ProjectItems projectItems)
        {
            foreach (ProjectItem projectItem in projectItems)
            {
                // add all the files for this project item
                AddProjectItemFiles(projectItem);

                // add sub-project items if there are any
                if (projectItem.ProjectItems != null)
                {
                    AddProjectItems(projectItem.ProjectItems);
                }

                if (projectItem.SubProject != null && projectItem.SubProject.ProjectItems != null)
                {
                    AddProjectItems(projectItem.SubProject.ProjectItems);
                }
            }            
        }

        /// <summary>
        /// Adds all the files for a project item
        /// </summary>
        public void AddProjectItemFiles(ProjectItem projectItem)
        {
            if (projectItem.FileCount == 0)
                return;

            // If we files inside the item removed handler, the removed item still shows up.  Ignore any project
            // items which we know are about to be removed.
            int index = pendingRemovedItems.FindIndex(delegate(ProjectItem o) { return o == projectItem; });
            if (index >= 0)
            {
                pendingRemovedItems.RemoveAt(index);
                return;
            }

            string guid = projectItem.Kind;
            if (IncludeItemFiles(guid))
            {
                // get each associated file for this project item
                for (short i = 1; i <= projectItem.FileCount; ++i)
                {                    
                    string fileName = projectItem.get_FileNames(i);
                    // Paranoid check; solution items, which we should filter out before we get here, can report a non-zero file count with 
                    // null names.
                    if (String.IsNullOrEmpty(fileName))
                        continue;

                    // if the extension is on our list of extensions to exclude, ignore it
                    String excludedExt = Array.Find<String>(extensionsToExclude,
                        delegate(string obj) { return fileName.EndsWith(obj, StringComparison.CurrentCultureIgnoreCase); } );

                    if (excludedExt != null)
                        continue;

                    // add the file to the file index
                    fileIndex.AddFile(fileName);
                }
            }
        }

        void DTEEvents_OnStartupComplete()
        {
            // ask user if we should bind to default keys, if appropriate
            if (promptForKeyBinding)
            {
                PromptForKeyBinding();
            }
        }

        void DTEEvents_OnBeginShutdown()
        {
        }

        void ProjectItemsEvents_ItemRenamed(ProjectItem ProjectItem, string OldName)
        {
            // reparse all files if an item is renamed -- we currently don't support removing a file name from the index
            ParseSolutionFiles();
        }

        void ProjectItemsEvents_ItemRemoved(ProjectItem ProjectItem)
        {
            // add this project item to the pending remove list -- it's not really removed at this point
            pendingRemovedItems.Add(ProjectItem);

            // reparse all files if an item is removed -- we currently don't support removing a file name from the index
            ParseSolutionFiles();
        }

        void SolutionEvents_ProjectRemoved(Project Project)
        {
            // reparse all files
            ParseSolutionFiles();
        }

        void ProjectItemsEvents_ItemAdded(ProjectItem projectItem)
        {
            // add files for this item to the file index
            AddProjectItemFiles(projectItem);            
        }

        void SolutionEvents_ProjectAdded(Project Project)
        {
            // reparse all files
            ParseSolutionFiles();
        }

        void SolutionEvents_Opened()
        {
            // reparse all files
            ParseSolutionFiles();
        }

        void SolutionEvents_BeforeClosing()
        {
            // remove all files from the index
            fileIndex.RemoveAll();
		}

		/// <summary>Implements the OnDisconnection method of the IDTExtensibility2 interface. Receives notification that the Add-in is being unloaded.</summary>
		/// <param term='disconnectMode'>Describes how the Add-in is being unloaded.</param>
		/// <param term='custom'>Array of parameters that are host application specific.</param>
		/// <seealso class='IDTExtensibility2' />
		public void OnDisconnection(ext_DisconnectMode disconnectMode, ref Array custom)
		{
			try
			{
				switch (disconnectMode)
				{
					case ext_DisconnectMode.ext_dm_HostShutdown:
					case ext_DisconnectMode.ext_dm_UserClosed:
						if (subMenu != null)
						{
							subMenu.Delete(true);
							subMenu = null;
						}
						break;
				}
			}
			catch (Exception)
			{
			}

			SaveExplorerVisibility();

			Properties.Settings.Default.Save(); 
		}

		private void SaveExplorerVisibility()
		{
			bool visible = false;

			try
			{
				// save the visibility state of explorer window
				Window toolWindow = applicationObject.Windows.Item(guidToolWindow);
				visible = (toolWindow != null) && toolWindow.Visible;
			}
			catch { }

			Properties.Settings.Default.ExplorerVisible = visible;
		}

		/// <summary>Implements the OnAddInsUpdate method of the IDTExtensibility2 interface. Receives notification when the collection of Add-ins has changed.</summary>
		/// <param term='custom'>Array of parameters that are host application specific.</param>
		/// <seealso class='IDTExtensibility2' />		
		public void OnAddInsUpdate(ref Array custom)
		{
		}

		/// <summary>Implements the OnStartupComplete method of the IDTExtensibility2 interface. Receives notification that the host application has completed loading.</summary>
		/// <param term='custom'>Array of parameters that are host application specific.</param>
		/// <seealso class='IDTExtensibility2' />
		public void OnStartupComplete(ref Array custom)
		{
		}

		/// <summary>Implements the OnBeginShutdown method of the IDTExtensibility2 interface. Receives notification that the host application is being unloaded.</summary>
		/// <param term='custom'>Array of parameters that are host application specific.</param>
		/// <seealso class='IDTExtensibility2' />
		public void OnBeginShutdown(ref Array custom)
		{
		}
		
        public bool IncludeItemFiles(string guid)
        {
            if (guid == EnvDTE.Constants.vsProjectItemKindPhysicalFile)
                return true;

            return false;
        }
	}
}

