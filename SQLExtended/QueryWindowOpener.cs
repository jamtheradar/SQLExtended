using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SQLExtended;

/// <summary>
/// Opens text in a new query window that is connected to a given server, the way SSMS's own "Script ... to New Query
/// Editor Window" does.
///
/// <para>Every other "open in a new window" in this extension writes a temp <c>.sql</c> file and opens it, which leaves the
/// connection to SSMS: the window gets whatever SSMS decides for an opened file, which is not necessarily the server the
/// script came from. Open Definition promises the same server and database, so it goes through
/// <c>ScriptFactory.CreateNewBlankScript(ScriptType.Sql, UIConnectionInfo, null)</c> — the factory SSMS itself uses — with
/// the source window's <c>UIConnectionInfo</c>. That is undocumented and reached by reflection, so the temp file is still
/// the fallback; the script carries a <c>USE</c> either way, which is what pins the database.</para>
/// </summary>
internal static class QueryWindowOpener
{
    /// <summary>
    /// Opens <paramref name="text"/> in a new query window connected through <paramref name="uiConnectionInfo"/> when one
    /// is given and SSMS's factory can be reached. Returns false when it fell back to opening an unconnected temp file (or
    /// could not open a window at all, in which case the text is on the clipboard). Must run on the UI thread.
    /// </summary>
    public static bool Open(string text, object uiConnectionInfo, string fileNamePrefix)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (Package.GetGlobalService(typeof(SDTE)) is not DTE dte)
            return false;

        if (uiConnectionInfo != null && TryOpenConnected(dte, text, uiConnectionInfo))
            return true;

        OpenAsTempFile(dte, text, fileNamePrefix);
        return false;
    }

    private static bool TryOpenConnected(DTE dte, string text, object uiConnectionInfo)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            object factory = ServiceCacheProxy.GetScriptFactory();
            MethodInfo create = factory == null ? null : FindCreateNewBlankScript(factory.GetType(), uiConnectionInfo.GetType());
            if (create == null)
            {
                Diagnostics.SQLExtendedLog.Warning("QueryWindow", "ScriptFactory.CreateNewBlankScript was not found; opening an unconnected window instead.");
                return false;
            }

            Document before = null;
            try { before = dte.ActiveDocument; } catch { }

            object scriptType = Enum.Parse(create.GetParameters()[0].ParameterType, "Sql");
            create.Invoke(factory, new[] { scriptType, uiConnectionInfo, null });

            // The factory makes the new window the active document. If it did not, there is no telling which window the
            // text would land in, and writing it into the user's own script is the one outcome worse than not opening.
            Document created = null;
            try { created = dte.ActiveDocument; } catch { }
            if (created == null || ReferenceEquals(created, before) || created.Object("TextDocument") is not TextDocument document)
            {
                Diagnostics.SQLExtendedLog.Warning("QueryWindow", "CreateNewBlankScript did not leave a new query window active.");
                return false;
            }

            document.StartPoint.CreateEditPoint().Insert(text ?? "");
            document.Selection.StartOfDocument();
            return true;
        }
        catch (Exception ex)
        {
            Diagnostics.SQLExtendedLog.Warning("QueryWindow", "Opening a connected query window failed", ex);
            return false;
        }
    }

    /// <summary>
    /// <c>CreateNewBlankScript(ScriptType, UIConnectionInfo, IDbConnection)</c>, on the factory's class or — if the class
    /// implements it explicitly — on the <c>IScriptFactory</c> interface it implements.
    /// </summary>
    private static MethodInfo FindCreateNewBlankScript(Type factoryType, Type connectionInfoType)
    {
        IEnumerable<MethodInfo> candidates = factoryType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Concat(factoryType.GetInterfaces().SelectMany(i => i.GetMethods()));
        return candidates.FirstOrDefault(m =>
        {
            if (!m.Name.EndsWith("CreateNewBlankScript", StringComparison.Ordinal)) return false;
            var p = m.GetParameters();
            return p.Length == 3 && p[0].ParameterType.IsEnum && p[1].ParameterType.IsAssignableFrom(connectionInfoType);
        });
    }

    /// <summary>The route every other "open in a new query window" in the extension takes.</summary>
    private static void OpenAsTempFile(DTE dte, string text, string fileNamePrefix)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SQLExtended", "Scripts");
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, $"{fileNamePrefix}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.sql");
            System.IO.File.WriteAllText(path, text ?? "", new System.Text.UTF8Encoding(false));
            dte.ItemOperations.OpenFile(path, EnvDTE.Constants.vsViewKindTextView);
        }
        catch (Exception ex)
        {
            Diagnostics.SQLExtendedLog.Warning("QueryWindow", "Opening a query window from a temp file failed; the script was copied to the clipboard", ex);
            try { System.Windows.Clipboard.SetText(text ?? ""); } catch { }
        }
    }
}
