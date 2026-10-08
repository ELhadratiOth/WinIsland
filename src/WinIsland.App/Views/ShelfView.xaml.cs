using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinIsland.Core.Diagnostics;
using WinIsland.Core.Modules;

namespace WinIsland.App.Views;

public sealed partial class ShelfView : UserControl
{
    public ShelfView(ShelfModule module)
    {
        Module = module;
        InitializeComponent();
    }

    public ShelfModule Module { get; }

    /// <summary>Drag out: hand the real files to the drop target (copied or moved by it, not by us).</summary>
    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        List<string> paths = [.. e.Items.OfType<ShelfItem>().Select(i => i.Path)];
        if (paths.Count == 0)
        {
            e.Cancel = true;
            return;
        }

        e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move | DataPackageOperation.Link;
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            DataProviderDeferral deferral = request.GetDeferral();
            try
            {
                var items = new List<IStorageItem>();
                foreach (string path in paths)
                {
                    items.Add(Directory.Exists(path)
                        ? await StorageFolder.GetFolderFromPathAsync(path)
                        : await StorageFile.GetFileFromPathAsync(path));
                }

                request.SetData(items);
            }
            catch (Exception ex)
            {
                AppLog.Warn(nameof(ShelfView), "Could not provide dragged files", ex);
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    private void OnItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ShelfItem item })
        {
            Module.Open(item);
        }
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ShelfItem item })
        {
            Module.Remove(item);
        }
    }
}
