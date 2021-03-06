﻿using pdfforge.Obsidian;
using pdfforge.Obsidian.Trigger;
using pdfforge.PDFCreator.Conversion.Jobs;
using pdfforge.PDFCreator.Conversion.Jobs.FolderProvider;
using pdfforge.PDFCreator.Conversion.Jobs.Jobs;
using pdfforge.PDFCreator.Conversion.Settings;
using pdfforge.PDFCreator.Conversion.Settings.Enums;
using pdfforge.PDFCreator.Conversion.Settings.GroupPolicies;
using pdfforge.PDFCreator.Core.Services;
using pdfforge.PDFCreator.Core.SettingsManagement;
using pdfforge.PDFCreator.Core.Workflow;
using pdfforge.PDFCreator.Core.Workflow.Exceptions;
using pdfforge.PDFCreator.Core.Workflow.Queries;
using pdfforge.PDFCreator.UI.Interactions;
using pdfforge.PDFCreator.UI.Interactions.Enums;
using pdfforge.PDFCreator.UI.Presentation.Commands.ProfileCommands;
using pdfforge.PDFCreator.UI.Presentation.Events;
using pdfforge.PDFCreator.UI.Presentation.Helper.Translation;
using pdfforge.PDFCreator.UI.Presentation.UserControls.Profiles;
using pdfforge.PDFCreator.UI.Presentation.ViewModelBases;
using pdfforge.PDFCreator.UI.Presentation.Workflow;
using pdfforge.PDFCreator.Utilities;
using pdfforge.PDFCreator.Utilities.IO;
using Prism.Events;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Data;
using System.Windows.Input;
using SystemInterface.IO;
using SystemWrapper.IO;

namespace pdfforge.PDFCreator.UI.Presentation.UserControls.PrintJob
{
    public class PrintJobViewModel : TranslatableViewModelBase<PrintJobViewTranslation>, IWorkflowViewModel
    {
        public IGpoSettings GpoSettings { get; }
        private readonly ISettingsProvider _settingsProvider;
        private readonly IFileNameQuery _saveFileQuery;
        private readonly IInteractionRequest _interactionRequest;
        private readonly ISelectedProfileProvider _selectedProfileProvider;
        private readonly IFile _file;
        private readonly ITempFolderProvider _tempFolderProvider;
        private readonly IPathUtil _pathUtil;
        private readonly IDispatcher _dispatcher;
        private readonly IDirectoryHelper _directoryHelper;
        private readonly IInteractiveProfileChecker _interactiveProfileChecker;
        private readonly PathWrapSafe _pathSafe = new PathWrapSafe();
        private Job _job;
        private string _outputFolder = "";
        private string _outputFilename = "";
        private string _latestDialogFilePath = "";
        private string _title;
        private string _author;
        private string _keyword;
        private string _subject;
        private readonly OutputFormatHelper _outputFormatHelper;

        public PrintJobViewModel(
            ISettingsProvider settingsProvider,
            ITranslationUpdater translationUpdater,
            IJobInfoQueue jobInfoQueue,
            IFileNameQuery saveFileQuery,
            IInteractionRequest interactionRequest,
            ICommandLocator commandsLocator,
            IEventAggregator eventAggregator,
            ISelectedProfileProvider selectedProfileProvider,
            ITempFolderProvider tempFolderProvider,
            IPathUtil pathUtil,
            IFile file,
            IGpoSettings gpoSettings,
            IDispatcher dispatcher,
            IDirectoryHelper directoryHelper,
            IInteractiveProfileChecker interactiveProfileChecker)
            : base(translationUpdater)
        {
            GpoSettings = gpoSettings;
            _settingsProvider = settingsProvider;
            _saveFileQuery = saveFileQuery;
            _interactionRequest = interactionRequest;
            _selectedProfileProvider = selectedProfileProvider;
            _file = file;
            _tempFolderProvider = tempFolderProvider;
            _pathUtil = pathUtil;
            _dispatcher = dispatcher;
            _directoryHelper = directoryHelper;
            _interactiveProfileChecker = interactiveProfileChecker;

            SaveCommand = new DelegateCommand(SaveExecute, CanExecute);
            SendByEmailCommand = new DelegateCommand(EmailExecute);
            MergeCommand = new DelegateCommand(MergeExecute);
            CancelCommand = new DelegateCommand(CancelExecute);
            SetOutputFormatCommand = new DelegateCommand<OutputFormat>(SetOutputFormatExecute);
            BrowseFileCommand = new DelegateCommand(BrowseFileExecute);

            _outputFormatHelper = new OutputFormatHelper();

            SetupEditProfileCommand(commandsLocator, eventAggregator);

            var settings = settingsProvider.Settings?.CopyAndPreserveApplicationSettings();
            Profiles = settings?.ConversionProfiles;

            jobInfoQueue.OnNewJobInfo += (sender, args) => UpdateNumberOfPrintJobsHint(jobInfoQueue.Count);
            UpdateNumberOfPrintJobsHint(jobInfoQueue.Count);

            var profileListView = CollectionViewSource.GetDefaultView(Profiles);
            if (profileListView != null)
            {
                profileListView.CurrentChanged += (sender, args) =>
                {
                    if (Job != null)
                        OutputFormat = ((ConversionProfile)profileListView.CurrentItem).OutputFormat;
                };
            }
        }

