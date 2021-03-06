﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.JSON.Core.Parser.TreeItems;
using Microsoft.JSON.Editor.Document;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.Web.LibraryManager.Contracts;
using Microsoft.Web.LibraryManager.Vsix.Resources;
using Newtonsoft.Json;
using Controller = Microsoft.Web.Editor.Controller;
using IVsTextBuffer = Microsoft.VisualStudio.TextManager.Interop.IVsTextBuffer;
using RunningDocumentTable = Microsoft.VisualStudio.Shell.RunningDocumentTable;
using Shell = Microsoft.VisualStudio.Shell;

namespace Microsoft.Web.LibraryManager.Vsix.UI.Models
{
    internal class InstallDialogViewModel : BindableBase
    {
        private readonly ILibraryCommandService _libraryCommandService;

        private readonly Action<bool> _closeDialog;
        private readonly string _configFileName;
        private readonly IDependencies _deps;
        private readonly Dispatcher _dispatcher;
        private readonly string _targetPath;
        private IProvider _activeProvider;
        private IReadOnlyList<ILibraryGroup> _availablePackages;
        private ILibraryCatalog _catalog;
        private IReadOnlyList<PackageItem> _displayRoots;
        private bool _isInstalling;
        private string _packageId;
        private ILibrary _selectedPackage;
        private FileSelectionType _fileSelectionType;
        private bool _anyFileSelected;
        private bool _isTreeViewEmpty;

        public InstallDialogViewModel(Dispatcher dispatcher, ILibraryCommandService libraryCommandService, string configFileName, IDependencies deps, string targetPath, Action<bool> closeDialog)
        {
            _libraryCommandService = libraryCommandService;
            _configFileName = configFileName;
            _targetPath = targetPath;
            _deps = deps;
            _dispatcher = dispatcher;
            _closeDialog = closeDialog;
            _anyFileSelected = false;
            _isTreeViewEmpty = true;

            List<IProvider> providers = new List<IProvider>();
            foreach (IProvider provider in deps.Providers.OrderBy(x => x.Id))
            {
                ILibraryCatalog catalog = provider.GetCatalog();

                if (catalog == null)
                {
                    continue;
                }

                if (_catalog == null)
                {
                    _activeProvider = provider;
                    _catalog = catalog;
                }

                providers.Add(provider);
            }

            Providers = providers;
            InstallPackageCommand = ActionCommand.Create(InstallPackageAsync, CanInstallPackage, false);
            Task t = LoadPackagesAsync();
        }

        public IReadOnlyList<ILibraryGroup> AvailablePackages
        {
            get { return _availablePackages; }
            set { Set(ref _availablePackages, value); }
        }

        public IReadOnlyList<PackageItem> DisplayRoots
        {
            get
            {
                IReadOnlyList<PackageItem> displayRoots = _displayRoots;

                if (displayRoots != null && displayRoots.Any())
                {
                    displayRoots.ElementAt(0).Name = Text.Files;
                }

                return _displayRoots;
            }

            set
            {
                if (String.IsNullOrEmpty(PackageId))
                {
                    Set(ref _displayRoots, null);
                }
                else
                {
                    Set(ref _displayRoots, value);
                }
            }
        }

        public ICommand InstallPackageCommand { get; }

        public bool IsTreeViewEmpty
        {
            get { return _isTreeViewEmpty; }
            set
            {
                if (Set(ref _isTreeViewEmpty, value))
                {
                    RefreshFileSelections();
                }
            }
        }

        public string PackageId
        {
            get { return _packageId; }
            set
            {
                // If libraryId is null, then we need to clear the tree view for files and show warning message.
                if (String.IsNullOrEmpty(value))
                {
                    if (Set(ref _packageId, value))
                    {
                        SelectedPackage = null;
                    }

                    AnyFileSelected = false;
                    DisplayRoots = null;
                }
                else if (Set(ref _packageId, value))
                {
                    RefreshFileSelections();
                }
            }
        }

        public IReadOnlyList<IProvider> Providers { get; }

        public HashSet<string> SelectedFiles { get; private set; }

