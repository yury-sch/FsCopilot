namespace FsCopilot.Views;

public partial class MainWindow : Window
{
    private const double RowSpacing = 10;

    private readonly double _baseHeight;
    private readonly Border _notesCard;

    public MainWindow()
    {
        InitializeComponent();

        _baseHeight = Height;

        // Increase window height when profile notes are visible
        _notesCard = this.FindControl<Border>("NotesCard")!;
        _notesCard.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty || e.Property == IsVisibleProperty) UpdateHeight();
        };
    }

    private void UpdateHeight() =>
        Height = _baseHeight + (_notesCard.IsVisible ? _notesCard.Bounds.Height + RowSpacing : 0);
}