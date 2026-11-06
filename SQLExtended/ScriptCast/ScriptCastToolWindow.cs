using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace SQLExtended.ScriptCast;

/// <summary>ScriptCast as a document-well tab, like Schema Compare: the results need the width. It can be dragged out and docked.</summary>
[Guid("8E3D51A7-2C94-4B0F-A6E1-5F72C0B94D13")]
public sealed class ScriptCastToolWindow : ToolWindowPane
{
    public ScriptCastToolWindow() : base(null)
    {
        Caption = "ScriptCast";
        ThreadHelper.ThrowIfNotOnUIThread();
        Content = new ScriptCastControl();
    }

    protected override void Dispose(bool disposing)
    {
        // Cancels a cast still running (each database's command is cancelled; nothing is left executing) and drops the
        // window-activation hook and the Cast List file watcher.
        if (disposing && Content is IDisposable control)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            control.Dispose();
        }
        base.Dispose(disposing);
    }
}
