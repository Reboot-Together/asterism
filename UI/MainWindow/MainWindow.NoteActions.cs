using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;

namespace AsterismApp;

public sealed partial class MainWindow
{
    private NoteInfo? _contextNote;

    private void ContextCreateNote_Click(object sender, RoutedEventArgs e) =>
        NewNote(_contextFolder ?? _workspace.RootPath);

    private void ContextCreateText_Click(object sender, RoutedEventArgs e) =>
        NewTextDocument(_contextFolder ?? _workspace.RootPath);

    private void NewTextToRoot_Click(object sender, RoutedEventArgs e) =>
        NewTextDocument(_workspace.RootPath);

    private async void CreateFolder_Click(object sender, RoutedEventArgs e)
    {
        var parent = _contextFolder ?? (_contextNote is null ? null : Path.GetDirectoryName(_contextNote.Path));
        if (parent is null) return;

        var name = await PromptForName("새 폴더", "폴더 이름", "새 폴더");
        if (name is null) return;
        try
        {
            var folder = _repository.CreateFolder(parent, name);
            RefreshFolders();
            ExpandFolder(parent);
            ExpandFolder(folder);
            ApplySearch();
        }
        catch (Exception exception)
        {
            await ShowMessage("폴더 생성 실패", exception.Message);
        }
    }

