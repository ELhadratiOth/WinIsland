using System.Collections.ObjectModel;
using WinIsland.Core.Geometry;
using WinIsland.Core.Helpers;
using WinIsland.Core.Layout;
using WinIsland.Core.Mvvm;

namespace WinIsland.Core.Modules;

/// <summary>
/// File shelf: drop files on the island to park them, drag them out later (to another folder,
/// a chat, an email). Only paths are kept; nothing is copied.
/// </summary>
public sealed class ShelfModule : IslandModule
{
    public const string ModuleId = "shelf";

    private readonly IShellLauncher _shell;
    private bool _isDropTarget;

    public ShelfModule(IShellLauncher shell)
        : base(ModuleId, "Shelf", "")
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        ClearCommand = new RelayCommand(Clear);
        InteractivePriority = ModulePriority.Background;
        AccentArgb = Palette.Purple;
    }

    public ObservableCollection<ShelfItem> Items { get; } = [];

    public bool IsEmpty => Items.Count == 0;

    public bool HasItems => Items.Count > 0;

    /// <summary>True while files are dragged over the island: the shelf opens to receive them.</summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set
        {
            if (SetProperty(ref _isDropTarget, value))
            {
                Update();
            }
        }
    }

    public RelayCommand ClearCommand { get; }

    public override DipSize GetSize(IslandSize size) => size switch
    {
        IslandSize.Expanded => new DipSize(440, 156),
        _ => base.GetSize(size),
    };

    public void Add(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path) && !Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                Items.Add(new ShelfItem(path));
            }
        }

        Update();
    }

    public void Remove(ShelfItem item)
    {
        Items.Remove(item);
        Update();
    }

    public void Open(ShelfItem item) => _shell.Open(item.Path);

    public void Reveal(ShelfItem item) => _shell.Reveal(item.Path);

    private void Clear()
    {
        Items.Clear();
        Update();
    }

    private void Update()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasItems));
        IsAvailable = Items.Count > 0 || _isDropTarget;
        CompactText = Items.Count == 1 ? Items[0].Name : $"{Items.Count} files";
    }
}

public sealed class ShelfItem(string path)
{
    public string Path { get; } = path;

    public string Name { get; } = System.IO.Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : path;

    public string Glyph { get; } = SizeText.GlyphFor(path);
}
