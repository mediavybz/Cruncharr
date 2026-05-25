using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Threading.Tasks;
using CRD.Downloader;
using CRD.Utils.Sonarr;
using CRD.ViewModels;

namespace CRD.Views;

public partial class SettingsPageView : UserControl{
    public SettingsPageView(){
        InitializeComponent();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e){
        if (DataContext is SettingsPageViewModel){
            _ = Task.Run(SonarrClient.Instance.RefreshSonarr);
            ProgramManager.Instance.StartRunners();
        }
    }
    
}
