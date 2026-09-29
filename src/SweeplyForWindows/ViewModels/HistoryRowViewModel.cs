using System.Windows.Input;
using Sweeply.Core;
using SweeplyForWindows.Localization;

namespace SweeplyForWindows.ViewModels;

/// <summary>One past clean on the Settings page, with its Undo button.</summary>
public sealed class HistoryRowViewModel
{
    public HistoryRowViewModel(CleanRecord record, Func<bool> canRun, Func<CleanRecord, Task> undo)
    {
        Record = record;
        UndoCommand = new RelayCommand(async _ => await undo(record), () => CanUndo && canRun());
    }

    public CleanRecord Record { get; }

    public string Text => Loc.Instance.Format("history.item",
        Record.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", Loc.Instance.Culture),
        Record.Items.Count, SizeFormatter.Format(Record.Bytes, Loc.Instance.Culture));

    public string StateText => Record.State switch
    {
        UndoState.Undone => Loc.Instance["history.undone"],
        UndoState.PartlyUndone => Loc.Instance.Format("history.partly", Record.RestoredCount, Record.NotRestoredCount),
        _ => "",
    };

    public bool CanUndo => Record.State != UndoState.Undone;

    public ICommand UndoCommand { get; }
}