        private bool CanExecute(object obj)
        {
            return true;
        }

        private void SetupEditProfileCommand(ICommandLocator commandsLocator, IEventAggregator eventAggregator)
        {
            if (commandsLocator != null)
            {
                EditProfileCommand = commandsLocator.CreateMacroCommand()
                    .AddCommand(new DelegateCommand(SetProfileForSelection))
                    .AddCommand<ShowLockLayerCommand>()
                    .AddCommand<OpenProfileCommand>()
                    .AddCommand<WaitProfileModificationCommand>()
                    .AddCommand(new DelegateCommand(x => eventAggregator.GetEvent<CloseMainWindowEvent>().Publish()))
                    .AddCommand(new DelegateCommand(UpdateJobProfileFromCurrentProperties))
                    .AddCommand<HideLockLayerCommand>()
                    .Build();
            }
        }

        private void UpdateJobProfileFromCurrentProperties(object obj)
        {
            _dispatcher.BeginInvoke(() =>
            {
                var settings = _settingsProvider.Settings?.Copy();
                Profiles = settings?.ConversionProfiles;
                if (Profiles != null)
                {
                    SelectedProfile = Profiles.First(profile => profile.Name == _selectedProfileProvider.SelectedProfile.Name);
                }
            });
        }

        private void SetProfileForSelection(object o)
        {
            _selectedProfileProvider.SelectedProfile = _selectedProfileProvider.Profiles.First(profile => profile.Name == Job.Profile.Name);
        }

        private void SetOutputFormatExecute(OutputFormat parameter)
        {
            OutputFormat = parameter;
        }

        private void ChangeOutputFormat()
        {
            if (Job?.Profile?.OutputFormat == null)
                return;

            var outputFormat = Job.Profile.OutputFormat;

            OutputFilename = _outputFormatHelper.EnsureValidExtension(OutputFilename, outputFormat);
        }

        private void ComposeOutputFilename()
        {
            _job.OutputFilenameTemplate = _pathUtil.Combine(OutputFolder, OutputFilename);
        }

        private void BrowseFileExecute(object parameter)
        {
            _directoryHelper.CreateDirectory(OutputFolder);

            var result = _saveFileQuery.GetFileName(OutputFolder, OutputFilename, OutputFormat);

            if (result.Success)
            {
                SetOutputFilenameAndFolder(result.Data.Filepath);
                _latestDialogFilePath = result.Data.Filepath;
                OutputFormat = result.Data.OutputFormat;
            }
        }

        public void SetJob(Job job)
        {
            if (job == null)
                return;

            _job = job;

            SetMetadata();
            SetOutputFilenameAndFolder(_job.OutputFilenameTemplate);
            OutputFormat = job.Profile.OutputFormat;
            RaisePropertyChanged(nameof(SelectedProfile));
            RaisePropertyChanged(nameof(Job));
        }

        private void SetMetadata()
        {
            Title = Job.TokenReplacer.ReplaceTokens(Job.Profile.TitleTemplate);
            Author = Job.TokenReplacer.ReplaceTokens(Job.Profile.AuthorTemplate);
            Subject = Job.TokenReplacer.ReplaceTokens(Job.Profile.SubjectTemplate);
            Keyword = Job.TokenReplacer.ReplaceTokens(Job.Profile.KeywordTemplate);

            RaisePropertyChanged(nameof(Title));
            RaisePropertyChanged(nameof(Author));
            RaisePropertyChanged(nameof(Subject));
            RaisePropertyChanged(nameof(Keyword));
        }

        private void SetOutputFilenameAndFolder(string filenameTemplate)
        {
            OutputFilename = _pathUtil.GetFileName(filenameTemplate);
            OutputFolder = _pathUtil.GetLongDirectoryName(filenameTemplate);
        }

        private void UpdateNumberOfPrintJobsHint(int numberOfPrintJobs)
        {
            if (numberOfPrintJobs <= 1)
                NumberOfPrintJobsHint = "";
            else if (numberOfPrintJobs > 99)
            {
                NumberOfPrintJobsHint = "99+";
            }
            else
            {
                NumberOfPrintJobsHint = numberOfPrintJobs.ToString();
            }

            RaisePropertyChanged(nameof(NumberOfPrintJobsHint));
        }

        public void ExecuteWorkflowStep(Job job)
        {
            SetJob(job);
        }

        private void SaveExecute(object obj)
        {
            ChangeOutputFormat(); //Ensure extension before the checks

            if (_interactiveProfileChecker.CheckWithErrorResultInOverlay(_job))
                if (CheckIfFileExistsElseNotifyUser())
                    CallFinishInteraction();
        }

