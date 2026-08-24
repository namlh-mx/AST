namespace AST.Shell.Services;

// Abstraction over the OpenFileDialog (impl in the Shell exe). Returns null if the user cancels.
public interface IFilePickerService
{
    PickedFile? PickPrivateKey();
}
