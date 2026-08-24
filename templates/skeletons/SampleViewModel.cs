// Reference skeleton — Prism ViewModel (AST project). NOT a working feature; copy the shape.
// Base = Prism.Mvvm.BindableBase. BCL-only: no System.Windows types (skill wpf-rule-mvvm-constraints).
using Prism.Commands;
using Prism.Mvvm;

namespace AST.ViewModels;

public sealed class SampleViewModel : BindableBase
{
    // Observable property = backing field + SetProperty (Prism), NOT CommunityToolkit [ObservableProperty].
    private string _title = string.Empty;
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    // Command = Prism DelegateCommand, lazily initialized.
    private DelegateCommand? _refreshCommand;
    public DelegateCommand RefreshCommand =>
        _refreshCommand ??= new DelegateCommand(Refresh);

    private void Refresh()
    {
        // Business/navigation logic here. Inject services via the constructor and
        // register the VM in App.xaml.cs when DryIoc cannot auto-wire an argument.
    }
}
