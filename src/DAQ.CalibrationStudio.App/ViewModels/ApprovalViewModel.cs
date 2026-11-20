using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DAQ.CalibrationStudio.Domain;

namespace DAQ.CalibrationStudio.App.ViewModels;

public sealed partial class ApprovalViewModel(StudioSession session, RunResult result, Action close) : ObservableObject
{
    [ObservableProperty] private string target = "Flash";
    [ObservableProperty] private string operatorId = session.Settings.Operator;
    [ObservableProperty] private bool confirmed;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private bool writing;

    public IReadOnlyList<string> Targets { get; } = ["Flash", "RAM"];
    public IReadOnlyList<AdjustmentRow> Rows { get; } = Presentation.AdjustmentRows(result);
    public string Identity => $"{result.Daq.Model} · {result.Daq.Serial} · {(result.Simulated ? "Simulation" : "Hardware")}";
    public string CommitText => $"In {Target} schreiben";
    public bool CanCommit => Confirmed && !string.IsNullOrWhiteSpace(OperatorId) && !Writing;
    public bool CanCancel => !Writing;
    public bool CanDiscard => !string.IsNullOrWhiteSpace(OperatorId) && !Writing;

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task CommitAsync()
    {
        Writing = true;
        Status = "Koeffizienten werden übertragen und zurückgelesen …";
        try
        {
            var approval = await session.Adjustment.ApplyAsync(result, OperatorId, Confirmed, Target == "Flash", session.LifetimeToken);
            Status = approval.Error ?? "Übertragung und Rücklesen bestätigt.";
            if (approval.Verified)
            {
                session.LastRunApproved = true;
                session.Say($"{(result.Simulated ? "Simulation · " : "")}Koeffizienten in {Target} geschrieben und zurückgelesen.");
                close();
            }
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { Writing = false; }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => close();

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task DiscardAsync()
    {
        Writing = true;
        try
        {
            await session.Adjustment.DiscardAsync(result, OperatorId, session.LifetimeToken);
            session.LastRunApproved = true;
            session.Say("Koeffizientenvorschlag verworfen.");
            close();
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { Writing = false; }
    }

    partial void OnTargetChanged(string value) => OnPropertyChanged(nameof(CommitText));
    partial void OnOperatorIdChanged(string value) => NotifyCommands();
    partial void OnConfirmedChanged(bool value) => NotifyCommands();
    partial void OnWritingChanged(bool value) => NotifyCommands();

    private void NotifyCommands()
    {
        CommitCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
    }
}
