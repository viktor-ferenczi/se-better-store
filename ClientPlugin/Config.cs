using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ClientPlugin.Settings;
using ClientPlugin.Settings.Elements;

namespace ClientPlugin;

public class Config : INotifyPropertyChanged
{
    #region Options

    private bool enabled = true;
    private bool mergeLots = true;

    #endregion

    #region User interface

    public readonly string Title = "Better Store";

    [Checkbox(
        description: "Opens the Better Store screen at Store blocks. Turn it off to get the game's own Store screen."
    )]
    public bool Enabled
    {
        get => enabled;
        set => SetField(ref enabled, value);
    }

    [Checkbox(
        description: "Shows an item sold both by the unit and in 1,000 unit lots as one line, and trades the cheapest mix. The Store screen has the same checkbox."
    )]
    public bool MergeLots
    {
        get => mergeLots;
        set => SetField(ref mergeLots, value);
    }

    #endregion

    #region Property change notification boilerplate

    public static readonly Config Default = new Config();
    public static readonly Config Current = ConfigStorage.Load();

    public event PropertyChangedEventHandler PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    #endregion
}
