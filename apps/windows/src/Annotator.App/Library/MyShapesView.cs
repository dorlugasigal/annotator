using Annotator.App.Pickers;
using Annotator.App.Shell;
using Annotator.Core;
using Annotator.Workspace;
using Annotator.Workspace.Library;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Annotator.App.Library;

/// <summary>What the Tools window keeps while it is hidden or rebuilt (the Mac view's @State and the controller's section).</summary>
internal sealed class LibraryState
{
    public ToolLibrarySection Section { get; set; } = ToolLibrarySection.Shapes;

    public string Name { get; set; } = string.Empty;

    public bool Editing { get; set; }

    public Guid? SelectedId { get; set; }

    public string? Message { get; set; }

    public string? ActionError { get; set; }
}

/// <summary>
/// Port of MyShapesLibrary (ToolLibrary.swift): save the selected drawings under a name, insert a saved shape at the canvas
/// center, and in Edit mode select one to rename or delete it, with the Mac's words, states and messages.
/// </summary>
internal sealed class MyShapesView
{
    private const double ButtonHeight = 24;
    private readonly WorkspaceModel _model;
    private readonly ShapeLibrary _library;
    private readonly ShapeThumbnails? _thumbnails;
    private readonly LibraryState _state;
    private readonly Action _inserted;
    private readonly MacStack _stack = new() { Spacing = 8 };
    private readonly TextBlock _heading;
    private readonly ParityButton _edit;
    private readonly TextBlock _editTitle;
    private readonly MacStack _manage = new() { Orientation = Orientation.Horizontal, Spacing = 8, Height = ButtonHeight };
    private readonly ParityButton _rename;
    private readonly ParityButton _delete;
    private readonly TextBox _name;
    private readonly Grid _saveRow = new() { Height = ButtonHeight };
    private readonly ParityButton _save;
    private readonly ParityButton _reload;
    private readonly WrappedText _explanation;
    private readonly WrappedText _error;
    private readonly WrappedText _message;
    private readonly ProgressRing _busy;
    private readonly TextBlock _empty;
    private readonly ScrollViewer _cellsViewer;
    private readonly Grid _cells = new() { ColumnSpacing = 8, RowSpacing = 10, Padding = new Thickness(2) };
    private readonly WrappedText _hint;
    private readonly Dictionary<Guid, Cell> _shown = [];
    private WorkspaceSnapshot _snapshot;
    private IReadOnlyList<SavedShape>? _built;
    private string? _thumbnailError;
    private bool _dialogOpen;

