namespace OnvifLib.Gui.Infrastructure;

/// <summary>
/// A yes/no prompt in front of anything the user cannot undo. Kept as an interface so the view
/// models stay free of a window reference.
/// </summary>
public interface IDialogService
{
  Task<bool> ConfirmAsync(string title, string message);
}