        public ILibrary SelectedPackage
        {
            get { return _selectedPackage; }
            set
            {
                if (value == null)
                {
                    IsTreeViewEmpty = true;
                }

                if (Set(ref _selectedPackage, value) && value != null)
                {
                    IsTreeViewEmpty = false;
                    bool canUpdateInstallStatusValue = false;
                    HashSet<string> selectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    Func<bool> canUpdateInstallStatus = () => canUpdateInstallStatusValue;
                    PackageItem root = new PackageItem(this, null, selectedFiles)
                    {
                        CanUpdateInstallStatus = canUpdateInstallStatus,
                        ItemType = PackageItemType.Folder,
                        Name = Path.GetFileName(_targetPath.TrimEnd('/', '\\')),
                        IsChecked = false
                    };

                    PackageItem packageItem = new PackageItem(this, root, selectedFiles)
                    {
                        CanUpdateInstallStatus = canUpdateInstallStatus,
                        Name = value.Name,
                        ItemType = PackageItemType.Folder,
                        IsChecked = false
                    };

                    //The node that children will be added to
                    PackageItem realParent = root;
                    //The node that will be set as the parent of the child nodes
                    PackageItem virtualParent = packageItem;

                    foreach (KeyValuePair<string, bool> file in value.Files)
                    {
                        string[] parts = file.Key.Split('/');
                        PackageItem currentRealParent = realParent;
                        PackageItem currentVirtualParent = virtualParent;

                        for (int i = 0; i < parts.Length; ++i)
                        {
                            bool isFolder = i != parts.Length - 1;

                            if (isFolder)
                            {
                                PackageItem next = currentRealParent.Children.FirstOrDefault(x => x.ItemType == PackageItemType.Folder && string.Equals(x.Name, parts[i]));

                                if (next == null)
                                {
                                    next = new PackageItem(this, currentVirtualParent, selectedFiles)
                                    {
                                        CanUpdateInstallStatus = canUpdateInstallStatus,
                                        Name = parts[i],
                                        ItemType = PackageItemType.Folder,
                                        IsChecked = false
                                    };

                                    List<PackageItem> children = new List<PackageItem>(currentRealParent.Children) { next };

                                    children.Sort((x, y) => x.ItemType == y.ItemType ? StringComparer.OrdinalIgnoreCase.Compare(x.Name, y.Name) : y.ItemType == PackageItemType.Folder ? 1 : -1);

                                    currentRealParent.Children = children;

                                    if (currentVirtualParent != currentRealParent)
                                    {
                                        currentVirtualParent.Children = children;
                                    }
                                }

                                currentRealParent = next;
                                currentVirtualParent = next;
                            }
                            else
                            {
                                PackageItem next = new PackageItem(this, currentVirtualParent, selectedFiles)
                                {
                                    CanUpdateInstallStatus = canUpdateInstallStatus,
                                    FullPath = file.Key,
                                    Name = parts[i],
                                    ItemType = PackageItemType.File,
                                    IsChecked = file.Value,
                                };

                                if (next.IsChecked ?? false)
                                {
                                    selectedFiles.Add(next.FullPath);
                                }

                                List<PackageItem> children = new List<PackageItem>(currentRealParent.Children) { next };
                                children.Sort((x, y) => x.ItemType == y.ItemType ? StringComparer.OrdinalIgnoreCase.Compare(x.Name, y.Name) : y.ItemType == PackageItemType.Folder ? -1 : 1);

                                currentRealParent.Children = children;

                                if (currentVirtualParent != currentRealParent)
                                {
                                    currentVirtualParent.Children = children;
                                }
                            }
                        }
                    }

                    _dispatcher.Invoke(() =>
                    {
                        canUpdateInstallStatusValue = true;
                        SetNodeOpenStates(root);
                        DisplayRoots = new[] { root };
                        SelectedFiles = selectedFiles;
                        InstallPackageCommand.CanExecute(null);
                    });
                }
            }
        }

        private void RefreshFileSelections()
        {
            SelectedProvider.GetCatalog().GetLibraryAsync(_packageId, CancellationToken.None).ContinueWith(x =>
            {
                if (x.IsFaulted || x.IsCanceled)
                {
                    SelectedPackage = null;
                    return;
                }

                SelectedPackage = x.Result;
            });
        }

        public FileSelectionType LibraryFilesToInstall
        {
            get
            {
                return _fileSelectionType;
            }
            set
            {
                _fileSelectionType = value;
                FileSelection.InstallationType = value;
                RefreshFileSelections();
            }
        }

