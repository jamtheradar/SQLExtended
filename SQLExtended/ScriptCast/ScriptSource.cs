using System;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;

namespace SQLExtended.ScriptCast;

/// <summary>
/// The query window a cast takes its script from: the last <c>.sql</c> document that had focus.
///
/// <para><c>DTE.ActiveDocument</c> alone won't do. ScriptCast is itself a tab in the document well, so clicking Cast
/// makes it the active window, and what "active" means for a tool window there isn't something to rely on. So the
/// pane remembers the last SQL document activated while it is open (and the one active when it was opened), and
/// says which one it is on the toolbar so there is no guessing which script will run.</para>
/// </summary>
internal sealed class ScriptSource : IDisposable
{
    private readonly WindowEvents _events;   // held: an unreferenced DTE event source is collected and stops firing
    private Document _document;

    public ScriptSource()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (Package.GetGlobalService(typeof(DTE)) is not DTE2 dte) return;

        try { Remember(dte.ActiveDocument); } catch { }
        try
        {
            _events = dte.Events.WindowEvents;
            _events.WindowActivated += OnWindowActivated;
        }
        catch (Exception ex)
        {
            Diagnostics.SQLExtendedLog.Warning("ScriptCast", "Could not watch window activation; the script source will be the window active when ScriptCast opened", ex);
        }
    }

    public event EventHandler Changed;

    /// <summary>The document's tab name, or null when there is none (or it has been closed).</summary>
    public string Label
    {
        get
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { return _document?.Name; }
            catch { _document = null; return null; }
        }
    }

    /// <summary>The selection when there is one, as SSMS runs it; otherwise the whole document. Null with no document.</summary>
    public string GetScript(out bool isSelection)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        isSelection = false;
        try
        {
            if (_document?.Object("TextDocument") is not TextDocument text) return null;
            if (text.Selection is { IsEmpty: false } selection)
            {
                isSelection = true;
                return selection.Text;
            }
            return text.StartPoint.CreateEditPoint().GetText(text.EndPoint);
        }
        catch
        {
            _document = null;   // closed since it was remembered
            return null;
        }
    }

    private void OnWindowActivated(Window gotFocus, Window lostFocus)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { Remember(gotFocus?.Document); } catch { }
    }

    private void Remember(Document document)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (document == null || ReferenceEquals(document, _document)) return;
        if (document.Object("TextDocument") is not TextDocument) return;

        string name = document.Name ?? "";
        string language = document.Language ?? "";
        if (!name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) && language.IndexOf("SQL", StringComparison.OrdinalIgnoreCase) < 0) return;

        _document = document;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_events != null) _events.WindowActivated -= OnWindowActivated;
    }
}
