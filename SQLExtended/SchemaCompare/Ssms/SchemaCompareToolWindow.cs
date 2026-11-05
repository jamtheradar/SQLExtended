using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using SQLExtended.SchemaCompare.UI;

namespace SQLExtended.SchemaCompare.Ssms;

/// <summary>Schema Compare as an SSMS document-well tab: the shared <see cref="SchemaCompareView"/> under <see cref="SsmsCompareHost"/>.</summary>
[Guid("5C0B7E2A-8F31-4D6B-9A47-2E61C3D8F0A5")]
public sealed class SchemaCompareToolWindow : ToolWindowPane
{
    public SchemaCompareToolWindow() : base(null)
    {
        Caption = "Schema Compare";
        ThreadHelper.ThrowIfNotOnUIThread();
        SsmsCompareHost.EnsureInstalled();
        Content = new SchemaCompareView();
    }
}