        public IProvider SelectedProvider
        {
            get { return _activeProvider; }
            set
            {
                if (Set(ref _activeProvider, value))
                {
                    _catalog = value.GetCatalog();
                    Task t = LoadPackagesAsync();
                }
            }
        }

        private static void SetNodeOpenStates(PackageItem item)
        {
            bool shouldBeOpen = false;

            foreach (PackageItem child in item.Children)
            {
                SetNodeOpenStates(child);
                shouldBeOpen |= child.IsChecked.GetValueOrDefault(true) || child.IsExpanded;
            }

            item.IsExpanded = shouldBeOpen;
        }

        public bool AnyFileSelected
        {
            get { return _anyFileSelected; }
            set { Set(ref _anyFileSelected, value); }
        }

        private bool CanInstallPackage()
        {
            if (_isInstalling)
            {
                return false;
            }

            AnyFileSelected = IsAnyFileSelected(DisplayRoots);
            return AnyFileSelected;
        }

        private static bool IsAnyFileSelected(IReadOnlyList<PackageItem> children)
        {
            if (children != null)
            {
                List<PackageItem> toProcess = children.ToList();

                for (int i = 0; i < toProcess.Count; i++)
                {
                    PackageItem child = toProcess[i];

                    if (child.IsChecked.HasValue && child.IsChecked.Value)
                    {
                        return true;
                    }

                    toProcess.AddRange(child.Children);
                }
            }

            return false;
        }

