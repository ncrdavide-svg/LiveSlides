using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Microsoft.Win32;

namespace LiveSlides.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private Point _dragStart;
    private Deck? _dragCandidate;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        PreviewKeyDown += OnPreviewKeyDown;
        Closed += (_, _) => _viewModel.Shutdown();
        _viewModel.PropertyChanged += (_, e) =>
        {
            // Keep the slide on air visible in the thumbnails panel as the show advances.
            if (e.PropertyName == nameof(MainViewModel.LiveThumb) && _viewModel.LiveThumb is { } live)
                Dispatcher.BeginInvoke(() => ThumbList.ScrollIntoView(live), System.Windows.Threading.DispatcherPriority.Background);
            // Same for the running order: the selection follows the deck on air (see
            // MainViewModel.UpdateLabels), and a long list scrolls so that it stays in view.
            if (e.PropertyName == nameof(MainViewModel.SelectedDeck) && _viewModel.SelectedDeck is { } deck)
                Dispatcher.BeginInvoke(() => DeckList.ScrollIntoView(deck), System.Windows.Threading.DispatcherPriority.Background);
        };
        Loaded += async (_, _) =>
        {
            // Reopen the last show folder, if it is still there.
            if (_viewModel.LastFolder is { } last && Directory.Exists(last))
                await _viewModel.LoadFolderAsync(last);
        };
    }

    // Show keys work wherever the focus is in this window, except while typing in a text box.
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox && e.Key != Key.Escape && e.Key != Key.F5) return;
        if (_viewModel.HandleKey(e.Key)) e.Handled = true;
    }

    private void TextBox_CommitOnEnter(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        // Give the focus back to the window so the show keys work again.
        Keyboard.ClearFocus();
        Focus();
        e.Handled = true;
    }

    // ------------------------------------------------------------ toolbar

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Cartella con le presentazioni (PowerPoint e PDF)" };
        if (_viewModel.Folder is { } current && Directory.Exists(current)) dialog.InitialDirectory = current;
        if (dialog.ShowDialog(this) == true)
            await _viewModel.LoadFolderAsync(dialog.FolderName);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await _viewModel.RefreshFolderAsync();
    private void RefreshTargets_Click(object sender, RoutedEventArgs e) => _viewModel.RefreshTargets();
    private async void Start_Click(object sender, RoutedEventArgs e) => await _viewModel.StartAsync();
    private void Stop_Click(object sender, RoutedEventArgs e) => _viewModel.Stop();
    private async void Next_Click(object sender, RoutedEventArgs e) => await _viewModel.NextAsync();
    private async void Previous_Click(object sender, RoutedEventArgs e) => await _viewModel.PreviousAsync();
    private async void Tappo_Click(object sender, RoutedEventArgs e) => await _viewModel.ToggleTappoAsync();

    // ------------------------------------------------------------ tappo

    private async void ChooseTappo_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Immagine tappo",
            Filter = "Immagini (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp"
        };
        if (_viewModel.Folder is { } folder && Directory.Exists(folder)) dialog.InitialDirectory = folder;
        if (dialog.ShowDialog(this) == true)
            await _viewModel.SetTappoAsync(dialog.FileName);
    }

    private async void RemoveTappo_Click(object sender, RoutedEventArgs e) => await _viewModel.SetTappoAsync(null);

    // ------------------------------------------------------------ running order

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(+1);

    private void MoveSelected(int delta)
    {
        if (_viewModel.SelectedDeck is not { } deck) return;
        _viewModel.MoveDeck(deck, _viewModel.Decks.IndexOf(deck) + delta);
        _viewModel.SaveShow();
    }

    private void DeckList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        // A click on the tick box is a tick, never the start of a drag.
        _dragCandidate = FindAncestor<CheckBox>(e.OriginalSource) is null ? DeckAt(e.OriginalSource) : null;
    }

    private void DeckList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { _dragCandidate = null; return; }
        if (_dragCandidate is null) return;

        var position = e.GetPosition(null);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var deck = _dragCandidate;
        _dragCandidate = null;
        _viewModel.SelectedDeck = deck;
        DragDrop.DoDragDrop(DeckList, new DataObject(typeof(Deck), deck), DragDropEffects.Move);
        _viewModel.SaveShow();
    }

    private void DeckList_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(typeof(Deck)) is Deck dragged)
        {
            e.Effects = DragDropEffects.Move;
            // Live reordering: the dragged row takes the place of the row under the pointer.
            if (DeckAt(e.OriginalSource) is { } target && !ReferenceEquals(target, dragged))
                _viewModel.MoveDeck(dragged, _viewModel.Decks.IndexOf(target));
        }
        else
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }
    }

    private async void DeckList_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(typeof(Deck)) is Deck) return; // already moved during DragOver
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            await _viewModel.ImportFilesAsync(files);
    }

    private async void ThumbList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource)?.DataContext is SlideThumb thumb)
            await _viewModel.GoToSlideAsync(thumb.Deck, thumb.Index);
    }

    private static Deck? DeckAt(object source) => FindAncestor<ListBoxItem>(source)?.DataContext as Deck;

    private static T? FindAncestor<T>(object source) where T : DependencyObject
    {
        var current = source as DependencyObject;
        while (current is not null && current is not T)
        {
            // Text runs are not visuals: climb the logical tree for those.
            current = current is Visual or Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return current as T;
    }
}
