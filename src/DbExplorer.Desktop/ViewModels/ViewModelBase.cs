using CommunityToolkit.Mvvm.ComponentModel;
using DbExplorer.Application.Sessions;

namespace DbExplorer.Desktop.ViewModels;

public abstract class ViewModelBase : ObservableObject;

/// <summary>Tab view models receive the active session (or null on disconnect).</summary>
public interface ISessionAware
{
    void Attach(DatabaseSession? session);
}