        private bool CheckIfFileExistsElseNotifyUser()
        {
            var filePath = _pathSafe.Combine(OutputFolder, OutputFilename);

            //Do not inform user, if SaveFileDialog already did
            if (filePath == _latestDialogFilePath)
                return true;

            if (!_file.Exists(filePath))
                return true;

            var title = Translation.ConfirmSaveAs.ToUpper(CultureInfo.CurrentCulture);
            var text = Translation.GetFileAlreadyExists(filePath);

            var interaction = new MessageInteraction(text, title, MessageOptions.YesNo, MessageIcon.Exclamation);

            _interactionRequest.Raise(interaction, NotifyFileExistsCallback);

            return false;
        }

        private void CallFinishInteraction()
        {
            _job.Passwords = JobPasswordHelper.GetJobPasswords(_job.Profile, _job.Accounts); //todo: Why here? Aren't we doing that already somewhere else?
            FinishInteraction();
        }

        private void NotifyFileExistsCallback(MessageInteraction interaction)
        {
            if (interaction.Response == MessageResponse.Yes)
            {
                CallFinishInteraction();
            }
            _latestDialogFilePath = "";
        }

        private void EmailExecute(object obj)
        {
            var tempDirectory = _pathUtil.Combine(_tempFolderProvider.TempFolder,
                Path.GetFileNameWithoutExtension(Path.GetRandomFileName()));
            Directory.CreateDirectory(tempDirectory);

            _job.OutputFilenameTemplate = _pathUtil.Combine(tempDirectory, OutputFilename);

            if (!_interactiveProfileChecker.CheckWithErrorResultInOverlay(_job))
            {
                try
                {
                    Directory.Delete(tempDirectory);
                }
                catch { }
            }

            _job.Profile.EmailClientSettings.Enabled = true;
            _job.Profile.OpenViewer = false;

            CallFinishInteraction();
        }

        private void CancelExecute(object obj)
        {
            // This needs to be called before the exceptions are thrown
            FinishInteraction();
            throw new AbortWorkflowException("User cancelled in the PrintJobView");
        }

        private void MergeExecute(object obj)
        {
            // This needs to be called before the exceptions are thrown
            FinishInteraction();
            throw new ManagePrintJobsException();
        }

        private void FinishInteraction()
        {
            StepFinished?.Invoke(this, EventArgs.Empty);
        }

        public ConversionProfile SelectedProfile
        {
            get { return Job?.Profile; }
            set
            {
                if (Job == null)
                    return;
                Job.Profile = value;
                RaisePropertyChanged();
                if (!string.IsNullOrWhiteSpace(Job.Profile.TargetDirectory))
                {
                    OutputFolder = Job.Profile.TargetDirectory;
                }
                SetMetadata();
            }
        }

        public string Title
        {
            get { return _title; }
            set
            {
                _title = value;
                if (Job?.JobInfo?.Metadata != null)
                    Job.JobInfo.Metadata.Title = _title;
            }
        }

        public string Author
        {
            get { return _author; }
            set
            {
                _author = value;
                if (Job?.JobInfo?.Metadata != null)
                    Job.JobInfo.Metadata.Author = _author;
            }
        }

        public string Keyword
        {
            get { return _keyword; }
            set
            {
                _keyword = value;
                if (Job?.JobInfo?.Metadata != null)
                    Job.JobInfo.Metadata.Keywords = _keyword;
            }
        }

        public string Subject
        {
            get { return _subject; }
            set
            {
                _subject = value;
                if (Job?.JobInfo?.Metadata != null)
                    Job.JobInfo.Metadata.Subject = _subject;
            }
        }

        public string NumberOfPrintJobsHint { get; private set; }

        public DelegateCommand<OutputFormat> SetOutputFormatCommand { get; }
        public DelegateCommand SaveCommand { get; }
        public ICommand SendByEmailCommand { get; }
        public ICommand BrowseFileCommand { get; }
        public ICommand MergeCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand EditProfileCommand { get; private set; }

        public Job Job
        {
            get { return _job; }
            private set
            {
                _job = value; RaisePropertyChanged();
            }
        }

        public ObservableCollection<ConversionProfile> Profiles { get; private set; }

        public IEnumerable<OutputFormat> OutputFormats => System.Enum.GetValues(typeof(OutputFormat)) as OutputFormat[];

        public OutputFormat OutputFormat
        {
            get { return Job?.Profile?.OutputFormat ?? OutputFormat.Pdf; }
            set
            {
                Job.Profile.OutputFormat = value;
                ChangeOutputFormat();
                RaisePropertyChanged();
            }
        }

        public string OutputFolder
        {
            get { return _outputFolder; }
            set
            {
                SetProperty(ref _outputFolder, value);
                ComposeOutputFilename();
                SaveCommand.RaiseCanExecuteChanged();
            }
        }

        public string OutputFilename
        {
            get { return _outputFilename; }
            set
            {
                SetProperty(ref _outputFilename, value);
                ComposeOutputFilename();
            }
        }

        public bool IsProfileEnabled
        {
            get
            {
                return GpoSettings != null ? !GpoSettings.DisableProfileManagement : true;
            }
        }

        public event EventHandler StepFinished;
    }
}
