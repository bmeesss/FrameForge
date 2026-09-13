using System.Windows;
using System.Windows.Input;
using FrameForge.Core.Models;

namespace FrameForge.App.Dialogs;

/// <summary>
/// Modal import confirmation. Default / Escape / Cancel write nothing.
/// Focus starts on Cancel.
/// </summary>
public partial class ImportConfirmDialog : Window
{
    public ImportConfirmDialogState State { get; }

    public ImportConfirmDialog(ImportConfirmDialogState state)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        DataContext = State;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            CancelButton.Focus();
            Keyboard.Focus(CancelButton);
        };
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        State.ChosenMode = null;
        DialogResult = false;
        Close();
    }

    private void ImportAsNew_Click(object sender, RoutedEventArgs e)
    {
        if (!State.CanImportAsNew)
        {
            return;
        }

        State.ChosenMode = IntelligenceImportMode.ImportAsNew;
        DialogResult = true;
        Close();
    }

    private void Merge_Click(object sender, RoutedEventArgs e)
    {
        if (!State.CanMerge)
        {
            return;
        }

        State.ChosenMode = IntelligenceImportMode.Merge;
        DialogResult = true;
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            State.ChosenMode = null;
            DialogResult = false;
            e.Handled = true;
            Close();
        }
    }
}