        private async void InstallPackageAsync()
        {
            try
            {
                ILibrary selectedPackage = SelectedPackage;
                _isInstalling = true;
                InstallPackageCommand.CanExecute(null);
                Manifest manifest = await Manifest.FromFileAsync(_configFileName, _deps, CancellationToken.None).ConfigureAwait(false);
                string targetPath = _targetPath;

                if (!string.IsNullOrEmpty(_configFileName))
                {
                    Uri configContainerUri = new Uri(_configFileName, UriKind.Absolute);
                    Uri targetUri = new Uri(targetPath, UriKind.Absolute);
                    targetPath = configContainerUri.MakeRelativeUri(targetUri).ToString();
                }

                if (String.IsNullOrEmpty(manifest.Version))
                {
                    manifest.Version = Manifest.SupportedVersions.Max().ToString();
                }

                LibraryInstallationState libraryInstallationState = new LibraryInstallationState
                {
                    LibraryId = PackageId,
                    ProviderId = selectedPackage.ProviderId,
                    DestinationPath = InstallationFolder.DestinationFolder,
                };

                // When "Include all files" option is checked, we don't want to write out the files to libman.json.
                // We will only list the files when user chose to install specific files.
                if (LibraryFilesToInstall == FileSelectionType.ChooseSpecificFilesToInstall)
                {
                    libraryInstallationState.Files = SelectedFiles.ToList();
                }

                manifest.AddLibrary(libraryInstallationState);

                await Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                EnvDTE.Project project = VsHelpers.DTE.SelectedItems.Item(1)?.ProjectItem?.ContainingProject;
                project?.AddFileToProjectAsync(_configFileName);

                RunningDocumentTable rdt = new RunningDocumentTable(Shell.ServiceProvider.GlobalProvider);
                string configFilePath = Path.GetFullPath(_configFileName);

                IVsTextBuffer textBuffer = rdt.FindDocument(configFilePath) as IVsTextBuffer;

                _dispatcher.Invoke(() =>
                {
                    _closeDialog(true);
                });

                // The file isn't open. So we'll write to disk directly
                if (textBuffer == null)
                {
                    manifest.AddLibrary(libraryInstallationState);

                    await manifest.SaveAsync(_configFileName, CancellationToken.None).ConfigureAwait(false);

                    await _libraryCommandService.RestoreAsync(_configFileName, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    // libman.json file is open, so we will write to the textBuffer.
                    InsertIntoTextBuffer(textBuffer, libraryInstallationState);

                    // Save manifest file so we can restore library files.
                    rdt.SaveFileIfDirty(configFilePath);
                }
            }
            catch { }
        }

        private void InsertIntoTextBuffer(IVsTextBuffer document, LibraryInstallationState libraryInstallationState)
        {
            ITextBuffer textBuffer = GetTextBuffer(document);

            if (textBuffer != null)
            {
                JSONMember libraries = GetLibraries(textBuffer);

                if (libraries?.Children != null)
                {
                    string insertionText;
                    JSONArray jsonArray = libraries.Children.OfType<JSONArray>().First();

                    string newLibrary = GetLibraryToBeInserted(libraryInstallationState);
                    bool containsLibrary = jsonArray.BlockItemChildren.Any();

                    int insertionIndex = libraries.AfterEnd - 1;

                    string lineBreakText = GetLineBreakTextFromPreviousLine(textBuffer, insertionIndex);

                    if (containsLibrary)
                    {
                        insertionText = "," + lineBreakText + newLibrary + lineBreakText;
                    }
                    else
                    {
                        insertionText = newLibrary + lineBreakText;
                    }

                    if (insertionIndex > 0)
                    {
                        FormatSelection(textBuffer, insertionIndex, insertionText);
                    }
                }
            }
        }

        private string GetLineBreakTextFromPreviousLine(ITextBuffer textBuffer, int pos)
        {
            int currentLineNumber = textBuffer.CurrentSnapshot.GetLineNumberFromPosition(pos);
            string lineBreakText = null;

            if (currentLineNumber > 0)
            {
                ITextSnapshotLine previousLine = textBuffer.CurrentSnapshot.GetLineFromLineNumber(currentLineNumber - 1);
                lineBreakText = previousLine.GetLineBreakText();
            }

            if (string.IsNullOrEmpty(lineBreakText))
            {
                lineBreakText = Environment.NewLine;
            }

            return lineBreakText;
        }

        private void FormatSelection(ITextBuffer textBuffer, int insertionIndex, string insertionText)
        {
            Controller.TextViewData viewData = Controller.TextViewConnectionListener.GetTextViewDataForBuffer(textBuffer);
            ITextView textView = viewData.LastActiveView;

            using (ITextEdit textEdit = textBuffer.CreateEdit())
            {
                textEdit.Insert(insertionIndex, insertionText);
                textEdit.Apply();
            }

            IOleCommandTarget commandTarget = Shell.Package.GetGlobalService(typeof(Shell.Interop.SUIHostCommandDispatcher)) as IOleCommandTarget;

            SendFocusToEditor(textView);

            SnapshotSpan snapshotSpan = new SnapshotSpan(textView.TextSnapshot, insertionIndex, insertionText.Length + 1);
            textView.Selection.Select(snapshotSpan, false);

            Guid guidVSStd2K = VSConstants.VSStd2K;

            commandTarget.Exec(
                ref guidVSStd2K,
                (uint)VSConstants.VSStd2KCmdID.FORMATSELECTION,
                (uint)OLECMDEXECOPT.OLECMDEXECOPT_DODEFAULT,
                IntPtr.Zero,
                IntPtr.Zero);

            textView.Selection.Clear();
        }

        private void SendFocusToEditor(ITextView textView)
        {
            FrameworkElement editorWindow = (textView as IWpfTextView).VisualElement;
            Keyboard.Focus(editorWindow);
        }

        private JSONMember GetLibraries(ITextBuffer textBuffer)
        {
            JSONEditorDocument document = JSONEditorDocument.FromTextBuffer(textBuffer);

            if (document != null)
            {
                IEnumerable<JSONMember> jsonMembers = document.JSONDocument.TopLevelValue.FindType<JSONObject>().Children.OfType<JSONMember>();

                return jsonMembers.FirstOrDefault(m => m.UnquotedNameText == ManifestConstants.Libraries);
            }

            return null;
        }

        private string GetLibraryToBeInserted(LibraryInstallationState libraryInstallationState)
        {
            JsonSerializerSettings settings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
            };

            return JsonConvert.SerializeObject(libraryInstallationState, settings);
        }

        private ITextBuffer GetTextBuffer(IVsTextBuffer document)
        {
            IComponentModel componentModel = Shell.ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel;
            IVsEditorAdaptersFactoryService adapterService = componentModel.GetService<IVsEditorAdaptersFactoryService>();

            return adapterService.GetDocumentBuffer(document);
        }

        private async Task LoadPackagesAsync()
        {
            IReadOnlyList<ILibraryGroup> groups = await _catalog.SearchAsync(string.Empty, 50, CancellationToken.None).ConfigureAwait(false);
            AvailablePackages = groups;
        }
    }
}