    private async void RenameFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_contextFolder is not string folder) return;

        var name = await PromptForName("이름 변경", "새 폴더 이름", Path.GetFileName(folder));
        if (name is null || name.Equals(Path.GetFileName(folder), StringComparison.Ordinal)) return;
        try
        {
            SaveCurrent();
            var selectedTitle = _selected?.Title;
            var renamed = _repository.RenameFolder(folder, name);
            _vaultTreeService.RemapOrderPath(_workspace.RootPath, folder, renamed);
            ReplaceExpandedFolderPath(folder, renamed);
            _contextFolder = renamed;
            RefreshNotes();
            SelectByTitle(selectedTitle);
        }
        catch (Exception exception)
        {
            await ShowMessage("이름 변경 실패", exception.Message);
        }
    }

    private async void ContextDeleteFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_contextFolder is not string folder) return;

        var confirmation = new ContentDialog
        {
            Title = "폴더 삭제",
            Content = $"'{Path.GetFileName(folder)}' 폴더와 안에 있는 모든 자료를 삭제할까요?\n\n가능하면 Windows 휴지통으로 이동합니다.",
            PrimaryButtonText = "삭제",
            CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            _saveTimer.Stop();
            SaveCurrent();
            _repository.MoveFolderToTrash(folder);
            _vaultTreeService.RemoveOrderPath(_workspace.RootPath, folder);
            RemoveExpandedFolderBranch(folder);
            _contextFolder = null;
            _selected = null;
            RefreshNotes();
            if (_notes.Count == 0) NewNote();
            else Select(_notes[0]);
        }
        catch (Exception exception)
        {
            await ShowMessage("폴더 삭제 실패", exception.Message);
        }
    }

    private async void RenameNote_Click(object sender, RoutedEventArgs e)
    {
        if (_contextNote is not NoteInfo note) return;

        if (_selected?.Path == note.Path)
        {
            SaveCurrent();
            note = _notes.FirstOrDefault(item => item.Path == note.Path) ?? note;
        }

        var title = await PromptForName("이름 변경", "새 노트 이름", note.Title);
        if (title is null || title.Equals(note.Title, StringComparison.Ordinal)) return;
        try
        {
            var renamed = note.IsNotebook ? _notebookRepository.Rename(note.Path, title) : note.IsPlainText
                ? _textRepository.Rename(note.Path, title)
                : _repository.Rename(note.Path, title);
            _vaultTreeService.RemapOrderPath(_workspace.RootPath, note.Path, renamed.Path);
            _selected = null;
            _contextNote = renamed;
            RefreshNotes();
            Select(renamed);
        }
        catch (Exception exception)
        {
            await ShowMessage("이름 변경 실패", exception.Message);
        }
    }

    private async void ContextDeleteNote_Click(object sender, RoutedEventArgs e)
    {
        if (_contextNote is not NoteInfo note) return;

        var confirmation = new ContentDialog
        {
            Title = "자료 삭제",
            Content = $"'{note.Title}' 자료를 삭제할까요?\n\n가능하면 Windows 휴지통으로 이동하며, 휴지통을 지원하지 않는 저장소에서는 볼트의 .trash 폴더로 이동합니다.",
            PrimaryButtonText = "삭제",
            CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            _saveTimer.Stop();
            _loading = true;
            _selected = null;
            if (note.IsNotebook) _notebookRepository.MoveToTrash(note.Path);
            else if (note.IsPlainText) _textRepository.MoveToTrash(note.Path);
            else _repository.MoveToTrash(note.Path);
            _vaultTreeService.RemoveOrderPath(_workspace.RootPath, note.Path);
            _loading = false;
            RefreshNotes();
            if (_notes.Count == 0) NewNote();
            else Select(_notes[0]);
        }
        catch (Exception exception)
        {
            _loading = false;
            _selected = note;
            await ShowMessage("삭제 실패", $"자료를 삭제하지 못했습니다.\n\n{exception.Message}");
        }
    }

    private void DuplicateNote_Click(object sender, RoutedEventArgs e)
    {
        if (_contextNote is not NoteInfo note) return;

        if (_selected?.Path == note.Path)
        {
            SaveCurrent();
            note = _notes.FirstOrDefault(item => item.Path == note.Path) ?? note;
        }

        NoteInfo copy;
        if (note.IsNotebook) copy = _notebookRepository.Duplicate(note.Path);
        else if (note.IsPlainText)
        {
            copy = _textRepository.Create(Path.GetDirectoryName(note.Path)!, $"{note.Title} 복사본");
            copy = _textRepository.Save(copy.Path, note.Body);
        }
        else
        {
            var metadata = note.Metadata with { Created = DateTime.Today };
            copy = _repository.Create($"{note.Title} 복사본", metadata);
            copy = _repository.Save(copy.Path, copy.Title, note.Body, metadata);
        }
        RefreshNotes();
        Select(copy);
    }

    private async void MoveNote_Click(object sender, RoutedEventArgs e)
    {
        if (_contextNote is not NoteInfo note) return;
        if (_selected?.Path == note.Path) SaveCurrent();

        var picker = CreateFolderPicker();
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        try
        {
            var moved = note.IsNotebook ? _notebookRepository.Move(note.Path, folder.Path) : note.IsPlainText
                ? _textRepository.Move(note.Path, folder.Path)
                : _repository.Move(note.Path, folder.Path);
            _vaultTreeService.RemapOrderPath(_workspace.RootPath, note.Path, moved.Path);
            _selected = null;
            RefreshNotes();
            Select(moved);
        }
        catch (Exception exception)
        {
            await ShowMessage(
                "이동 실패",
                $"자료는 현재 저장소 내부의 폴더로만 이동할 수 있습니다.\n\n{exception.Message}");
        }
    }

    private void ShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        var path = _contextNote?.Path ?? _contextPdfPath ?? _contextFolder;
        if (path is null) return;

        var startInfo = new ProcessStartInfo("explorer.exe");
        if (File.Exists(path)) startInfo.ArgumentList.Add($"/select,{path}");
        else startInfo.ArgumentList.Add(path);
        Process.Start(startInfo);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = _workspace.RootPath,
            UseShellExecute = true
        });

    private async void ChangeFolder_Click(object sender, RoutedEventArgs e) =>
        await ChangeWorkspaceFolderAsync();

    private async Task<bool> ChangeWorkspaceFolderAsync()
    {
        var picker = CreateFolderPicker();
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return false;

        SaveCurrent();
        _workspace.SetRootPath(folder.Path);
        _repository.SetRootPath(folder.Path);
        _textRepository.SetRootPath(folder.Path);
        _notebookRepository.SetRootPath(folder.Path);
        _pdfRepository.SetRootPath(folder.Path);
        _expandedFolders.Clear();
        _folderExpansionInitialized = false;
        _selected = null;
        RefreshNotes();
        if (_notes.Count == 0) NewNote();
        else Select(_notes[0]);
        return true;
    }

    private FolderPicker CreateFolderPicker()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(this));
        return picker;
    }

    private async Task<string?> PromptForName(string title, string label, string initialValue)
    {
        var input = new TextBox
        {
            Header = label,
            Text = initialValue,
            SelectionStart = 0,
            SelectionLength = initialValue.Length
        };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = input,
            PrimaryButtonText = "확인",
            CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot
        };
        var result = await dialog.ShowAsync();
        var value = input.Text.Trim();
        return result == ContentDialogResult.Primary && value.Length > 0 ? value : null;
    }
}