    public MyShapesView(WorkspaceModel model, ShapeLibrary library, ShapeThumbnails? thumbnails, LibraryState state, Action inserted)
    {
        _model = model;
        _library = library;
        _thumbnails = thumbnails;
        _state = state;
        _inserted = inserted;
        _snapshot = model.Snapshot;

        // HStack { caption; Spacer; Edit or Done }
        var header = new Grid { Height = ButtonHeight };
        _heading = LibraryParts.Text("Your saved drawings", 10, "AnnotatorSecondaryText", macWidth: 99.5);
        _heading.HorizontalAlignment = HorizontalAlignment.Left;
        header.Children.Add(_heading);
        _edit = LibraryParts.Bordered("Edit", 47.5, ButtonHeight, 13);
        _editTitle = (TextBlock)((Border)_edit.Content).Child;
        _edit.HorizontalAlignment = HorizontalAlignment.Right;
        _edit.Click += (_, _) =>
        {
            _state.Editing = !_state.Editing;
            _state.SelectedId = null;
            Update();
        };
        header.Children.Add(_edit);
        _stack.Children.Add(header);

        // Editing: HStack { Rename...; Delete... (destructive) }
        _rename = LibraryParts.Bordered("Rename...", LibraryParts.RegularWidth("Rename..."), ButtonHeight, 13);
        _rename.Click += (_, _) => _ = RenameAsync();
        _delete = LibraryParts.Bordered("Delete...", LibraryParts.RegularWidth("Delete..."), ButtonHeight, 13, "AnnotatorErrorText");
        _delete.Click += (_, _) => _ = DeleteAsync();
        _manage.Children.Add(_rename);
        _manage.Children.Add(_delete);
        _stack.Children.Add(_manage);

        // TextField("Shape name").textFieldStyle(.roundedBorder)
        _name = NameField();
        _stack.Children.Add(_name);

        // HStack { Save selection; Spacer; Reload }
        _save = LibraryParts.Bordered("Save selection", 112, ButtonHeight, 13);
        _save.HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetAutomationId(_save, "my-shapes-save");
        _save.Click += (_, _) => _ = SaveAsync();
        _reload = LibraryParts.Bordered("Reload", 65.5, ButtonHeight, 13);
        _reload.HorizontalAlignment = HorizontalAlignment.Right;
        _reload.Click += (_, _) => _ = Run(_library.ReloadAsync());
        _saveRow.Children.Add(_save);
        _saveRow.Children.Add(_reload);
        _stack.Children.Add(_saveRow);

        _explanation = new WrappedText("Save drawings or text. Covers, pixel effects and images cannot be saved.", 10, "AnnotatorSecondaryText");
        _stack.Children.Add(_explanation);

        _error = new WrappedText(string.Empty, 10, "AnnotatorErrorText");
        _error.Block.IsTextSelectionEnabled = true;
        _stack.Children.Add(_error);
        _message = new WrappedText(string.Empty, 10, "AnnotatorSecondaryText");
        _stack.Children.Add(_message);

        // ProgressView().controlSize(.small)
        _busy = new ProgressRing { Width = 16, Height = 16, IsActive = false, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(_busy, "Updating My shapes");
        _stack.Children.Add(_busy);

        // Text("No saved shapes yet.").font(.callout).padding(.vertical, 4)
        _empty = LibraryParts.Text("No saved shapes yet.", 12, "AnnotatorPrimaryText", macWidth: 0);
        _empty.Margin = new Thickness(0, 4, 0, 4);
        _empty.HorizontalAlignment = HorizontalAlignment.Left;
        _stack.Children.Add(_empty);

        // ScrollView { LazyVGrid([.flexible(), .flexible()], spacing: 10) { cells }.padding(2) }.frame(height: 230)
        _cells.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _cells.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _cellsViewer = new ScrollViewer
        {
            Height = 230,
            Content = _cells,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ZoomMode = ZoomMode.Disabled,
            IsTabStop = false,
        };
        _stack.Children.Add(_cellsViewer);

        _hint = new WrappedText(string.Empty, 10, "AnnotatorSecondaryText");
        _stack.Children.Add(_hint);

        _library.Changed += Update;
        Update();
    }

    /// <summary>The section's content, as wide as the scroll area that holds it.</summary>
    public FrameworkElement Root => _stack;

    /// <summary>Stops following the library (the window's content is replaced).</summary>
    public void Detach() => _library.Changed -= Update;

    /// <summary>The section appeared: read the library again (the Mac's .task { await library.reload() }).</summary>
    public void Appeared() => _ = Run(_library.ReloadAsync());

    public void Apply(WorkspaceSnapshot snapshot)
    {
        _snapshot = snapshot;
        Update();
    }

    /// <summary>Focuses the name field, as the Mac's Save to My shapes leaves the person ready to type a name.</summary>
    public void FocusName()
    {
        if (!_state.Editing)
        {
            _name.Focus(FocusState.Programmatic);
        }
    }

    private SavedShape? SelectedShape => _library.Shapes.FirstOrDefault(shape => shape.Id == _state.SelectedId);

    /// <summary>Shows the library's, the workspace's and the view's state, as the Mac's body does on every change.</summary>
    private void Update()
    {
        bool editing = _state.Editing;
        bool busy = _library.IsBusy;
        bool empty = _library.Shapes.Count == 0;
        _heading.Text = editing ? "Select a saved shape to manage it." : "Your saved drawings";
        _heading.MinWidth = editing ? 0 : 99.5;
        string editTitle = editing ? "Done" : "Edit";
        if (_editTitle.Text != editTitle)
        {
            _editTitle.Text = editTitle;
            _edit.Width = editing ? LibraryParts.RegularWidth("Done") : 47.5;
        }
        AutomationProperties.SetName(_edit, editing ? "Done editing saved shapes" : "Edit saved shapes");
        _edit.IsEnabled = !busy && !empty;
        _manage.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        _rename.IsEnabled = SelectedShape is not null && !busy;
        _delete.IsEnabled = SelectedShape is not null && !busy;
        _name.Visibility = _saveRow.Visibility = _explanation.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        _save.IsEnabled = _snapshot.SelectionCount > 0 && !busy && _library.IsLoaded;
        _reload.IsEnabled = !busy;
        string? error = _state.ActionError ?? _thumbnailError ?? _library.ErrorMessage;
        _error.Text = error ?? string.Empty;
        _error.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        _message.Text = _state.Message ?? string.Empty;
        _message.Visibility = error is null && _state.Message is not null ? Visibility.Visible : Visibility.Collapsed;
        _busy.IsActive = busy;
        _busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _empty.Text = _library.IsLoaded ? "No saved shapes yet." : "Loading saved shapes...";
        _empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        _cellsViewer.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        _hint.Text = empty ? "Select something in your drawing, enter a name, then click Save selection."
            : editing ? "Rename or delete the selected saved shape. Existing copies stay unchanged."
            : "Click to insert. Drawings saved together arrive as one group; Ungroup separates them.";
        if (!ReferenceEquals(_built, _library.Shapes))
        {
            BuildCells();
        }
        foreach (Cell cell in _shown.Values)
        {
            cell.Update(editing, editing && cell.Shape.Id == _state.SelectedId,
                !busy && _library.IsLoaded && (editing || _snapshot.ActiveDisplay is not null));
        }
    }

    private void BuildCells()
    {
        _built = _library.Shapes;
        _cells.Children.Clear();
        _cells.RowDefinitions.Clear();
        _shown.Clear();
        IReadOnlyList<SavedShape> shapes = _library.Shapes;
        for (int index = 0; index < shapes.Count; index++)
        {
            if (index % 2 == 0)
            {
                _cells.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            var cell = new Cell(shapes[index], Activate);
            Grid.SetRow(cell.Button, index / 2);
            Grid.SetColumn(cell.Button, index % 2);
            _cells.Children.Add(cell.Button);
            _shown[shapes[index].Id] = cell;
        }
        if (_thumbnails is { } thumbnails && shapes.Count > 0)
        {
            _ = DrawThumbnailsAsync(thumbnails, [.. shapes]);
        }
    }

    /// <summary>Draws the thumbnails of <paramref name="shapes"/> at the cells' size and shows each one that is still listed.</summary>
    private async Task DrawThumbnailsAsync(ShapeThumbnails thumbnails, SavedShape[] shapes)
    {
        double scale = _stack.XamlRoot?.RasterizationScale ?? 1;
        double width = Math.Max(40, (ContentWidth() - 4 - 8) / 2 - 8);
        try
        {
            IReadOnlyDictionary<Guid, ImageSource> drawn = await thumbnails.DrawAsync(shapes, width, Cell.ThumbnailHeight, scale);
            foreach ((Guid id, ImageSource image) in drawn)
            {
                if (_shown.TryGetValue(id, out Cell? cell))
                {
                    cell.Show(image);
                }
            }
            if (_thumbnailError is not null)
            {
                _thumbnailError = null;
                Update();
            }
        }
        catch (ThumbnailFailure failure)
        {
            _thumbnailError = failure.Message;
            Update();
        }
    }

    private double ContentWidth() => _stack.ActualWidth > 0 ? _stack.ActualWidth : Workspace.Library.ToolLibraryLayout.ViewportWidth(Workspace.Library.ToolLibraryLayout.WindowWidth);

    /// <summary>A click on a saved shape: selects it while editing, otherwise inserts it at the canvas center and closes Tools.</summary>
    private void Activate(SavedShape shape)
    {
        if (_state.Editing)
        {
            _state.SelectedId = shape.Id;
            Update();
            return;
        }
        try
        {
            _model.InsertSavedShape(shape);
            _state.ActionError = null;
            _state.Message = $"Inserted {shape.Name} at the canvas center.";
            Update();
            _inserted();
        }
        catch (Exception error) when (error is ShapeLibraryError or CanvasError)
        {
            _state.ActionError = error.Message;
            Update();
        }
    }

    /// <summary>Mac saveSelection: checks the selection, then saves it under the typed name.</summary>
    private async Task SaveAsync()
    {
        if (_library.IsBusy || !_library.IsLoaded)
        {
            return;
        }
        ShapeSaveSource source = _model.ShapeToSave();
        if (source.Problem is not null || source.Document is not { } document)
        {
            _state.ActionError = source.Problem;
            Update();
            return;
        }
        string name = _state.Name;
        _state.ActionError = null;
        _state.Message = null;
        Update();
        if (await _library.SaveAsync(name, document) is { } saved)
        {
            _state.Name = string.Empty;
            _name.Text = string.Empty;
            _state.Message = $"Saved {saved.Name} to My shapes.";
        }
        Update();
    }

    /// <summary>The Mac's "Rename saved shape" alert with the name field, Save and Cancel.</summary>
    private async Task RenameAsync()
    {
        if (SelectedShape is not { } shape || _dialogOpen || _stack.XamlRoot is not { } root)
        {
            return;
        }
        TextBox field = NameField();
        field.Text = shape.Name;
        field.Width = 280;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Rename saved shape",
            Content = field,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        field.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Enter)
            {
                args.Handled = true;
                dialog.Hide();
                _ = Run(_library.RenameAsync(shape, field.Text));
            }
        };
        if (await Show(dialog) == ContentDialogResult.Primary)
        {
            await _library.RenameAsync(shape, field.Text);
        }
    }

