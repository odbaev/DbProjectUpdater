using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using DbProjectUpdater.Command;
using DbProjectUpdater.Model;
using Microsoft.Build.Evaluation;
using Microsoft.SqlServer.Management.Smo;
using Microsoft.Win32;

namespace DbProjectUpdater.ViewModel
{
    public class UpdaterViewModel : INotifyPropertyChanged
    {
        private UpdaterModel _updater = new UpdaterModel();

        private bool _isConnectServer;
        private bool _isUpdateDbProject;

        private string _serverName;
        private int _progressValue;

        private readonly OpenFileDialog _projectDialog;

        private readonly IProgress<(int, int)> _progress;

        public UpdaterViewModel()
        {
            _projectDialog = new OpenFileDialog()
            {
                Filter = "VS Database Project|*.sqlproj"
            };

            _progress = new Progress<(int Step, int DbObjectsNumber)>(t =>
            {
                if (ProgressMaxValue != t.DbObjectsNumber)
                {
                    ProgressMaxValue = t.DbObjectsNumber;
                    OnPropertyChanged("ProgressMaxValue");
                }

                ProgressValue += t.Step;
            });

            ConnectServerCommand = new ButtonCommand(ConnectServerAsync, () => !string.IsNullOrWhiteSpace(ServerName) && !IsConnectServer);
            OpenProjectDialogCommand = new ButtonCommand(OpenDbProject, () => true);
            UpdateDbProjectCommand = new ButtonCommand(UpdateDbProjectAsync, () => _updater.Db != null && _updater.DbProject != null && !IsUpdateDbProject);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ButtonCommand OpenProjectDialogCommand { get; set; }
        public ButtonCommand ConnectServerCommand { get; set; }
        public ButtonCommand UpdateDbProjectCommand { get; set; }

        public IEnumerable<string> ServerNames => Properties.Settings.Default.Servers.Cast<string>();

        public string ServerName
        {
            get => _serverName;
            set
            {
                _serverName = value;

                ConnectServerCommand.RaiseCanExecuteChanged();
            }
        }

        public IEnumerable<string> DbNames => _updater.Server?.Databases.Cast<Database>().Where(db => !db.IsSystemObject).Select(db => db.Name);

        public string DbName
        {
            get => _updater.Db?.Name;
            set
            {
                _updater.Db = _updater.Server?.Databases[value];

                UpdateDbProjectCommand.RaiseCanExecuteChanged();
            }
        }

        public string DbProjectFileName
        {
            get => _updater.DbProject?.FullPath;
            set
            {
                _updater.DbProject = ProjectCollection.GlobalProjectCollection.LoadedProjects.FirstOrDefault(p => p.FullPath == value);

                if (_updater.DbProject == null)
                {
                    try
                    {
                        _updater.DbProject = new Project(value);
                    }
                    catch { }
                }

                OnPropertyChanged("DbProjectFileName");

                UpdateDbProjectCommand.RaiseCanExecuteChanged();
            }
        }

        public bool IsConnectServer
        {
            get => _isConnectServer;
            set
            {
                _isConnectServer = value;

                OnPropertyChanged("IsConnectServer");

                ConnectServerCommand.RaiseCanExecuteChanged();
            }
        }

        public bool IsUpdateDbProject
        {
            get => _isUpdateDbProject;
            set
            {
                _isUpdateDbProject = value;

                UpdateDbProjectCommand.RaiseCanExecuteChanged();
            }
        }

        public int ProgressMaxValue { get; set; } = 1;

        public int ProgressValue
        {
            get => _progressValue;
            set
            {
                _progressValue = value;

                OnPropertyChanged("ProgressValue");
            }
        }

        public async void ConnectServerAsync()
        {
            IsConnectServer = true;

            try
            {
                await Task.Run(() =>
                {
                    _updater.Server = new Server(ServerName.Trim());

                    if (_updater.Server.Information.Version != null && Properties.Settings.Default.Servers.IndexOf(_updater.Server.Name) == -1)
                    {
                        Properties.Settings.Default.Servers.Add(_updater.Server.Name);
                        Properties.Settings.Default.Save();
                    }
                });
            }
            catch
            {
                _updater.Server = null;

                MessageBox.Show("Can't connect to server.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            OnPropertyChanged("ServerNames");
            OnPropertyChanged("DbNames");

            IsConnectServer = false;
        }

        public void OpenDbProject()
        {
            if (_projectDialog.ShowDialog() == true)
            {
                DbProjectFileName = _projectDialog.FileName;
            }
        }

        public async void UpdateDbProjectAsync()
        {
            IsUpdateDbProject = true;

            ProgressValue = 0;

            try
            {
                await Task.Run(() =>
                {
                    _updater.UpdateDbProject(_progress);
                });
            }
            catch (Exception ex)
            {
                string separator = string.Concat(Enumerable.Repeat(Environment.NewLine, 2));
                string message = ex is AggregateException aex ? string.Join(separator, aex.InnerExceptions.Select(e => e.Message)) : ex.Message;

                MessageBox.Show(message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            IsUpdateDbProject = false;
        }

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
