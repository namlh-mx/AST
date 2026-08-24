using System.IO;
using AST.Shell.Services;
using Microsoft.Win32;

namespace AST.Services;

public sealed class WpfFilePickerService : IFilePickerService
{
    public PickedFile? PickPrivateKey()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Chọn khóa bí mật quản trị",
            Filter = "Private key (*.pem;*.key;*.p8)|*.pem;*.key;*.p8|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return null;
        var bytes = File.ReadAllBytes(dialog.FileName);
        return new PickedFile(dialog.FileName, bytes);
    }
}