    /// <summary>The Mac's "Delete saved shape?" alert: Delete removes it from My shapes; copies on canvases stay.</summary>
    private async Task DeleteAsync()
    {
        if (SelectedShape is not { } shape || _dialogOpen || _stack.XamlRoot is not { } root)
        {
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Delete saved shape?",
            Content = new TextBlock { Text = $"Delete {shape.Name} from My shapes? Copies on your canvas will stay.", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await Show(dialog) == ContentDialogResult.Primary)
        {
            _state.ActionError = null;
            _state.Message = null;
            Update();
            await _library.DeleteAsync(shape);
        }
    }

    private async Task<ContentDialogResult> Show(ContentDialog dialog)
    {
        _dialogOpen = true;
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    /// <summary>Waits for a library operation; its outcome shows through <see cref="ShapeLibrary.Changed"/>.</summary>
    private static async Task Run(Task operation) => await operation;

    /// <summary>The Mac's rounded text field: 24 points high, the system font at 13 points, "Shape name" as its placeholder.</summary>
    private TextBox NameField()
    {
        var field = new TextBox
        {
            Height = ButtonHeight,
            MinHeight = 0,
            MinWidth = 0,
            FontSize = 13,
            Padding = new Thickness(6, 3, 6, 2),
            PlaceholderText = "Shape name",
            Text = _state.Name,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
        };
        SolidColorBrush fill = LibraryColors.FieldFill, stroke = LibraryColors.FieldStroke;
        foreach (string key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused" })
        {
            field.Resources[key] = fill;
        }
        foreach (string key in new[] { "TextControlBorderBrush", "TextControlBorderBrushPointerOver" })
        {
            field.Resources[key] = stroke;
        }
        field.Resources["TextControlThemeMinHeight"] = 0.0;
        AutomationProperties.SetName(field, "Shape name");
        AutomationProperties.SetAutomationId(field, "my-shapes-name");
        field.TextChanged += (_, _) =>
        {
            if (field == _name)
            {
                _state.Name = field.Text;
            }
        };
        field.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Enter && field == _name)
            {
                args.Handled = true;
                _ = SaveAsync();
            }
        };
        return field;
    }

    /// <summary>
    /// One saved shape: its thumbnail over a gray rounded box and its name in up to two lines, padded by 4 points, with a
    /// 2-point accent ring while it is selected for editing.
    /// </summary>
    private sealed class Cell
    {
        public const double ThumbnailHeight = 72;
        private readonly Border _ring;
        private readonly Border _thumbnail;

        public Cell(SavedShape shape, Action<SavedShape> activate)
        {
            Shape = shape;
            _thumbnail = new Border
            {
                Height = ThumbnailHeight,
                CornerRadius = new CornerRadius(6),
                Background = LibraryColors.ThumbnailFill,
                IsHitTestVisible = false,
            };
            AutomationProperties.SetAccessibilityView(_thumbnail, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            // A picture of the saved shape: it keeps its colors in a contrast theme (WIN-UI-07).
            Shell.OwnColors.Mark(_thumbnail);
            TextBlock name = LibraryParts.Label(shape.Name, 10, "AnnotatorPrimaryText");
            name.TextWrapping = TextWrapping.Wrap;
            name.MaxLines = 2;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.VerticalAlignment = VerticalAlignment.Center;
            var nameHost = new Grid { Height = 30 };
            nameHost.Children.Add(name);
            var content = new MacStack { Spacing = 4, Margin = new Thickness(2) };
            content.Children.Add(_thumbnail);
            content.Children.Add(nameHost);
            _ring = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(2), Child = content };
            Button = new ParityButton
            {
                Style = AppResources.Style("AnnotatorPlainButton"),
                CornerRadius = new CornerRadius(8),
                Content = _ring,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                AccessibleSelected = false,
            };
            Button.Click += (_, _) => activate(Shape);
        }

        public SavedShape Shape { get; }

        public ParityButton Button { get; }

        public void Update(bool editing, bool selected, bool enabled)
        {
            AutomationProperties.SetName(Button, editing ? $"Select saved shape {Shape.Name}" : $"Insert {Shape.Name}");
            string help = editing ? $"Select {Shape.Name} to rename or delete it" : $"Insert {Shape.Name} at the canvas center";
            AutomationProperties.SetHelpText(Button, help);
            ToolTipService.SetToolTip(Button, help);
            Button.AccessibleSelected = selected;
            Button.IsEnabled = enabled;
            _ring.BorderBrush = selected ? AppResources.Brush("AnnotatorAccentBrush") : null;
        }

        public void Show(ImageSource image) =>
            _thumbnail.Child = new Image { Source = image, Stretch = Stretch.Uniform, IsHitTestVisible = false };
    }
}
