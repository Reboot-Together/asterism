using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace AsterismApp;

public sealed partial class MainWindow
{
    private string? _contextFolder;
    private string? _contextPdfPath;
    private VaultItem? _draggedItem;
    private FrameworkElement? _vaultDropElement;
    private VaultDropMode _vaultDropMode;

    private enum VaultDropMode
    {
        None,
        Before,
        Into,
        After
    }

    private void VaultItem_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string path) return;
        var item = FindVaultItem(path);
        if (item is not { IsFolder: true, IsRoot: false }) return;

        _folderExpansionService.ToggleExclusive(
            _workspace.RootPath,
            ExplorerFolders,
            _expandedFolders,
            item.Path);
        ApplySearch();
        e.Handled = true;
    }

    private void VaultItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string path) return;
        var item = FindVaultItem(path);
        if (item is null) return;
        if (item.IsVirtual)
        {
            e.Handled = true;
            return;
        }

        _contextNote = item.Note;
        _contextFolder = item.IsFolder ? item.Path : null;
        _contextPdfPath = item.IsPdf ? item.Path : null;
        NoteList.SelectedItem = item;

        var menu = new MenuFlyout();
        if (item.IsFolder)
        {
            menu.Items.Add(MenuItem("새 노트", ContextCreateNote_Click));
            menu.Items.Add(MenuItem("새 TXT", ContextCreateText_Click));
            menu.Items.Add(MenuItem("새 폴더", CreateFolder_Click));
            menu.Items.Add(MenuItem("PDF·IPYNB 가져오기", ImportPdf_Click));
            if (!item.IsRoot)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(MenuItem("이름 변경", RenameFolder_Click));
            }
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MenuItem("탐색기에서 보기", ShowInExplorer_Click));
            if (!item.IsRoot)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(MenuItem("삭제", ContextDeleteFolder_Click));
            }
        }
        else if (item.IsPdf)
        {
            menu.Items.Add(MenuItem("열기", OpenContextPdf_Click));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MenuItem("이름 변경", RenamePdf_Click));
            menu.Items.Add(MenuItem("폴더로 이동", MovePdf_Click));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MenuItem("탐색기에서 보기", ShowInExplorer_Click));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MenuItem("삭제", ContextDeletePdf_Click));
        }
        else
        {
            menu.Items.Add(MenuItem("옆에 열기", OpenNoteToSide_Click));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MenuItem("이름 변경", RenameNote_Click));
            menu.Items.Add(MenuItem("복사본 만들기", DuplicateNote_Click));
            menu.Items.Add(MenuItem("폴더로 이동", MoveNote_Click));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MenuItem("탐색기에서 보기", ShowInExplorer_Click));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MenuItem("삭제", ContextDeleteNote_Click));
        }

        menu.ShowAt(element);
        e.Handled = true;
    }

    private void NoteList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        _contextNote = null;
        _contextFolder = _workspace.RootPath;
        _contextPdfPath = null;

        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem("새 노트", ContextCreateNote_Click));
        menu.Items.Add(MenuItem("새 TXT", ContextCreateText_Click));
        menu.Items.Add(MenuItem("새 폴더", CreateFolder_Click));
        menu.Items.Add(MenuItem("PDF·IPYNB 가져오기", ImportPdf_Click));
        menu.ShowAt(NoteList, e.GetPosition(NoteList));
        e.Handled = true;
    }

    private void NoteList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        _draggedItem = e.Items.OfType<VaultItem>().FirstOrDefault();
        if (_draggedItem is null || _draggedItem.IsRoot || _draggedItem.IsVirtual)
        {
            e.Cancel = true;
            return;
        }

        e.Data.RequestedOperation = DataPackageOperation.Move;
        e.Data.SetText(_draggedItem.Path);
    }

    private void VaultItem_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string path) return;
        var target = FindVaultItem(path);
        var mode = ResolveDropMode(_draggedItem, target, element, e.GetPosition(element).Y);
        if (!CanDrop(_draggedItem, target, mode))
        {
            ResetDropIndicator();
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.Caption = mode switch
        {
            VaultDropMode.Before => $"{target!.Name} 앞에 배치",
            VaultDropMode.After => $"{target!.Name} 뒤에 배치",
            _ => $"{target!.Name}(으)로 이동"
        };
        e.DragUIOverride.IsCaptionVisible = true;
        ShowDropIndicator(element, mode);
        e.Handled = true;
    }

    private void VaultItem_DragLeave(object sender, DragEventArgs e)
    {
        if (ReferenceEquals(sender, _vaultDropElement)) ResetDropIndicator();
    }

    private async void VaultItem_Drop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string path) return;
        var target = FindVaultItem(path);
        var source = _draggedItem;
        var mode = ResolveDropMode(source, target, element, e.GetPosition(element).Y);
        _draggedItem = null;
        ResetDropIndicator();
        if (!CanDrop(source, target, mode)) return;

        try
        {
            var selectedTitle = _selected?.Title;
            var sourcePath = source!.Path;
            var destinationPath = sourcePath;
            if (mode == VaultDropMode.Into)
            {
                if (source.IsFolder)
                {
                    SaveCurrent();
                    destinationPath = _repository.MoveFolder(sourcePath, target!.Path);
                    _vaultTreeService.RemapOrderPath(_workspace.RootPath, sourcePath, destinationPath);
                    ReplaceExpandedFolderPath(sourcePath, destinationPath);
                }
                else if (source.Note is NoteInfo note)
                {
                    if (_selected?.Path == note.Path) SaveCurrent();
                    destinationPath = (note.IsNotebook ? _notebookRepository.Move(note.Path, target!.Path) : note.IsPlainText
                        ? _textRepository.Move(note.Path, target!.Path)
                        : _repository.Move(note.Path, target!.Path)).Path;
                    _vaultTreeService.RemapOrderPath(_workspace.RootPath, sourcePath, destinationPath);
                }
                else if (source.IsPdf)
                {
                    destinationPath = _pdfRepository.Move(sourcePath, target!.Path).Path;
                    _vaultTreeService.RemapOrderPath(_workspace.RootPath, sourcePath, destinationPath);
                }
                ExpandFolder(target!.Path);
                RefreshNotes();
                SelectByTitle(selectedTitle);
                if (source.IsPdf) await ShowPdfModeAsync(destinationPath);
            }
            else
            {
                var targetParent = Path.GetDirectoryName(target!.Path)!;
                var sourceParent = Path.GetDirectoryName(sourcePath)!;
                var movedAcrossFolder = !sourceParent.Equals(targetParent, StringComparison.OrdinalIgnoreCase);
                if (movedAcrossFolder)
                {
                    if (source.IsFolder)
                    {
                        SaveCurrent();
                        destinationPath = _repository.MoveFolder(sourcePath, targetParent);
                        _vaultTreeService.RemapOrderPath(_workspace.RootPath, sourcePath, destinationPath);
                        ReplaceExpandedFolderPath(sourcePath, destinationPath);
                    }
                    else if (source.Note is NoteInfo note)
                    {
                        if (_selected?.Path == note.Path) SaveCurrent();
                        destinationPath = (note.IsNotebook ? _notebookRepository.Move(note.Path, targetParent) : note.IsPlainText
                            ? _textRepository.Move(note.Path, targetParent)
                            : _repository.Move(note.Path, targetParent)).Path;
                        _vaultTreeService.RemapOrderPath(_workspace.RootPath, sourcePath, destinationPath);
                    }
                    else if (source.IsPdf)
                    {
                        destinationPath = _pdfRepository.Move(sourcePath, targetParent).Path;
                        _vaultTreeService.RemapOrderPath(_workspace.RootPath, sourcePath, destinationPath);
                    }
                    RefreshNotes();
                }

                var siblings = _vaultTreeService.OrderedChildren(
                    _workspace.RootPath,
                    targetParent,
                    _notes,
                    _folders,
                    _pdfDocuments);
                _vaultTreeService.Reorder(
                    _workspace.RootPath,
                    targetParent,
                    siblings,
                    destinationPath,
                    target.Path,
                    mode == VaultDropMode.After);
                ApplySearch();
                if (movedAcrossFolder) SelectByTitle(selectedTitle);
                if (source.IsPdf) await ShowPdfModeAsync(destinationPath);
            }
        }
        catch (Exception exception)
        {
            await ShowMessage("이동 실패", exception.Message);
        }
        finally
        {
            e.Handled = true;
        }
    }

    private static VaultDropMode ResolveDropMode(
        VaultItem? source,
        VaultItem? target,
        FrameworkElement element,
        double pointerY)
    {
        if (source is null || target is null || source.Path.Equals(target.Path, StringComparison.OrdinalIgnoreCase))
            return VaultDropMode.None;
        var ratio = element.ActualHeight <= 0 ? .5 : pointerY / element.ActualHeight;
        if (ratio < .28) return VaultDropMode.Before;
        if (ratio > .72) return VaultDropMode.After;
        if (target.IsFolder) return VaultDropMode.Into;
        return ratio < .5 ? VaultDropMode.Before : VaultDropMode.After;
    }

    private static bool CanDrop(VaultItem? source, VaultItem? target, VaultDropMode mode)
    {
        if (source is null
            || source.IsRoot
            || source.IsVirtual
            || target is null
            || target.IsVirtual
            || mode == VaultDropMode.None)
            return false;

        var destinationFolder = mode == VaultDropMode.Into
            ? target.IsFolder ? target.Path : null
            : Path.GetDirectoryName(target.Path);
        if (destinationFolder is null) return false;
        if (mode == VaultDropMode.Into
            && Path.GetDirectoryName(source.Path)?.Equals(destinationFolder, StringComparison.OrdinalIgnoreCase) == true)
            return false;
        if (!source.IsFolder) return true;

        var sourcePrefix = source.Path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return !destinationFolder.Equals(source.Path, StringComparison.OrdinalIgnoreCase)
            && !destinationFolder.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase);
    }

    private void ShowDropIndicator(FrameworkElement element, VaultDropMode mode)
    {
        if (!ReferenceEquals(_vaultDropElement, element)) ResetDropIndicator();
        _vaultDropElement = element;
        _vaultDropMode = mode;
        if (element is not Border border) return;
        border.BorderBrush = (Brush)Application.Current.Resources["Positive"];
        border.BorderThickness = mode switch
        {
            VaultDropMode.Before => new Thickness(0, 1, 0, 0),
            VaultDropMode.After => new Thickness(0, 0, 0, 1),
            _ => new Thickness(1)
        };
    }

    private void ResetDropIndicator()
    {
        if (_vaultDropElement is Border border)
        {
            border.BorderBrush = null;
            border.BorderThickness = new Thickness(0);
        }
        _vaultDropElement = null;
        _vaultDropMode = VaultDropMode.None;
    }

    private VaultItem? FindVaultItem(string path) => _vaultItems.FirstOrDefault(item =>
        item.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    private void ExpandFolder(string folder)
    {
        _folderExpansionService.ExpandExclusive(
            _workspace.RootPath,
            ExplorerFolders,
            _expandedFolders,
            folder);
    }

    private static MenuFlyoutItem MenuItem(string text, RoutedEventHandler handler)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += handler;
        return item;
    }

    private void ReplaceExpandedFolderPath(string source, string destination)
    {
        var sourcePrefix = source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var affected = _expandedFolders
            .Where(path => path.Equals(source, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var oldPath in affected)
        {
            _expandedFolders.Remove(oldPath);
            _expandedFolders.Add(destination + oldPath[source.Length..]);
        }
    }

    private void RemoveExpandedFolderBranch(string folder)
    {
        var prefix = folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        _expandedFolders.RemoveWhere(path =>
            path.Equals(folder, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private void SelectByTitle(string? title)
    {
        if (title is null) return;
        var note = _notes.FirstOrDefault(item => item.Title.Equals(title, StringComparison.OrdinalIgnoreCase));
        if (note is not null) Select(note);
    }
}
