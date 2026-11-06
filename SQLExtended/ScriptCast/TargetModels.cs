using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace SQLExtended.ScriptCast;

internal abstract class Notifier : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;
    protected void Raise([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A database row in the targets list.</summary>
internal sealed class TargetDatabase(TargetServer server, string name, bool isSystem, bool isMissing) : Notifier
{
    private bool _isChecked, _isVisible = true;

    public TargetServer Server { get; } = server;
    public string Name { get; } = name;
    public bool IsSystem { get; } = isSystem;

    /// <summary>Named by the Cast List but not on the server (or the server isn't connected): shown, never cast to.</summary>
    public bool IsMissing { get; } = isMissing;

    public bool CanCheck => !IsMissing && Server.IsConnected;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            bool v = value && CanCheck;
            if (_isChecked == v) return;
            _isChecked = v;
            Raise();
            Server.OnDatabaseCheckChanged();
        }
    }

    public bool IsVisible { get => _isVisible; set { if (_isVisible == value) return; _isVisible = value; Raise(); } }

    public string Note => IsMissing ? (Server.IsConnected ? "not on this server" : "") : IsSystem ? "system" : "";
}

/// <summary>A server row: connected in Object Explorer, or named by the loaded Cast List and not connected.</summary>
internal sealed class TargetServer : Notifier
{
    private bool _isConnected, _isLoading, _isExpanded = true, _isVisible = true;
    private string _error = "";
    private bool _suppress;

    public TargetServer(string name, string connectionString)
    {
        Name = name;
        ConnectionString = connectionString;
        _isConnected = connectionString != null;
    }

    public string Name { get; }
    public string ConnectionString { get; private set; }
    public ObservableCollection<TargetDatabase> Databases { get; } = new();

    public bool IsConnected { get => _isConnected; private set { _isConnected = value; Raise(); Raise(nameof(IsUnavailable)); Raise(nameof(StatusText)); } }
    public bool IsUnavailable => !IsConnected;
    public bool IsLoading { get => _isLoading; set { _isLoading = value; Raise(); Raise(nameof(StatusText)); } }
    public string Error { get => _error; set { _error = value ?? ""; Raise(); Raise(nameof(StatusText)); Raise(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;
    public bool IsExpanded { get => _isExpanded; set { _isExpanded = value; Raise(); Raise(nameof(ExpandGlyph)); } }
    public bool IsVisible { get => _isVisible; set { if (_isVisible == value) return; _isVisible = value; Raise(); } }
    public string ExpandGlyph => IsExpanded ? "▾" : "▸";

    public IEnumerable<TargetDatabase> Checked => Databases.Where(d => d.IsChecked);

    public string StatusText
    {
        get
        {
            if (!IsConnected) return "not connected";
            if (IsLoading) return "loading…";
            if (HasError) return "could not list databases";
            int total = Databases.Count(d => !d.IsMissing), ticked = Databases.Count(d => d.IsChecked);
            return ticked == 0 ? $"{total}" : $"{ticked} of {total}";
        }
    }

    /// <summary>
    /// The server's tick box: true when every visible database is ticked, false when none are, null between. Setting
    /// it ticks or clears the <b>visible</b> databases only, so a filter narrows what Select All on a server takes.
    /// </summary>
    public bool? AllChecked
    {
        get
        {
            var visible = Databases.Where(d => d.IsVisible && d.CanCheck).ToList();
            if (visible.Count == 0) return false;
            int ticked = visible.Count(d => d.IsChecked);
            return ticked == 0 ? false : ticked == visible.Count ? true : null;
        }
        set => SetVisible(value == true);
    }

    public void SetVisible(bool isChecked)
    {
        _suppress = true;
        try
        {
            foreach (var db in Databases.Where(d => d.IsVisible)) db.IsChecked = isChecked;
        }
        finally { _suppress = false; }
        OnDatabaseCheckChanged();
    }

    public void OnDatabaseCheckChanged()
    {
        if (_suppress) return;
        Raise(nameof(AllChecked));
        Raise(nameof(StatusText));
        CheckChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler CheckChanged;

    public void MarkConnected(string connectionString)
    {
        ConnectionString = connectionString;
        IsConnected = true;
        Error = "";
    }

    public void MarkDisconnected()
    {
        ConnectionString = null;
        IsConnected = false;
        IsLoading = false;
    }

    /// <summary>Replaces the database rows, keeping the ticks of databases still present.</summary>
    public void SetDatabases(IEnumerable<(string Name, bool IsSystem)> databases, IEnumerable<string> missing)
    {
        var ticked = new HashSet<string>(Databases.Where(d => d.IsChecked).Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
        _suppress = true;
        try
        {
            Databases.Clear();
            foreach (var (name, isSystem) in databases)
                Databases.Add(new TargetDatabase(this, name, isSystem, isMissing: false) { IsChecked = ticked.Contains(name) });
            foreach (var name in missing)
                Databases.Add(new TargetDatabase(this, name, isSystem: false, isMissing: true));
        }
        finally { _suppress = false; }
        OnDatabaseCheckChanged();
        Raise(nameof(StatusText));
    }
}
