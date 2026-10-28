using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace BannerlordSage.EditorProbe
{
    // In-process Qt calls made by the MCP bridge. No desktop input or window activation.
    internal sealed class NativeQtTemplate : IDisposable
    {
        private volatile bool _stopped;
        public bool Accepted { get; private set; }
        public string Diagnostic { get; private set; }

        [DllImport("Qt5Widgets.dll", EntryPoint = "?activeModalWidget@QApplication@@SAPEAVQWidget@@XZ", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr ActiveModalWidget();
        [DllImport("Qt5Core.dll", EntryPoint = "?inherits@QObject@@QEBA_NPEBD@Z", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool Inherits(IntPtr obj, [MarshalAs(UnmanagedType.LPStr)] string name);
        [DllImport("Qt5Core.dll", EntryPoint = "?children@QObject@@QEBAAEBV?$QList@PEAVQObject@@@@XZ", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr Children(IntPtr obj);
        [DllImport("Qt5Widgets.dll", EntryPoint = "?windowTitle@QWidget@@QEBA?AVQString@@XZ", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr WindowTitle(IntPtr widget, out IntPtr result);
        [DllImport("Qt5Widgets.dll", EntryPoint = "?currentText@QComboBox@@QEBA?AVQString@@XZ", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr CurrentText(IntPtr combo, out IntPtr result);
        [DllImport("Qt5Widgets.dll", EntryPoint = "?itemText@QComboBox@@QEBA?AVQString@@H@Z", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr ItemText(IntPtr combo, out IntPtr result, int index);
        [DllImport("Qt5Widgets.dll", EntryPoint = "?count@QComboBox@@QEBAHXZ", CallingConvention = CallingConvention.Cdecl)]
        private static extern int ComboCount(IntPtr combo);
        [StructLayout(LayoutKind.Sequential)]
        // QGenericArgument stores data before name, although its constructor
        // receives (name, data). Preserve that native field order.
        private struct Argument { public IntPtr Data; public IntPtr Name; }
        [DllImport("Qt5Core.dll", EntryPoint = "?invokeMethod@QMetaObject@@SA_NPEAVQObject@@PEBDW4ConnectionType@Qt@@VQGenericArgument@@333333333@Z", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool InvokeMethod(IntPtr obj, [MarshalAs(UnmanagedType.LPStr)] string name, int connection,
            Argument a0, Argument a1, Argument a2, Argument a3, Argument a4, Argument a5, Argument a6, Argument a7, Argument a8, Argument a9);
        [DllImport("Qt5Core.dll", EntryPoint = "??1QString@@QEAA@XZ", CallingConvention = CallingConvention.Cdecl)]
        private static extern void DestroyQString(ref IntPtr value);
        [DllImport("Qt5Core.dll", EntryPoint = "?singleShot@QTimer@@SAXHPEBVQObject@@PEBD@Z", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SingleShot(int milliseconds, IntPtr receiver, [MarshalAs(UnmanagedType.LPStr)] string member);

        private static string QString(IntPtr value)
        {
            int size = Marshal.ReadInt32(value, 4);
            if (size < 0 || size > 4096) throw new InvalidOperationException("Unexpected Qt string length.");
            return Marshal.PtrToStringUni(new IntPtr(value.ToInt64() + Marshal.ReadInt64(value, 16)), size);
        }

        private static IntPtr FindCombo(IntPtr dialog)
        {
            var pending = new Queue<IntPtr>();
            pending.Enqueue(dialog);
            for (int visited = 0; pending.Count > 0 && visited < 256; visited++)
            {
                IntPtr obj = pending.Dequeue();
                if (Inherits(obj, "QComboBox"))
                {
                    return obj;
                }
                IntPtr list = Marshal.ReadIntPtr(Children(obj));
                int begin = Marshal.ReadInt32(list, 8), end = Marshal.ReadInt32(list, 12);
                if (begin < 0 || end < begin || end - begin > 1024) return IntPtr.Zero;
                for (int i = begin; i < end; i++) pending.Enqueue(Marshal.ReadIntPtr(list, 16 + i * 8));
            }
            return IntPtr.Zero;
        }

        public NativeQtTemplate(Action<string> log, string template = "default")
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(template, "^[A-Za-z][A-Za-z0-9_]{0,127}$"))
                throw new InvalidOperationException("Invalid terrain layer template name.");
            if (ActiveModalWidget() != IntPtr.Zero) throw new InvalidOperationException("Resolve the existing editor dialog before generating terrain.");
            ThreadPool.QueueUserWorkItem(_ => {
                IntPtr ownedDialog = IntPtr.Zero;
                try
                {
                    DateTime deadline = DateTime.UtcNow.AddSeconds(10);
                    bool selectionQueued = false;
                    while (!_stopped && DateTime.UtcNow < deadline)
                    {
                        Thread.Sleep(50);
                        IntPtr modal = ActiveModalWidget();
                        if (modal == IntPtr.Zero) continue;
                        IntPtr title;
                        WindowTitle(modal, out title);
                        string text;
                        try { text = QString(title); }
                        finally { DestroyQString(ref title); }
                        IntPtr combo = FindCombo(modal);
                        if (text != "Select Template" || !Inherits(modal, "QDialog") || combo == IntPtr.Zero)
                        { Diagnostic = "Only the terrain Select Template dialog may be confirmed."; log(Diagnostic); return; }
                        ownedDialog = modal;
                        IntPtr current;
                        CurrentText(combo, out current);
                        string selected;
                        try { selected = QString(current); }
                        finally { DestroyQString(ref current); }
                        if (selected != template)
                        {
                            if (selectionQueued) continue;
                            int count = ComboCount(combo), match = -1;
                            if (count < 1 || count > 4096) throw new InvalidOperationException("Invalid template list.");
                            for (int i = 0; i < count; i++)
                            {
                                IntPtr item;
                                ItemText(combo, out item, i);
                                try { if (QString(item) == template) match = i; }
                                finally { DestroyQString(ref item); }
                            }
                            if (match < 0) { Diagnostic = "Requested terrain template is not available."; SingleShot(0, modal, "1reject()"); return; }
                            IntPtr type = Marshal.StringToHGlobalAnsi("int"), data = Marshal.AllocHGlobal(4);
                            try
                            {
                                Marshal.WriteInt32(data, match);
                                Argument empty = new Argument();
                                // Qt::QueuedConnection=2; the value is copied before this call returns.
                                if (!InvokeMethod(combo, "setCurrentIndex", 2, new Argument { Name = type, Data = data },
                                    empty, empty, empty, empty, empty, empty, empty, empty, empty))
                                    throw new InvalidOperationException("Cannot queue terrain template selection.");
                            }
                            finally { Marshal.FreeHGlobal(data); Marshal.FreeHGlobal(type); }
                            selectionQueued = true;
                            continue;
                        }
                        // Thread-safe Qt scheduling: accept executes on the dialog's own thread.
                        SingleShot(0, modal, "1accept()");
                        Accepted = true;
                        log("Queued native acceptance of Select Template: " + template + ".");
                        return;
                    }
                    if (!_stopped && ownedDialog != IntPtr.Zero && ActiveModalWidget() == ownedDialog)
                    { Diagnostic = "Terrain template selection timed out; cancelling its owned dialog."; SingleShot(0, ownedDialog, "1reject()"); }
                }
                catch (Exception error)
                {
                    Diagnostic = error.Message; log("Template confirmation failed: " + error.Message);
                    if (ownedDialog != IntPtr.Zero && ActiveModalWidget() == ownedDialog) SingleShot(0, ownedDialog, "1reject()");
                }
            });
        }

        public void Dispose() { _stopped = true; }
    }
}
